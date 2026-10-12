// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using BenchmarkDotNet.Attributes;
using Edict.Engine;
using Edict.Harness;

namespace Edict.Benchmarks;

/// <summary>
/// SV_ClipMoveToEntity over a map's whole trace corpus (inputs only), per hull check variant. Both variants pass
/// the gate on every record (tests/Edict.Engine.Tests). One operation is one trace.
/// </summary>
[MemoryDiagnoser]
public unsafe class HullTraceBenchmarks
{
    private const int Traces = 100_000;

    private struct Input
    {
        public ClipEntity Ent;
        public Vec3 Start, Mins, Maxs, End;
    }

    private TraceMap _map = null!;
    private Input[] _inputs = null!;

    [Params("start", "lq_e0m3")]
    public string Map { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _map = new TraceMap(Map);
        TraceCorpus.TraceRecord[] t = _map.Corpus.Traces;
        if (t.Length != Traces)
            throw new InvalidOperationException($"{Map}: {t.Length} traces, expected {Traces}");
        _inputs = new Input[t.Length];
        for (int i = 0; i < t.Length; i++)
            _inputs[i] = new Input { Ent = TraceMap.Entity(t[i]), Start = t[i].Start, Mins = t[i].Mins, Maxs = t[i].Maxs, End = t[i].End };
    }

    [GlobalCleanup]
    public void Cleanup() => _map.Dispose();

    private float Run<TCheck>() where TCheck : struct, IHullCheck
    {
        ClipWorld world = _map.World;
        float sum = 0;
        foreach (ref readonly Input i in _inputs.AsSpan())
            sum += world.ClipMoveToEntity<TCheck>(i.Ent, i.Start, i.Mins, i.Maxs, i.End).Fraction;
        return sum;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Traces)]
    public float Recursive() => Run<RecursiveCheck>();

    [Benchmark(OperationsPerInvoke = Traces)]
    public float Iterative() => Run<IterativeCheck>();
}
