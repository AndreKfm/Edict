// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using static Edict.Exact.Tests.Differential;

namespace Edict.Exact.Tests;

/// <summary>
/// docs/TESTING.md R7: deliberately wrong implementations that the differential comparison must reject, run over the
/// same inputs the real tests use. A known-bad variant that passes means the suite is blind there.
/// </summary>
public sealed class KnownBadTests
{
    private static void MustDiverge(string variant, Func<string?> firstDivergence)
    {
        string? report = firstDivergence();
        Assert.True(report is not null, $"R7: known-bad variant '{variant}' was not detected");
    }

    private static string? FirstUnary(Func<double, double> port, Func<double, double> reference, ulong seed, int count, double lo, double hi)
    {
        var rng = new SplitMix64(seed);
        for (int i = 0; i < count; i++)
        {
            double x = rng.Uniform(lo, hi);
            string? r = Compare("f", Hex(x), port(x), reference(x));
            if (r is not null)
                return r;
        }
        return null;
    }

    [Fact]
    public void Runtime_Math_Sin_instead_of_CMath() =>
        MustDiverge("Math.Sin", () => FirstUnary(Math.Sin, Oracle.Sin, 1, 1_000_000, -12.566370614359172, 12.566370614359172));

    [Fact]
    public void Runtime_Math_Atan2_instead_of_CMath() =>
        MustDiverge("Math.Atan2", () =>
        {
            var rng = new SplitMix64(11);
            for (int i = 0; i < 1_000_000; i++)
            {
                float fy = (float)rng.Uniform(-4096, 4096), fx = (float)rng.Uniform(-4096, 4096);
                string? r = Compare("atan2", "", Math.Atan2(fy, fx), Oracle.Atan2(fy, fx));
                if (r is not null)
                    return r;
            }
            return null;
        });

    [Fact]
    public void One_ulp_off() =>
        MustDiverge("CMath.Sin + 1 ulp", () => FirstUnary(x => Math.BitIncrement(CMath.Sin(x)), Oracle.Sin, 1, 1_000, -1, 1));

    // A first candidate, the inner polynomial r of KSin summed in a different order, was not detected: measured
    // directly against CMath.Sin it is bit-identical on 10^7 arguments in [-pi/4, pi/4] (the reordering changes r
    // below what survives the final additions). It is no known-bad variant, and the suite was not blind. The
    // variant below distributes the outer product and differs in 25 % of the same arguments.
    [Fact]
    public void Distributed_product_in_the_sin_kernel() =>
        MustDiverge("sin kernel: x + v*S1 + v*z*r instead of x + v*(S1 + z*r)", () => FirstUnary(x =>
        {
            double z = x * x, w = z * z, v = z * x;
            double r = 8.33333333332248946124e-03 + z * (-1.98412698298579493134e-04 + z * 2.75573137070700676789e-06)
                       + z * w * (-2.50507602534068634195e-08 + z * 1.58969099521155010221e-10);
            return x + v * -1.66666666666666324348e-01 + v * z * r;
        }, Oracle.Sin, 2, 1_000_000, -0.78, 0.78));

    [Fact]
    public void Hardware_cast_instead_of_CCast() =>
        MustDiverge("(int)float as cvttss2si (0x80000000 out of range), the reference before pinning", () =>
        {
            static int Cvttss2si(float f) => f >= -2147483648f && f < 2147483648f ? (int)f : int.MinValue;
            foreach (float f in new[] { 3e9f, -3e9f, float.NaN, float.PositiveInfinity })
            {
                string? r = Compare("(int)float", Hex(f), Cvttss2si(f), Oracle.FloatToInt(f));
                if (r is not null)
                    return r;
            }
            return null;
        });

    [Fact]
    public void System_Random_instead_of_CRand() =>
        MustDiverge("System.Random", () =>
        {
            var random = new Random(1);
            Oracle.Srand(1);
            for (int i = 0; i < 1000; i++)
            {
                string? r = Compare("rand", $"call {i}", random.Next(0x8000), Oracle.Rand());
                if (r is not null)
                    return r;
            }
            return null;
        });

    [Fact]
    public void Missing_rand_call() =>
        MustDiverge("one rand() call skipped", () =>
        {
            var port = new CRand();
            Oracle.Srand(1);
            Oracle.Rand();      // the reference consumes one more value, e.g. _Host_Frame's per-frame rand()
            for (int i = 0; i < 1000; i++)
            {
                string? r = Compare("rand", $"call {i}", port.Next(), Oracle.Rand());
                if (r is not null)
                    return r;
            }
            return null;
        });

    [Fact]
    public void Sign_only_strcmp() =>
        MustDiverge("strcmp returning -1/0/1", () =>
        {
            byte[] a = "plat"u8.ToArray(), b = [];
            return Compare("strcmp", "\"plat\", \"\"", Math.Sign(CString.Strcmp(a, b)), Oracle.Strcmp(a, b));
        });

    // .NET's ToString("F1") was a first candidate here and matched the reference on the exact ties 0.25, 0.75, 2.5
    // and -0.25: it is correctly rounded, ties to even, like glibc and UCRT. Not a known-bad variant (it allocates,
    // which the analyzer rejects, but that is a different gate). Rounding half away from zero is.
    [Fact]
    public void Ftos_rounding_half_away_from_zero() =>
        MustDiverge("ftos rounding ties away from zero", () =>
        {
            Span<byte> reference = stackalloc byte[256];
            foreach (float v in new[] { 0.25f, 0.75f, 2.5f, -0.25f })
            {
                double tenths = Math.Round(v * 10.0, MidpointRounding.AwayFromZero) / 10;
                string port = tenths.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5);
                int m = Oracle.Ftos(v, reference);
                string r = System.Text.Encoding.Latin1.GetString(reference[..m]);
                if (port != r)
                    return $"ftos({v}): port \"{port}\", reference \"{r}\"";
            }
            return null;
        });

    [Fact]
    public void Atof_with_float_Parse() =>
        MustDiverge("float.Parse instead of (float)strtod (no double rounding)", () =>
        {
            // 1 + 2^-24 is halfway between two floats; the trailing 1 lifts the decimal just above it. strtod rounds
            // to the double 1 + 2^-24, and the float conversion then ties to even (1.0); float.Parse rounds once (up).
            byte[] s = "1.000000059604644775390625000001"u8.ToArray();
            float port = float.Parse("1.000000059604644775390625000001", System.Globalization.CultureInfo.InvariantCulture);
            return Compare("atof", "1.000000059604644775390625000001", port, Oracle.EpairFloat(s));
        });

    // A first candidate, Q_atof adding (c - '0') instead of computing (val*10 + c) - '0', gave the same float on a
    // 39-digit input: the double results differ at most in low bits that the float conversion drops. Not a
    // known-bad variant on that input. Porting Q_atof as atof is: atof skips white space and accepts '+' and
    // exponents, Q_atof does not.
    [Fact]
    public void Q_atof_ported_as_atof() =>
        MustDiverge("Q_atof implemented with atof semantics", () =>
        {
            foreach (string text in new[] { " 1.5", "+2", "1e5" })
            {
                byte[] s = System.Text.Encoding.Latin1.GetBytes(text);
                string? r = Compare("Q_atof", text, (float)CAtof.Atof(s), Oracle.QAtof(s));
                if (r is not null)
                    return r;
            }
            return null;
        });
}
