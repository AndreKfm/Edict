// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Exact.Tests;

/// <summary>
/// Tests that use state of the reference in the oracle (the rand seed): never in parallel with each other
/// (docs/TESTING.md, Oracle library). Found 2026-10-13 on .NET 11, whose timing let KnownBadTests reseed rand while
/// CBasicsTests compared a sequence; stateless oracle functions need no collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OracleStateCollection
{
    public const string Name = "oracle state";
}
