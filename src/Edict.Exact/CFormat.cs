// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace Edict.Exact;

/// <summary>
/// The reference's formatting of numbers into QuakeC strings (PF_ftos, PF_vtos), written into caller-provided
/// buffers. Finite values follow C's printf on the exact binary value (round half to even on the last digit, as
/// glibc and UCRT do); NaN and infinities follow the reference's pinned output (ref/src/edict_libc.c).
/// </summary>
[NoAlloc(Constraints.All)]
public static class CFormat
{
    /// <summary>Longest output of <see cref="Ftos"/>: "-" + 39 integer digits + ".0", rounded up.</summary>
    public const int MaxFtosLength = 48;

    /// <summary>Longest output of <see cref="Vtos"/>.</summary>
    public const int MaxVtosLength = 3 * MaxFtosLength + 4;

    /// <summary>
    /// PF_ftos: <c>v == (int)v ? "%d" : "%5.1f"</c>, with the reference's cast (<see cref="CCast.I32(float)"/>).
    /// <paramref name="dest"/> must hold <see cref="MaxFtosLength"/> bytes; returns the length.
    /// </summary>
    public static int Ftos(float v, Span<byte> dest)
    {
        int i = CCast.I32(v);
        return v == (float)i ? Int(i, dest) : Fixed5_1(v, dest);
    }

    /// <summary>PF_vtos: <c>"'%5.1f %5.1f %5.1f'"</c>. <paramref name="dest"/> must hold <see cref="MaxVtosLength"/> bytes.</summary>
    public static int Vtos(float x, float y, float z, Span<byte> dest)
    {
        int n = 0;
        dest[n++] = (byte)'\'';
        n += Fixed5_1(x, dest[n..]);
        dest[n++] = (byte)' ';
        n += Fixed5_1(y, dest[n..]);
        dest[n++] = (byte)' ';
        n += Fixed5_1(z, dest[n..]);
        dest[n++] = (byte)'\'';
        return n;
    }

    /// <summary>C's <c>"%d"</c>.</summary>
    public static int Int(int value, Span<byte> dest)
    {
        int n = 0;
        uint magnitude = (uint)value;
        if (value < 0)
        {
            dest[n++] = (byte)'-';
            magnitude = 0u - (uint)value;
        }
        return n + UInt(magnitude, dest[n..]);
    }

    /// <summary>C's <c>"%5.1f"</c> of a float (promoted to double, which is exact).</summary>
    public static int Fixed5_1(float value, Span<byte> dest)
    {
        Span<byte> body = stackalloc byte[MaxFtosLength];
        uint bits = Unsafe.BitCast<float, uint>(value);
        bool negative = (bits >> 31) != 0;
        int exponent = (int)((bits >> 23) & 0xff);
        uint mantissa = bits & 0x7fffff;
        int n = 0;

        if (negative && !(exponent == 0xff && mantissa != 0))
            body[n++] = (byte)'-';
        if (exponent == 0xff)
        {
            // pinned: "inf" with its sign, "nan" without one (docs/adr/D8), padded like any other conversion
            body[n++] = mantissa != 0 ? (byte)'n' : (byte)'i';
            body[n++] = mantissa != 0 ? (byte)'a' : (byte)'n';
            body[n++] = mantissa != 0 ? (byte)'n' : (byte)'f';
            return Pad(body[..n], 5, dest);
        }

        uint m = exponent == 0 ? mantissa : mantissa | 0x800000;
        int e = exponent == 0 ? -149 : exponent - 150;     // value = m * 2^e exactly
        int digit;
        if (e >= 0)
        {
            n += ShiftedToDecimal(m, e, body[n..]);
            digit = 0;
        }
        else
        {
            // integer part m >> k, first decimal of the fraction, rounded half to even on the exact remainder
            int k = -e;
            uint integer = 0;
            bool up = false;
            digit = 0;
            if (k <= 60)
            {
                ulong fraction = k < 32 ? m & ((1u << k) - 1) : m;
                integer = k < 32 ? m >> k : 0;
                ulong scaled = fraction * 10;
                digit = (int)(scaled >> k);
                ulong twice = (scaled & ((1UL << k) - 1)) << 1, unit = 1UL << k;
                up = twice > unit || (twice == unit && (digit & 1) == 1);
            }
            // k > 60: value * 10 < 2^28 * 2^-61, far below half a tenth: integer 0, digit 0, no rounding up
            if (up && ++digit == 10)
            {
                digit = 0;
                integer++;
            }
            n += UInt(integer, body[n..]);
        }
        body[n++] = (byte)'.';
        body[n++] = (byte)('0' + digit);
        return Pad(body[..n], 5, dest);
    }

    private static int Pad(ReadOnlySpan<byte> body, int width, Span<byte> dest)
    {
        int spaces = width > body.Length ? width - body.Length : 0;
        dest[..spaces].Fill((byte)' ');
        body.CopyTo(dest[spaces..]);
        return spaces + body.Length;
    }

    private static int UInt(uint value, Span<byte> dest)
    {
        Span<byte> digits = stackalloc byte[10];
        int count = 0;
        do
        {
            digits[count++] = (byte)('0' + value % 10);
            value /= 10;
        } while (value != 0);
        for (int i = 0; i < count; i++)
            dest[i] = digits[count - 1 - i];
        return count;
    }

    // m * 2^e (m < 2^24, e <= 104, so below 2^128: at most 39 digits) in base-10^9 limbs, then printed.
    private static int ShiftedToDecimal(uint m, int e, Span<byte> dest)
    {
        const uint Base = 1_000_000_000;
        Span<uint> limbs = stackalloc uint[5];
        limbs[0] = m % Base;
        limbs[1] = m / Base;
        int used = limbs[1] != 0 ? 2 : 1;
        while (e > 0)
        {
            int s = e > 29 ? 29 : e;
            e -= s;
            ulong carry = 0;
            for (int i = 0; i < used; i++)
            {
                ulong t = ((ulong)limbs[i] << s) + carry;
                limbs[i] = (uint)(t % Base);
                carry = t / Base;
            }
            while (carry != 0)
            {
                limbs[used++] = (uint)(carry % Base);
                carry /= Base;
            }
        }
        int n = UInt(limbs[used - 1], dest);
        for (int i = used - 2; i >= 0; i--)
        {
            uint limb = limbs[i];
            for (int d = 8; d >= 0; d--)
            {
                dest[n + d] = (byte)('0' + limb % 10);
                limb /= 10;
            }
            n += 9;
        }
        return n;
    }
}
