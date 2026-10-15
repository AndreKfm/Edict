/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: pinned sin, cos, tan, atan, atan2 (docs/PLAN.md D3). The C# port implements the same
operations in the same order, so both sides produce identical bits on every platform, independent of the C
library the reference is built with.

Algorithms and constants follow fdlibm as carried by musl (sin.c, cos.c, tan.c, __sin.c, __cos.c, __tan.c,
__rem_pio2.c, atan.c, atan2.c), written from the published algorithms. fdlibm notice:

    Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
    Developed at SunPro, a Sun Microsystems, Inc. business.
    Permission to use, copy, modify, and distribute this software is freely granted, provided that this
    notice is preserved.

Deviation from musl: arguments with |x| >= 2^20 * pi/2 are first reduced with fmod (x, 2 pi) instead of the
multi-precision __rem_pio2_large. That is deterministic but loses accuracy for such arguments; WinQuake never
passes them for real game angles. Requires IEEE double arithmetic with round-to-nearest and no contraction
(build flags: -ffp-contract=off, SSE2).
*/
#include "edict_libc.h"

typedef unsigned long long	u64;

static u64 bits (double x)		{ u64 u; memcpy (&u, &x, 8); return u; }
static unsigned int hi (double x)	{ return (unsigned int)(bits (x) >> 32); }
static unsigned int lo (double x)	{ return (unsigned int)bits (x); }

/*
==============================================================================
Kernels on [-pi/4, pi/4]
==============================================================================
*/

static const double
S1 = -1.66666666666666324348e-01,	// 0xBFC55555 55555549
S2 =  8.33333333332248946124e-03,	// 0x3F811111 1110F8A6
S3 = -1.98412698298579493134e-04,	// 0xBF2A01A0 19C161D5
S4 =  2.75573137070700676789e-06,	// 0x3EC71DE3 57B1FE7D
S5 = -2.50507602534068634195e-08,	// 0xBE5AE5E6 8A2B9CEB
S6 =  1.58969099521155010221e-10;	// 0x3DE5D93A 5ACFD57C

static const double
C1 =  4.16666666666666019037e-02,	// 0x3FA55555 5555554C
C2 = -1.38888888888741095749e-03,	// 0xBF56C16C 16C15177
C3 =  2.48015872894767294178e-05,	// 0x3EFA01A0 19CB1590
C4 = -2.75573143513906633035e-07,	// 0xBE927E4F 809C52AD
C5 =  2.08757232129817482790e-09,	// 0x3E21EE9E BDB4B1C4
C6 = -1.13596475577881948265e-11;	// 0xBDA8FAE9 BE8838D4

static double k_sin (double x, double y, int iy)
{
	double	z, r, v, w;

	z = x * x;
	w = z * z;
	r = S2 + z * (S3 + z * S4) + z * w * (S5 + z * S6);
	v = z * x;
	if (iy == 0)
		return x + v * (S1 + z * r);
	return x - ((z * (0.5 * y - v * r) - y) - v * S1);
}

static double k_cos (double x, double y)
{
	double	hz, z, r, w;

	z = x * x;
	w = z * z;
	r = z * (C1 + z * (C2 + z * C3)) + w * w * (C4 + z * (C5 + z * C6));
	hz = 0.5 * z;
	w = 1.0 - hz;
	return w + (((1.0 - w) - hz) + (z * r - x * y));
}

/*
==============================================================================
Argument reduction: x = n * pi/2 + (y[0] + y[1])
==============================================================================
*/

static const double
toint   = 6755399441055744.0,			// 1.5 * 2^52
invpio2 = 6.36619772367581382433e-01,	// 0x3FE45F30 6DC9C883
pio4    = 7.85398163397448278999e-01,	// 0x3FE921FB 54442D18
pio2_1  = 1.57079632673412561417e+00,	// 0x3FF921FB 54400000
pio2_1t = 6.07710050650619224932e-11,	// 0x3DD0B461 1A626331
pio2_2  = 6.07710050630396597660e-11,	// 0x3DD0B461 1A600000
pio2_2t = 2.02226624879595063154e-21,	// 0x3BA3198A 2E037073
pio2_3  = 2.02226624871116645580e-21,	// 0x3BA3198A 2E000000
pio2_3t = 8.47842766036889956997e-32,	// 0x397B839A 252049C1
twopi   = 6.28318530717958623200e+00;	// 0x401921FB 54442D18

