// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace Edict.Engine;

/// <summary><c>plane_t</c> (world.h).</summary>
public struct Plane
{
    public Vec3 Normal;
    public float Dist;
}

/// <summary><c>trace_t</c> (world.h). <see cref="Ent"/> is an edict index; -1 is C's NULL.</summary>
public struct Trace
{
    public bool AllSolid;                             // if true, plane is not valid
    public bool StartSolid;                           // if true, the initial point was in a solid area
    public bool InOpen, InWater;
    public float Fraction;                            // time completed, 1.0 = didn't hit anything
    public Vec3 EndPos;                               // final position
    public Plane Plane;                               // surface normal at impact
    public int Ent;                                   // entity the surface is on
}

/// <summary>
/// Line testing in hulls (world.c, the C paths compiled with <c>id386 == 0</c>). Two variants of
/// <c>SV_RecursiveHullCheck</c> with identical results: the literal recursion and an iterative one with an explicit
/// stack. C checks node numbers against the hull's range on every visit and calls Sys_Error; here every hull is
/// validated once when the model is loaded (<see cref="BrushLoader"/>), so the checks cannot fail and are left out.
/// </summary>
[NoAlloc(Constraints.All)]
public static unsafe class HullTrace
{
    /// <summary>1/32 epsilon to keep floating point happy. A double, as in C: the crossing fraction is computed in double.</summary>
    public const double DistEpsilon = 0.03125;

    /// <summary><c>SV_HullPointContents</c>.</summary>
    public static int HullPointContents(Hull* hull, int num, in Vec3 p)
    {
        while (num >= 0)
        {
            DClipNode* node = hull->ClipNodes + num;
            MPlane* plane = hull->Planes + node->PlaneNum;
            float d;

            if (plane->Type < 3)
                d = Vec3.At(p, plane->Type) - plane->Dist;
            else
                d = Vec3.Dot(plane->Normal, p) - plane->Dist;
            if (d < 0)
                num = node->Children[1];
            else
                num = node->Children[0];
        }
        return num;
    }

    /// <summary><c>SV_RecursiveHullCheck</c>, literally.</summary>
    public static bool RecursiveHullCheck(Hull* hull, int num, float p1f, float p2f, in Vec3 p1, in Vec3 p2, ref Trace trace)
    {
        // check for empty
        if (num < 0)
        {
            Leaf(num, ref trace);
            return true;        // empty
        }

        // find the point distances
        DClipNode* node = hull->ClipNodes + num;
        MPlane* plane = hull->Planes + node->PlaneNum;
        float t1, t2;

        if (plane->Type < 3)
        {
            t1 = Vec3.At(p1, plane->Type) - plane->Dist;
            t2 = Vec3.At(p2, plane->Type) - plane->Dist;
        }
        else
        {
            t1 = Vec3.Dot(plane->Normal, p1) - plane->Dist;
            t2 = Vec3.Dot(plane->Normal, p2) - plane->Dist;
        }

        if (t1 >= 0 && t2 >= 0)
            return RecursiveHullCheck(hull, node->Children[0], p1f, p2f, p1, p2, ref trace);
        if (t1 < 0 && t2 < 0)
            return RecursiveHullCheck(hull, node->Children[1], p1f, p2f, p1, p2, ref trace);

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
        if (!RecursiveHullCheck(hull, node->Children[side], p1f, midf, p1, mid, ref trace))
            return false;

        if (HullPointContents(hull, node->Children[side ^ 1], mid) != Bsp.ContentsSolid)
            // go past the node
            return RecursiveHullCheck(hull, node->Children[side ^ 1], midf, p2f, mid, p2, ref trace);

        if (trace.AllSolid)
            return false;       // never got out of the solid area

        Impact(hull, plane, side, frac, p1f, p2f, p1, p2, mid, midf, ref trace);
        return false;
    }

    // The frame of a crossing whose near side is being traced.
    private struct Frame
    {
        public int Num, Side;
        public float Frac, P1f, P2f, Midf;
        public Vec3 P1, P2, Mid;
    }

    private const int StackDepth = 64;

    [InlineArray(StackDepth)]
    private struct FrameStack
    {
        private Frame _element0;
    }

