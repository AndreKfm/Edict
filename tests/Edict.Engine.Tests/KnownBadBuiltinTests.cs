// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// docs/TESTING.md R7 for the builtin gates: <see cref="BuiltinTests.Mutant"/>, one deliberate mistake in one
/// builtin at a time, run through the builtin fuzz (level 1) and the capture replay (level 2). A mutant that passes
/// both means the builtin gates are blind there.
/// </summary>
[Collection(OracleCollection.Name)]
[Trait("Tier", "long")]
public sealed class KnownBadBuiltinTests
{
    private static string? Run(BuiltinTests.Mutation m)
    {
        Action<Server, int> call = BuiltinTests.Mutant(m);
        int[] target = BuiltinTests.Target(m);
        if (BuiltinTests.FirstDivergence(call, only: target.Contains) is { } fuzz)
            return "builtin fuzz: " + fuzz;
        using var files = new QFileSystem(TraceCorpus.DataDirectory);
        foreach (string map in VmReplay.Maps())
        {
            using var replay = new VmReplay(Path.Combine(VmReplay.Directory, map + ".ecp"), files, call: call);
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

    [Theory]
    [InlineData(BuiltinTests.Mutation.RandomBy32768)]
    [InlineData(BuiltinTests.Mutation.VectoyawInFloat)]
    [InlineData(BuiltinTests.Mutation.RintFloatSum)]
    [InlineData(BuiltinTests.Mutation.NormalizeInDouble)]
    [InlineData(BuiltinTests.Mutation.MakevectorsRuntimeSin)]
    [InlineData(BuiltinTests.Mutation.AnglemodInFloat)]
    [InlineData(BuiltinTests.Mutation.ChangeyawStrictBound)]
    [InlineData(BuiltinTests.Mutation.CvarWithAtof)]
    [InlineData(BuiltinTests.Mutation.PrecacheNoReturn)]
    [InlineData(BuiltinTests.Mutation.FtosAlwaysFixed)]
    [InlineData(BuiltinTests.Mutation.AllocNoRelax)]
    [InlineData(BuiltinTests.Mutation.AllocFloatTime)]
    [InlineData(BuiltinTests.Mutation.AllocFromOne)]
    [InlineData(BuiltinTests.Mutation.FreeKeepsOrigin)]
    [InlineData(BuiltinTests.Mutation.FindTakesFree)]
    [InlineData(BuiltinTests.Mutation.FindradiusFloatCentre)]
    [InlineData(BuiltinTests.Mutation.FindradiusTakesNotSolid)]
    [InlineData(BuiltinTests.Mutation.NextentTakesFree)]
    public void Known_bad_variant_is_detected(BuiltinTests.Mutation m)
    {
        string? d = Run(m);
        Assert.True(d is not null, $"R7: known-bad builtin variant {m} matched everywhere: the builtin gates are blind there");
        TestContext.Current.TestOutputHelper?.WriteLine($"{m}: {d}");
    }
}
