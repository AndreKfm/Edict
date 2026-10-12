/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: C library functions whose results reach the simulation state and whose exact values
the C standard leaves to the implementation. Force-included into every file (build.sh: -include), so id's
sources stay unchanged. The C# port implements the same definitions. docs/PLAN.md D3, section A.1.

The system headers are included first and the names are redirected afterwards, so the C library's own
declarations stay intact and only id's code calls the pinned versions. A side effect is intended: the compiler
can no longer fold calls such as sin (constant) at compile time with its own math library.
*/
#ifndef EDICT_LIBC_H
#define EDICT_LIBC_H

#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// OP_NE_S stores strcmp's return value as a QuakeC float (pr_exec.c: c->_float = strcmp (...)). The standard
// fixes only the sign: glibc returns the difference of the first differing bytes, MSVC and the sanitizer
// interceptors return -1/0/1. Pinned to the difference of the first differing bytes as unsigned char.
int edict_strcmp (const char *a, const char *b);

// rand: the MSVC linear congruential generator (seed 1 until srand, which WinQuake never calls):
// seed = seed * 214013 + 2531011, result (seed >> 16) & 0x7fff. glibc's rand is a different generator.
int edict_rand (void);
void edict_srand (unsigned int seed);

// sin, cos, atan, atan2: fdlibm algorithms (edict_libm.c). Results of libm differ between C libraries in the
// last bit; sqrt, floor, ceil and fabs are exact in IEEE arithmetic and stay with the C library.
double edict_sin (double x);
double edict_cos (double x);
double edict_atan (double x);
double edict_atan2 (double y, double x);

// sprintf: PF_ftos/PF_vtos format floats into QuakeC strings (state). The C libraries agree on finite values
// (checked: correctly rounded, ties to even, on glibc and UCRT) but print NaN differently ("-nan" vs "-nan(ind)").
// edict_sprintf passes every conversion to the C library one at a time and prints NaN and infinities itself, in
// glibc style: optional sign, "nan"/"inf" (upper case for F/E/G/A), padded with spaces to the field width.
// Supported: flags, width and precision as digits, conversions d i u x X o c s p f F e E g G a A %; anything
// else is a Sys_Error. vsprintf (console output) is not redirected: it does not reach the state.
int edict_sprintf (char *out, const char *fmt, ...);

// atof: ED_ParseEpair stores atof (s) in float fields. glibc and UCRT parse finite values alike but return
// different NaN bit patterns for "nan". Pinned: strtod, then every NaN becomes the quiet NaN 0x7ff8000000000000
// with the sign of the parsed value.
double edict_atof (const char *s);

// EDICT_LIBC_NO_REDIRECT: declarations only, for tests that compare against the C library.
#ifndef EDICT_LIBC_NO_REDIRECT
#define strcmp	edict_strcmp
#define rand	edict_rand
#define srand	edict_srand
#define sin		edict_sin
#define cos		edict_cos
#define atan	edict_atan
#define atan2	edict_atan2
#define sprintf	edict_sprintf
#define atof	edict_atof
#endif

#endif
