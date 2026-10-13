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
                       $"{replay.BuiltinCalls} builtin calls ({replay.PortedBuiltinCalls} run by the port), {replay.WordsChecked} words compared";
        output.WriteLine(stats);
        Assert.True(replay.Entries > 0 && replay.BuiltinCalls > 0, "the capture replayed nothing: " + stats);
    }
}
