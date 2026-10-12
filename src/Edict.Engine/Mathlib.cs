// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>mathlib.c, as far as ported.</summary>
[NoAlloc(Constraints.All)]
public static class Mathlib
{
    /// <summary><c>Length</c>: the sum of squares in float, left to right; <c>sqrt</c> in double, stored to float.</summary>
    public static float Length(in Vec3 v)
    {
        float length = 0;
        length += v.X * v.X;
        length += v.Y * v.Y;
        length += v.Z * v.Z;
        return (float)Math.Sqrt(length);
    }
}
