// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Edict.Engine;

/// <summary>
/// C's <c>vec3_t</c> (<c>float[3]</c>). The helpers are mathlib.h's macros, operation for operation: per component,
/// left to right, in float. No operators, so that every float expression in ported code reads like its C original.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[NoAlloc(Constraints.All)]
public struct Vec3(float x, float y, float z)
{
    public float X = x, Y = y, Z = z;

    /// <summary><c>v[i]</c> for i in 0..2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float At(in Vec3 v, int i) => Unsafe.Add(ref Unsafe.AsRef(in v.X), i);

    /// <summary><c>DotProduct (a, b)</c>: <c>a[0]*b[0] + a[1]*b[1] + a[2]*b[2]</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(in Vec3 a, in Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary><c>VectorSubtract (a, b, c)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 Subtract(in Vec3 a, in Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary><c>VectorAdd (a, b, c)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 Add(in Vec3 a, in Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
}