static int rem_pio2 (double x, double *y)
{
	double			fn, r, t, w;
	int				n, ex, ey;
	unsigned int	ix;

	ix = hi (x) & 0x7fffffff;
	if (ix >= 0x413921fb)	// |x| >= 2^20 * pi/2: see the header comment
	{
		x = fmod (x, twopi);
		ix = hi (x) & 0x7fffffff;
	}
	fn = x * invpio2 + toint - toint;
	n = (int)fn;
	r = x - fn * pio2_1;
	w = fn * pio2_1t;
	if (r - w < -pio4)
	{
		n--; fn--;
		r = x - fn * pio2_1;
		w = fn * pio2_1t;
	}
	else if (r - w > pio4)
	{
		n++; fn++;
		r = x - fn * pio2_1;
		w = fn * pio2_1t;
	}
	y[0] = r - w;
	ey = (hi (y[0]) >> 20) & 0x7ff;
	ex = ix >> 20;
	if (ex - ey > 16)
	{
		t = r;
		w = fn * pio2_2;
		r = t - w;
		w = fn * pio2_2t - ((t - r) - w);
		y[0] = r - w;
		ey = (hi (y[0]) >> 20) & 0x7ff;
		if (ex - ey > 49)
		{
			t = r;
			w = fn * pio2_3;
			r = t - w;
			w = fn * pio2_3t - ((t - r) - w);
			y[0] = r - w;
		}
	}
	y[1] = (r - y[0]) - w;
	return n;
}

/*
==============================================================================
sin, cos
==============================================================================
*/

double edict_sin (double x)
{
	double			y[2];
	unsigned int	ix = hi (x) & 0x7fffffff;

	if (ix <= 0x3fe921fb)			// |x| <= pi/4
	{
		if (ix < 0x3e500000)		// |x| < 2^-26
			return x;
		return k_sin (x, 0.0, 0);
	}
	if (ix >= 0x7ff00000)			// inf or nan
		return x - x;
	switch (rem_pio2 (x, y) & 3)
	{
	case 0:		return  k_sin (y[0], y[1], 1);
	case 1:		return  k_cos (y[0], y[1]);
	case 2:		return -k_sin (y[0], y[1], 1);
	default:	return -k_cos (y[0], y[1]);
	}
}

double edict_cos (double x)
{
	double			y[2];
	unsigned int	ix = hi (x) & 0x7fffffff;

	if (ix <= 0x3fe921fb)
	{
		if (ix < 0x3e46a09e)		// |x| < 2^-27 * sqrt(2)
			return 1.0;
		return k_cos (x, 0.0);
	}
	if (ix >= 0x7ff00000)
		return x - x;
	switch (rem_pio2 (x, y) & 3)
	{
	case 0:		return  k_cos (y[0], y[1]);
	case 1:		return -k_sin (y[0], y[1], 1);
	case 2:		return -k_cos (y[0], y[1]);
	default:	return  k_sin (y[0], y[1], 1);
	}
}

/*
==============================================================================
tan (musl's __tan.c and tan.c)
==============================================================================
*/

static const double T[] =
{
	 3.33333333333334091986e-01,	// 3FD55555 55555563
	 1.33333333333201242699e-01,	// 3FC11111 1110FE7A
	 5.39682539762260521377e-02,	// 3FABA1BA 1BB341FE
	 2.18694882948595424599e-02,	// 3F9664F4 8406D637
	 8.86323982359930005737e-03,	// 3F8226E3 E96E8493
	 3.59207910759131235356e-03,	// 3F6D6D22 C9560328
	 1.45620945432529025516e-03,	// 3F57DBC8 FEE08315
	 5.88041240820264096874e-04,	// 3F4344D8 F2F26501
	 2.46463134818469906812e-04,	// 3F3026F7 1A8D1068
	 7.81794442939557092300e-05,	// 3F147E88 A03792A6
	 7.14072491382608190305e-05,	// 3F12B80F 32F0A7E9
	-1.85586374855275456654e-05,	// BEF375CB DB605373
	 2.59073051863633712884e-05,	// 3EFB2A70 74BF7AD4
};
static const double
	pio4lo = 3.06161699786838301793e-17;	// 3C81A626 33145C07 (pio4: above, with rem_pio2)

