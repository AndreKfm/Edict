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

#define ORACLE_VERSION	1

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
