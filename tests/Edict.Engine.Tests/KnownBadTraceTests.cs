// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// docs/TESTING.md R7 for the trace gate: deliberately wrong copies of SV_RecursiveHullCheck, each run over the same
/// corpus as the real variants. A variant that matches every record means the corpus is blind there.
/// </summary>
/// <remarks>
/// The first corpus (uniform points) missed three of the variants below: the backup loop ran 5 times in 10^6
/// traces, too rarely to see <see cref="Mutation.NoBackup"/> or <see cref="Mutation.FloatBackupStep"/>. The
/// generator now chains traces as SV_FlyMove does, starts some on sloped planes, and picks 4 % of the records by
/// importance sampling so that the loop runs (ref/src/edict_traces.c). The third, DIST_EPSILON as a float, turned
/// out not to be bad: see <see cref="Float_epsilon_is_bit_identical"/>.
/// </remarks>
public sealed unsafe class KnownBadTraceTests
{
    public enum Mutation
    {
        None,
        FloatEpsilon,           // DIST_EPSILON as a float literal: the numerator of the crossing fraction in float
        DoubleFrac,             // frac declared double: midf and mid computed in double, rounded once
        UnaryMinusNormal,       // -normal instead of vec3_origin - normal: -0 instead of +0 on axial planes
        SideOnZero,             // side = (t1 <= 0) instead of (t1 < 0)
        FloatBackupStep,        // frac -= 0.1f instead of the double 0.1
        NoBackup,               // the "shouldn't really happen" backup loop left out
        MidfFromFrac,           // midf = frac instead of p1f + (p2f - p1f)*frac
        ReorderedDot,           // a2*b2 + a1*b1 + a0*b0
    }

    [ThreadStatic]
    private static Mutation _mutation;

    private readonly struct MutantCheck : IHullCheck
    {
        public static bool Check(Hull* hull, int num, float p1f, float p2f, in Vec3 p1, in Vec3 p2, ref Trace trace) =>
            Mutant(hull, num, p1f, p2f, p1, p2, ref trace);
    }

    private static float Dot(in Vec3 a, in Vec3 b) =>
        _mutation == Mutation.ReorderedDot ? a.Z * b.Z + a.Y * b.Y + a.X * b.X : Vec3.Dot(a, b);

    private static int Contents(Hull* hull, int num, in Vec3 p)
    {
        while (num >= 0)
        {
            DClipNode* node = hull->ClipNodes + num;
            MPlane* plane = hull->Planes + node->PlaneNum;
            float d = plane->Type < 3 ? Vec3.At(p, plane->Type) - plane->Dist : Dot(plane->Normal, p) - plane->Dist;
            num = d < 0 ? node->Children[1] : node->Children[0];
        }
        return num;
    }

    private static Vec3 Lerp(in Vec3 p1, in Vec3 p2, double frac) =>
        _mutation == Mutation.DoubleFrac
            ? new((float)(p1.X + frac * (p2.X - p1.X)), (float)(p1.Y + frac * (p2.Y - p1.Y)), (float)(p1.Z + frac * (p2.Z - p1.Z)))
            : Lerp(p1, p2, (float)frac);

    private static Vec3 Lerp(in Vec3 p1, in Vec3 p2, float frac) =>
        new(p1.X + frac * (p2.X - p1.X), p1.Y + frac * (p2.Y - p1.Y), p1.Z + frac * (p2.Z - p1.Z));

    private static float Midf(float p1f, float p2f, double frac) =>
        _mutation == Mutation.DoubleFrac ? (float)(p1f + (p2f - p1f) * frac) : p1f + (p2f - p1f) * (float)frac;

    // A copy of HullTrace.RecursiveHullCheck with the mutation points marked. frac is a double variable that holds
    // a float value except under DoubleFrac.
    private static bool Mutant(Hull* hull, int num, float p1f, float p2f, in Vec3 p1, in Vec3 p2, ref Trace trace)
    {
        Mutation m = _mutation;
        if (num < 0)
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
            return true;
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
            t1 = Dot(plane->Normal, p1) - plane->Dist;
            t2 = Dot(plane->Normal, p2) - plane->Dist;
        }

        if (t1 >= 0 && t2 >= 0)
            return Mutant(hull, node->Children[0], p1f, p2f, p1, p2, ref trace);
        if (t1 < 0 && t2 < 0)
            return Mutant(hull, node->Children[1], p1f, p2f, p1, p2, ref trace);