    /// <summary>
    /// <c>SV_RecursiveHullCheck</c> with an explicit stack: the two calls in tail position become jumps, the near-side
    /// call pushes a frame. Visits nodes in the same order and computes the same values as the recursion. Deeper
    /// than <see cref="StackDepth"/> crossings, the rest of the subtree is handed to <see cref="RecursiveHullCheck"/>.
    /// </summary>
    public static bool IterativeHullCheck(Hull* hull, int num, float p1f, float p2f, in Vec3 p1In, in Vec3 p2In, ref Trace trace)
    {
        FrameStack stack = default;
        int depth = 0;
        Vec3 p1 = p1In, p2 = p2In;
        bool result;

    descend:
        // evaluate the call (hull, num, p1f, p2f, p1, p2) until it returns or traces a near side
        while (true)
        {
            if (num < 0)
            {
                Leaf(num, ref trace);
                result = true;
                break;
            }

            DClipNode* node = hull->ClipNodes + num;
            MPlane* plane = hull->Planes + node->PlaneNum;
            float t1, t2;

            if (plane->Type < 3)
            {
                t1 = Vec3.At(p1, plane->Type) - plane->Dist;
                t2 = Vec3.At(p2, plane->Type) - plane->Dist;
            }
            else
            {
                t1 = Vec3.Dot(plane->Normal, p1) - plane->Dist;
                t2 = Vec3.Dot(plane->Normal, p2) - plane->Dist;
            }

            if (t1 >= 0 && t2 >= 0)
            {
                num = node->Children[0];
                continue;
            }
            if (t1 < 0 && t2 < 0)
            {
                num = node->Children[1];
                continue;
            }

            if (depth == StackDepth)
            {
                result = RecursiveHullCheck(hull, num, p1f, p2f, p1, p2, ref trace);
                break;
            }

            float frac;
            if (t1 < 0)
                frac = (float)((t1 + DistEpsilon) / (t1 - t2));
            else
                frac = (float)((t1 - DistEpsilon) / (t1 - t2));
            if (frac < 0)
                frac = 0;
            if (frac > 1)
                frac = 1;

            ref Frame f = ref stack[depth++];
            f.Num = num;
            f.Side = t1 < 0 ? 1 : 0;
            f.Frac = frac;
            f.P1f = p1f;
            f.P2f = p2f;
            f.Midf = p1f + (p2f - p1f) * frac;
            f.P1 = p1;
            f.P2 = p2;
            f.Mid = Lerp(p1, p2, frac);

            // move up to the node
            num = node->Children[f.Side];
            p2f = f.Midf;
            p2 = f.Mid;
        }

        // return result to the frame that traced this near side
        while (depth > 0)
        {
            ref Frame f = ref stack[--depth];
            if (!result)
                continue;

            DClipNode* node = hull->ClipNodes + f.Num;
            int far = node->Children[f.Side ^ 1];
            if (HullPointContents(hull, far, f.Mid) != Bsp.ContentsSolid)
            {   // go past the node: this frame's result is that call's result
                num = far;
                p1f = f.Midf;
                p2f = f.P2f;
                p1 = f.Mid;
                p2 = f.P2;
                goto descend;
            }

            result = false;
            if (trace.AllSolid)
                continue;       // never got out of the solid area
            Impact(hull, hull->Planes + node->PlaneNum, f.Side, f.Frac, f.P1f, f.P2f, f.P1, f.P2, f.Mid, f.Midf, ref trace);
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Leaf(int num, ref Trace trace)
    {
        if (num != Bsp.ContentsSolid)
        {
            trace.AllSolid = false;
            if (num == Bsp.ContentsEmpty)
                trace.InOpen = true;
            else
                trace.InWater = true;
        }
        else
            trace.StartSolid = true;
    }

    /// <summary><c>mid[i] = p1[i] + frac*(p2[i] - p1[i])</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vec3 Lerp(in Vec3 p1, in Vec3 p2, float frac) =>
        new(p1.X + frac * (p2.X - p1.X), p1.Y + frac * (p2.Y - p1.Y), p1.Z + frac * (p2.Z - p1.Z));

    /// <summary>The other side of the node is solid: this is the impact point.</summary>
    private static void Impact(Hull* hull, MPlane* plane, int side, float frac, float p1f, float p2f, in Vec3 p1, in Vec3 p2,
                               Vec3 mid, float midf, ref Trace trace)
    {
        if (side == 0)
        {
            trace.Plane.Normal = plane->Normal;
            trace.Plane.Dist = plane->Dist;
        }
        else
        {
            trace.Plane.Normal = Vec3.Subtract(default, plane->Normal);   // vec3_origin - normal: +0, never -0
            trace.Plane.Dist = -plane->Dist;
        }

        while (HullPointContents(hull, hull->FirstClipNode, mid) == Bsp.ContentsSolid)
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
