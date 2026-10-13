/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict oracle library: level-1 entry points for the differential tests (docs/TESTING.md). Linked from the same
object files as the reference executable (R3). Every entry point calls the reference code itself (id's
functions or the pinned C library functions through the same redirects id's code uses), never a copy of it.
All arguments are blittable; strings are NUL-terminated byte buffers owned by the caller.
*/
#include "quakedef.h"
#include "edict_hash.h"

#ifdef _WIN32
#define EDICT_EXPORT	__declspec(dllexport)
#else
#define EDICT_EXPORT	__attribute__((visibility("default")))
#endif

#define ORACLE_VERSION	7

void PF_ftos (void);	// pr_cmds.c, not declared in a header
void PF_vtos (void);

EDICT_EXPORT int EdictOracle_Version (void) { return ORACLE_VERSION; }

// pinned C library (edict_libc.h redirects these names, exactly as in id's code)
EDICT_EXPORT double EdictOracle_Sin (double x) { return sin (x); }
EDICT_EXPORT double EdictOracle_Cos (double x) { return cos (x); }
EDICT_EXPORT double EdictOracle_Atan (double x) { return atan (x); }
EDICT_EXPORT double EdictOracle_Atan2 (double y, double x) { return atan2 (y, x); }
EDICT_EXPORT void EdictOracle_Srand (unsigned int seed) { srand (seed); }
EDICT_EXPORT int EdictOracle_Rand (void) { return rand (); }
EDICT_EXPORT int EdictOracle_Strcmp (const char *a, const char *b) { return strcmp (a, b); }

// C conversions as the reference compiles them (-fno-strict-float-cast-overflow: saturating, NaN to 0)
EDICT_EXPORT int EdictOracle_FloatToInt (float f) { return (int)f; }
EDICT_EXPORT int EdictOracle_DoubleToInt (double d) { return (int)d; }

// the reference's XXH64 (ref/src/edict_xxh64.c), which the port's hash streams must match
EDICT_EXPORT edict_u64 EdictOracle_XXH64 (const void *data, int length) { return EdictHash_XXH64 (data, length); }

// id's functions
EDICT_EXPORT float EdictOracle_Anglemod (float a) { return anglemod (a); }
EDICT_EXPORT float EdictOracle_QAtof (char *s) { return Q_atof (s); }

// ED_ParseEpair, ev_float: *(float *)d = atof (s);
EDICT_EXPORT float EdictOracle_EpairFloat (const char *s) { return atof (s); }

// PF_ftos and PF_vtos themselves, on a minimal globals block (they use only pr_globals, pr_strings and
// pr_string_temp). Returns the string length; the string is copied to out.
static float	oracle_globals[64];
static char		oracle_strings[512];

static int ReturnString (char *out, int size)
{
	const char	*s = pr_strings + G_INT (OFS_RETURN);
	int			n = strlen (s);

	if (n >= size)
		n = size - 1;
	memcpy (out, s, n);
	out[n] = 0;
	return n;
}

static void SetupGlobals (void)
{
	extern char	*pr_string_temp;

	pr_globals = oracle_globals;
	pr_strings = oracle_strings;
	pr_string_temp = oracle_strings + 256;	// patch 0002 moved it to the hunk; any buffer near pr_strings works
}

EDICT_EXPORT int EdictOracle_Ftos (float v, char *out, int size)
{
	SetupGlobals ();
	G_FLOAT (OFS_PARM0) = v;
	PF_ftos ();
	return ReturnString (out, size);
}

EDICT_EXPORT int EdictOracle_Vtos (float x, float y, float z, char *out, int size)
{
	SetupGlobals ();
	G_VECTOR (OFS_PARM0)[0] = x;
	G_VECTOR (OFS_PARM0)[1] = y;
	G_VECTOR (OFS_PARM0)[2] = z;
	PF_vtos ();
	return ReturnString (out, size);
}

/*
==============================================================================
The QuakeC VM on caller-provided programs (level 1 for pr_exec.c)

PR_ExecuteProgram itself, run on statements, functions, globals, strings and edicts the caller provides, so the
port's VM can be compared on inputs a game rarely produces (signed zeros, NaN payloads, empty strings at nonzero
offsets). The programs must not call builtins and must not fail (PR_RunError would longjmp out of the oracle).
==============================================================================
*/

extern int	localstack_used, pr_depth, pr_xstatement;	// pr_exec.c
extern dfunction_t	*pr_xfunction;

// sizeof (edict_t) - sizeof (entvars_t): the header in front of every edict's fields (docs/adr/D7).
EDICT_EXPORT int EdictOracle_EdictHeaderBytes (void) { return sizeof (edict_t) - sizeof (entvars_t); }

EDICT_EXPORT void EdictOracle_VmRun (dstatement_t *statements, int numstatements, dfunction_t *functions,
	int numfunctions, float *globals, int numglobals, char *strings, int numstrings, void *edicts, int entityfields,
	int active, int fnum)
{
	static dprograms_t	header;

	header.version = PROG_VERSION;
	header.numstatements = numstatements;
	header.numfunctions = numfunctions;
	header.numglobals = numglobals;
	header.numstrings = numstrings;
	header.entityfields = entityfields;
	progs = &header;
	pr_statements = statements;
	pr_functions = functions;
	pr_globals = globals;
	pr_global_struct = (globalvars_t *)globals;
	pr_strings = strings;
	pr_edict_size = entityfields * 4 + sizeof (edict_t) - sizeof (entvars_t);
	sv.edicts = edicts;
	sv.state = active ? ss_active : ss_loading;
	pr_depth = 0;
	localstack_used = 0;
	pr_xfunction = NULL;
	pr_xstatement = 0;
	if (fnum >= 0)
		PR_ExecuteProgram (fnum);
}

/*
==============================================================================
Builtins on caller-provided memory (level 1 for pr_cmds.c)

One builtin, pr_builtins[num] () itself, on globals, strings and edicts set up as for EdictOracle_VmRun. Only for
builtins that read nothing but this memory and the rand stream: rand is seeded with seed first, and the seed
afterwards is returned. pr_string_temp points to strings + stringtemp (ftos, vtos write there).
==============================================================================
*/

extern builtin_t	*pr_builtins;	// pr_cmds.c
extern int			pr_argc;		// pr_exec.c

// The header fields of edict n in an edict area laid out by the reference (free, freetime); set, then read back.
EDICT_EXPORT void EdictOracle_EdictHeader (void *edicts, int entityfields, int n, int set, int *free, float *freetime)
{
	edict_t	*e = (edict_t *)((byte *)edicts + n * (entityfields * 4 + sizeof (edict_t) - sizeof (entvars_t)));

	if (set)
	{
		memset (e, 0, sizeof (edict_t) - sizeof (entvars_t));	// unlinked (area.prev NULL: SV_UnlinkEdict returns)
		e->free = *free;
		e->freetime = *freetime;
	}
	*free = e->free;
	*freetime = e->freetime;
}

// num_edicts in and out; maxclients, max_edicts and time as the server has them.
EDICT_EXPORT unsigned int EdictOracle_Builtin (int num, int argc, float *globals, int numglobals, char *strings,
	int numstrings, int stringtemp, void *edicts, int entityfields, int active, unsigned int seed, int *num_edicts,
	int max_edicts, int maxclients, double time)
{
	extern char	*pr_string_temp;

	EdictOracle_VmRun (NULL, 0, NULL, 0, globals, numglobals, strings, numstrings, edicts, entityfields, active, -1);
	pr_string_temp = strings + stringtemp;
	pr_argc = argc;
	sv.num_edicts = *num_edicts;
	sv.max_edicts = max_edicts;
	svs.maxclients = maxclients;
	sv.time = time;
	srand (seed);
	pr_builtins[num] ();
	*num_edicts = sv.num_edicts;
	return edict_rand_state ();
}

// BOX_ON_PLANE_SIDE (mathlib.h), the way SV_FindTouchedLeafs calls it: the macro's axial fast path, else
// BoxOnPlaneSide. signbits as Mod_LoadPlanes computes them.
EDICT_EXPORT int EdictOracle_BoxOnPlaneSide (float *emins, float *emaxs, float *normal, float dist, int type)
{
	mplane_t	p;
	int			j;

	memset (&p, 0, sizeof (p));
	for (j = 0 ; j < 3 ; j++)
	{
		p.normal[j] = normal[j];
		if (normal[j] < 0)
			p.signbits |= 1 << j;
	}
	p.dist = dist;
	p.type = type;
	return BOX_ON_PLANE_SIDE (emins, emaxs, &p);
}

/*
==============================================================================
A world in the oracle (level 1 for world.c, sv_move.c, the PVS and the builtins that use them)

The caller passes a map's BSP file; Mod_LoadBrushModel loads it as SV_SpawnServer's Mod_ForName would, onto the
oracle's own hunk, and SV_ClearWorld sets up the area nodes. sv.models[] gets the world and its inline models, as
SV_SpawnServer sets them. Edicts live in caller memory as for EdictOracle_Builtin (EdictOracle_World sets that
memory up for the calls that follow); entities are linked with SV_LinkEdict itself, without touch functions.
==============================================================================
*/

void Mod_LoadBrushModel (model_t *mod, void *buffer);	// model.c, not declared in a header
void SV_NewChaseDir (edict_t *actor, edict_t *enemy, float dist);	// sv_move.c, likewise
model_t *Mod_FindName (char *name);	// model.c, likewise
short ShortSwap (short l);	// common.c, likewise
short ShortNoSwap (short l);
int LongSwap (int l);
int LongNoSwap (int l);
float FloatSwap (float f);
float FloatNoSwap (float f);
extern char		loadname[32];		// model.c
extern model_t	*loadmodel;
extern byte		checkpvs[MAX_MAP_LEAFS/8];	// pr_cmds.c

#define ORACLE_HUNK	(64 * 1024 * 1024)

static model_t	oracle_world;
static int		oracle_mark = -1;
static char		*oracle_hunk_strings;
static int		oracle_progs_mark = -1;	// above a progs image (EdictOracle_Progs): a world loaded after it keeps it	// a pr_strings on the hunk: string_t offsets to hunk strings must fit an int

// The oracle's own hunk, set up once (with COM_Init's byte order setup, which the model loader reads through);
// oracle_mark is the low mark above Memory_Init's zone. -1 if there is no memory.
static int OracleHunk (void)
{
	void	*hunk;

	if (oracle_mark >= 0)
		return 0;
	hunk = malloc (ORACLE_HUNK);
	if (!hunk)
		return -1;
	BigShort = ShortSwap;
	LittleShort = ShortNoSwap;
	BigLong = LongSwap;
	LittleLong = LongNoSwap;
	BigFloat = FloatSwap;
	LittleFloat = FloatNoSwap;
	Memory_Init (hunk, ORACLE_HUNK);
	Mod_Init ();
	oracle_hunk_strings = Hunk_AllocName (16, "ostrings");
	oracle_mark = Hunk_LowMark ();
	return 0;
}

// Returns the world's numleafs (visleafs), or -1 if there is no hunk.
EDICT_EXPORT int EdictOracle_LoadWorld (void *bsp, const char *name)
{
	int		i;
	char	sub[8];

	if (OracleHunk () < 0)
		return -1;
	Hunk_FreeToLowMark (oracle_progs_mark >= 0 ? oracle_progs_mark : oracle_mark);	// a progs loaded first stays
	memset (&oracle_world, 0, sizeof (oracle_world));
	strcpy (oracle_world.name, name);
	COM_FileBase (oracle_world.name, loadname);
	loadmodel = &oracle_world;
	Mod_LoadBrushModel (&oracle_world, bsp);

	memset (sv.models, 0, sizeof (sv.models));
	sv.worldmodel = &oracle_world;
	sv.models[1] = sv.worldmodel;
	for (i = 1 ; i < sv.worldmodel->numsubmodels ; i++)
	{
		sprintf (sub, "*%i", i);
		sv.models[i + 1] = Mod_FindName (sub);
	}
	SV_ClearWorld ();
	return sv.worldmodel->numleafs;
}

// The edict memory and the server state that SV_Move, SV_LinkEdict and the builtins read.
EDICT_EXPORT void EdictOracle_World (void *edicts, int entityfields, int num_edicts, int max_edicts, int maxclients)
{
	pr_edict_size = entityfields * 4 + sizeof (edict_t) - sizeof (entvars_t);
	sv.edicts = edicts;
	sv.num_edicts = num_edicts;
	sv.max_edicts = max_edicts;
	svs.maxclients = maxclients;
}

// SV_LinkEdict (n, false); the edict's leafs are returned (num_leafs, leafnums up to 16).
EDICT_EXPORT int EdictOracle_LinkEdict (int n, short *leafnums)
{
	edict_t	*e = EDICT_NUM (n);

	SV_LinkEdict (e, false);
	memcpy (leafnums, e->leafnums, sizeof (e->leafnums));
	return e->num_leafs;
}

EDICT_EXPORT void EdictOracle_UnlinkEdict (int n) { SV_UnlinkEdict (EDICT_NUM (n)); }

// SV_Move; out: allsolid, startsolid, inopen, inwater, fraction, endpos[3], normal[3], dist (as words), and the
// trace entity's number (-1: NULL) as the return value.
EDICT_EXPORT int EdictOracle_Move (float *start, float *mins, float *maxs, float *end, int type, int passedict,
	float *out)
{
	trace_t	tr = SV_Move (start, mins, maxs, end, type, passedict < 0 ? NULL : EDICT_NUM (passedict));
	float	*endpos = out + 5, *normal = out + 8;

	((int *)out)[0] = tr.allsolid;
	((int *)out)[1] = tr.startsolid;
	((int *)out)[2] = tr.inopen;
	((int *)out)[3] = tr.inwater;
	out[4] = tr.fraction;
	VectorCopy (tr.endpos, endpos);
	VectorCopy (tr.plane.normal, normal);
	out[11] = tr.plane.dist;
	return tr.ent ? NUM_FOR_EDICT (tr.ent) : -1;
}

// Mod_PointInLeaf on the world: the leaf's index.
EDICT_EXPORT int EdictOracle_PointInLeaf (float *p) { return Mod_PointInLeaf (p, sv.worldmodel) - sv.worldmodel->leafs; }

// Mod_LeafPVS of leaf n: (numleafs + 7) >> 3 bytes into out; returns that count.
EDICT_EXPORT int EdictOracle_LeafPvs (int n, byte *out)
{
	int		row = (sv.worldmodel->numleafs + 7) >> 3;

	memcpy (out, Mod_LeafPVS (sv.worldmodel->leafs + n, sv.worldmodel), row);
	return row;
}

// PF_checkclient's state: sv.lastcheck, sv.lastchecktime and checkpvs; set, then read back.
EDICT_EXPORT void EdictOracle_CheckState (int set, int *lastcheck, double *lastchecktime, byte *pvs)
{
	if (set)
	{
		sv.lastcheck = *lastcheck;
		sv.lastchecktime = *lastchecktime;
		memcpy (checkpvs, pvs, sizeof (checkpvs));
	}
	*lastcheck = sv.lastcheck;
	*lastchecktime = sv.lastchecktime;
	memcpy (pvs, checkpvs, sizeof (checkpvs));
}

// SV_NewChaseDir (actor, enemy, dist) with rand seeded first; globals as PF_changeyaw reads them (self = actor).
// Returns the seed afterwards. Without triggers in the world, SV_movestep's relinking runs no QuakeC.
EDICT_EXPORT unsigned int EdictOracle_NewChaseDir (float *globals, int actor, int enemy, float dist, unsigned int seed)
{
	pr_globals = globals;
	pr_global_struct = (globalvars_t *)globals;
	srand (seed);
	SV_NewChaseDir (EDICT_NUM (actor), EDICT_NUM (enemy), dist);
	return edict_rand_state ();
}

// The cvar values PF_aim reads (var->value; nothing registers the cvars in the oracle).
EDICT_EXPORT void EdictOracle_AimCvars (float aim, float tp)
{
	extern cvar_t	sv_aim;

	sv_aim.value = aim;
	teamplay.value = tp;
}

/*
==============================================================================
The entity parser (level 1 for COM_Parse and pr_edict.c)
==============================================================================
*/

char *ED_NewString (char *string);						// pr_edict.c, not declared in a header
qboolean ED_ParseEpair (void *base, ddef_t *key, char *s);	// likewise

// COM_Parse (data): the token into token (size bytes), and how far it read: the offset of the returned position,
// or -1 for NULL (the end of the text).
EDICT_EXPORT int EdictOracle_ComParse (char *data, char *token, int size)
{
	char	*next = COM_Parse (data);
	int		n = strlen (com_token);

	if (n >= size)
		n = size - 1;
	memcpy (token, com_token, n);
	token[n] = 0;
	return next ? (int)(next - data) : -1;
}

// ED_ParseEpair on a value of the given type (ev_float, ev_vector or ev_string) into three words at out; for
// ev_string the new string's text goes to text (size bytes). The hunk strings are freed again afterwards. Returns
// ED_ParseEpair's result, or -1 if there is no hunk.
EDICT_EXPORT int EdictOracle_ParseEpair (int type, char *s, float *out, char *text, int size)
{
	ddef_t	key;
	int		mark, r, n;
	char	*str;

	if (OracleHunk () < 0)
		return -1;
	pr_strings = oracle_hunk_strings;	// as patch 0002 has it in the engine: near the hunk strings
	mark = Hunk_LowMark ();
	memset (&key, 0, sizeof (key));
	key.type = type;
	key.ofs = 0;
	r = ED_ParseEpair (out, &key, s);
	if (type == ev_string)
	{
		str = pr_strings + *(int *)out;
		n = strlen (str);
		if (n >= size)
			n = size - 1;
		memcpy (text, str, n);
		text[n] = 0;
	}
	Hunk_FreeToLowMark (mark);
	return r;
}


// A progs.dat image copied onto the oracle's hunk and set up as PR_LoadProgs points into it (little-endian, so no
// swapping), for ED_ParseEdict's field and function lookups. Frees what the hunk held above the world's mark.
EDICT_EXPORT int EdictOracle_Progs (void *image, int length)
{
	if (OracleHunk () < 0)
		return -1;
	Hunk_FreeToLowMark (oracle_mark);
	progs = Hunk_AllocName (length, "oprogs");
	memcpy (progs, image, length);
	pr_functions = (dfunction_t *)((byte *)progs + progs->ofs_functions);
	pr_strings = (char *)progs + progs->ofs_strings;
	pr_globaldefs = (ddef_t *)((byte *)progs + progs->ofs_globaldefs);
	pr_fielddefs = (ddef_t *)((byte *)progs + progs->ofs_fielddefs);
	pr_statements = (dstatement_t *)((byte *)progs + progs->ofs_statements);
	pr_global_struct = (globalvars_t *)((byte *)progs + progs->ofs_globals);
	pr_globals = (float *)pr_global_struct;
	pr_edict_size = progs->entityfields * 4 + sizeof (edict_t) - sizeof (entvars_t);
	oracle_progs_mark = Hunk_LowMark ();
	return 0;
}

// ED_ParseEdict (data, edict n) on caller edicts (max_edicts of them; edict 0 is sv.edicts); returns how far it read.
// The strings it allocates stay until EdictOracle_ProgsReset. The text must be a valid entity (ED_ParseEdict's errors
// end the process).
EDICT_EXPORT int EdictOracle_ParseEdict (char *data, void *edicts, int max_edicts, int n)
{
	sv.edicts = edicts;
	sv.max_edicts = max_edicts;
	return (int)(ED_ParseEdict (data, EDICT_NUM (n)) - data);
}

// The text of string offset ofs (relative to pr_strings) into out (size bytes); its length.
EDICT_EXPORT int EdictOracle_String (int ofs, char *out, int size)
{
	const char	*s = pr_strings + ofs;
	int			n = strlen (s);

	if (n >= size)
		n = size - 1;
	memcpy (out, s, n);
	out[n] = 0;
	return n;
}

// Frees the strings ED_ParseEdict allocated since EdictOracle_Progs.
EDICT_EXPORT void EdictOracle_ProgsReset (void) { Hunk_FreeToLowMark (oracle_progs_mark); }

/*
==============================================================================
Physics on a world (level 1 for sv_phys.c)

SV_Physics itself on caller edicts and globals, with the world of EdictOracle_LoadWorld and the progs image of
EdictOracle_Progs (loaded first) for the QuakeC it calls: the caller points every function it calls (StartFrame,
PlayerPreThink, PlayerPostThink, think, touch, blocked) at a function that does nothing.
==============================================================================
*/

extern cvar_t	sv_gravity, sv_maxvelocity, sv_nostep;	// sv_phys.c
extern edict_t	*sv_player;							// sv_user.c

static client_t	oracle_clients[MAX_SCOREBOARD];
static byte		oracle_datagram[MAX_DATAGRAM];

// maxclients client slots, active as given; the cvar values SV_Physics reads (nothing registers them here).
EDICT_EXPORT void EdictOracle_PhysicsSetup (int maxclients, int *active, float gravity, float maxvelocity, float nostep)
{
	int		i;

	memset (oracle_clients, 0, sizeof (oracle_clients));
	for (i = 0 ; i < maxclients ; i++)
		oracle_clients[i].active = active[i];
	svs.clients = oracle_clients;
	svs.maxclients = maxclients;
	sv_gravity.value = gravity;
	sv_maxvelocity.value = maxvelocity;
	sv_nostep.value = nostep;
	memset (sv.sound_precache, 0, sizeof (sv.sound_precache));	// SV_StartSound finds nothing precached
	sv.datagram.data = oracle_datagram;
	sv.datagram.maxsize = sizeof (oracle_datagram);
	sv.datagram.cursize = 0;
}

// One SV_Physics on the given globals and edicts at sv.time with host_frametime; sv.time and num_edicts come back.
EDICT_EXPORT void EdictOracle_Physics (float *globals, void *edicts, int *num_edicts, int max_edicts, double *time,
	double frametime)
{
	extern int	localstack_used, pr_depth;	// pr_exec.c

	pr_globals = globals;
	pr_global_struct = (globalvars_t *)globals;
	sv.edicts = edicts;
	sv.num_edicts = *num_edicts;
	sv.max_edicts = max_edicts;
	sv.time = *time;
	host_frametime = frametime;
	sv.active = true;
	sv.state = ss_active;
	sv_player = EDICT_NUM (1);
	sv.datagram.cursize = 0;
	pr_depth = 0;
	localstack_used = 0;
	SV_Physics ();
	*num_edicts = sv.num_edicts;
	*time = sv.time;
}

/*
==============================================================================
A client's datagram on a world (level 1 for SV_WriteClientdataToMessage and SV_WriteEntitiesToClient)

On the world of EdictOracle_LoadWorld, the progs image of EdictOracle_Progs and caller edicts linked with
EdictOracle_LinkEdict. The buffer allows overflow (SV_SendClientDatagram's own leaves allowoverflow uninitialized),
so that the caller can put the 16-byte check on its edge and see what an overflow does.
==============================================================================
*/

void SV_WriteEntitiesToClient (edict_t *clent, sizebuf_t *msg);	// sv_main.c, not declared in a header

// The baseline of edict n (SV_CreateBaseline's entity_state_t).
EDICT_EXPORT void EdictOracle_SetBaseline (void *edicts, int entityfields, int n, entity_state_t *baseline)
{
	pr_edict_size = entityfields * 4 + sizeof (edict_t) - sizeof (entvars_t);
	sv.edicts = edicts;
	EDICT_NUM (n)->baseline = *baseline;
}

// SV_WriteClientdataToMessage (if clientdata) and SV_WriteEntitiesToClient for edict clent, as sv_player, into out
// from cursize start of maxsize; returns the new cursize, or -1 - cursize if the buffer overflowed.
EDICT_EXPORT int EdictOracle_ClientDatagram (float *globals, void *edicts, int num_edicts, int clent, int clientdata,
	byte *out, int start, int maxsize)
{
	sizebuf_t	msg;

	pr_globals = globals;
	pr_global_struct = (globalvars_t *)globals;
	sv.edicts = edicts;
	sv.num_edicts = num_edicts;
	sv_player = EDICT_NUM (clent);
	memset (&msg, 0, sizeof (msg));
	msg.data = out;
	msg.maxsize = maxsize;
	msg.cursize = start;
	msg.allowoverflow = true;
	if (clientdata)
		SV_WriteClientdataToMessage (sv_player, &msg);
	SV_WriteEntitiesToClient (sv_player, &msg);
	return msg.overflowed ? -1 - msg.cursize : msg.cursize;
}

qboolean SV_SendClientDatagram (client_t *client);	// sv_main.c, likewise

static byte		oracle_sent[MAX_DATAGRAM];
static int		oracle_sent_len;

static int OracleSendUnreliable (qsocket_t *sock, sizebuf_t *data)
{
	oracle_sent_len = data->cursize <= (int)sizeof (oracle_sent) ? data->cursize : -1;
	if (oracle_sent_len > 0)
		memcpy (oracle_sent, data->data, oracle_sent_len);
	return 1;
}

// SV_SendClientDatagram itself for client 0 (edict clent, as sv_player) at sv.time, with sv.datagram holding
// datagram[length]; the message goes to a socket of driver 0, whose unreliable send the oracle takes over for the
// call. Returns the message's length (out gets it), -1 if nothing was sent. The function's own buffer leaves
// allowoverflow uninitialized: the caller keeps the message clear of an overflow.
EDICT_EXPORT int EdictOracle_SendClientDatagram (float *globals, void *edicts, int num_edicts, int clent, double time,
	byte *datagram, int length, byte *out)
{
	static qsocket_t	sock;
	int					(*send) (qsocket_t *sock, sizebuf_t *data);

	pr_globals = globals;
	pr_global_struct = (globalvars_t *)globals;
	sv.edicts = edicts;
	sv.num_edicts = num_edicts;
	sv.time = time;
	sv_player = EDICT_NUM (clent);
	memset (&sock, 0, sizeof (sock));
	oracle_clients[0].edict = sv_player;
	oracle_clients[0].netconnection = &sock;
	memcpy (oracle_datagram, datagram, length);
	sv.datagram.data = oracle_datagram;
	sv.datagram.maxsize = sizeof (oracle_datagram);
	sv.datagram.cursize = length;
	sv.datagram.overflowed = false;
	send = net_drivers[0].SendUnreliableMessage;
	net_drivers[0].SendUnreliableMessage = OracleSendUnreliable;
	oracle_sent_len = -1;
	SV_SendClientDatagram (&oracle_clients[0]);
	net_drivers[0].SendUnreliableMessage = send;
	oracle_clients[0].netconnection = NULL;
	sv.datagram.cursize = 0;
	if (oracle_sent_len > 0)
		memcpy (out, oracle_sent, oracle_sent_len);
	return oracle_sent_len;
}

/*
==============================================================================
Message builtins (level 1 for the Write* family, sound, particle, the prints, stuffcmd, lightstyle, ambientsound,
makestatic)

EdictOracle_MessageSetup gives the server its message buffers (sv.datagram, sv.reliable_datagram and sv.signon as
SV_SpawnServer sizes them, without allowoverflow: the caller keeps what one call writes small), maxclients clients
with their own messages (allowoverflow, as SV_ConnectClient sets it) and the precache lists the builtins look names
up in. EdictOracle_Builtin then runs a builtin; EdictOracle_TakeMessage reads a buffer and empties it.
==============================================================================
*/

static byte		oracle_reliable[MAX_DATAGRAM];
static byte		oracle_signon[NET_MAXMESSAGE];
static char		oracle_empty[1];

EDICT_EXPORT void EdictOracle_MessageSetup (int maxclients, int *active, int *spawned, char **models, int nmodels,
	char **sounds, int nsounds)
{
	int		i;

	memset (oracle_clients, 0, sizeof (oracle_clients));
	for (i = 0 ; i < maxclients ; i++)
	{
		oracle_clients[i].active = active[i];
		oracle_clients[i].spawned = spawned[i];
		oracle_clients[i].message.data = oracle_clients[i].msgbuf;
		oracle_clients[i].message.maxsize = sizeof (oracle_clients[i].msgbuf);
		oracle_clients[i].message.allowoverflow = true;
	}
	svs.clients = oracle_clients;
	svs.maxclients = maxclients;
	memset (&sv.datagram, 0, sizeof (sv.datagram));
	sv.datagram.data = oracle_datagram;
	sv.datagram.maxsize = sizeof (oracle_datagram);
	memset (&sv.reliable_datagram, 0, sizeof (sv.reliable_datagram));
	sv.reliable_datagram.data = oracle_reliable;
	sv.reliable_datagram.maxsize = sizeof (oracle_reliable);
	memset (&sv.signon, 0, sizeof (sv.signon));
	sv.signon.data = oracle_signon;
	sv.signon.maxsize = sizeof (oracle_signon);
	memset (sv.model_precache, 0, sizeof (sv.model_precache));
	memset (sv.sound_precache, 0, sizeof (sv.sound_precache));
	sv.model_precache[0] = sv.sound_precache[0] = oracle_empty;
	for (i = 0 ; i < nmodels && i < MAX_MODELS - 1 ; i++)
		sv.model_precache[i + 1] = models[i];
	for (i = 0 ; i < nsounds && i < MAX_SOUNDS - 1 ; i++)
		sv.sound_precache[i + 1] = sounds[i];
}

// which: 0 sv.datagram, 1 sv.reliable_datagram, 2 sv.signon, 3 + i client i's message. Copies at most size bytes;
// returns cursize, or -1 - cursize if the buffer overflowed. The buffer is emptied.
EDICT_EXPORT int EdictOracle_TakeMessage (int which, byte *out, int size)
{
	sizebuf_t	*b;
	int			n;

	if (which == 0)
		b = &sv.datagram;
	else if (which == 1)
		b = &sv.reliable_datagram;
	else if (which == 2)
		b = &sv.signon;
	else
		b = &oracle_clients[which - 3].message;
	n = b->cursize;
	memcpy (out, b->data, n < size ? n : size);
	n = b->overflowed ? -1 - n : n;
	b->cursize = 0;
	b->overflowed = false;
	return n;
}
