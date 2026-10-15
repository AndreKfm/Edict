// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine.Tests;

/// <summary>
/// The gate of P6 step 9's server side (docs/PLAN.md): the multiplayer corpus (tools/multis.sh, data/multi or
/// EDICT_MULTI), a dedicated server with four slots and the clients of a version 2 tape, who join, leave, reconnect,
/// lose their connection and send host commands (say, tell, status, ping, pause, kill, name, color; in coop the
/// cheats and kick). The cut-3 gate's comparisons (<see cref="LoopGateTests"/>): every frame and spawn record, and
/// the messages to every client, as the reference's.
/// </summary>
public sealed class MultiGateTests
{
    /// <summary>The multiplayer corpus (data/multi, or EDICT_MULTI).</summary>
    public static string Directory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_MULTI");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, "multi") : dir;
        }
    }

    public static IEnumerable<TheoryDataRow<string>> Runs =>
        (System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.etp") : [])
        .Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).Select(r => new TheoryDataRow<string>(r));

    /// <summary>The run's map: the name after the mode (dm_, coop_).</summary>
    private static string MapOf(string run) => run[(run.IndexOf('_') + 1)..];

    [Theory]
    [MemberData(nameof(Runs))]
    public void Multiplayer_hashes_as_the_reference_every_frame(string run)
    {
        string edh = Path.Combine(Directory, run + ".edh");
        string? d = LoopGateTests.FirstDivergence(MapOf(run), edh, Path.ChangeExtension(edh, ".etp"),
            Path.ChangeExtension(edh, ".ecp"), out int frames, out int spawns);
        Assert.True(d is null, $"{run}, after {frames} frames hashed alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {frames} frames and {spawns} spawns hashed as the reference");
    }
}
