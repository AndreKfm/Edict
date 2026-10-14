/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: per-frame state hashes (.edh), docs/PLAN.md section E.1.

Every value is serialized into a canonical little-endian byte stream and hashed with XXH64. Nothing that depends
on memory layout enters the gated lanes: strings are hashed by content, entity references by edict index, and
padding and pointers (edict_t.area) are never read. The C# port produces the same streams.

File layout (all integers little-endian):
  header   "EDH1"  u32 version (4)  u32 flags
  record   'M'  char map[64]  u32 progs_crc  u32 entityfields  u32 numglobals          on every new map, and before
                                                                                      every spawn record
  record   'S'  u32 host_framecount  u64 sv.time bits  u32 num_edicts
                u64 lane_edicts  u64 lane_globals_typed  u64 lane_globals_raw  u64 lane_messages
                u64 entity_hash x num_edicts                                          after ED_LoadFromFile in
                                                                                      SV_SpawnServer (patch 0005):
                                                                                      the cut-2 frame-0 gate
  record   'F'  u32 host_framecount  u64 sv.time bits  u32 num_edicts
                u64 lane_edicts  u64 lane_globals_typed  u64 lane_globals_raw  u64 lane_messages
                [u64 entity_hash x num_edicts, if EDH_FLAG_PER_ENTITY]                on every server frame
  record   'I'  u32 rand state  u32 numcvars  numcvars x { char name[] NUL  char string[] NUL }
                                                                                      once (-edictclient), after
                                                                                      host frame 0 and before its
                                                                                      'C' record: the rand state
                                                                                      before frame 0, the cvars
                                                                                      after it (frame 0 runs
                                                                                      quake.rc's configs)
  record   'C'  u32 host_framecount  u64 cl.time bits  u32 cl.num_entities  u32 rand state
                u64 lane_client  u64 lane_entities  u64 lane_effects
                [u64 entity_hash x cl.num_entities, if EDH_FLAG_PER_ENTITY]           after every host frame of a
                                                                                      connected client (a demo, a
                                                                                      listen server: -edictclient)
  record   'V'  u32 host_framecount  u32 width  u32 height  u64 frame buffer hash  u64 palette hash
                                                                                      at every VID_Update (the
                                                                                      screen's present, edict_vid.c;
                                                                                      -edictscreen): the frame buffer
                                                                                      row by row, the 256 x RGB
                                                                                      palette it is shown through;
                                                                                      a frame can present more than
                                                                                      once (Con_Printf during the
                                                                                      signon redraws the screen)
Version 1 had no spawn records, version 2 no messages lane, version 3 no client records, version 4 no screen records.
Lanes:
  edicts         sv.time, num_edicts, state, paused, serverflags, lightstyles, then (index, entity hash) pairs
  globals_typed  every globals word covered by a globaldef, typed like entity fields
  globals_raw    every globals word as raw bits; contains layout-dependent entity offsets: diagnostic only
  messages       what the server sent to the tape connection since the previous record (edict_tape.c), each
                 message as u8 kind (1 reliable, 2 unreliable)  u32 length  bytes; XXH64 of the empty stream if
                 nothing was sent

Client lanes (EdictHash_ClientFrame, below):
  client         cls.state and signon, cl's scalars, stats, items, color shifts, view and velocity vectors, times,
                 precache lists by name, scores, light styles, the visible entities as references, the rand state
  entities       (index, hash) of cl_entities, then the statics, the temp entities and the gun model in full
  effects        the dynamic lights, the beams, the numbers of active and free particles

Diagnostic: -edictmessages <file> (.emg) writes the messages themselves, for localizing a difference in that lane:
  header   "EMG1"  u32 version (1)
  record   u8 kind (1, 2)  u32 host_framecount  u32 length  byte data[length]      every message sent
  record   u8 'S' or 'F'  u32 host_framecount  u32 0                                at every spawn and frame record
                                                                                    of the .edh, which ends the
                                                                                    messages its lane covers
*/
#include "quakedef.h"
#include "edict_hash.h"
#include "edict_capture.h"

/*
==============================================================================
Canonical byte streams
==============================================================================
*/

#define STREAM_SIZE	(1 << 20)
static unsigned char	stream[STREAM_SIZE];
static int				stream_len;

static void put8 (int v)
{
	if (stream_len >= STREAM_SIZE)
		Sys_Error ("EdictHash: stream overflow");
	stream[stream_len++] = (unsigned char)v;
}

static void put32 (unsigned int v)
{
	put8 (v); put8 (v >> 8); put8 (v >> 16); put8 (v >> 24);
}

static void put64 (edict_u64 v)
{
	put32 ((unsigned int)v); put32 ((unsigned int)(v >> 32));
}

// Every NaN hashes as the same bits: which NaN float arithmetic produces is not part of the state (docs/adr/D8).
static void putfloat (float f)
{
	unsigned int bits;
	memcpy (&bits, &f, 4);
	put32 (f != f ? 0x7fc00000u : bits);
}

static void putdouble (double d)
{
	edict_u64 bits;
	memcpy (&bits, &d, 8);
	put64 (d != d ? 0x7ff8000000000000ull : bits);
}

static void putstring (const char *s)
{
	while (*s)
		put8 (*s++);
	put8 (0);
}

/*
==============================================================================
Messages to clients
==============================================================================
*/

static FILE			*edh;

#define SENT_SIZE	(1 << 20)
static unsigned char	sent[SENT_SIZE];	// the messages lane's stream since the last record
static int				sent_len;
static FILE				*emg;

static void SentPut (const void *data, int length)
{
	if (sent_len + length > SENT_SIZE)
		Sys_Error ("EdictHash: message stream overflow");
	memcpy (sent + sent_len, data, length);
	sent_len += length;
}

static void EmgWrite (int kind, int length, const void *data)
{
	unsigned char	h[9];
	unsigned int	fc = (unsigned int)host_framecount, len = (unsigned int)length;
	int				i;

	h[0] = (unsigned char)kind;
	for (i = 0 ; i < 4 ; i++)
	{
		h[1 + i] = (unsigned char)(fc >> (8 * i));
		h[5 + i] = (unsigned char)(len >> (8 * i));
	}
	if (fwrite (h, 1, 9, emg) != 9 || (length && fwrite (data, 1, length, emg) != (size_t)length))
		Sys_Error ("EdictHash: write failed");
}

void EdictHash_Sent (int kind, const void *data, int length)
{
	unsigned char	h[5];
	int				i;

	if (!edh)
		return;
	h[0] = (unsigned char)kind;
	for (i = 0 ; i < 4 ; i++)
		h[1 + i] = (unsigned char)((unsigned int)length >> (8 * i));
	SentPut (h, 5);
	SentPut (data, length);
	if (emg)
		EmgWrite (kind, length, data);
}

// The lane over what was sent since the last record; starts the next one.
static edict_u64 HashMessages (int kind)
{
	edict_u64	h = EdictHash_XXH64 (sent, sent_len);

	sent_len = 0;
	if (emg)
		EmgWrite (kind, 0, NULL);
	return h;
}

/*
==============================================================================
Word types for entity fields and globals
==============================================================================
*/

#define WT_RAW		0	// no def covers the word
#define WT_FLOAT	1
#define WT_STRING	2
#define WT_ENTITY	3
#define WT_FUNCTION	4
#define WT_FIELD	5
#define WT_POINTER	6

static unsigned char	*fieldtypes;	// [progs->entityfields]
static unsigned char	*globaltypes;	// [progs->numglobals]
static dprograms_t		*typed_for;		// progs the maps were built for

static void BuildTypes (unsigned char *types, int count, ddef_t *defs, int numdefs)
{
	int		i, t, ofs;

	memset (types, WT_RAW, count);
	for (i = 0 ; i < numdefs ; i++)
	{
		t = defs[i].type & ~DEF_SAVEGLOBAL;
		ofs = defs[i].ofs;
		if (t == ev_vector)
		{
			if (ofs + 3 <= count)
				types[ofs] = types[ofs + 1] = types[ofs + 2] = WT_FLOAT;
			continue;
		}
		if (ofs >= count)
			continue;
		switch (t)
		{
		case ev_float:		types[ofs] = WT_FLOAT; break;
		case ev_string:		types[ofs] = WT_STRING; break;
		case ev_entity:		types[ofs] = WT_ENTITY; break;
		case ev_function:	types[ofs] = WT_FUNCTION; break;
		case ev_field:		types[ofs] = WT_FIELD; break;
		case ev_pointer:	types[ofs] = WT_POINTER; break;
		default:			break;	// ev_void
		}
	}
}

// One word, typed. Returns without writing for pointers (layout-dependent, never in a gated lane).
static void PutWord (int type, int raw)
{
	float	f;

	switch (type)
	{
	case WT_FLOAT:
		memcpy (&f, &raw, 4);
		putfloat (f);
		break;
	case WT_STRING:
		putstring (pr_strings + raw);
		break;
	case WT_ENTITY:
		// EDICT_TO_PROG is a byte offset with a layout-dependent stride: hash the index
		if (raw % pr_edict_size)
			Sys_Error ("EdictHash: entity value %i is not a multiple of %i", raw, pr_edict_size);
		put32 ((unsigned int)(raw / pr_edict_size));
		break;
	case WT_POINTER:
		break;
	default:		// function index, field offset (in words), untyped
		put32 ((unsigned int)raw);
		break;
	}
}

/*
==============================================================================
Lanes
==============================================================================
*/

static edict_u64 HashEntity (int index)
{
	edict_t	*e = EDICT_NUM (index);
	int		*words = (int *)&e->v;
	int		i;

	stream_len = 0;
	put32 (index);
	put8 (e->free ? 1 : 0);
	putfloat (e->freetime);
	put32 (e->num_leafs);
	for (i = 0 ; i < e->num_leafs && i < MAX_ENT_LEAFS ; i++)
		put32 ((unsigned int)(unsigned short)e->leafnums[i]);
	for (i = 0 ; i < 3 ; i++) putfloat (e->baseline.origin[i]);
	for (i = 0 ; i < 3 ; i++) putfloat (e->baseline.angles[i]);
	put32 (e->baseline.modelindex);
	put32 (e->baseline.frame);
	put32 (e->baseline.colormap);
	put32 (e->baseline.skin);
	put32 (e->baseline.effects);
	for (i = 0 ; i < progs->entityfields ; i++)
		PutWord (fieldtypes[i], words[i]);
	return EdictHash_XXH64 (stream, stream_len);
}

static edict_u64 HashGlobals (int typed)
{
	int		*words = (int *)pr_globals;
	int		i;

	stream_len = 0;
	for (i = 0 ; i < progs->numglobals ; i++)
	{
		if (!typed)
			put32 ((unsigned int)words[i]);
		else if (globaltypes[i] != WT_RAW && globaltypes[i] != WT_POINTER)
		{
			put32 (i);
			PutWord (globaltypes[i], words[i]);
		}
	}
	return EdictHash_XXH64 (stream, stream_len);
}

/*
==============================================================================
Output
==============================================================================
*/

static int			edh_flags;
static edict_u64	*entity_hashes;
static int			entity_hashes_size;

static void FlushStream (void)
{
	if (fwrite (stream, 1, stream_len, edh) != (size_t)stream_len)
		Sys_Error ("EdictHash: write failed");
	stream_len = 0;
}

void EdictHash_Open (char *path, int flags)
{
	edh = fopen (path, "wb");
	if (!edh)
		Sys_Error ("EdictHash: cannot open %s", path);
	edh_flags = flags;
	stream_len = 0;
	put8 ('E'); put8 ('D'); put8 ('H'); put8 ('1');
	put32 (5);
	put32 (flags);
	FlushStream ();
}

void EdictHash_OpenMessages (char *path)
{
	static const unsigned char	header[8] = { 'E', 'M', 'G', '1', 1, 0, 0, 0 };

	if (!edh)
		Sys_Error ("EdictHash: -edictmessages needs -edicthash (its records end the messages of each lane)");
	emg = fopen (path, "wb");
	if (!emg)
		Sys_Error ("EdictHash: cannot open %s", path);
	if (fwrite (header, 1, 8, emg) != 8)
		Sys_Error ("EdictHash: write failed");
}

static void MapRecord (void)
{
	char	name[64];
	int		i;

	fieldtypes = realloc (fieldtypes, progs->entityfields);
	globaltypes = realloc (globaltypes, progs->numglobals);
	if (!fieldtypes || !globaltypes)
		Sys_Error ("EdictHash: out of memory");
	BuildTypes (fieldtypes, progs->entityfields, pr_fielddefs, progs->numfielddefs);
	BuildTypes (globaltypes, progs->numglobals, pr_globaldefs, progs->numglobaldefs);
	typed_for = progs;

	memset (name, 0, sizeof (name));
	strncpy (name, sv.name, sizeof (name) - 1);
	stream_len = 0;
	put8 ('M');
	for (i = 0 ; i < 64 ; i++)
		put8 (name[i]);
	put32 (progs->crc);
	put32 (progs->entityfields);
	put32 (progs->numglobals);
	FlushStream ();
}

static void StateRecord (int kind, int per_entity)
{
	edict_u64	lane_edicts, lane_typed, lane_raw, lane_messages;
	int			i, n;

	n = sv.num_edicts;
	if (n > entity_hashes_size)
	{
		entity_hashes = realloc (entity_hashes, n * sizeof (edict_u64));
		if (!entity_hashes)
			Sys_Error ("EdictHash: out of memory");
		entity_hashes_size = n;
	}
	for (i = 0 ; i < n ; i++)
		entity_hashes[i] = HashEntity (i);

	stream_len = 0;
	putdouble (sv.time);
	put32 (n);
	put32 (sv.state);
	put32 (sv.paused ? 1 : 0);
	put32 (svs.serverflags);
	for (i = 0 ; i < MAX_LIGHTSTYLES ; i++)
	{
		if (sv.lightstyles[i])
		{
			put8 (1);
			putstring (sv.lightstyles[i]);
		}
		else
			put8 (0);
	}
	for (i = 0 ; i < n ; i++)
	{
		put32 (i);
		put64 (entity_hashes[i]);
	}
	lane_edicts = EdictHash_XXH64 (stream, stream_len);
	lane_typed = HashGlobals (1);
	lane_raw = HashGlobals (0);
	lane_messages = HashMessages (kind);

	stream_len = 0;
	put8 (kind);
	put32 (host_framecount);
	putdouble (sv.time);
	put32 (n);
	put64 (lane_edicts);
	put64 (lane_typed);
	put64 (lane_raw);
	put64 (lane_messages);
	if (per_entity)
		for (i = 0 ; i < n ; i++)
			put64 (entity_hashes[i]);
	FlushStream ();
}

void EdictHash_Frame (void)
{
	if (!edh)
		return;
	if (progs != typed_for)
		MapRecord ();
	StateRecord ('F', edh_flags & EDH_FLAG_PER_ENTITY);
}

// The hunk may give a new map's progs the old one's address, so the map record is written here unconditionally.
static void EdictHash_Spawn (void)
{
	if (!edh)
		return;
	MapRecord ();
	StateRecord ('S', 1);
}

void Edict_Spawned (void)
{
	if (edict_capture)
		EdictCapture_Spawn ();
	EdictHash_Spawn ();
}

/*
==============================================================================
The client (docs/PLAN.md, P5): one 'C' record after every host frame of a demo playback

The client as the harness runs it (-edictclient): no screen updates, so nothing of SCR_UpdateScreen's (the palette's
color shifts, the particles' movement and expiry in R_DrawParticles) is part of the state. Pointers are hashed as
what they point to: a model or sound by its name, a colormap as 0 (vid.colormap), 1 + i (scores[i]'s translation)
or -1 (none), a visible entity by its array and index. Renderer-only fields (efrags, visframe, dlightframe and bits,
trivial_accept, topnode) and the scoreboard's translation tables (derived from the colors) are left out.
==============================================================================
*/

extern int			num_temp_entities;			// cl_tent.c

/*
Diagnostic: -edictclientdump <first> <last> <file> writes, for the client records of host_framecount first to last,
the canonical streams themselves instead of only their hashes, to name the field where a lane differs:
  record   u32 host_framecount  u8 kind  u32 index  u32 length  byte stream[length]
kinds: 'e' one of cl_entities (index), 'E' the entities lane, 'c' the client lane, 'f' the effects lane.
*/
static FILE	*client_dump;
static int	client_dump_first, client_dump_last;

static void ClientDump (int kind, int index)
{
	unsigned char	h[13];
	unsigned int	v[3];
	int				i, j;

	if (!client_dump || host_framecount < client_dump_first || host_framecount > client_dump_last)
		return;
	v[0] = (unsigned int)host_framecount;
	v[1] = (unsigned int)index;
	v[2] = (unsigned int)stream_len;
	for (i = 0 ; i < 3 ; i++)
		for (j = 0 ; j < 4 ; j++)
			h[(i == 0 ? 0 : 1 + 4 * i) + j] = (unsigned char)(v[i] >> (8 * j));
	h[4] = (unsigned char)kind;
	if (fwrite (h, 1, 13, client_dump) != 13 || fwrite (stream, 1, stream_len, client_dump) != (size_t)stream_len)
		Sys_Error ("EdictHash: write failed");
}

void EdictHash_OpenClientDump (char *path, int first, int last)
{
	client_dump = fopen (path, "wb");
	if (!client_dump)
		Sys_Error ("EdictHash: cannot open %s", path);
	client_dump_first = first;
	client_dump_last = last;
}
extern particle_t	*active_particles, *free_particles;	// r_part.c

static void putvec (vec3_t v)
{
	putfloat (v[0]); putfloat (v[1]); putfloat (v[2]);
}

static void putmodel (model_t *m)
{
	putstring (m ? m->name : "");
}

static int ColormapIndex (byte *colormap)
{
	int		i;

	if (!colormap)
		return -1;
	if (colormap == vid.colormap)
		return 0;
	for (i = 0 ; i < cl.maxclients && cl.scores ; i++)
		if (colormap == cl.scores[i].translations)
			return 1 + i;
	Sys_Error ("EdictHash: a colormap that is neither vid.colormap nor a translation");
	return 0;
}

static void PutClientEntity (entity_t *e)
{
	put32 (e->forcelink);
	put32 (e->update_type);
	putvec (e->baseline.origin);
	putvec (e->baseline.angles);
	put32 (e->baseline.modelindex);
	put32 (e->baseline.frame);
	put32 (e->baseline.colormap);
	put32 (e->baseline.skin);
	put32 (e->baseline.effects);
	putdouble (e->msgtime);
	putvec (e->msg_origins[0]);
	putvec (e->msg_origins[1]);
	putvec (e->origin);
	putvec (e->msg_angles[0]);
	putvec (e->msg_angles[1]);
	putvec (e->angles);
	putmodel (e->model);
	put32 (e->frame);
	putfloat (e->syncbase);
	put32 ((unsigned int)ColormapIndex (e->colormap));
	put32 (e->effects);
	put32 (e->skinnum);
}

// The array and index of a visible entity (cl_visedicts): 0 cl_entities, 1 statics, 2 temp entities, 3 viewent.
static void PutEntityRef (entity_t *e)
{
	if (e >= cl_entities && e < cl_entities + MAX_EDICTS)
		{ put8 (0); put32 (e - cl_entities); }
	else if (e >= cl_static_entities && e < cl_static_entities + MAX_STATIC_ENTITIES)
		{ put8 (1); put32 (e - cl_static_entities); }
	else if (e >= cl_temp_entities && e < cl_temp_entities + MAX_TEMP_ENTITIES)
		{ put8 (2); put32 (e - cl_temp_entities); }
	else if (e == &cl.viewent)
		{ put8 (3); put32 (0); }
	else
		Sys_Error ("EdictHash: a visible entity outside the client's arrays");
}

static edict_u64 HashClientState (void)
{
	int		i, j;

	stream_len = 0;
	put32 (cls.state);
	put32 (cls.signon);
	put32 (cl.movemessages);
	putvec (cl.cmd.viewangles);
	putfloat (cl.cmd.forwardmove); putfloat (cl.cmd.sidemove); putfloat (cl.cmd.upmove);
	for (i = 0 ; i < MAX_CL_STATS ; i++)
		put32 (cl.stats[i]);
	put32 (cl.items);
	for (i = 0 ; i < 32 ; i++)
		putfloat (cl.item_gettime[i]);
	putfloat (cl.faceanimtime);
	for (i = 0 ; i < NUM_CSHIFTS ; i++)
	{
		for (j = 0 ; j < 3 ; j++)
			put32 (cl.cshifts[i].destcolor[j]);
		put32 (cl.cshifts[i].percent);
		for (j = 0 ; j < 3 ; j++)
			put32 (cl.prev_cshifts[i].destcolor[j]);
		put32 (cl.prev_cshifts[i].percent);
	}
	putvec (cl.mviewangles[0]); putvec (cl.mviewangles[1]); putvec (cl.viewangles);
	putvec (cl.mvelocity[0]); putvec (cl.mvelocity[1]); putvec (cl.velocity);
	putvec (cl.punchangle);
	putfloat (cl.idealpitch); putfloat (cl.pitchvel); put32 (cl.nodrift); putfloat (cl.driftmove);
	putdouble (cl.laststop);
	putfloat (cl.viewheight); putfloat (cl.crouch);
	put32 (cl.paused); put32 (cl.onground); put32 (cl.inwater);
	put32 (cl.intermission); put32 (cl.completed_time);
	putdouble (cl.mtime[0]); putdouble (cl.mtime[1]); putdouble (cl.time); putdouble (cl.oldtime);
	putfloat (cl.last_received_message);
	for (i = 0 ; i < MAX_MODELS ; i++)
		putmodel (cl.model_precache[i]);
	for (i = 0 ; i < MAX_SOUNDS ; i++)
		putstring (cl.sound_precache[i] ? cl.sound_precache[i]->name : "");
	putstring (cl.levelname);
	put32 (cl.viewentity); put32 (cl.maxclients); put32 (cl.gametype);
	putmodel (cl.worldmodel);
	put32 (cl.num_entities); put32 (cl.num_statics);
	put32 (cl.cdtrack); put32 (cl.looptrack);
	for (i = 0 ; i < cl.maxclients && cl.scores ; i++)
	{
		putstring (cl.scores[i].name);
		putfloat (cl.scores[i].entertime);
		put32 (cl.scores[i].frags);
		put32 (cl.scores[i].colors);
	}
	for (i = 0 ; i < MAX_LIGHTSTYLES ; i++)
	{
		put32 (cl_lightstyle[i].length);
		putstring (cl_lightstyle[i].map);
	}
	put32 (cl_numvisedicts);
	for (i = 0 ; i < cl_numvisedicts ; i++)
		PutEntityRef (cl_visedicts[i]);
	put32 (edict_rand_state ());
	ClientDump ('c', 0);
	return EdictHash_XXH64 (stream, stream_len);
}

static edict_u64 HashClientEntity (entity_t *e)
{
	stream_len = 0;
	PutClientEntity (e);
	ClientDump ('e', e - cl_entities);
	return EdictHash_XXH64 (stream, stream_len);
}

static edict_u64 HashClientEffects (void)
{
	particle_t	*p;
	int			i, n;

	stream_len = 0;
	for (i = 0 ; i < MAX_DLIGHTS ; i++)
	{
		putvec (cl_dlights[i].origin);
		putfloat (cl_dlights[i].radius);
		putfloat (cl_dlights[i].die);
		putfloat (cl_dlights[i].decay);
		putfloat (cl_dlights[i].minlight);
		put32 (cl_dlights[i].key);
	}
	for (i = 0 ; i < MAX_BEAMS ; i++)
	{
		put32 (cl_beams[i].entity);
		putmodel (cl_beams[i].model);
		putfloat (cl_beams[i].endtime);
		putvec (cl_beams[i].start);
		putvec (cl_beams[i].end);
	}
	for (n = 0, p = active_particles ; p ; p = p->next)
		n++;
	put32 (n);
	for (n = 0, p = free_particles ; p ; p = p->next)
		n++;
	put32 (n);
	ClientDump ('f', 0);
	return EdictHash_XXH64 (stream, stream_len);
}

void EdictHash_ClientStart (unsigned int rand_before)
{
	cvar_t	*var;
	int		n;

	if (!edh)
		return;
	stream_len = 0;
	put8 ('I');
	put32 (rand_before);
	for (n = 0, var = cvar_vars ; var ; var = var->next)
		n++;
	put32 (n);
	for (var = cvar_vars ; var ; var = var->next)
	{
		putstring (var->name);
		putstring (var->string);
	}
	FlushStream ();
}

void EdictHash_ClientFrame (void)
{
	edict_u64	lane_client, lane_entities, lane_effects;
	int			i, n;

	if (!edh || cls.state != ca_connected)
		return;		// a demo playback or a listen server's local client
	n = cl.num_entities;
	if (n > entity_hashes_size)
	{
		entity_hashes = realloc (entity_hashes, n * sizeof (edict_u64));
		if (!entity_hashes)
			Sys_Error ("EdictHash: out of memory");
		entity_hashes_size = n;
	}
	for (i = 0 ; i < n ; i++)
		entity_hashes[i] = HashClientEntity (&cl_entities[i]);

	// the entities lane: (index, hash) of cl_entities, then the statics, the temp entities and the gun model
	stream_len = 0;
	for (i = 0 ; i < n ; i++)
	{
		put32 (i);
		put64 (entity_hashes[i]);
	}
	for (i = 0 ; i < cl.num_statics ; i++)
		PutClientEntity (&cl_static_entities[i]);
	put32 (num_temp_entities);
	for (i = 0 ; i < num_temp_entities ; i++)
		PutClientEntity (&cl_temp_entities[i]);
	PutClientEntity (&cl.viewent);
	ClientDump ('E', 0);
	lane_entities = EdictHash_XXH64 (stream, stream_len);
	lane_client = HashClientState ();
	lane_effects = HashClientEffects ();

	stream_len = 0;
	put8 ('C');
	put32 (host_framecount);
	putdouble (cl.time);
	put32 (n);
	put32 (edict_rand_state ());
	put64 (lane_client);
	put64 (lane_entities);
	put64 (lane_effects);
	if (edh_flags & EDH_FLAG_PER_ENTITY)
		for (i = 0 ; i < n ; i++)
			put64 (entity_hashes[i]);
	FlushStream ();
}

void EdictHash_Screen (const unsigned char *buffer, int width, int height, const unsigned char *palette)
{
	edict_u64	frame_hash, palette_hash;

	if (!edh)
		return;
	frame_hash = EdictHash_XXH64 (buffer, width * height);
	palette_hash = EdictHash_XXH64 (palette, 768);
	stream_len = 0;
	put8 ('V');
	put32 (host_framecount);
	put32 (width);
	put32 (height);
	put64 (frame_hash);
	put64 (palette_hash);
	FlushStream ();
}

/*
Diagnostic: -edictdumpglobals <framecount> <file> writes the raw globals words (u32 each) and, after them,
one line per globaldef ("ofs type name\n") at the given host_framecount.
*/
void EdictHash_DumpGlobals (void)
{
	static int	frame = -1;
	int			i;
	FILE		*f;

	if (frame == -1)
	{
		i = COM_CheckParm ("-edictdumpglobals");
		frame = (i && i < com_argc - 2) ? Q_atoi (com_argv[i + 1]) : 0;
	}
	if (!frame || host_framecount != frame)
		return;
	i = COM_CheckParm ("-edictdumpglobals");
	f = fopen (com_argv[i + 2], "wb");
	if (!f)
		Sys_Error ("EdictHash: cannot open %s", com_argv[i + 2]);
	fwrite (pr_globals, 4, progs->numglobals, f);
	for (i = 0 ; i < progs->numglobaldefs ; i++)
		fprintf (f, "%i %i %s\n", pr_globaldefs[i].ofs, pr_globaldefs[i].type & ~DEF_SAVEGLOBAL,
			pr_strings + pr_globaldefs[i].s_name);
	fclose (f);
}

void EdictHash_Close (void)
{
	if (edh)
		fclose (edh);
	edh = NULL;
	if (emg)
		fclose (emg);
	emg = NULL;
	if (client_dump)
		fclose (client_dump);
	client_dump = NULL;
}
