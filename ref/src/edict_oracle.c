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

#define ORACLE_VERSION	2

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
	PR_ExecuteProgram (fnum);
}
