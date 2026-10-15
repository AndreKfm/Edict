// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// docs/TESTING.md R7 for the VM gates: <see cref="MutantVm"/>, a copy of <see cref="QcVm"/> with one deliberate
/// mistake at a time, run through the opcode fuzz (level 1) and replayed over the captures (level 2). A mutant that
/// passes both means the VM gates are blind there.
/// </summary>
/// <remarks>
/// Replay alone detected 4 of the 10 (first run, 2026-10-13): the game never produces -0.0 in a condition, NaN in a
/// comparison, an empty string at a nonzero offset or a frame equal to its new value with the other sign of zero,
/// and float/double differences of nextthink do not occur on its frame times. The opcode fuzz is there for these.
/// </remarks>
[Collection(OracleCollection.Name)]
[Trait("Tier", "long")]
public sealed unsafe class KnownBadVmTests
{
    private sealed class Mutant(Progs progs, nint globals, nint edicts, int header) : VmReplay.IVm
    {
        private readonly MutantVm _vm = new(progs, (int*)globals, (byte*)edicts, header, VmReplay.NumBuiltins);
        public int Argc => _vm.Argc;
        public void Execute(VmReplay replay, int fnum)
        {
            var host = new VmReplay.Host(replay);
            _vm.Execute(ref host, fnum);
        }
    }

    private sealed class FuzzMutant(VmOpcodeFuzz f) : VmOpcodeFuzz.IVm
    {
        private readonly MutantVm _vm = new(f.Progs, (int*)f.PortGlobals, f.PortEdicts, f.Header, VmReplay.NumBuiltins);
        public void Execute(int fnum)
        {
            var host = new VmOpcodeFuzz.Host(f);
            _vm.Execute(ref host, fnum);
        }
    }

    /// <summary>The first divergence (opcode fuzz, then all maps), or null if the mutant passes both.</summary>
    private static string? Run(MutantVm.Mutation m)
    {
        MutantVm.M = m;
        try
        {
            if (VmOpcodeTests.FirstDivergence(f => new FuzzMutant(f)) is { } fuzz)
                return "opcode fuzz: " + fuzz;
            using var files = new QFileSystem(TraceCorpus.DataDirectory);
            foreach (string map in VmReplay.Maps())
            {
                using var replay = new VmReplay(Path.Combine(VmReplay.Directory, map + ".ecp"), files,
                                                (p, g, e, h) => new Mutant(p, g, e, h));
                try
                {
                    replay.Run();
                }
                catch (ReplayDivergence d)
                {
                    return "replay: " + d.Message;
                }
            }
            return null;
        }
        finally
        {
            MutantVm.M = MutantVm.Mutation.None;
        }
    }

    [Fact]
    public void Unmutated_copy_matches_every_boundary() => Assert.Null(Run(MutantVm.Mutation.None));

    [Theory]
    [InlineData(MutantVm.Mutation.StateFloatTime)]
    [InlineData(MutantVm.Mutation.IfFloat)]
    [InlineData(MutantVm.Mutation.NeSSign)]
    [InlineData(MutantVm.Mutation.MulVReordered)]
    [InlineData(MutantVm.Mutation.NoLocalSave)]
    [InlineData(MutantVm.Mutation.ReturnOneWord)]
    [InlineData(MutantVm.Mutation.LeAsLt)]
    [InlineData(MutantVm.Mutation.NotSOffsetOnly)]
    [InlineData(MutantVm.Mutation.EqFAsWords)]
    [InlineData(MutantVm.Mutation.StateAlwaysFrame)]
    public void Known_bad_variant_is_detected(MutantVm.Mutation m)
    {
        string? d = Run(m);
        Assert.True(d is not null, $"R7: known-bad VM variant {m} matched every boundary: the captures are blind there");
    }
}
