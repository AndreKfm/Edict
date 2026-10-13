// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using static Edict.Exact.Tests.Differential;

namespace Edict.Exact.Tests;

/// <summary>CCast, CRand and CString against the oracle.</summary>
[Collection(OracleStateCollection.Name)]
public sealed class CBasicsTests
{
    private static readonly float[] FloatEdges =
    [
        0f, -0f, 0.5f, -0.5f, 0.99999994f, -0.99999994f, 1f, -1f, 1.5f, -1.5f, 2147483520f, -2147483520f,
        2147483648f, -2147483648f, 2147483904f, -2147483904f, 3e9f, -3e9f, 1e30f, -1e30f, float.MaxValue,
        -float.MaxValue, float.Epsilon, float.PositiveInfinity, float.NegativeInfinity, float.NaN,
    ];

    [Fact]
    public void Float_to_int_edges()
    {
        foreach (float f in FloatEdges)
            Pass(Compare("(int)float", Hex(f), CCast.I32(f), Oracle.FloatToInt(f)));
    }

    [Fact]
    public void Double_to_int_edges()
    {
        double[] edges = [0, -0.0, 0.5, -0.5, 2147483647.0, 2147483647.9, 2147483648.0, -2147483648.0, -2147483648.9,
                          -2147483649.0, 1e300, -1e300, double.PositiveInfinity, double.NegativeInfinity, double.NaN];
        foreach (double d in edges)
            Pass(Compare("(int)double", Hex(d), CCast.I32(d), Oracle.DoubleToInt(d)));
    }

    /// <summary>All 2^32 float bit patterns (docs/TESTING.md R4 d).</summary>
    [Fact]
    public void Float_to_int_exhaustive()
    {
        string? first = null;
        Parallel.For(0, 256, chunk =>
        {
            uint start = (uint)chunk << 24;
            for (uint i = 0; i < (1u << 24); i++)
            {
                float f = BitConverter.UInt32BitsToSingle(start + i);
                int port = CCast.I32(f), reference = Oracle.FloatToInt(f);
                if (port != reference)
                {
                    Interlocked.CompareExchange(ref first, Compare("(int)float", Hex(f), port, reference), null);
                    return;
                }
            }
        });
        Pass(first);
    }

    [Fact]
    public void Rand_sequences()
    {
        // default seed (1), then several explicit seeds
        var port = new CRand();
        Oracle.Srand(1);
        for (int i = 0; i < 1_000_000; i++)
            Pass(Compare("rand", $"seed 1, call {i}", port.Next(), Oracle.Rand()));
        foreach (uint seed in new uint[] { 0, 2, 12345, 0x7fffffff, 0xffffffff })
        {
            port.Srand(seed);
            Oracle.Srand(seed);
            for (int i = 0; i < 100_000; i++)
                Pass(Compare("rand", $"seed {seed}, call {i}", port.Next(), Oracle.Rand()));
        }
    }

    [Fact]
    public void Strcmp_cases()
    {
        string[] words = ["", "a", "b", "ab", "abc", "player", "plat", "monster_army", "monster_dog", "ÿ", "\u0080x"];
        foreach (string a in words)
            foreach (string b in words)
            {
                byte[] x = System.Text.Encoding.Latin1.GetBytes(a), y = System.Text.Encoding.Latin1.GetBytes(b);
                Pass(Compare("strcmp", $"\"{a}\", \"{b}\"", CString.Strcmp(x, y), Oracle.Strcmp(x, y)));
            }
    }

    [Fact]
    public void Strcmp_random()
    {
        var rng = new SplitMix64(12);
        Span<byte> a = stackalloc byte[16], b = stackalloc byte[16];
        for (int i = 0; i < 1_000_000; i++)
        {
            int la = (int)(rng.Next() % 8), lb = (int)(rng.Next() % 8);
            for (int k = 0; k < la; k++) a[k] = (byte)(1 + rng.Next() % 255);   // no embedded zero
            for (int k = 0; k < lb; k++) b[k] = (rng.Next() % 2 == 0 && k < la) ? a[k] : (byte)(1 + rng.Next() % 255);
            Pass(Compare("strcmp", $"case {i}", CString.Strcmp(a[..la], b[..lb]), Oracle.Strcmp(a[..la], b[..lb])));
        }
    }
}
