// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine.Tests;

/// <summary>
/// The gate of P6, step 8 (docs/PLAN.md): the demo and listen runs of data/sound, made by the reference's sound build
/// (ref/build.sh SOUND=1: snd_dma.c, snd_mix.c, snd_mem.c and the harness's DMA driver) with the 3D view cleared
/// (-edict2d, +showram 0). Its S_Init adds cvars, commands and console lines, its S_StartSound draws from rand: every
/// sound record — the DMA buffer after each host frame with paintedtime and soundtime — must hash as the reference's,
/// and every present, client and server record as there (<see cref="ScreenGateTests"/>' comparisons).
/// </summary>
public sealed class SoundGateTests
{
    /// <summary>The sound corpus (tools/screens.sh with the sound build and EXTRA="-edict2d +showram 0": data/sound, or EDICT_SOUND).</summary>
    public static string SoundDirectory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("EDICT_SOUND");
            return string.IsNullOrEmpty(dir) ? Path.Combine(Path.GetDirectoryName(VmReplay.Directory)!, "sound") : dir;
        }
    }

    public static IEnumerable<TheoryDataRow<string>> Runs => ScreenGateTests.RunsOf(SoundDirectory);
    public static IEnumerable<TheoryDataRow<string>> ListenRuns => ScreenGateTests.RunsOf(Path.Combine(SoundDirectory, "listen"));

    [Theory]
    [MemberData(nameof(Runs))]
    public void Sound_mixes_as_the_reference(string run)
    {
        string? d = ScreenGateTests.FirstDivergence(SoundDirectory, true, run, out int presents, out int clientFrames, sound: true);
        Assert.True(d is null, $"{run}, after {presents} presents and {clientFrames} client frames alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {presents} presents and {clientFrames} client frames as the reference");
    }

    [Theory]
    [MemberData(nameof(ListenRuns))]
    public void Listen_sound_mixes_as_the_reference(string run)
    {
        string? d = ScreenGateTests.ListenDivergence(SoundDirectory, true, run, out int presents, out int frames, sound: true);
        Assert.True(d is null, $"{run}, after {presents} presents and {frames} client frames alike: {d}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{run}: {presents} presents and {frames} client frames as the reference");
    }
}
