// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Exact;

/// <summary>C string functions on byte strings, with the reference's pinned semantics (ref/src/edict_libc.c).</summary>
[NoAlloc(Constraints.All)]
public static class CString
{
    /// <summary>
    /// The reference's strcmp: the difference of the first differing bytes as unsigned char. A string ends at its
    /// first zero byte or at the end of the span, whichever comes first. QuakeC's <c>OP_NE_S</c> stores this value
    /// as a float, so its magnitude is part of the state, not only its sign.
    /// </summary>
    public static int Strcmp(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int i = 0;
        while (true)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x == 0 || x != y)
                return x - y;
            i++;
        }
    }
}
