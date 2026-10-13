// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// The cut-3 gate (docs/PLAN.md section C): the port's host runs the whole tape by itself — "map", every host
/// frame, the clients' messages from the tape, physics, restarts and changelevels — with no capture in the loop, and
/// after every frame its state must hash as the reference's frame record, at every spawn as its spawn record, and what
/// it sent the client since the previous record must hash as the record's messages lane. What the port takes from the
/// reference: the cvars, maxclients and the rand state at the first spawn (Host_Init and the configs ran before it),
/// from the capture of the same run. Where the run has an .emg, a difference in the messages is shown to the byte.
/// </summary>
public sealed unsafe class LoopGateTests
{
    public static IEnumerable<TheoryDataRow<string>> MapNames => VmReplay.Maps().Select(m => new TheoryDataRow<string>(m));

    /// <summary>The long runs of tools/longruns.sh (data/long, or EDICT_LONG).</summary>
    public static string LongDirectory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_LONG");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, "long") : dir;
        }
    }

    public static IEnumerable<TheoryDataRow<string>> LongMaps =>
        (Directory.Exists(LongDirectory) ? Directory.GetFiles(LongDirectory, "*.edh") : [])
        .Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).Select(m => new TheoryDataRow<string>(m));

    internal static string? FirstDivergence(string map, out int frames, out int spawns) =>
        FirstDivergence(map, Path.Combine(VmReplay.Directory, map + ".edh"), Path.Combine(VmReplay.Directory, map + ".etp"),
                        out frames, out spawns);

    /// <summary>
    /// The first difference, or null; <paramref name="frames"/> gets the number of frames that hashed alike. The
    /// inputs at the first spawn come from the corpus capture of <paramref name="map"/>.
    /// </summary>
    internal static string? FirstDivergence(string map, string edhPath, string tapePath, out int frames, out int spawns)
    {
        string capture = Path.Combine(VmReplay.Directory, map + ".ecp");
        VmReplay.SpawnInputs inputs = VmReplay.FirstSpawn(capture);
        var edh = new EdhFile(edhPath);
        string emgPath = Path.ChangeExtension(edhPath, ".emg");
        EmgFile? emg = File.Exists(emgPath) ? new EmgFile(emgPath) : null;
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        var tape = new TapeNet(tapePath);
        using var host = new Host(files, new Cvars(inputs.Cvars), inputs.MaxClients, inputs.RandSeed, tape,
                                  inputs.HeaderBytes, VmReplay.NumBuiltins);
        int spawn = 0, hashed = 0;
        string? failure = null;
        StateHash? hash = null;
        host.Spawned = sv =>
        {
            if (failure is not null)
                return;
            if (spawn >= edh.Spawns.Count)
            {
                failure = $"spawn {spawn}: the reference has no spawn record for it";
                return;
            }
            EdhFile.Spawn r = edh.Spawns[spawn++];
            hash ??= new StateHash(sv.Progs);
            StateHash.Lanes l = hash.Of(sv);
            if (sv.NumEdicts != r.NumEdicts)
                failure = $"spawn of {r.Map}: num_edicts {sv.NumEdicts}, the reference's {r.NumEdicts}";
            else
                for (int e = 0; e < r.NumEdicts && failure is null; e++)
                    if (l.Entities[e] != r.Entities[e])
                        failure = $"spawn of {r.Map}: edict {e} ({SpawnGateTests.Classname(sv, e)}) hashes differently";
            if (failure is null && l.GlobalsTyped != r.GlobalsTyped)
                failure = $"spawn of {r.Map}: typed globals lane differs";
            if (failure is null && l.Edicts != r.Edicts)
                failure = $"spawn of {r.Map}: edicts lane differs";
            if (failure is null && Xxh64.Hash(tape.Sent) != r.Messages)
                failure = $"spawn of {r.Map}: messages lane differs" + (emg is null ? "" : ": " + EmgFile.Difference(tape.Sent, emg.Spawns[spawn - 1]));
            tape.TakeSent();
        };
        host.AddText(Encoding.Latin1.GetBytes($"map {map}\n"));
        float time = host.HostFramerate();
        int total = edh.Frames;
        for (int frame = 0; frame < total && failure is null; frame++)
        {
            host.Frame(time);
            if (failure is not null)
                break;
            Server? sv = host.Sv;
            if (sv is null || !sv.Active)
                continue;
            if (!edh.FrameRecords.TryGetValue((uint)host.FrameCount, out EdhFile.Frame r))
            {
                failure = $"frame {frame}: the reference wrote no frame record {host.FrameCount}";
                break;
            }
            hash ??= new StateHash(sv.Progs);
            StateHash.Lanes l = hash.Of(sv);
            if (BitConverter.DoubleToInt64Bits(sv.Time) != BitConverter.DoubleToInt64Bits(r.Time))
                failure = $"frame {frame}: sv.time {sv.Time:R}, the reference's {r.Time:R}";
            else if (sv.NumEdicts != r.NumEdicts)
                failure = $"frame {frame}: num_edicts {sv.NumEdicts}, the reference's {r.NumEdicts}";
            else if (l.GlobalsTyped != r.GlobalsTyped)
                failure = $"frame {frame}: typed globals lane differs";
            else if (l.Edicts != r.Edicts)
                failure = $"frame {frame}: edicts lane differs";
            else if (Xxh64.Hash(tape.Sent) != r.Messages)
                failure = $"frame {frame}: messages lane differs"
                          + (emg is null ? "" : ": " + EmgFile.Difference(tape.Sent, emg.Frames[(uint)host.FrameCount]));
            else
                hashed++;
            tape.TakeSent();
        }
        frames = hashed;
        spawns = spawn;
        return failure;
    }

    /// <summary>The cut-3 gate on new tapes: ten minutes (43,200 frames) per map, every frame hashed.</summary>
    [Theory]
    [MemberData(nameof(LongMaps))]
    public void Long_run_hashes_as_the_reference_every_frame(string map)
    {
        string? d = FirstDivergence(map, Path.Combine(LongDirectory, map + ".edh"), Path.Combine(LongDirectory, map + ".etp"),
                                    out int frames, out int spawns);
        Assert.True(d is null, $"{map} (long run), after {frames} frames hashed alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{map} (long run): {frames} frames and {spawns} spawns hashed as the reference");
    }

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Loop_hashes_as_the_reference_every_frame(string map)
    {
        string? d = FirstDivergence(map, out int frames, out int spawns);
        Assert.True(d is null, $"{map}, after {frames} frames hashed alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{map}: {frames} frames and {spawns} spawns hashed as the reference");
    }
}
