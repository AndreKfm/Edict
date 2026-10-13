// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine.Tests;

/// <summary>
/// Tests that run reference code through the oracle library: WinQuake keeps its state in globals, one instance per
/// process, so these tests never run in parallel with each other (docs/TESTING.md, Oracle library).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OracleCollection
{
    public const string Name = "oracle";
}
