// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace Edict.Exact.Tests;

/// <summary>Bitwise comparison with a first-divergence report (docs/TESTING.md R1, R5).</summary>
internal static class Differential
{
    public static ulong Bits(double d) => Unsafe.BitCast<double, ulong>(d);

    public static uint Bits(float f) => Unsafe.BitCast<float, uint>(f);

    /// <summary>Null when equal in every bit, otherwise the report for the first divergence.</summary>
    public static string? Compare(string function, string input, double port, double reference) =>
        Bits(port) == Bits(reference)
            ? null
            : $"{function}({input}): port {port:R} (0x{Bits(port):X16}), reference {reference:R} (0x{Bits(reference):X16})";

    public static string? Compare(string function, string input, int port, int reference) =>
        port == reference ? null : $"{function}({input}): port {port}, reference {reference}";

    public static string Hex(double d) => $"{d:R} (0x{Bits(d):X16})";

    public static string Hex(float f) => $"{f:R} (0x{Bits(f):X8})";

    /// <summary>Fails the test with the report, if any.</summary>
    public static void Pass(string? report)
    {
        if (report is not null)
            Assert.Fail("first divergence: " + report);
    }

    /// <summary>splitmix64, the same generator the tape generator uses.</summary>
    public struct SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public double Uniform(double lo, double hi) => lo + (hi - lo) * ((Next() >> 11) * (1.0 / 9007199254740992.0));

        public double AnyDouble() => Unsafe.BitCast<ulong, double>(Next());
    }
}