static double low0 (double x)
{
	u64 u = bits (x) & 0xffffffff00000000ULL;

	memcpy (&x, &u, 8);
	return x;
}

// tan (x + y) on [-pi/4, pi/4], or -1 / tan (x + y) if odd
static double k_tan (double x, double y, int odd)
{
	double			z, r, v, w, s, a, w0, a0;
	unsigned int	hx = hi (x);
	int				big = (hx & 0x7fffffff) >= 0x3FE59428, sign = 0;	// |x| >= 0.6744

	if (big)
	{
		sign = hx >> 31;
		if (sign)
		{
			x = -x;
			y = -y;
		}
		x = (pio4 - x) + (pio4lo - y);
		y = 0.0;
	}
	z = x * x;
	w = z * z;
	r = T[1] + w * (T[3] + w * (T[5] + w * (T[7] + w * (T[9] + w * T[11]))));
	v = z * (T[2] + w * (T[4] + w * (T[6] + w * (T[8] + w * (T[10] + w * T[12])))));
	s = z * x;
	r = y + z * (s * (r + v) + y) + s * T[0];
	w = x + r;
	if (big)
	{
		s = 1 - 2 * odd;
		v = s - 2.0 * (x + (r - w * w / (w + s)));
		return sign ? -v : v;
	}
	if (!odd)
		return w;
	// -1.0 / (x + r) has up to 2 ulp error, so it is computed accurately
	w0 = low0 (w);
	v = r - (w0 - x);		// w0 + v = r + x
	a0 = a = -1.0 / w;
	a0 = low0 (a0);
	return a0 + a * (1.0 + a0 * w0 + a0 * v);
}

double edict_tan (double x)
{
	double			y[2];
	int				n;
	unsigned int	ix = hi (x) & 0x7fffffff;

	if (ix <= 0x3fe921fb)			// |x| <= pi/4
	{
		if (ix < 0x3e400000)		// |x| < 2^-27
			return x;
		return k_tan (x, 0.0, 0);
	}
	if (ix >= 0x7ff00000)			// inf or nan
		return x - x;
	n = rem_pio2 (x, y);			// before y is read: C leaves the order of arguments open
	return k_tan (y[0], y[1], n & 1);
}

/*
==============================================================================
atan, atan2
==============================================================================
*/

static const double atanhi[] =
{
	4.63647609000806093515e-01,	// atan(0.5)hi  0x3FDDAC67 0561BB4F
	7.85398163397448278999e-01,	// atan(1.0)hi  0x3FE921FB 54442D18
	9.82793723247329054082e-01,	// atan(1.5)hi  0x3FEF730B D281F69B
	1.57079632679489655800e+00,	// atan(inf)hi  0x3FF921FB 54442D18
};

static const double atanlo[] =
{
	2.26987774529616870924e-17,	// 0x3C7A2B7F 222F65E2
	3.06161699786838301793e-17,	// 0x3C81A626 33145C07
	1.39033110312309984516e-17,	// 0x3C700788 7AF0CBBD
	6.12323399573676603587e-17,	// 0x3C91A626 33145C07
};

static const double aT[] =
{
	 3.33333333333329318027e-01,	// 0x3FD55555 5555550D
	-1.99999999998764832476e-01,	// 0xBFC99999 9998EBC4
	 1.42857142725034663711e-01,	// 0x3FC24924 920083FF
	-1.11111104054623557880e-01,	// 0xBFBC71C6 FE231671
	 9.09088713343650656196e-02,	// 0x3FB745CD C54C206E
	-7.69187620504482999495e-02,	// 0xBFB3B0F2 AF749A6D
	 6.66107313738753120669e-02,	// 0x3FB10D66 A0D03D51
	-5.83357013379057348645e-02,	// 0xBFADDE2D 52DEFD9A
	 4.97687799461593236017e-02,	// 0x3FA97B4B 24760DEB
	-3.65315727442169155270e-02,	// 0xBFA2B444 2C6A6C2F
	 1.62858201153657823623e-02,	// 0x3F90AD3A E322DA11
};

