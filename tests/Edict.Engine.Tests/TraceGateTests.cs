// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// The phase-1 gate (docs/PLAN.md section C, cut 1): every trace of the reference corpus, run through the port's
/// SV_ClipMoveToEntity with each hull check variant, matches the reference in every field and bit, and so do the
/// world hull contents at start and end.
/// </summary>
public sealed unsafe class TraceGateTests
{
    public static IEnumerable<TheoryDataRow<string>> MapNames => TraceCorpus.Maps().Select(m => new TheoryDataRow<string>(m));

    /// <summary>Runs every record of the map; null if all match, else the report for the first divergence.</summary>
    internal static string? FirstDivergence<TCheck>(TraceMap f) where TCheck : struct, IHullCheck
    {
        TraceCorpus.TraceRecord[] traces = f.Corpus.Traces;
        for (int i = 0; i < traces.Length; i++)
        {
            ref TraceCorpus.TraceRecord t = ref traces[i];
            Trace port = f.World.ClipMoveToEntity<TCheck>(TraceMap.Entity(t), t.Start, t.Mins, t.Maxs, t.End);
            string? d = Bits.FirstDifference(port, t.Trace);
            if (d is not null)
                return $"{f.Corpus.Map} trace {i} (seed {f.Corpus.Seed}), {typeof(TCheck).Name}: {d}; inputs: {Bits.Inputs(t)}";
        }
        return null;
    }

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Recursive_hull_check_matches_every_record(string map)
    {
        using var f = new TraceMap(map);
        string? d = FirstDivergence<RecursiveCheck>(f);
        Assert.True(d is null, "first divergence: " + d);
    }

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Iterative_hull_check_matches_every_record(string map)
    {
        using var f = new TraceMap(map);
        string? d = FirstDivergence<IterativeCheck>(f);
        Assert.True(d is null, "first divergence: " + d);
    }

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Hull_point_contents_match_every_record(string map)
    {
        using var f = new TraceMap(map);
        Model* world = f.Set.World;
        TraceCorpus.TraceRecord[] traces = f.Corpus.Traces;
        for (int i = 0; i < traces.Length; i++)
        {
            ref TraceCorpus.TraceRecord t = ref traces[i];
            for (int h = 0; h < 3; h++)
            {
                Hull* hull = (Hull*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref world->Hulls[h]);
                int s = HullTrace.HullPointContents(hull, hull->FirstClipNode, t.Start);
                int e = HullTrace.HullPointContents(hull, hull->FirstClipNode, t.End);
                Assert.True(s == t.ContentsStart[h] && e == t.ContentsEnd[h],
                            $"first divergence: {map} trace {i}, hull {h}: contents at start port {s}, reference {t.ContentsStart[h]}; " +
                            $"at end port {e}, reference {t.ContentsEnd[h]}; inputs: {Bits.Inputs(t)}");
            }
        }
    }
}