        double frac;
        if (m == Mutation.FloatEpsilon)
            frac = t1 < 0 ? (t1 + 0.03125f) / (t1 - t2) : (t1 - 0.03125f) / (t1 - t2);
        else if (m == Mutation.DoubleFrac)
            frac = t1 < 0 ? (t1 + HullTrace.DistEpsilon) / (t1 - t2) : (t1 - HullTrace.DistEpsilon) / (t1 - t2);
        else if (t1 < 0)
            frac = (float)((t1 + HullTrace.DistEpsilon) / (t1 - t2));
        else
            frac = (float)((t1 - HullTrace.DistEpsilon) / (t1 - t2));
        if (frac < 0)
            frac = 0;
        if (frac > 1)
            frac = 1;

        float midf = m == Mutation.MidfFromFrac ? (float)frac : Midf(p1f, p2f, frac);
        Vec3 mid = Lerp(p1, p2, frac);
        int side = m == Mutation.SideOnZero ? (t1 <= 0 ? 1 : 0) : (t1 < 0 ? 1 : 0);

        if (!Mutant(hull, node->Children[side], p1f, midf, p1, mid, ref trace))
            return false;
        if (Contents(hull, node->Children[side ^ 1], mid) != Bsp.ContentsSolid)
            return Mutant(hull, node->Children[side ^ 1], midf, p2f, mid, p2, ref trace);
        if (trace.AllSolid)
            return false;

        if (side == 0)
        {
            trace.Plane.Normal = plane->Normal;
            trace.Plane.Dist = plane->Dist;
        }
        else
        {
            trace.Plane.Normal = m == Mutation.UnaryMinusNormal
                ? new Vec3(-plane->Normal.X, -plane->Normal.Y, -plane->Normal.Z)
                : Vec3.Subtract(default, plane->Normal);
            trace.Plane.Dist = -plane->Dist;
        }

        while (m != Mutation.NoBackup && Contents(hull, hull->FirstClipNode, mid) == Bsp.ContentsSolid)
        {
            frac = m switch
            {
                Mutation.FloatBackupStep => (float)frac - 0.1f,
                Mutation.DoubleFrac => frac - 0.1,
                _ => (float)(frac - 0.1),
            };
            if (frac < 0)
            {
                trace.Fraction = midf;
                trace.EndPos = mid;
                return false;
            }
            midf = Midf(p1f, p2f, frac);
            mid = Lerp(p1, p2, frac);
        }
        trace.Fraction = midf;
        trace.EndPos = mid;
        return false;
    }

    /// <summary>The first divergence of the mutant over all maps, or null if it matches every record.</summary>
    private static string? Run(Mutation m)
    {
        _mutation = m;
        try
        {
            foreach (string map in TraceCorpus.Maps())
            {
                using var f = new TraceMap(map);
                string? d = TraceGateTests.FirstDivergence<MutantCheck>(f);
                if (d is not null)
                    return d;
            }
            return null;
        }
        finally
        {
            _mutation = Mutation.None;
        }
    }

    /// <summary>Unmutated, the copy must pass: otherwise the copy, not the mutation, would be what the gate detects.</summary>
    [Fact]
    public void Unmutated_copy_matches_every_record() => Assert.Null(Run(Mutation.None));

    [Theory]
    [InlineData(Mutation.DoubleFrac)]
    [InlineData(Mutation.UnaryMinusNormal)]
    [InlineData(Mutation.SideOnZero)]
    [InlineData(Mutation.FloatBackupStep)]
    [InlineData(Mutation.NoBackup)]
    [InlineData(Mutation.MidfFromFrac)]
    [InlineData(Mutation.ReorderedDot)]
    public void Known_bad_variant_is_detected(Mutation m)
    {
        string? d = Run(m);
        Assert.True(d is not null, $"R7: known-bad variant {m} matched every record: the corpus is blind there");
    }

    /// <summary>
    /// Not a known-bad variant, measured and then shown: the numerator <c>t1 -/+ DIST_EPSILON</c> moves t1 toward
    /// zero, which is exact in float for 1/32 &lt;= |t1| &lt; 2^19 (map coordinates stay within +-4096). For
    /// |t1| &lt; 1/32 it can round, but then numerator and denominator have opposite signs and frac is clamped to 0
    /// either way. Measured on the first corpus: 1.5·10^6 crossings, 2 different unclamped fractions, both clamped.
    /// </summary>
    [Fact]
    public void Float_epsilon_is_bit_identical() => Assert.Null(Run(Mutation.FloatEpsilon));
}
