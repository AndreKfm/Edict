// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Edict.Engine.Tests.VmOpcodeFuzz;

namespace Edict.Engine.Tests;

/// <summary>
/// Level 1 for BOX_ON_PLANE_SIDE / BoxOnPlaneSide (mathlib): the port's against the reference's through the oracle,
/// on axial and sloped planes, random boxes, and planes through a box corner, where the comparisons with dist are
/// exact equalities (a game rarely puts a box corner exactly on a sloped plane; the replay could not see a strict
/// comparison there).
/// </summary>
[Collection(OracleCollection.Name)]
public sealed unsafe class BoxOnPlaneSideTests
{
    private static readonly nint Lib = NativeLibrary.Load(Environment.GetEnvironmentVariable("EDICT_ORACLE")
        ?? throw new InvalidOperationException("EDICT_ORACLE is not set (docs/TESTING.md)"));
    private static readonly delegate* unmanaged<float*, float*, float*, float, int, int> BopsFn =
        (delegate* unmanaged<float*, float*, float*, float, int, int>)NativeLibrary.GetExport(Lib, "EdictOracle_BoxOnPlaneSide");

    private static readonly Vec3[] NiceNormals =
    [
        new(0.6f, 0.8f, 0), new(0.8f, 0, 0.6f), new(0, 0.6f, 0.8f), new(0.70710677f, 0.70710677f, 0),
        new(0.70710677f, 0, 0.70710677f), new(0.57735026f, 0.57735026f, 0.57735026f), new(0.4472136f, 0.8944272f, 0),
    ];

    /// <summary>C's dot product order: n0*c0 + n1*c1 + n2*c2 in float.</summary>
    private static float Dot(Vec3 n, Vec3 c) => n.X * c.X + n.Y * c.Y + n.Z * c.Z;

    internal static string? FirstDivergence(int count = 2_000_000)
    {
        var rng = new Rng(11);
        float Coord() => rng.Below(3) switch
        {
            0 => rng.Below(8192) - 4096,
            1 => (rng.Below(65536) - 32768) / 8f,
            _ => BitConverter.UInt32BitsToSingle(rng.Float()),
        };

        for (int n = 0; n < count; n++)
        {
            // the plane
            int type;
            Vec3 normal;
            int kind = rng.Below(4);
            if (kind == 0)
            {
                type = rng.Below(3);
                normal = default;
                Unsafe.Add(ref normal.X, type) = rng.Below(2) == 0 ? 1 : -1;
            }
            else
            {
                type = 3 + rng.Below(3);
                if (kind == 1)
                {
                    normal = NiceNormals[rng.Below(NiceNormals.Length)];
                    normal = new Vec3(rng.Below(2) == 0 ? normal.X : -normal.X, rng.Below(2) == 0 ? normal.Y : -normal.Y,
                                      rng.Below(2) == 0 ? normal.Z : -normal.Z);
                    if (rng.Below(2) == 0)
                        normal = new Vec3(normal.Z, normal.X, normal.Y);
                }
                else
                {
                    var v = new Vec3((float)((rng.Next() >> 11) * (1.0 / 9007199254740992.0) * 2 - 1),
                                     (float)((rng.Next() >> 11) * (1.0 / 9007199254740992.0) * 2 - 1),
                                     (float)((rng.Next() >> 11) * (1.0 / 9007199254740992.0) * 2 - 1));
                    float len = Mathlib.Length(v);
                    normal = kind == 2 && len > 0 ? new Vec3(v.X / len, v.Y / len, v.Z / len)
                           : new Vec3(BitConverter.UInt32BitsToSingle(rng.Float()), BitConverter.UInt32BitsToSingle(rng.Float()), BitConverter.UInt32BitsToSingle(rng.Float()));
                }
            }

            // the box
            Vec3 a = new(Coord(), Coord(), Coord()), b = new(Coord(), Coord(), Coord());
            Vec3 mins = new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));
            Vec3 maxs = new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));
            if (rng.Below(8) == 0)
                maxs = mins;

            // dist: anywhere, or exactly through the corner dist1 or dist2 uses, or on the axial extent
            Vec3 near = new(normal.X < 0 ? mins.X : maxs.X, normal.Y < 0 ? mins.Y : maxs.Y, normal.Z < 0 ? mins.Z : maxs.Z);
            Vec3 far = new(normal.X < 0 ? maxs.X : mins.X, normal.Y < 0 ? maxs.Y : mins.Y, normal.Z < 0 ? maxs.Z : mins.Z);
            float dist = rng.Below(5) switch
            {
                0 => Dot(normal, near),
                1 => Dot(normal, far),
                2 => type < 3 ? Vec3.At(rng.Below(2) == 0 ? mins : maxs, type) : Dot(normal, near),
                3 => BitConverter.UInt32BitsToSingle(rng.Float()),
                _ => Coord(),
            };

            var plane = new MPlane { Normal = normal, Dist = dist, Type = (byte)type };
            for (int j = 0; j < 3; j++)
                if (Vec3.At(normal, j) < 0)
                    plane.SignBits |= (byte)(1 << j);

            int port = Mathlib.BoxOnPlaneSideMacro(mins, maxs, &plane);
            int reference = BopsFn((float*)&mins, (float*)&maxs, (float*)&normal, dist, type);
            if (port != reference)
                return $"case {n}: box {Hex(mins)}..{Hex(maxs)}, normal {Hex(normal)}, dist {VmOpcodeFuzz.Hex(BitConverter.SingleToUInt32Bits(dist))}, type {type}: port {port}, reference {reference}";
        }
        return null;
    }

    private static string Hex(Vec3 v) =>
        $"({VmOpcodeFuzz.Hex(BitConverter.SingleToUInt32Bits(v.X))}, {VmOpcodeFuzz.Hex(BitConverter.SingleToUInt32Bits(v.Y))}, {VmOpcodeFuzz.Hex(BitConverter.SingleToUInt32Bits(v.Z))})";

    [Fact]
    public void Matches_the_reference_on_planes_through_box_corners()
    {
        string? d = FirstDivergence();
        Assert.True(d is null, "first divergence: " + d);
    }
}
