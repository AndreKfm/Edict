// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Edict.Harness;

namespace Edict.Engine.Tests;

/// <summary>
/// Level 1 for the state hash: <see cref="Xxh64"/> against the reference's XXH64 (EdictOracle_XXH64) on every length
/// up to 1,100 (all tail paths, one to 34 stripes) and random contents. The streams themselves are checked by the
/// capture replay, which hashes the reference's state at every spawn point (<see cref="VmReplay"/>).
/// </summary>
[Collection(OracleCollection.Name)]
public sealed unsafe class StateHashTests
{
    private static readonly nint Lib = NativeLibrary.Load(Environment.GetEnvironmentVariable("EDICT_ORACLE")
        ?? throw new InvalidOperationException("EDICT_ORACLE is not set (docs/TESTING.md)"));
    private static readonly delegate* unmanaged<byte*, int, ulong> Xxh64Fn =
        (delegate* unmanaged<byte*, int, ulong>)NativeLibrary.GetExport(Lib, "EdictOracle_XXH64");

    [Fact]
    public void Xxh64_matches_the_reference()
    {
        var rng = new VmOpcodeFuzz.Rng(5);
        byte[] data = new byte[1100];
        for (int round = 0; round < 4; round++)
        {
            for (int i = 0; i < data.Length; i++)
                data[i] = round == 0 ? (byte)0 : round == 1 ? (byte)0xff : (byte)rng.Bits();
            fixed (byte* p = data)
                for (int len = 0; len <= data.Length; len++)
                {
                    ulong r = Xxh64Fn(p, len), q = Xxh64.Hash(data.AsSpan(0, len));
                    Assert.True(q == r, $"length {len}, round {round}: port {q:x16}, reference {r:x16}");
                }
        }
    }
}
