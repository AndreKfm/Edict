/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Accuracy of the pinned libm (edict_libm.c) against the C library's libm, which is not the reference but a
yardstick: a wrong constant or a transcription error in edict_libm.c shows up as a large error here.
Build: clang -O2 -ffp-contract=off -DEDICT_LIBC_NO_REDIRECT -I../src libm_accuracy.c ../src/edict_libm.c -lm
*/
#include <stdio.h>
#include <string.h>
#include "edict_libc.h"

typedef unsigned long long u64;

static u64 state = 0x9E3779B97F4A7C15ULL;
static u64 next (void)	// splitmix64
{
	u64 z = (state += 0x9E3779B97F4A7C15ULL);
	z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
	z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
	return z ^ (z >> 31);
}
static double uniform (double lo, double hi) { return lo + (hi - lo) * ((next () >> 11) * (1.0 / 9007199254740992.0)); }
static double anydouble (void) { u64 u = next (); double d; memcpy (&d, &u, 8); return d; }

static long long ordered (double d)	// monotonic integer image of a double, for ULP distances
{
	long long i;
	memcpy (&i, &d, 8);
	return i < 0 ? (long long)0x8000000000000000ULL - i : i;
}

typedef struct { const char *name; long n, same; long long maxulp; double worst; int nanmismatch; } stat_t;

static void check (stat_t *s, double arg, double mine, double ref)
{
	long long d;
	s->n++;
	if ((mine != mine) || (ref != ref))
	{
		if ((mine != mine) != (ref != ref))
			s->nanmismatch++;
		else
			s->same++;
		return;
	}
	if (!memcmp (&mine, &ref, 8)) { s->same++; return; }
	d = ordered (mine) - ordered (ref);
	if (d < 0) d = -d;
	if (d > s->maxulp) { s->maxulp = d; s->worst = arg; }
}

static void report (stat_t *s)
{
	printf ("%-34s n=%9ld  bit-identical %8.4f%%  max error %lld ulp (at %.17g)  nan mismatches %d\n",
		s->name, s->n, 100.0 * s->same / s->n, s->maxulp, s->worst, s->nanmismatch);
}

int main (void)
{
	stat_t a = {"sin, [-4pi, 4pi]"}, b = {"cos, [-4pi, 4pi]"}, c = {"sin/cos, Quake angles (float deg)"};
	stat_t d = {"sin/cos, [-1e5, 1e5]"}, e = {"atan, any double"}, f = {"atan2, any doubles"};
	stat_t g = {"atan2, Quake vectors (float)"}, h = {"sin/cos/atan2, special values"};
	stat_t t1 = {"tan, (-pi/2, pi/2)"}, t2 = {"tan, [-1e5, 1e5]"}, t3 = {"tan, fov/360*pi (float fov)"};
	long i;
	int j, k;
	static const double special[] = { 0.0, -0.0, 1e-310, -1e-310, 1e-30, 1.0, -1.0, 1.5707963267948966,
		3.141592653589793, 1e300, -1e300, 1.0 / 0.0, -1.0 / 0.0, 0.0 / 0.0 };
	int ns = sizeof (special) / sizeof (special[0]);

	for (i = 0 ; i < 10000000 ; i++)
	{
		double x = uniform (-12.566370614359172, 12.566370614359172);
		check (&a, x, edict_sin (x), sin (x));
		check (&b, x, edict_cos (x), cos (x));
	}
	for (i = 0 ; i < 2000000 ; i++)
	{
		// AngleVectors: angle (float) = angles[YAW] * (M_PI*2 / 360), then sin ((double)angle)
		float deg = (float)uniform (-1080.0, 1080.0);
		float ang = (float)(deg * (3.14159265358979323846 * 2 / 360));
		check (&c, ang, edict_sin (ang), sin (ang));
		check (&c, ang, edict_cos (ang), cos (ang));
		double x = uniform (-1e5, 1e5);
		check (&d, x, edict_sin (x), sin (x));
		check (&d, x, edict_cos (x), cos (x));
	}
	for (i = 0 ; i < 5000000 ; i++)
	{
		double x = anydouble (), y = anydouble ();
		check (&e, x, edict_atan (x), atan (x));
		check (&f, y, edict_atan2 (y, x), atan2 (y, x));
		float vx = (float)uniform (-4096, 4096), vy = (float)uniform (-4096, 4096);
		check (&g, vy, edict_atan2 (vy, vx), atan2 (vy, vx));
	}
	for (i = 0 ; i < 5000000 ; i++)
	{
		double x = uniform (-1.5707963267948966, 1.5707963267948966);
		check (&t1, x, edict_tan (x), tan (x));
		x = uniform (-1e5, 1e5);
		check (&t2, x, edict_tan (x), tan (x));
		// CalcFov and R_ViewChanged: tan (fov_x/360*M_PI) of a float fov in 10..170
		float fov = (float)uniform (10, 170);
		x = fov / 360 * 3.14159265358979323846;
		check (&t3, x, edict_tan (x), tan (x));
	}
	for (j = 0 ; j < ns ; j++)
	{
		check (&h, special[j], edict_tan (special[j]), tan (special[j]));
		check (&h, special[j], edict_sin (special[j]), sin (special[j]));
		check (&h, special[j], edict_cos (special[j]), cos (special[j]));
		for (k = 0 ; k < ns ; k++)
			check (&h, special[j], edict_atan2 (special[j], special[k]), atan2 (special[j], special[k]));
	}
	report (&a); report (&b); report (&c); report (&d); report (&e); report (&f); report (&g); report (&h);
	report (&t1); report (&t2); report (&t3);
	return 0;
}
