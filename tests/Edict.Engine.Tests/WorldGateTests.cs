// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// The gate of P6, step 4 (docs/PLAN.md): the demo and listen runs of data/screen4 with the whole 3D view drawn, the
/// entities and the gun off (r_drawentities 0, r_drawviewmodel 0): the world's surfaces through the edge list, the
/// surface cache with its light, the sky, the water's turbulence and warp, the particles. Every present must hash as
/// the reference's, every client and server record as there (<see cref="ScreenGateTests"/>' comparisons).
/// </summary>
public sealed class WorldGateTests
{
    /// <summary>
    /// The world corpus of step 4 (tools/screens.sh with EXTRA="+r_drawentities 0 +r_drawviewmodel 0":
    /// data/screen4, or EDICT_SCREEN4): the whole view, without entities and the gun.
    /// </summary>
    public static string WorldDirectory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_SCREEN4");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, "screen4") : dir;
        }
    }

    public static IEnumerable<TheoryDataRow<string>> Runs => ScreenGateTests.RunsOf(WorldDirectory);
    public static IEnumerable<TheoryDataRow<string>> ListenRuns => ScreenGateTests.RunsOf(Path.Combine(WorldDirectory, "listen"));

    [Theory]
    [MemberData(nameof(Runs))]
    public void World_presents_as_the_reference(string run)
    {
        string? d = ScreenGateTests.FirstDivergence(WorldDirectory, false, run, out int presents, out int clientFrames);
        Assert.True(d is null, $"{run}, after {presents} presents and {clientFrames} client frames alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {presents} presents and {clientFrames} client frames as the reference");
    }

    [Theory]
    [MemberData(nameof(ListenRuns))]
    public void Listen_world_presents_as_the_reference(string run)
    {
        string? d = ScreenGateTests.ListenDivergence(WorldDirectory, false, run, out int presents, out int frames);
        Assert.True(d is null, $"{run}, after {presents} presents and {frames} client frames alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {presents} presents and {frames} client frames as the reference");
    }
}

/// <summary>
/// The gate of P6, step 5 (docs/PLAN.md): the demo and listen runs of data/screen with the whole frame drawn — the
/// world, the brush entities, the alias models, the sprites, the gun and the particles — every present, client
/// record and server record as the reference's.
/// </summary>
public sealed class FullFrameGateTests
{
    /// <summary>The full-frame corpus (tools/screens.sh without EXTRA: data/screen, or EDICT_SCREEN).</summary>
    public static string FullDirectory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_SCREEN");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, "screen") : dir;
        }
    }

    public static IEnumerable<TheoryDataRow<string>> Runs => ScreenGateTests.RunsOf(FullDirectory);
    public static IEnumerable<TheoryDataRow<string>> ListenRuns => ScreenGateTests.RunsOf(Path.Combine(FullDirectory, "listen"));

    [Theory]
    [MemberData(nameof(Runs))]
    public void Frames_present_as_the_reference(string run)
    {
        string? d = ScreenGateTests.FirstDivergence(FullDirectory, false, run, out int presents, out int clientFrames);
        Assert.True(d is null, $"{run}, after {presents} presents and {clientFrames} client frames alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {presents} presents and {clientFrames} client frames as the reference");
    }

    [Theory]
    [MemberData(nameof(ListenRuns))]
    public void Listen_frames_present_as_the_reference(string run)
    {
        string? d = ScreenGateTests.ListenDivergence(FullDirectory, false, run, out int presents, out int frames);
        Assert.True(d is null, $"{run}, after {presents} presents and {frames} client frames alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {presents} presents and {frames} client frames as the reference");
    }
}
