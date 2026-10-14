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
            int commands = host.Cmd.Executed;
            long before = GC.GetAllocatedBytesForCurrentThread();
            host.Frame(time);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (frame == 0 || host.Cmd.Executed != commands)
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

    private sealed class CirclingMouse : IMouse
    {
        private int _n;
        public bool Active => true;

        public void TakeMotion(out int mx, out int my)
        {
            mx = _n % 7 - 3;
            my = _n++ % 5 - 2;
        }
    }

    /// <summary>
    /// A player's game (<see cref="Host.CreateGame"/>, P6 step 6): after the spawn, frames with a key held (+forward,
    /// +attack), the mouse moving under +mlook and a weapon impulse allocate nothing but those that ran a command;
    /// with the sound (step 8) its mix, the sounds it starts and their first loads too.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Game_frames_allocate_nothing(bool sound)
    {
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using Host host = Host.CreateGame(files, ["+map", "start"], sound: sound);
        host.Cl!.Mouse = new CirclingMouse();
        for (int i = 0; i < 200; i++)
            host.Frame(0.014f);
        host.Cmd.ExecuteString("+mlook");
        Screen s = host.Screen!;
        var allocating = new List<(int Frame, long Bytes)>();
        for (int frame = 0; frame < 2000; frame++)
        {
            int key = frame % 400 < 200 ? 'w' : Screen.KMouse1;
            if (frame % 200 == 0)
                s.KeyEvent(key, true);
            if (frame % 200 == 150)
                s.KeyEvent(key, false);
            int commands = host.Cmd.Executed;
            long before = GC.GetAllocatedBytesForCurrentThread();
            host.Frame(0.014f);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (host.Cmd.Executed == commands && bytes != 0)
                allocating.Add((frame, bytes));
        }
        Assert.True(allocating.Count == 0, $"{allocating.Count} game frames without a command allocated, the first at frame "
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
    public void Screen_frames_allocate_nothing(string run) => ScreenFramesAllocateNothing(FullFrameGateTests.FullDirectory, run, false);

    public static IEnumerable<TheoryDataRow<string>> SoundRuns => SoundGateTests.Runs;

    /// <summary>The sound corpus' runs with the sound (P6 step 8): the mix, the sounds started and their first loads allocate nothing either.</summary>
    [Theory]
    [MemberData(nameof(SoundRuns))]
    public void Sound_frames_allocate_nothing(string run) => ScreenFramesAllocateNothing(SoundGateTests.SoundDirectory, run, true);

    private static void ScreenFramesAllocateNothing(string dir, string run, bool sound)
    {
        var edh = new EdhFile(Path.Combine(dir, run + ".edh"));
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        string cmdline = Encoding.Latin1.GetString(edh.ClientCvars.First(c => Encoding.Latin1.GetString(c.Name) == "cmdline").Value);
        string[] args = cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];
        using var host = new ClientHost(files, edh.ClientRandState, args, cmdline, withScreen: true, sound: sound);
        float time = float.Parse(ClientHost.HarnessFrameRate, System.Globalization.CultureInfo.InvariantCulture);
        var allocating = new List<(int Frame, long Bytes)>();
        int commandFrames = 0;
        for (int frame = 0; frame < 100_000 && !host.Ended; frame++)
        {
            int commands = host.Cmd.Executed;
            long before = GC.GetAllocatedBytesForCurrentThread();
            host.Frame(time);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (frame == 0 || host.Cmd.Executed != commands)
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
