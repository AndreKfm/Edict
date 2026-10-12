// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Exact;

/// <summary>
/// C's float-to-int conversions as the x86-64 reference emits them (cvttss2si / cvttsd2si): truncation toward zero,
/// and 0x80000000 for NaN and for values outside the int range. C leaves those cases undefined; .NET 9+ saturates
/// (and .NET 11 also for 8- and 16-bit targets), so a plain cast differs exactly where the reference is
/// least predictable. Simulation code converts only through these methods (docs/PLAN.md §A.2).
/// </summary>
[NoAlloc(Constraints.All)]
public static class CCast
{
    /// <summary><c>(int)f</c> for a C <c>float</c>.</summary>
    public static int I32(float f) => f >= -2147483648f && f < 2147483648f ? (int)f : int.MinValue;

    /// <summary><c>(int)d</c> for a C <c>double</c>.</summary>
    public static int I32(double d) => d > -2147483649.0 && d < 2147483648.0 ? (int)d : int.MinValue;
}
