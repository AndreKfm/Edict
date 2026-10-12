// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace Edict.Exact;

/// <summary>
/// The reference's pinned sin, cos, atan and atan2 (ref/src/edict_libm.c), operation for operation, so that results
/// are bit-identical to the C reference on every platform. Never use <see cref="Math.Sin"/> and friends in simulation
/// code: their last bit depends on the runtime and the C library underneath.
/// </summary>
/// <remarks>
/// fdlibm algorithms as carried by musl. fdlibm notice: Copyright (C) 1993 by Sun Microsystems, Inc. All rights
/// reserved. Developed at SunPro, a Sun Microsystems, Inc. business. Permission to use, copy, modify, and distribute
/// this software is freely granted, provided that this notice is preserved.
/// </remarks>
[NoAlloc(Constraints.All)]
public static class CMath
{
    private static uint Hi(double x) => (uint)(Unsafe.BitCast<double, ulong>(x) >> 32);

    private static uint Lo(double x) => (uint)Unsafe.BitCast<double, ulong>(x);

    // Kernels on [-pi/4, pi/4]

    private const double S1 = -1.66666666666666324348e-01;
    private const double S2 = 8.33333333332248946124e-03;
    private const double S3 = -1.98412698298579493134e-04;
    private const double S4 = 2.75573137070700676789e-06;
    private const double S5 = -2.50507602534068634195e-08;
    private const double S6 = 1.58969099521155010221e-10;

    private const double C1 = 4.16666666666666019037e-02;
    private const double C2 = -1.38888888888741095749e-03;
    private const double C3 = 2.48015872894767294178e-05;
    private const double C4 = -2.75573143513906633035e-07;
    private const double C5 = 2.08757232129817482790e-09;
    private const double C6 = -1.13596475577881948265e-11;

    private static double KSin(double x, double y, int iy)
    {
        double z = x * x;
        double w = z * z;
        double r = S2 + z * (S3 + z * S4) + z * w * (S5 + z * S6);
        double v = z * x;
        if (iy == 0)
            return x + v * (S1 + z * r);
        return x - ((z * (0.5 * y - v * r) - y) - v * S1);
    }

    private static double KCos(double x, double y)
    {
        double z = x * x;
        double w = z * z;
        double r = z * (C1 + z * (C2 + z * C3)) + w * w * (C4 + z * (C5 + z * C6));
        double hz = 0.5 * z;
        w = 1.0 - hz;
        return w + (((1.0 - w) - hz) + (z * r - x * y));
    }

    // Argument reduction: x = n * pi/2 + (y0 + y1)

    private const double ToInt = 6755399441055744.0;
    private const double InvPio2 = 6.36619772367581382433e-01;
    private const double Pio4 = 7.85398163397448278999e-01;
    private const double Pio2_1 = 1.57079632673412561417e+00;
    private const double Pio2_1t = 6.07710050650619224932e-11;
    private const double Pio2_2 = 6.07710050630396597660e-11;
    private const double Pio2_2t = 2.02226624879595063154e-21;
    private const double Pio2_3 = 2.02226624871116645580e-21;
    private const double Pio2_3t = 8.47842766036889956997e-32;
    private const double TwoPi = 6.28318530717958623200e+00;

    private static int RemPio2(double x, out double y0, out double y1)
    {
        uint ix = Hi(x) & 0x7fffffff;
        if (ix >= 0x413921fb)
        {
            x %= TwoPi;     // C fmod: exact, sign of the dividend
            ix = Hi(x) & 0x7fffffff;
        }
        double fn = x * InvPio2 + ToInt - ToInt;
        int n = (int)fn;
        double r = x - fn * Pio2_1;
        double w = fn * Pio2_1t;
        if (r - w < -Pio4)
        {
            n--; fn--;
            r = x - fn * Pio2_1;
            w = fn * Pio2_1t;
        }
        else if (r - w > Pio4)
        {
            n++; fn++;
            r = x - fn * Pio2_1;
            w = fn * Pio2_1t;
        }
        y0 = r - w;
        int ey = (int)((Hi(y0) >> 20) & 0x7ff);
        int ex = (int)(ix >> 20);
        if (ex - ey > 16)
        {
            double t = r;
            w = fn * Pio2_2;
            r = t - w;
            w = fn * Pio2_2t - ((t - r) - w);
            y0 = r - w;
            ey = (int)((Hi(y0) >> 20) & 0x7ff);
            if (ex - ey > 49)
            {
                t = r;
                w = fn * Pio2_3;
                r = t - w;
                w = fn * Pio2_3t - ((t - r) - w);
                y0 = r - w;
            }
        }
        y1 = (r - y0) - w;
        return n;
    }

    /// <summary>sin as the reference computes it (edict_sin).</summary>
    public static double Sin(double x)
    {
        uint ix = Hi(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb)
        {
            if (ix < 0x3e500000)
                return x;
            return KSin(x, 0.0, 0);
        }
        if (ix >= 0x7ff00000)
            return x - x;
        return (RemPio2(x, out double y0, out double y1) & 3) switch
        {
            0 => KSin(y0, y1, 1),
            1 => KCos(y0, y1),
            2 => -KSin(y0, y1, 1),
            _ => -KCos(y0, y1),
        };
    }

