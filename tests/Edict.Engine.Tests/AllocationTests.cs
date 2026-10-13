// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// P4 (docs/PLAN.md section D): the host loop allocates nothing in a frame that does not spawn a map. The port's host
/// runs a corpus tape as in the loop gate, without hashing; the managed bytes this thread allocates are measured
/// around every host frame. Frames that spawn (map, restart, changelevel: the load path) are counted apart.
/// </summary>
public sealed class AllocationTests
{
    public static IEnumerable<TheoryDataRow<string>> MapNames => VmReplay.Maps().Select(m => new TheoryDataRow<string>(m));

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Host_frames_allocate_nothing(string map)
    {
        VmReplay.SpawnInputs inputs = VmReplay.FirstSpawn(Path.Combine(VmReplay.Directory, map + ".ecp"));
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        var tape = new TapeNet(Path.Combine(VmReplay.Directory, map + ".etp"));
        using var host = new Host(files, new Cvars(inputs.Cvars), inputs.MaxClients, inputs.RandSeed, tape,
                                  inputs.HeaderBytes, VmReplay.NumBuiltins);
        bool spawned = false;
        host.Spawned = _ => spawned = true;
        host.AddText(Encoding.Latin1.GetBytes($"map {map}\n"));
        float time = host.HostFramerate();
        var allocating = new List<(int Frame, long Bytes)>();
        int spawnFrames = 0;
        long spawnBytes = 0;
        for (int frame = 0; frame < 4000; frame++)
        {
            spawned = false;
            long before = GC.GetAllocatedBytesForCurrentThread();
            host.Frame(time);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            tape.TakeSent();
            if (spawned)
            {
                spawnFrames++;
                spawnBytes += bytes;
            }
            else if (bytes != 0)
                allocating.Add((frame, bytes));
        }
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{map}: {spawnFrames} spawn frames ({spawnBytes:N0} bytes); {allocating.Count} other frames allocated" +
            (allocating.Count == 0 ? "" : ": " + string.Join(", ", allocating.Take(12).Select(a => $"{a.Frame} ({a.Bytes} B)"))));
        Assert.True(allocating.Count == 0, $"{map}: {allocating.Count} frames without a spawn allocated, the first at frame "
                                           + $"{(allocating.Count > 0 ? allocating[0].Frame : -1)} ({(allocating.Count > 0 ? allocating[0].Bytes : 0)} bytes)");
    }
}
