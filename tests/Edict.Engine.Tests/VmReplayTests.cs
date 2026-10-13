// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// Cut 2, step 2 gate (docs/PLAN.md section C): the port's QuakeC VM replays the reference's capture of spawn and
/// 4,000 tape frames per map, and changes exactly the words the reference's VM changed, at every builtin call and
/// every return to the engine.
/// </summary>
public sealed class VmReplayTests(ITestOutputHelper output)
{
    public static IEnumerable<TheoryDataRow<string>> MapNames => VmReplay.Maps().Select(m => new TheoryDataRow<string>(m));

    [Theory]
    [MemberData(nameof(MapNames))]
    public void Vm_matches_the_reference_at_every_boundary(string map)
    {
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using var replay = new VmReplay(Path.Combine(VmReplay.Directory, map + ".ecp"), files);
        try
        {
            replay.Run();
        }
        catch (ReplayDivergence d)
        {
            Assert.Fail("first divergence: " + d.Message);
        }
        string stats = $"{map}: {replay.Entries} engine entries, {replay.QcCalls} QuakeC calls, " +
                       $"{replay.BuiltinCalls} builtin calls ({replay.PortedBuiltinCalls} run by the port), {replay.Links} links, {replay.SpawnsHashed} spawn points hashed as the reference, {replay.WordsChecked} words compared";
        output.WriteLine(stats);
        Assert.True(replay.Entries > 0 && replay.BuiltinCalls > 0, "the capture replayed nothing: " + stats);
    }

    /// <summary>
    /// Cut 3, step 2: the port runs the start of every server frame (new clients, the tape's messages, the host
    /// commands, SV_ClientThink); what it changed must be what the reference recorded by the frame's next entry.
    /// </summary>
    [Theory]
    [MemberData(nameof(MapNames))]
    public void Server_frame_start_matches_the_reference(string map)
    {
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        using var replay = new VmReplay(Path.Combine(VmReplay.Directory, map + ".ecp"), files, clients: true);
        try
        {
            replay.Run();
        }
        catch (ReplayDivergence d)
        {
            Assert.Fail("first divergence: " + d.Message);
        }
        output.WriteLine($"{map}: {replay.PortFrameStarts} server frame starts run by the port, {replay.Entries} engine entries");
        Assert.True(replay.PortFrameStarts > 0, "no server frame ran");
    }
}