    /// <summary>cos as the reference computes it (edict_cos).</summary>
    public static double Cos(double x)
    {
        uint ix = Hi(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb)
        {
            if (ix < 0x3e46a09e)
                return 1.0;
            return KCos(x, 0.0);
        }
        if (ix >= 0x7ff00000)
            return x - x;
        return (RemPio2(x, out double y0, out double y1) & 3) switch
        {
            0 => KCos(y0, y1),
            1 => -KSin(y0, y1, 1),
            2 => -KCos(y0, y1),
            _ => KSin(y0, y1, 1),
        };
    }

    private static ReadOnlySpan<double> AtanHi =>
    [
        4.63647609000806093515e-01,
        7.85398163397448278999e-01,
        9.82793723247329054082e-01,
        1.57079632679489655800e+00,
    ];

    private static ReadOnlySpan<double> AtanLo =>
    [
        2.26987774529616870924e-17,
        3.06161699786838301793e-17,
        1.39033110312309984516e-17,
        6.12323399573676603587e-17,
    ];

    private static ReadOnlySpan<double> AT =>
    [
        3.33333333333329318027e-01,
        -1.99999999998764832476e-01,
        1.42857142725034663711e-01,
        -1.11111104054623557880e-01,
        9.09088713343650656196e-02,
        -7.69187620504482999495e-02,
        6.66107313738753120669e-02,
        -5.83357013379057348645e-02,
        4.97687799461593236017e-02,
        -3.65315727442169155270e-02,
        1.62858201153657823623e-02,
    ];

    /// <summary>atan as the reference computes it (edict_atan).</summary>
    public static double Atan(double x)
    {
        uint ix = Hi(x) & 0x7fffffff;
        bool sign = (Hi(x) >> 31) != 0;
        int id;
        double z;

        if (ix >= 0x44100000)
        {
            if (double.IsNaN(x))
                return x;
            z = AtanHi[3];
            return sign ? -z : z;
        }
        if (ix < 0x3fdc0000)
        {
            if (ix < 0x3e400000)
                return x;
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3ff30000)
            {
                if (ix < 0x3fe60000)
                {
                    id = 0;
                    x = (2.0 * x - 1.0) / (2.0 + x);
                }
                else
                {
                    id = 1;
                    x = (x - 1.0) / (x + 1.0);
                }
            }
            else if (ix < 0x40038000)
            {
                id = 2;
                x = (x - 1.5) / (1.0 + 1.5 * x);
            }
            else
            {
                id = 3;
                x = -1.0 / x;
            }
        }
        ReadOnlySpan<double> aT = AT;
        z = x * x;
        double w = z * z;
        double s1 = z * (aT[0] + w * (aT[2] + w * (aT[4] + w * (aT[6] + w * (aT[8] + w * aT[10])))));
        double s2 = w * (aT[1] + w * (aT[3] + w * (aT[5] + w * (aT[7] + w * aT[9]))));
        if (id < 0)
            return x - x * (s1 + s2);
        z = AtanHi[id] - (x * (s1 + s2) - AtanLo[id] - x);
        return sign ? -z : z;
    }

    private const double Pi = 3.1415926535897931160e+00;
    private const double PiLo = 1.2246467991473531772e-16;

    /// <summary>atan2 as the reference computes it (edict_atan2).</summary>
    public static double Atan2(double y, double x)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
            return x + y;
        uint ix = Hi(x), lx = Lo(x);
        uint iy = Hi(y), ly = Lo(y);
        if (((ix - 0x3ff00000) | lx) == 0)
            return Atan(y);
        uint m = ((iy >> 31) & 1) | ((ix >> 30) & 2);
        ix &= 0x7fffffff;
        iy &= 0x7fffffff;

        if ((iy | ly) == 0)
        {
            return m switch
            {
                0 or 1 => y,
                2 => Pi,
                _ => -Pi,
            };
        }
        if ((ix | lx) == 0)
            return (m & 1) != 0 ? -Pi / 2 : Pi / 2;
        if (ix == 0x7ff00000)
        {
            if (iy == 0x7ff00000)
            {
                return m switch
                {
                    0 => Pi / 4,
                    1 => -Pi / 4,
                    2 => 3 * Pi / 4,
                    _ => -3 * Pi / 4,
                };
            }
            return m switch
            {
                0 => 0.0,
                1 => -0.0,
                2 => Pi,
                _ => -Pi,
            };
        }
        if (ix + (64 << 20) < iy || iy == 0x7ff00000)
            return (m & 1) != 0 ? -Pi / 2 : Pi / 2;
        double z;
        if ((m & 2) != 0 && iy + (64 << 20) < ix)
            z = 0;
        else
            z = Atan(Math.Abs(y / x));
        return m switch
        {
            0 => z,
            1 => -z,
            2 => Pi - (z - PiLo),
            _ => (z - PiLo) - Pi,
        };
    }
}
