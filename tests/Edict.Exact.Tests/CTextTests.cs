// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using static Edict.Exact.Tests.Differential;

namespace Edict.Exact.Tests;

/// <summary>CFormat (PF_ftos, PF_vtos) and CAtof (ED_ParseEpair's atof, Q_atof) against the oracle.</summary>
public sealed class CTextTests
{
    private static string? CompareText(string function, string input, ReadOnlySpan<byte> port, ReadOnlySpan<byte> reference) =>
        port.SequenceEqual(reference)
            ? null
            : $"{function}({input}): port \"{Encoding.Latin1.GetString(port)}\", reference \"{Encoding.Latin1.GetString(reference)}\"";

    private static string? CompareFloat(string function, string input, float port, float reference) =>
        Bits(port) == Bits(reference)
            ? null
            : $"{function}({input}): port {Hex(port)}, reference {Hex(reference)}";

    private static void Ftos(float v)
    {
        Span<byte> port = stackalloc byte[CFormat.MaxFtosLength];
        Span<byte> reference = stackalloc byte[256];
        int n = CFormat.Ftos(v, port);
        int m = Oracle.Ftos(v, reference);
        Pass(CompareText("ftos", Hex(v), port[..n], reference[..m]));
    }

    private static readonly float[] FloatEdges =
    [
        0f, -0f, 0.25f, 0.75f, 1.25f, 2.5f, -0.25f, -0.75f, 0.05f, 0.15f, 0.35f, 0.45f, 0.95f, 99.95f, -0.01f, -0.04f, -0.05f,
        -0.06f, 123.45f, 1e-45f, -1e-45f, 1.1754942e-38f, 1e-10f, 16777216f, 16777217f, 8388607.5f, 2147483520f, 2147483648f,
        -2147483648f, -2147483904f, 3e9f, 1e10f, 1e30f, float.MaxValue, -float.MaxValue, float.PositiveInfinity,
        float.NegativeInfinity, float.NaN, BitConverter.UInt32BitsToSingle(0x7fc00000), BitConverter.UInt32BitsToSingle(0xffc00000),
        BitConverter.UInt32BitsToSingle(0x7f800001), BitConverter.UInt32BitsToSingle(0xff812345),
    ];

    [Fact]
    public void Ftos_edges()
    {
        foreach (float v in FloatEdges)
            Ftos(v);
    }

    [Fact]
    public void Ftos_ties_and_tenths()
    {
        // exact ties at the first decimal (k + 0.25, k + 0.75) and values near tenths
        for (int k = -100_000; k <= 100_000; k++)
        {
            Ftos(k + 0.25f);
            Ftos(k + 0.75f);
            Ftos(k / 10f);
            Ftos(k / 100f);
        }
    }

    [Fact]
    public void Ftos_random()
    {
        var rng = new SplitMix64(13);
        for (int i = 0; i < 1_000_000; i++)
        {
            Ftos(BitConverter.UInt32BitsToSingle((uint)rng.Next()));
            Ftos((float)rng.Uniform(-10000, 10000));
        }
    }

    [Fact]
    public void Vtos_random()
    {
        var rng = new SplitMix64(14);
        Span<byte> port = stackalloc byte[CFormat.MaxVtosLength];
        Span<byte> reference = stackalloc byte[256];
        for (int i = 0; i < 200_000; i++)
        {
            // |component| < 1e30: larger vectors overflow pr_string_temp[128] in the reference (undefined behaviour)
            float x = (float)rng.Uniform(-5000, 5000), y = (float)rng.Uniform(-1e6, 1e6);
            float z = BitConverter.UInt32BitsToSingle((uint)rng.Next() & 0xefffffff);
            if (!(Math.Abs(z) < 1e30f))
                z = 0.5f;
            int n = CFormat.Vtos(x, y, z, port);
            int m = Oracle.Vtos(x, y, z, reference);
            Pass(CompareText("vtos", $"{Hex(x)}, {Hex(y)}, {Hex(z)}", port[..n], reference[..m]));
        }
    }

    // ---- atof / Q_atof ----

