// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using BenchmarkDotNet.Attributes;
using Edict.Engine;
using Edict.Harness;

namespace Edict.Benchmarks;

/// <summary>
/// The port's host loop on a tape, as the loop gate runs it (tests/Edict.Engine.Tests, LoopGateTests) but without
/// hashing: one operation is one host frame, every frame of the tape after frame 0, which runs "map" and spawns. The
/// tapes do not restart, so no spawn is timed; every timed frame allocates nothing (AllocationTests). The reference's
/// time on the same frames: edict_ref -edictframetime (ref/src/edict_sys.c). The ten-minute run of lq_e0m3
/// (tools/longruns.sh) is the case to compare with the reference, whose clock () is too coarse for 4,000 frames.
/// </summary>
[MemoryDiagnoser]
[InvocationCount(1)]
public class FrameBenchmarks
{
    private int _frames;

    private QFileSystem _files = null!;
    private SpawnInputs _inputs = null!;
    private Host? _host;
    private TapeNet _tape = null!;
    private float _time;

    /// <summary>A tape as "directory/map": the long runs in data/long, the corpus in data/captures.</summary>
    [Params("long/lq_e0m3", "captures/lq_e0m3", "captures/start")]
    public string Tape { get; set; } = "";

    private string Map => Path.GetFileName(Tape);

    private string TapePath => Path.Combine(Path.GetDirectoryName(CaptureInputs.Directory)!, Tape + ".etp");

    [GlobalSetup]
    public void Setup()
    {
        _files = new QFileSystem(TraceCorpus.DataDirectory);
        _inputs = CaptureInputs.FirstSpawn(Path.Combine(CaptureInputs.Directory, Map + ".ecp"));
        using FileStream f = File.OpenRead(TapePath);
        Span<byte> header = stackalloc byte[16];
        f.ReadExactly(header);
        _frames = BitConverter.ToInt32(header[12..]) - 1;    // the tape's frame count, less frame 0
    }

    /// <summary>A fresh host, and frame 0, which runs "map" and spawns.</summary>
    [IterationSetup]
    public void Spawn()
    {
        _host?.Dispose();
        _tape = new TapeNet(TapePath);
        _host = new Host(_files, new Cvars(_inputs.Cvars), _inputs.MaxClients, _inputs.RandSeed, _tape, _inputs.HeaderBytes,
                         numBuiltins: 79);
        _host.AddText(Encoding.Latin1.GetBytes($"map {Map}\n"));
        _time = _host.HostFramerate();
        _host.Frame(_time);
        _tape.TakeSent();
    }

    [Benchmark]
    public int HostFrame()
    {
        Host host = _host!;
        for (int i = 0; i < _frames; i++)
        {
            host.Frame(_time);
            _tape.TakeSent();
        }
        return host.FrameCount;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _host?.Dispose();
        _files.Dispose();
    }
}
