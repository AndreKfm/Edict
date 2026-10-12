// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Exact;

/// <summary>
/// C's float-to-int conversions as the reference compiles them: with -fno-strict-float-cast-overflow (ref/build.sh)
/// clang saturates out-of-range values to the target range and converts NaN to 0. C itself leaves these cases
/// undefined, and without the flag clang 18 and clang 23 produced different results for the same line
/// (docs/PLAN.md, section A.2). Written out explicitly instead of relying on the .NET cast, whose semantics changed
/// in .NET 9 (int, long) and .NET 11 (8- and 16-bit targets). Simulation code converts only through these methods.
/// </summary>
[NoAlloc(Constraints.All)]
public static class CCast
{
    /// <summary><c>(int)f</c> for a C <c>float</c>: truncation, saturation, NaN to 0.</summary>
    public static int I32(float f) =>
        f >= 2147483648f ? int.MaxValue : f >= -2147483648f ? (int)f : float.IsNaN(f) ? 0 : int.MinValue;

    /// <summary><c>(int)d</c> for a C <c>double</c>: truncation, saturation, NaN to 0.</summary>
    public static int I32(double d) =>
        d >= 2147483648.0 ? int.MaxValue : d > -2147483649.0 ? (int)d : double.IsNaN(d) ? 0 : int.MinValue;
}
