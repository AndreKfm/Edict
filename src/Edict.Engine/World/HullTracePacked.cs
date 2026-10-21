// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine;

/// <summary>
/// <c>SV_HullPointContents</c> and <c>SV_RecursiveHullCheck</c> over the packed node layout (<see cref="PackedNode"/>):
/// clip node and plane in one 32-byte record, so a visit reads one cache line instead of two dependent loads. Same
/// nodes in the same order, same arithmetic as <see cref="RecursiveHullCheck"/> (docs/PLAN.md section B.4).
/// </summary>
public static unsafe partial class HullTrace
{
    /// <summary><c>SV_HullPointContents</c> over the packed nodes.</summary>
    public static int PackedPointContents(Hull* hull, int num, in Vec3 p)
    {
        while (num >= 0)
        {
            PackedNode* node = hull->Packed + num;
            float d;

            if (node->Type < 3)
                d = Vec3.At(p, node->Type) - node->Dist;
            else
                d = Vec3.Dot(node->Normal, p) - node->Dist;
            if (d < 0)
                num = node->Children[1];
            else
                num = node->Children[0];
        }
        return num;
    }

    /// <summary><c>SV_RecursiveHullCheck</c> over the packed nodes, the arithmetic of <see cref="RecursiveHullCheck"/>.</summary>
    public static bool PackedHullCheck(Hull* hull, int num, float p1f, float p2f, in Vec3 p1, in Vec3 p2, ref Trace trace)
    {
        // check for empty
        if (num < 0)
        {
            Leaf(num, ref trace);
            return true;        // empty
        }

        // find the point distances
        PackedNode* node = hull->Packed + num;
        float t1, t2;

        if (node->Type < 3)
        {
            t1 = Vec3.At(p1, node->Type) - node->Dist;
            t2 = Vec3.At(p2, node->Type) - node->Dist;
        }
        else
        {
            t1 = Vec3.Dot(node->Normal, p1) - node->Dist;
            t2 = Vec3.Dot(node->Normal, p2) - node->Dist;
        }

        if (t1 >= 0 && t2 >= 0)
            return PackedHullCheck(hull, node->Children[0], p1f, p2f, p1, p2, ref trace);
        if (t1 < 0 && t2 < 0)
            return PackedHullCheck(hull, node->Children[1], p1f, p2f, p1, p2, ref trace);

        // put the crosspoint DIST_EPSILON pixels on the near side
        float frac;
        if (t1 < 0)
            frac = (float)((t1 + DistEpsilon) / (t1 - t2));
        else
            frac = (float)((t1 - DistEpsilon) / (t1 - t2));
        if (frac < 0)
            frac = 0;
        if (frac > 1)
            frac = 1;

        float midf = p1f + (p2f - p1f) * frac;
        Vec3 mid = Lerp(p1, p2, frac);

        int side = t1 < 0 ? 1 : 0;

        // move up to the node
        if (!PackedHullCheck(hull, node->Children[side], p1f, midf, p1, mid, ref trace))
            return false;

        if (PackedPointContents(hull, node->Children[side ^ 1], mid) != Bsp.ContentsSolid)
            // go past the node
            return PackedHullCheck(hull, node->Children[side ^ 1], midf, p2f, mid, p2, ref trace);

        if (trace.AllSolid)
            return false;       // never got out of the solid area

        PackedImpact(hull, node, side, frac, p1f, p2f, p1, p2, mid, midf, ref trace);
        return false;
    }

    /// <summary><see cref="Impact"/> over the packed nodes: the plane is the node's.</summary>
    private static void PackedImpact(Hull* hull, PackedNode* node, int side, float frac, float p1f, float p2f, in Vec3 p1, in Vec3 p2,
                                     Vec3 mid, float midf, ref Trace trace)
    {
        if (side == 0)
        {
            trace.Plane.Normal = node->Normal;
            trace.Plane.Dist = node->Dist;
        }
        else
        {
            trace.Plane.Normal = Vec3.Subtract(default, node->Normal);   // vec3_origin - normal: +0, never -0
            trace.Plane.Dist = -node->Dist;
        }

        while (PackedPointContents(hull, hull->FirstClipNode, mid) == Bsp.ContentsSolid)
        {   // shouldn't really happen, but does occasionally
            frac = (float)(frac - 0.1);
            if (frac < 0)
            {
                trace.Fraction = midf;
                trace.EndPos = mid;
                // Con_DPrintf ("backup past 0\n"): output only, with developer set
                return;
            }
            midf = p1f + (p2f - p1f) * frac;
            mid = Lerp(p1, p2, frac);
        }

        trace.Fraction = midf;
        trace.EndPos = mid;
    }
}
