// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Exact;

namespace Edict.Engine;

/// <summary>mathlib.c, as far as ported. C's float/double mix is kept literally (docs/PLAN.md A.2).</summary>
[NoAlloc(Constraints.All)]
public static class Mathlib
{
    public const int Pitch = 0, Yaw = 1, Roll = 2;

    /// <summary><c>Length</c>: the sum of squares in float, left to right; <c>sqrt</c> in double, stored to float.</summary>
    public static float Length(in Vec3 v)
    {
        float length = 0;
        length += v.X * v.X;
        length += v.Y * v.Y;
        length += v.Z * v.Z;
        return (float)Math.Sqrt(length);
    }

    /// <summary><c>anglemod</c>: <c>(360.0/65536) * ((int)(a*(65536/360.0)) &amp; 65535)</c>, in double.</summary>
    public static float Anglemod(float a) => (float)(360.0 / 65536 * (CCast.I32(a * (65536 / 360.0)) & 65535));

    /// <summary>
    /// <c>AngleVectors</c>: each angle to a float in radians (a double product), sin and cos of it in double through
    /// the pinned libm, stored to float; the vectors in float with C's operand order.
    /// </summary>
    public static void AngleVectors(in Vec3 angles, out Vec3 forward, out Vec3 right, out Vec3 up)
    {
        float angle = (float)(Vec3.At(angles, Yaw) * (Math.PI * 2 / 360));
        float sy = (float)CMath.Sin(angle);
        float cy = (float)CMath.Cos(angle);
        angle = (float)(Vec3.At(angles, Pitch) * (Math.PI * 2 / 360));
        float sp = (float)CMath.Sin(angle);
        float cp = (float)CMath.Cos(angle);
        angle = (float)(Vec3.At(angles, Roll) * (Math.PI * 2 / 360));
        float sr = (float)CMath.Sin(angle);
        float cr = (float)CMath.Cos(angle);

        forward = new Vec3(cp * cy, cp * sy, -sp);
        right = new Vec3(-1 * sr * sp * cy + -1 * cr * -sy, -1 * sr * sp * sy + -1 * cr * cy, -1 * sr * cp);
        up = new Vec3(cr * sp * cy + -sr * -sy, cr * sp * sy + -sr * cy, cr * cp);
    }
}
