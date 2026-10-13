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

    private static readonly SysErrorException BopsError = new("BoxOnPlaneSide:  Bad signbits");

    /// <summary>
    /// <c>BOX_ON_PLANE_SIDE</c> (mathlib.h): axial planes compare the box's extent on the axis directly, the others
    /// go to <see cref="BoxOnPlaneSide"/>. 1: in front, 2: behind, 3: crossing.
    /// </summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = "BOPS_Error (Sys_Error) for signbits outside 0..7, as in C")]
    public static unsafe int BoxOnPlaneSideMacro(in Vec3 emins, in Vec3 emaxs, MPlane* p) =>
        p->Type < 3
            ? p->Dist <= Vec3.At(emins, p->Type) ? 1 : p->Dist >= Vec3.At(emaxs, p->Type) ? 2 : 3
            : BoxOnPlaneSide(emins, emaxs, p);

    /// <summary><c>BoxOnPlaneSide</c>, general case: the box corners nearest and farthest along the normal by signbits.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = "BOPS_Error (Sys_Error) for signbits outside 0..7, as in C")]
    public static unsafe int BoxOnPlaneSide(in Vec3 emins, in Vec3 emaxs, MPlane* p)
    {
        float dist1, dist2;
        Vec3 n = p->Normal;
        switch (p->SignBits)
        {
            case 0:
                dist1 = n.X * emaxs.X + n.Y * emaxs.Y + n.Z * emaxs.Z;
                dist2 = n.X * emins.X + n.Y * emins.Y + n.Z * emins.Z;
                break;
            case 1:
                dist1 = n.X * emins.X + n.Y * emaxs.Y + n.Z * emaxs.Z;
                dist2 = n.X * emaxs.X + n.Y * emins.Y + n.Z * emins.Z;
                break;
            case 2:
                dist1 = n.X * emaxs.X + n.Y * emins.Y + n.Z * emaxs.Z;
                dist2 = n.X * emins.X + n.Y * emaxs.Y + n.Z * emins.Z;
                break;
            case 3:
                dist1 = n.X * emins.X + n.Y * emins.Y + n.Z * emaxs.Z;
                dist2 = n.X * emaxs.X + n.Y * emaxs.Y + n.Z * emins.Z;
                break;
            case 4:
                dist1 = n.X * emaxs.X + n.Y * emaxs.Y + n.Z * emins.Z;
                dist2 = n.X * emins.X + n.Y * emins.Y + n.Z * emaxs.Z;
                break;
            case 5:
                dist1 = n.X * emins.X + n.Y * emaxs.Y + n.Z * emins.Z;
                dist2 = n.X * emaxs.X + n.Y * emins.Y + n.Z * emaxs.Z;
                break;
            case 6:
                dist1 = n.X * emaxs.X + n.Y * emins.Y + n.Z * emins.Z;
                dist2 = n.X * emins.X + n.Y * emaxs.Y + n.Z * emaxs.Z;
                break;
            case 7:
                dist1 = n.X * emins.X + n.Y * emins.Y + n.Z * emins.Z;
                dist2 = n.X * emaxs.X + n.Y * emaxs.Y + n.Z * emaxs.Z;
                break;
            default:
                throw BopsError;
        }

        int sides = 0;
        if (dist1 >= p->Dist)
            sides = 1;
        if (dist2 < p->Dist)
            sides |= 2;
        return sides;
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
