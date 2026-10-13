/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict oracle library: level-1 entry points for the differential tests (docs/TESTING.md). Linked from the same
object files as the reference executable (R3). Every entry point calls the reference code itself (id's
functions or the pinned C library functions through the same redirects id's code uses), never a copy of it.
All arguments are blittable; strings are NUL-terminated byte buffers owned by the caller.
*/
#include "quakedef.h"

#ifdef _WIN32
#define EDICT_EXPORT	__declspec(dllexport)
#else
#define EDICT_EXPORT	__attribute__((visibility("default")))
#endif

#define ORACLE_VERSION	3

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

// Returns the world's numleafs (visleafs), or -1 if there is no hunk.
EDICT_EXPORT int EdictOracle_LoadWorld (void *bsp, const char *name)
{
	int		i;
	char	sub[8];

	if (oracle_mark < 0)
	{
		void	*hunk = malloc (ORACLE_HUNK);

		if (!hunk)
			return -1;
		// COM_Init's byte order setup (little-endian), which the model loader reads through
		BigShort = ShortSwap;
		LittleShort = ShortNoSwap;
		BigLong = LongSwap;
		LittleLong = LongNoSwap;
		BigFloat = FloatSwap;
		LittleFloat = FloatNoSwap;
		Memory_Init (hunk, ORACLE_HUNK);
		Mod_Init ();
		oracle_mark = Hunk_LowMark ();
	}
	Hunk_FreeToLowMark (oracle_mark);
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
