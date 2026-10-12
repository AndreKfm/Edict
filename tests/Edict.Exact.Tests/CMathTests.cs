// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using static Edict.Exact.Tests.Differential;

namespace Edict.Exact.Tests;

/// <summary>CMath against the oracle: edge table, seeded random sets, Quake-style float angles (docs/TESTING.md R4).</summary>
public sealed class CMathTests
{
    private static readonly double[] Edges =
    [
        0.0, -0.0, double.Epsilon, -double.Epsilon, 1e-310, -1e-310, 2.2250738585072014e-308, 1e-30, -1e-30,
        7.450580596923828e-9, 1.4901161193847656e-8, 3.725290298461914e-9,   // around the 2^-26 / 2^-27 cut-offs
        0.7853981633974483, -0.7853981633974483, 0.7853981633974484,        // pi/4 boundary
        1.5707963267948966, -1.5707963267948966, 3.141592653589793, -3.141592653589793, 4.71238898038469, 6.283185307179586,
        1.0, -1.0, 0.4375, 0.6875, 1.1875, 2.4375, 7.0 / 16, 11.0 / 16, 19.0 / 16, 39.0 / 16,
        1647099.3291652855, 1647099.3291652856, 1647100.0,                 // 2^20 * pi/2: start of the fmod fallback
        1e5, -1e5, 1e10, 1e22, 1e300, -1e300, double.MaxValue, -double.MaxValue, 7.378697629483821e19, 7.378697629483822e19,
        double.PositiveInfinity, double.NegativeInfinity, double.NaN,
    ];

    private static void Unary(string name, Func<double, double> port, Func<double, double> reference, IEnumerable<double> inputs)
    {
        foreach (double x in inputs)
        {
            string? report = Compare(name, Hex(x), port(x), reference(x));
            Pass(report);
        }
    }

    private static IEnumerable<double> Random(ulong seed, int count, Func<SplitMix64, (SplitMix64, double)> draw)
    {
        var rng = new SplitMix64(seed);
        for (int i = 0; i < count; i++)
        {
            (rng, double x) = draw(rng);
            yield return x;
        }
    }

    private static IEnumerable<double> UniformSet(ulong seed, int count, double lo, double hi) =>
        Random(seed, count, r => { double x = r.Uniform(lo, hi); return (r, x); });

    private static IEnumerable<double> AnySet(ulong seed, int count) =>
        Random(seed, count, r => { double x = r.AnyDouble(); return (r, x); });

    // AngleVectors: angle (float) = angles[YAW] * (M_PI*2 / 360), then sin ((double)angle)
    private static IEnumerable<double> QuakeAngles(ulong seed, int count) =>
        Random(seed, count, r =>
        {
            float deg = (float)r.Uniform(-1080.0, 1080.0);
            float angle = (float)(deg * (Math.PI * 2 / 360));
            return (r, (double)angle);
        });

    [Fact] public void Sin_edges() => Unary("sin", CMath.Sin, Oracle.Sin, Edges);
    [Fact] public void Cos_edges() => Unary("cos", CMath.Cos, Oracle.Cos, Edges);
    [Fact] public void Atan_edges() => Unary("atan", CMath.Atan, Oracle.Atan, Edges);

    [Fact] public void Sin_random() => Unary("sin", CMath.Sin, Oracle.Sin, UniformSet(1, 1_000_000, -12.566370614359172, 12.566370614359172).Concat(UniformSet(2, 1_000_000, -1e5, 1e5)).Concat(AnySet(3, 1_000_000)));
    [Fact] public void Cos_random() => Unary("cos", CMath.Cos, Oracle.Cos, UniformSet(4, 1_000_000, -12.566370614359172, 12.566370614359172).Concat(UniformSet(5, 1_000_000, -1e5, 1e5)).Concat(AnySet(6, 1_000_000)));
    [Fact] public void Atan_random() => Unary("atan", CMath.Atan, Oracle.Atan, UniformSet(7, 1_000_000, -10, 10).Concat(AnySet(8, 1_000_000)));

    [Fact] public void Sin_cos_quake_angles()
    {
        Unary("sin", CMath.Sin, Oracle.Sin, QuakeAngles(9, 1_000_000));
        Unary("cos", CMath.Cos, Oracle.Cos, QuakeAngles(10, 1_000_000));
    }

    [Fact]
    public void Atan2_edges()
    {
        foreach (double y in Edges)
            foreach (double x in Edges)
                Pass(Compare("atan2", $"{Hex(y)}, {Hex(x)}", CMath.Atan2(y, x), Oracle.Atan2(y, x)));
    }

    [Fact]
    public void Atan2_random()
    {
        var rng = new SplitMix64(11);
        for (int i = 0; i < 1_000_000; i++)
        {
            double y = rng.AnyDouble(), x = rng.AnyDouble();
            Pass(Compare("atan2", $"{Hex(y)}, {Hex(x)}", CMath.Atan2(y, x), Oracle.Atan2(y, x)));
            // vectoyaw / vectoangles: float components
            float fy = (float)rng.Uniform(-4096, 4096), fx = (float)rng.Uniform(-4096, 4096);
            Pass(Compare("atan2", $"{Hex(fy)}, {Hex(fx)}", CMath.Atan2(fy, fx), Oracle.Atan2(fy, fx)));
        }
    }
}