    private static readonly string[] AtofEdges =
    [
        "", "0", "-0", "+0", "1.5", " 1.5", "\t\n\v\f\r 2", "+2", "-2", ".5", "5.", ".", "-.", "-", "+", "abc", "-abc", "1,5",
        "1e40", "-1e40", "1e-50", "1e-45", "7e-46", "1.401298464324817e-45", "7.006492321624085e-46", "7.006492321624086e-46",
        "3.4028234663852886e38", "3.4028235677973366e38", "3.4028235677973362e38", "1e309", "1e-400", "2.2250738585072014e-308",
        "5e", "5e+", "5e-", "5ex", "1e5x", "0.1", "0.3", "123456789012345678901234567890",
        "1.000000059604644775390625", "1.000000059604644775390625000001", "1.0000000596046447753906249",
        "0x1p3", "0x10", "0X1P-1", "0x1.8p1", "0x.8", "0x", "0x.", "0xg", "-0x1p3", "0x1.fffffep127", "0x1.ffffffp127",
        "0x1p-149", "0x1p-150", "0x1.000001p-150", "0x1p-1074", "0x1p-1075", "0x1.0000000000001p-1075", "0x123456789abcdef0123p0",
        "0x1.00000100000000001p0", "0x1p99999999999", "0x1p-99999999999",
        "inf", "INF", "-inf", "infinity", "-INFINITY", "infx", "in", "nan", "-nan", "NaN", "nan(123)", "nan(", "nan(abc)x", "na",
    ];

    private static void Atof(string text)
    {
        byte[] s = Encoding.Latin1.GetBytes(text);
        Pass(CompareFloat("atof", $"\"{text}\"", (float)CAtof.Atof(s), Oracle.EpairFloat(s)));
        Pass(CompareFloat("Q_atof", $"\"{text}\"", CAtof.QAtof(s), Oracle.QAtof(s)));
    }

    [Fact]
    public void Atof_edges()
    {
        foreach (string s in AtofEdges)
            Atof(s);
        foreach (string s in new[] { "'a'", "'ÿ'", "-'a'", "'", "1.2.3", "--1", "-0x1F", "0xffffffffffffffffffff", "1.5e3" })
            Atof(s);
    }

    private static string RandomDecimal(ref SplitMix64 rng)
    {
        var b = new StringBuilder();
        if (rng.Next() % 8 == 0) b.Append(' ');
        switch (rng.Next() % 4) { case 0: b.Append('-'); break; case 1: b.Append('+'); break; }
        int whole = (int)(rng.Next() % 12), fraction = (int)(rng.Next() % 12);
        for (int i = 0; i < whole; i++) b.Append((char)('0' + rng.Next() % 10));
        if (rng.Next() % 4 != 0)
        {
            b.Append('.');
            for (int i = 0; i < fraction; i++) b.Append((char)('0' + rng.Next() % 10));
        }
        if (rng.Next() % 3 == 0)
        {
            b.Append(rng.Next() % 2 == 0 ? 'e' : 'E');
            switch (rng.Next() % 3) { case 0: b.Append('-'); break; case 1: b.Append('+'); break; }
            b.Append((int)(rng.Next() % 50));
        }
        if (rng.Next() % 8 == 0) b.Append("x1");
        return b.ToString();
    }

    private static string RandomHex(ref SplitMix64 rng)
    {
        const string digits = "0123456789abcdefABCDEF";
        var b = new StringBuilder(rng.Next() % 2 == 0 ? "0x" : "-0X");
        int whole = (int)(rng.Next() % 18), fraction = (int)(rng.Next() % 18);
        for (int i = 0; i < whole; i++) b.Append(digits[(int)(rng.Next() % 22)]);
        if (rng.Next() % 2 == 0)
        {
            b.Append('.');
            for (int i = 0; i < fraction; i++) b.Append(digits[(int)(rng.Next() % 22)]);
        }
        if (rng.Next() % 2 == 0)
            b.Append('p').Append((int)(rng.Next() % 2400) - 1200);
        return b.ToString();
    }

    [Fact]
    public void Atof_random_decimal()
    {
        var rng = new SplitMix64(15);
        for (int i = 0; i < 500_000; i++)
            Atof(RandomDecimal(ref rng));
    }

    [Fact]
    public void Atof_random_hex()
    {
        var rng = new SplitMix64(16);
        for (int i = 0; i < 200_000; i++)
            Atof(RandomHex(ref rng));
    }

    [Fact]
    public void Atof_round_trip_of_every_tenth_float_pattern()
    {
        // shortest round-trip text of float values, as map files are written; covers subnormals and the top range
        for (uint bits = 0; bits < 0x7f800000; bits += 4093)
        {
            float f = BitConverter.UInt32BitsToSingle(bits);
            Atof(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Atof(((double)f).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
