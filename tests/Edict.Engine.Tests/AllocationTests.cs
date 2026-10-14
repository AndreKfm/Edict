// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// P4 (docs/PLAN.md section D): the host loop allocates nothing in a frame that does not spawn a map. The port's host
/// runs a tape (the corpus, and the long runs if present) as in the loop gate, without hashing; the managed bytes this thread allocates are measured
/// around every host frame. Frames that spawn (map, restart, changelevel: the load path) are counted apart.
/// The class runs alone (<see cref="AllocationCollection"/>): the thread's count comes from its allocation context,
/// and a GC that another test's allocations start in the middle of a frame can retire that context and move the count
/// by its unused rest (up to 8 KB; seen once per run or so while the suite ran in parallel, in a random frame).
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class AllocationTests
{
    /// <summary>The corpus tapes, and the long runs of tools/longruns.sh where they exist.</summary>
    public static IEnumerable<TheoryDataRow<string>> Tapes =>
        VmReplay.Maps().Select(m => Path.Combine(VmReplay.Directory, m + ".etp"))
            .Concat(Directory.Exists(LongRunTests.LongDirectory) ? Directory.GetFiles(LongRunTests.LongDirectory, "*.etp").Order(StringComparer.Ordinal) : [])
            .Select(t => new TheoryDataRow<string>(Path.GetRelativePath(Path.GetDirectoryName(VmReplay.Directory)!, t)));

    [Theory]
    [MemberData(nameof(Tapes))]
    public void Host_frames_allocate_nothing(string tapePath)
    {
        string map = Path.GetFileNameWithoutExtension(tapePath);
        SpawnInputs inputs = VmReplay.FirstSpawn(Path.Combine(VmReplay.Directory, map + ".ecp"));
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        var tape = new TapeNet(Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, tapePath));
        using var host = new Host(files, new Cvars(inputs.Cvars), inputs.MaxClients, inputs.RandSeed, tape,
                                  inputs.HeaderBytes, VmReplay.NumBuiltins);
        bool spawned = false;
        host.Spawned = _ => spawned = true;
        host.AddText(Encoding.Latin1.GetBytes($"map {map}\n"));
        float time = host.HostFramerate();
        var allocating = new List<(int Frame, long Bytes)>();
        int spawnFrames = 0;
        long spawnBytes = 0;
        int frames = BitConverter.ToInt32(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, tapePath)).AsSpan(12, 4));
        for (int frame = 0; frame < frames; frame++)
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
            $"{tapePath}: {spawnFrames} spawn frames ({spawnBytes:N0} bytes); {allocating.Count} other frames allocated" +
            (allocating.Count == 0 ? "" : ": " + string.Join(", ", allocating.Take(12).Select(a => $"{a.Frame} ({a.Bytes} B)"))));
        Assert.True(allocating.Count == 0, $"{tapePath}: {allocating.Count} frames without a spawn allocated, the first at frame "
                                           + $"{(allocating.Count > 0 ? allocating[0].Frame : -1)} ({(allocating.Count > 0 ? allocating[0].Bytes : 0)} bytes)");
    }

    public static IEnumerable<TheoryDataRow<string>> ClientRuns => ClientGateTests.Runs;

    /// <summary>
    /// The client's host on every demo of the client corpus: nothing allocated in a frame but frame 0 (playdemo: the
    /// demo and its models load) and frames that ran a command (the command parser is the host's, unchecked).
    /// </summary>
    [Theory]
    [MemberData(nameof(ClientRuns))]
    public void Client_frames_allocate_nothing(string run)
    {
        var edh = new EdhFile(Path.Combine(ClientGateTests.Directory, run + ".edh"));
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using var host = new ClientHost(files, new Cvars(edh.ClientCvars), edh.ClientRandState);
        host.AddText(Encoding.Latin1.GetBytes($"playdemo edict/{run}\n"));
        float time = host.HostFramerate();
        var allocating = new List<(int Frame, long Bytes)>();
        int commandFrames = 0;
        for (int frame = 0; frame < 100_000 && !host.Ended; frame++)
        {
            int commands = host.CommandsExecuted;
            long before = GC.GetAllocatedBytesForCurrentThread();
            host.Frame(time);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (frame == 0 || host.CommandsExecuted != commands)
                commandFrames++;
            else if (bytes != 0)
                allocating.Add((frame, bytes));
        }
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{run}: {commandFrames} frames with playdemo or a command; {allocating.Count} other frames allocated" +
            (allocating.Count == 0 ? "" : ": " + string.Join(", ", allocating.Take(12).Select(a => $"{a.Frame} ({a.Bytes} B)"))));
        Assert.True(allocating.Count == 0, $"{run}: {allocating.Count} client frames without a command allocated, the first at frame "
                                           + $"{(allocating.Count > 0 ? allocating[0].Frame : -1)} ({(allocating.Count > 0 ? allocating[0].Bytes : 0)} bytes)");
    }

    public static IEnumerable<TheoryDataRow<string>> ScreenRuns => FullFrameGateTests.Runs;

    /// <summary>
    /// The client's host started in full, with the screen (the full-frame corpus' runs, everything drawn): nothing
    /// allocated in a frame but frame 0 (quake.rc, playdemo) and frames that ran a command; the screen's update, the
    /// console, the view, the particles and the renderer allocate nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(ScreenRuns))]
    public void Screen_frames_allocate_nothing(string run)
    {
        var edh = new EdhFile(Path.Combine(FullFrameGateTests.FullDirectory, run + ".edh"));
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        string cmdline = Encoding.Latin1.GetString(edh.ClientCvars.First(c => Encoding.Latin1.GetString(c.Name) == "cmdline").Value);
        string[] args = cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];
        using var host = new ClientHost(files, edh.ClientRandState, args, cmdline, withScreen: true);
        float time = float.Parse(ClientHost.HarnessFrameRate, System.Globalization.CultureInfo.InvariantCulture);
        var allocating = new List<(int Frame, long Bytes)>();
        int commandFrames = 0;
        for (int frame = 0; frame < 100_000 && !host.Ended; frame++)
        {
            int commands = host.CommandsExecuted;
            long before = GC.GetAllocatedBytesForCurrentThread();
            host.Frame(time);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (frame == 0 || host.CommandsExecuted != commands)
                commandFrames++;
            else if (bytes != 0)
                allocating.Add((frame, bytes));
        }
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{run}: {commandFrames} frames with quake.rc, playdemo or a command; {allocating.Count} other frames allocated" +
            (allocating.Count == 0 ? "" : ": " + string.Join(", ", allocating.Take(12).Select(a => $"{a.Frame} ({a.Bytes} B)"))));
        Assert.True(allocating.Count == 0, $"{run}: {allocating.Count} screen frames without a command allocated, the first at frame "
                                           + $"{(allocating.Count > 0 ? allocating[0].Frame : -1)} ({(allocating.Count > 0 ? allocating[0].Bytes : 0)} bytes)");
    }
}

/// <summary>The allocation gate's collection: run when no other test runs, so that no other thread's allocations start a GC.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationCollection
{
    public const string Name = "allocation";
}
