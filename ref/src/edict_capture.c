/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: record mode for the QuakeC VM (docs/TESTING.md, Capture), -edictcapture <file>.

Records every entry of the engine into QuakeC, every QuakeC-to-QuakeC call and every builtin call, and at each of
these boundaries what changed in the memory the VM sees: the progs globals and the fields of every edict in use.
A shadow copy of that memory is kept; each boundary record carries the words that differ from it (then the shadow
is updated). So the stream says, in order:
  'E'  what the engine changed before entering QuakeC          (the replay applies it)
  'B'  what the VM changed up to a builtin call                 (the replay's VM must have changed exactly that)
  'R'  what the builtin changed                                 (the replay applies it; the builtin is not run)
  'L'  what the VM changed up to its return to the engine       (checked like 'B')
A builtin that runs QuakeC itself (touch functions from walkmove, for example) shows as nested 'E' ... 'L' between
its 'B' and 'R'. Linking is recorded as events too ('K' before SV_LinkEdict, 'k' after it with the leafs and the
area node the edict went into, 'U' at SV_UnlinkEdict), so that the port links in the same order the reference did,
whether the engine, a replayed builtin or the port's own code linked. Replaying the stream runs only the VM: cut 2, step 2 (docs/PLAN.md section C).

Engine state the builtins read but the VM does not see is recorded too: the rand seed (engine and builtins draw
from one stream) and sv.time at every entry and after every builtin; per map svs.maxclients, the cvars and the
offset of pr_string_temp, where ftos and vtos write; and in every diff the edict headers' free flag and freetime
that changed (ED_Alloc reads them).

Words are raw 32-bit values. With the reference's edict stride in the port (docs/adr/D7) entity offsets and field
pointers are equal on both sides. String offsets are not, for strings outside the progs string block (allocated
on the hunk by the engine): their contents are recorded wherever a string-typed word that changed points to one,
and after every builtin whose return value points to pr_string_temp (ftos, vtos).

File layout (all integers little-endian):
  header  "ECP1"  u32 version
  'M'  char map[64]  u32 progs_crc  u32 numglobals  u32 entityfields  u32 edict_header_bytes  u32 max_edicts
       u32 numstrings  i32 string_temp (pr_string_temp - pr_strings)  u32 maxclients
                                                          also when the same map spawns again (restart after death)
       u32 numcvars  numcvars x { char name[] NUL  char string[] NUL }
                                                          before the first record on a new map; resets the shadow
  'F'  u32 fnum  char name[]  NUL                         first use of a function: its name from the progs
  'E'  u32 fnum  u32 host_framecount  u8 sv.state  u32 self (edict number)  u32 other (edict number)
       u32 rand_seed  f64 sv.time  diff
  'L'  u32 fnum  u32 return[3]  diff
  'C'  u32 fnum
  'B'  u16 builtin  u32 fnum  u8 argc  u32 parm[3 * argc]  diff
  'R'  u16 builtin  u32 return[3]  u32 rand_seed  f64 sv.time  diff
  'K'  u32 edict  u8 touch_triggers  diff                 SV_LinkEdict starts
  'k'  u32 edict  u32 num_leafs  i16 leafnums[num_leafs]  i32 area node (-1: not linked)  u8 list (0 none, 1 solid,
       2 trigger)  i32 previous edict in the list (-1: the head)  diff
                                                          SV_LinkEdict returns (touch functions ran in between)
  'U'  u32 edict                                          SV_UnlinkEdict
  'S'  u32 rand_seed  f64 sv.time  u32 svs.serverflags  64 x { u8 set  [char style[] NUL] }  diff
                                                          after ED_LoadFromFile (patch 0005): the frame-0 state
  'H'  u32 host_framecount  f64 host_frametime  u32 rand_seed  f64 sv.time  diff
                                                          Host_ServerFrame begins (patch 0006)
  diff:  u32 num_edicts  u32 n  n x { u32 word  u32 value }  u32 m  m x { i32 string offset  char text[]  NUL }
         u32 h  h x { u32 edict  u32 free  u32 freetime bits }
         word w < numglobals is global w; otherwise edict (w - numglobals) / entityfields,
         field word (w - numglobals) % entityfields
*/
#include "quakedef.h"
#include "edict_capture.h"

#define ECP_VERSION		8
#define BUFFER_SIZE		(1 << 16)

extern unsigned short	pr_crc;				// pr_edict.c
extern char				*pr_string_temp;	// pr_cmds.c (on the hunk, patch 0002)

int		edict_capture;

static FILE				*out;
static unsigned char	buffer[BUFFER_SIZE];
static int				buffered;

static char				map_name[64];		// the map the tables below are for
static int				last_state;			// sv.state at the last entry: active, then loading is a new spawn
static unsigned char	*named;				// [numfunctions]: name already written
static unsigned int		*shadow;			// [numglobals + max_edicts * entityfields]
static unsigned char	*global_string;		// [numglobals]: covered by an ev_string globaldef
static unsigned char	*field_string;		// [entityfields]: covered by an ev_string fielddef
static int				*changed;			// scratch: indices of the words of one diff
static unsigned int		*shadow_header;		// [2 * max_edicts]: free, freetime bits

static void Flush (void)
{
	if (buffered && fwrite (buffer, 1, buffered, out) != (size_t)buffered)
		Sys_Error ("EdictCapture: write failed");
	buffered = 0;
}

static void put8 (int v)
{
	if (buffered == BUFFER_SIZE)
		Flush ();
	buffer[buffered++] = (unsigned char)v;
}

static void put16 (int v)
{
	put8 (v); put8 (v >> 8);
}

static void put32 (unsigned int v)
{
	put8 (v); put8 (v >> 8); put8 (v >> 16); put8 (v >> 24);
}

static void putword (int ofs)
{
	put32 (((unsigned int *)pr_globals)[ofs]);
}

static void put64 (unsigned long long v)
{
	put32 ((unsigned int)v); put32 ((unsigned int)(v >> 32));
}

static void putdouble (double d)
{
	unsigned long long	bits;

	memcpy (&bits, &d, 8);
	put64 (bits);
}

static void putname (const char *s)
{
	for ( ; *s ; s++)
		put8 (*s);
	put8 (0);
}

// The engine state the builtins read besides the VM's memory.
static void putengine (void)
{
	put32 (edict_rand_state ());
	putdouble (sv.time);
}

static void putstring (int ofs)
{
	const char	*s;

	put32 (ofs);
	for (s = pr_strings + ofs ; *s ; s++)
		put8 (*s);
	put8 (0);
}

void EdictCapture_Open (char *path)
{
	out = fopen (path, "wb");
	if (!out)
		Sys_Error ("EdictCapture: cannot open %s", path);
	put8 ('E'); put8 ('C'); put8 ('P'); put8 ('1');
	put32 (ECP_VERSION);
	edict_capture = 1;
}

void EdictCapture_Close (void)
{
	if (!out)
		return;
	Flush ();
	fclose (out);
	out = NULL;
	edict_capture = 0;
}

static void *Alloc (int size)
{
	void	*p = calloc (size, 1);

	if (!p)
		Sys_Error ("EdictCapture: out of memory");
	return p;
}

static void MarkStrings (unsigned char *types, int count, ddef_t *defs, int numdefs)
{
	int		i;

	for (i = 0 ; i < numdefs ; i++)
		if ((defs[i].type & ~DEF_SAVEGLOBAL) == ev_string && defs[i].ofs < count)
			types[defs[i].ofs] = 1;
}

// A new map: new progs (reloaded per map, often at the same address), new tables, a zero shadow.
static void NewMap (void)
{
	cvar_t	*var;
	int		i, n = strlen (sv.name);
	int		words = progs->numglobals + sv.max_edicts * progs->entityfields;

	free (named); free (shadow); free (global_string); free (field_string); free (changed); free (shadow_header);
	named = Alloc (progs->numfunctions);
	shadow = Alloc (words * 4);
	changed = Alloc (words * sizeof (int));
	shadow_header = Alloc (sv.max_edicts * 2 * 4);
	global_string = Alloc (progs->numglobals);
	field_string = Alloc (progs->entityfields);
	MarkStrings (global_string, progs->numglobals, pr_globaldefs, progs->numglobaldefs);
	MarkStrings (field_string, progs->entityfields, pr_fielddefs, progs->numfielddefs);
	strncpy (map_name, sv.name, sizeof (map_name));

	put8 ('M');
	for (i = 0 ; i < 64 ; i++)
		put8 (i < n ? sv.name[i] : 0);
	put32 (pr_crc);
	put32 (progs->numglobals);
	put32 (progs->entityfields);
	put32 (pr_edict_size - progs->entityfields * 4);
	put32 (sv.max_edicts);
	put32 (progs->numstrings);
	put32 (pr_string_temp - pr_strings);
	put32 (svs.maxclients);
	for (n = 0, var = cvar_vars ; var ; var = var->next)
		n++;
	put32 (n);
	for (var = cvar_vars ; var ; var = var->next)
	{
		putname (var->name);
		putname (var->string);
	}
}

static void CheckMap (void)
{
	if (!named || strncmp (map_name, sv.name, sizeof (map_name)) || (sv.state == ss_loading && last_state == ss_active))
		NewMap ();
	last_state = sv.state;
}

static unsigned int *Word (int w)
{
	int		e, f;

	if (w < progs->numglobals)
		return (unsigned int *)pr_globals + w;
	e = (w - progs->numglobals) / progs->entityfields;
	f = (w - progs->numglobals) % progs->entityfields;
	return (unsigned int *)&EDICT_NUM (e)->v + f;
}

static qboolean IsString (int w)
{
	if (w < progs->numglobals)
		return global_string[w];
	return field_string[(w - progs->numglobals) % progs->entityfields];
}

static qboolean EngineString (int ofs)
{
	return ofs < 0 || ofs >= progs->numstrings;
}

// Whether edict e's free flag or freetime differs from the shadow; with update, the shadow takes the new values.
static qboolean HeaderChanged (int e, qboolean update)
{
	edict_t			*ed = EDICT_NUM (e);
	unsigned int	f = ed->free, t;

	memcpy (&t, &ed->freetime, 4);
	if (f == shadow_header[2 * e] && t == shadow_header[2 * e + 1])
		return false;
	if (update)
	{
		shadow_header[2 * e] = f;
		shadow_header[2 * e + 1] = t;
	}
	return true;
}

// The words that differ from the shadow, the engine strings they name, and extra (unless -1): a string offset
// whose text is recorded in any case.
static void Diff (int extra)
{
	int				n = 0, m = 0, i, w, end;
	unsigned int	*p;

	end = progs->numglobals + sv.num_edicts * progs->entityfields;
	for (w = 0 ; w < end ; w++)
	{
		p = Word (w);
		if (*p != shadow[w])
		{
			shadow[w] = *p;
			changed[n++] = w;
		}
	}
	put32 (sv.num_edicts);
	put32 (n);
	for (i = 0 ; i < n ; i++)
	{
		put32 (changed[i]);
		put32 (shadow[changed[i]]);
	}
	for (i = 0 ; i < n ; i++)
		if (IsString (changed[i]) && EngineString ((int)shadow[changed[i]]))
			m++;
	if (extra != -1)
		m++;
	put32 (m);
	for (i = 0 ; i < n ; i++)
		if (IsString (changed[i]) && EngineString ((int)shadow[changed[i]]))
			putstring ((int)shadow[changed[i]]);
	if (extra != -1)
		putstring (extra);

	// edict headers: free and freetime
	for (n = 0, w = 0 ; w < sv.num_edicts ; w++)
		if (HeaderChanged (w, 0))
			n++;
	put32 (n);
	for (w = 0 ; w < sv.num_edicts ; w++)
		if (HeaderChanged (w, 1))
		{
			put32 (w);
			put32 (shadow_header[2 * w]);
			put32 (shadow_header[2 * w + 1]);
		}
}

static void Name (int fnum)
{
	const char	*s;

	if (fnum < 0 || fnum >= progs->numfunctions || named[fnum])
		return;
	named[fnum] = 1;
	put8 ('F');
	put32 (fnum);
	for (s = pr_strings + pr_functions[fnum].s_name ; *s ; s++)
		put8 (*s);
	put8 (0);
}

static int EdictNumber (int e)
{
	return NUM_FOR_EDICT (PROG_TO_EDICT (e));
}

void EdictCapture_Enter (int fnum)
{
	CheckMap ();
	Name (fnum);
	put8 ('E');
	put32 (fnum);
	put32 (host_framecount);
	put8 (sv.state);
	put32 (EdictNumber (pr_global_struct->self));
	put32 (EdictNumber (pr_global_struct->other));
	putengine ();
	Diff (-1);
}

void EdictCapture_Leave (int fnum)
{
	put8 ('L');
	put32 (fnum);
	putword (OFS_RETURN); putword (OFS_RETURN + 1); putword (OFS_RETURN + 2);
	Diff (-1);
}

void EdictCapture_Call (int fnum)
{
	Name (fnum);
	put8 ('C');
	put32 (fnum);
}

void EdictCapture_Builtin (int num, int fnum)
{
	int		i;

	Name (fnum);
	put8 ('B');
	put16 (num);
	put32 (fnum);
	put8 (pr_argc);
	for (i = 0 ; i < pr_argc * 3 ; i++)
		putword (OFS_PARM0 + i);
	Diff (-1);
}

// world.c's areanode_t (not in a header), for finding the node a link belongs to; patch 0004 removes the static
typedef struct edict_areanode_s
{
	int		axis;
	float	dist;
	struct edict_areanode_s	*children[2];
	link_t	trigger_edicts;
	link_t	solid_edicts;
} edict_areanode_t;
#define EDICT_AREA_NODES	32		// AREA_NODES
extern edict_areanode_t	sv_areanodes[EDICT_AREA_NODES];

void EdictCapture_Link (edict_t *ent, int touch)
{
	put8 ('K');
	put32 (NUM_FOR_EDICT (ent));
	put8 (touch);
	Diff (-1);
}

void EdictCapture_Linked (edict_t *ent)
{
	link_t	*l;
	int		i, node = -1, list = 0;

	put8 ('k');
	put32 (NUM_FOR_EDICT (ent));
	put32 (ent->num_leafs);
	for (i = 0 ; i < ent->num_leafs ; i++)
		put16 (ent->leafnums[i]);
	if (ent->area.prev)
		for (l = ent->area.next ; node < 0 ; l = l->next)	// the list's head is the only link outside an edict
			for (i = 0 ; i < EDICT_AREA_NODES ; i++)
				if (l == &sv_areanodes[i].solid_edicts || l == &sv_areanodes[i].trigger_edicts)
				{
					node = i;
					list = l == &sv_areanodes[i].solid_edicts ? 1 : 2;
				}
	put32 (node);
	put8 (list);
	put32 (node < 0 || ent->area.prev == &sv_areanodes[node].solid_edicts || ent->area.prev == &sv_areanodes[node].trigger_edicts
		? -1 : NUM_FOR_EDICT (EDICT_FROM_AREA (ent->area.prev)));
	Diff (-1);
}

// The frame-0 point (patch 0005, through Edict_Spawned): the engine's changes since the last spawn function and the
// engine state the hash covers besides the VM's memory, so that the replay can hash what the reference hashed.
void EdictCapture_Spawn (void)
{
	int		i;

	put8 ('S');
	putengine ();
	put32 (svs.serverflags);
	for (i = 0 ; i < MAX_LIGHTSTYLES ; i++)
	{
		put8 (sv.lightstyles[i] ? 1 : 0);
		if (sv.lightstyles[i])
			putname (sv.lightstyles[i]);
	}
	Diff (-1);
}

// Host_ServerFrame begins (patch 0006): in a frame that spawns a map, after SV_SpawnServer's own two SV_Physics.
void EdictCapture_ServerFrame (void)
{
	put8 ('H');
	put32 (host_framecount);
	putdouble (host_frametime);
	putengine ();
	Diff (-1);
}

void EdictCapture_Unlink (edict_t *ent)
{
	put8 ('U');
	put32 (NUM_FOR_EDICT (ent));
}

void EdictCapture_BuiltinDone (int num)
{
	int		ret = G_INT (OFS_RETURN);

	put8 ('R');
	put16 (num);
	putword (OFS_RETURN); putword (OFS_RETURN + 1); putword (OFS_RETURN + 2);
	putengine ();
	Diff (pr_strings + ret == pr_string_temp ? ret : -1);
}
