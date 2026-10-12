// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Edict.Exact;

/// <summary>
/// The reference's string-to-number conversions. Strings are C strings: they end at the first zero byte or at the
/// end of the span.
/// </summary>
[NoAlloc(Constraints.All)]
public static class CAtof
{
    private static byte At(ReadOnlySpan<byte> s, int i) => i < s.Length ? s[i] : (byte)0;

    private static bool IsDigit(byte c) => c >= '0' && c <= '9';

    private static int HexValue(byte c) =>
        c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    private static byte Lower(byte c) => c >= 'A' && c <= 'Z' ? (byte)(c + 32) : c;

    private static bool MatchesLower(ReadOnlySpan<byte> s, int i, ReadOnlySpan<byte> word)
    {
        for (int k = 0; k < word.Length; k++)
            if (Lower(At(s, i + k)) != word[k])
                return false;
        return true;
    }

    /// <summary>
    /// The reference's atof (ref/src/edict_libc.c): C strtod in the C locale, then NaN pinned to
    /// 0x7ff8000000000000 with the parsed sign. Decimal and hexadecimal forms, "inf", "infinity", "nan",
    /// "nan(chars)"; leading white space; trailing text ignored; no conversion gives +0.0.
    /// ED_ParseEpair stores the result in a float: <c>(float)Atof (s)</c> (double rounding, as in C).
    /// </summary>
    public static double Atof(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (At(s, i) is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r')
            i++;
        bool negative = false;
        if (At(s, i) is (byte)'+' or (byte)'-')
            negative = At(s, i++) == '-';

        double magnitude;
        if (MatchesLower(s, i, "inf"u8))
            magnitude = double.PositiveInfinity;
        else if (MatchesLower(s, i, "nan"u8))
        {
            ulong nan = (negative ? 0x8000000000000000UL : 0) | 0x7ff8000000000000UL;
            return Unsafe.BitCast<ulong, double>(nan);
        }
        else if (At(s, i) == '0' && Lower(At(s, i + 1)) == 'x'
                 && (HexValue(At(s, i + 2)) >= 0 || (At(s, i + 2) == '.' && HexValue(At(s, i + 3)) >= 0)))
            magnitude = Hex(s, i + 2);
        else
        {
            int start = i;
            int digits = 0;
            while (IsDigit(At(s, i))) { i++; digits++; }
            if (At(s, i) == '.')
            {
                i++;
                while (IsDigit(At(s, i))) { i++; digits++; }
            }
            if (digits == 0)
                return 0.0;     // no conversion: strtod returns +0.0 whatever the sign
            if (Lower(At(s, i)) == 'e')
            {
                int j = i + 1;
                if (At(s, j) is (byte)'+' or (byte)'-')
                    j++;
                if (IsDigit(At(s, j)))
                {
                    while (IsDigit(At(s, j)))
                        j++;
                    i = j;
                }
            }
            // the text is validated above, so TryParse succeeds; .NET parses decimal text correctly rounded (IEEE 754)
            double.TryParse(s[start..i], NumberStyles.Float, NumberFormatInfo.InvariantInfo, out magnitude);
        }
        return negative ? -magnitude : magnitude;
    }

    // Hexadecimal significand with optional binary exponent, correctly rounded (round half to even).
    private static double Hex(ReadOnlySpan<byte> s, int i)
    {
        ulong mantissa = 0;
        long exponent = 0;
        bool sticky = false, point = false;
        while (true)
        {
            byte c = At(s, i);
            if (c == '.' && !point)
            {
                point = true;
                i++;
                continue;
            }
            int v = HexValue(c);
            if (v < 0)
                break;
            if (mantissa >> 56 == 0)
            {
                mantissa = mantissa * 16 + (uint)v;
                if (point)
                    exponent -= 4;
            }
            else
            {
                sticky |= v != 0;
                if (!point)
                    exponent += 4;
            }
            i++;
        }
        if (Lower(At(s, i)) == 'p')
        {
            int j = i + 1;
            bool negativeExponent = false;
            if (At(s, j) is (byte)'+' or (byte)'-')
                negativeExponent = At(s, j++) == '-';
            if (IsDigit(At(s, j)))
            {
                long e = 0;
                while (IsDigit(At(s, j)))
                {
                    if (e < 100_000)
                        e = e * 10 + (At(s, j) - '0');
                    j++;
                }
                exponent += negativeExponent ? -e : e;
            }
        }
        if (mantissa == 0)
            return 0.0;

        // value = mantissa * 2^exponent, mantissa normalized to [2^63, 2^64)
        int lz = BitOperations.LeadingZeroCount(mantissa);
        mantissa <<= lz;
        exponent -= lz;
        long unbiased = exponent + 63;
        if (unbiased > 1023)
            return double.PositiveInfinity;
        int keep = unbiased >= -1022 ? 53 : 53 - (int)(-1022 - unbiased);     // significant bits that fit
        if (keep <= 0)
        {
            // below half the smallest subnormal, or exactly half (ties to even: zero), or above half
            bool aboveHalf = keep == 0 && (mantissa > 0x8000000000000000UL || (mantissa == 0x8000000000000000UL && sticky));
            return aboveHalf ? double.Epsilon : 0.0;
        }
        int drop = 64 - keep;
        ulong kept = mantissa >> drop;
        ulong rest = mantissa & ((1UL << drop) - 1);
        ulong half = 1UL << (drop - 1);
        if (rest > half || (rest == half && (sticky || (kept & 1) == 1)))
            kept++;
        return Math.ScaleB((double)kept, (int)(exponent + drop));
    }

    /// <summary>
    /// Q_atof (common.c), operation for operation: optional '-', "0x" hexadecimal, "'c'" character, or decimal with
    /// the last '.' marking the decimal point. Each digit is added as <c>(val*10 + c) - '0'</c> in double, each
    /// decimal place removed by its own division by 10; the result is converted to float.
    /// </summary>
    public static float QAtof(ReadOnlySpan<byte> str)
    {
        int i = 0;
        int sign = 1;
        if (At(str, 0) == '-')
        {
            sign = -1;
            i++;
        }
        double val = 0;
        int c;

        if (At(str, i) == '0' && (At(str, i + 1) == 'x' || At(str, i + 1) == 'X'))
        {
            i += 2;
            while (true)
            {
                c = At(str, i++);
                if (c >= '0' && c <= '9')
                    val = val * 16 + c - '0';
                else if (c >= 'a' && c <= 'f')
                    val = val * 16 + c - 'a' + 10;
                else if (c >= 'A' && c <= 'F')
                    val = val * 16 + c - 'A' + 10;
                else
                    return (float)(val * sign);
            }
        }

        if (At(str, i) == '\'')
            return sign * (sbyte)At(str, i + 1);    // char is signed on the reference platforms

        int decimalPoint = -1, total = 0;
        while (true)
        {
            c = At(str, i++);
            if (c == '.')
            {
                decimalPoint = total;
                continue;
            }
            if (c < '0' || c > '9')
                break;
            val = val * 10 + c - '0';
            total++;
        }
        if (decimalPoint == -1)
            return (float)(val * sign);
        while (total > decimalPoint)
        {
            val /= 10;
            total--;
        }
        return (float)(val * sign);
    }
}