double edict_atan (double x)
{
	double			w, s1, s2, z;
	unsigned int	ix = hi (x) & 0x7fffffff;
	int				sign = hi (x) >> 31;
	int				id;

	if (ix >= 0x44100000)			// |x| >= 2^66
	{
		if (x != x)
			return x;
		z = atanhi[3];
		return sign ? -z : z;
	}
	if (ix < 0x3fdc0000)			// |x| < 0.4375
	{
		if (ix < 0x3e400000)		// |x| < 2^-27
			return x;
		id = -1;
	}
	else
	{
		x = fabs (x);
		if (ix < 0x3ff30000)		// |x| < 1.1875
		{
			if (ix < 0x3fe60000)	// 7/16 <= |x| < 11/16
			{
				id = 0;
				x = (2.0 * x - 1.0) / (2.0 + x);
			}
			else					// 11/16 <= |x| < 19/16
			{
				id = 1;
				x = (x - 1.0) / (x + 1.0);
			}
		}
		else
		{
			if (ix < 0x40038000)	// |x| < 2.4375
			{
				id = 2;
				x = (x - 1.5) / (1.0 + 1.5 * x);
			}
			else					// 2.4375 <= |x| < 2^66
			{
				id = 3;
				x = -1.0 / x;
			}
		}
	}
	z = x * x;
	w = z * z;
	s1 = z * (aT[0] + w * (aT[2] + w * (aT[4] + w * (aT[6] + w * (aT[8] + w * aT[10])))));
	s2 = w * (aT[1] + w * (aT[3] + w * (aT[5] + w * (aT[7] + w * aT[9]))));
	if (id < 0)
		return x - x * (s1 + s2);
	z = atanhi[id] - (x * (s1 + s2) - atanlo[id] - x);
	return sign ? -z : z;
}

static const double
pi    = 3.1415926535897931160e+00,	// 0x400921FB 54442D18
pi_lo = 1.2246467991473531772e-16;	// 0x3CA1A626 33145C07

double edict_atan2 (double y, double x)
{
	double			z;
	unsigned int	m, ix, iy, lx, ly;

	if (x != x || y != y)
		return x + y;
	ix = hi (x); lx = lo (x);
	iy = hi (y); ly = lo (y);
	if (((ix - 0x3ff00000) | lx) == 0)	// x == 1.0
		return edict_atan (y);
	m = ((iy >> 31) & 1) | ((ix >> 30) & 2);	// 2 * sign(x) + sign(y)
	ix &= 0x7fffffff;
	iy &= 0x7fffffff;

	if ((iy | ly) == 0)				// y == 0
	{
		switch (m)
		{
		case 0:
		case 1:		return y;
		case 2:		return pi;
		default:	return -pi;
		}
	}
	if ((ix | lx) == 0)				// x == 0
		return (m & 1) ? -pi / 2 : pi / 2;
	if (ix == 0x7ff00000)			// x is inf
	{
		if (iy == 0x7ff00000)
		{
			switch (m)
			{
			case 0:		return pi / 4;
			case 1:		return -pi / 4;
			case 2:		return 3 * pi / 4;
			default:	return -3 * pi / 4;
			}
		}
		switch (m)
		{
		case 0:		return 0.0;
		case 1:		return -0.0;
		case 2:		return pi;
		default:	return -pi;
		}
	}
	if (ix + (64 << 20) < iy || iy == 0x7ff00000)	// |y/x| > 2^64
		return (m & 1) ? -pi / 2 : pi / 2;
	if ((m & 2) && iy + (64 << 20) < ix)			// |y/x| < 2^-64, x < 0
		z = 0;
	else
		z = edict_atan (fabs (y / x));
	switch (m)
	{
	case 0:		return z;
	case 1:		return -z;
	case 2:		return pi - (z - pi_lo);
	default:	return (z - pi_lo) - pi;
	}
}
