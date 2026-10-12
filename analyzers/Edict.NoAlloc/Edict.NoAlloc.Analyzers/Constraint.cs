// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.NoAlloc.Analyzers;

/// <summary>The analyzer's copy of <c>Edict.NoAlloc.Constraints</c>; the attribute's enum values map one to one.</summary>
[Flags]
internal enum Constraint
{
    None = 0,
    NoAlloc = 1,
    NoLock = 2,
    NoBlock = 4,
    NoAsync = 8,
    NoThrow = 16,
    NoDynamic = 32,
    NoInit = 64,
    All = NoAlloc | NoLock | NoBlock | NoAsync | NoThrow | NoDynamic | NoInit,
}

internal static class Constraints
{
    /// <summary><c>Edict.NoAlloc.Allow.Spin</c>: lifts spin operations under NoLock only.</summary>
    public const int AllowSpin = 128;

    private static readonly (Constraint Flag, string Name)[] Names =
    [
        (Constraint.NoAlloc, nameof(Constraint.NoAlloc)),
        (Constraint.NoLock, nameof(Constraint.NoLock)),
        (Constraint.NoBlock, nameof(Constraint.NoBlock)),
        (Constraint.NoAsync, nameof(Constraint.NoAsync)),
        (Constraint.NoThrow, nameof(Constraint.NoThrow)),
        (Constraint.NoDynamic, nameof(Constraint.NoDynamic)),
        (Constraint.NoInit, nameof(Constraint.NoInit)),
    ];

    /// <summary>The constraint names in declaration order, comma separated: <c>NoAlloc, NoThrow</c>.</summary>
    public static string Describe(Constraint constraints) =>
        string.Join(", ", Names.Where(name => (constraints & name.Flag) != 0).Select(name => name.Name));

    /// <summary>A constraint name as the allowlist writes it; <c>All</c> means every constraint.</summary>
    public static bool TryParse(string name, out Constraint constraint)
    {
        if (name == nameof(Constraint.All))
        {
            constraint = Constraint.All;
            return true;
        }
        foreach (var (flag, text) in Names)
        {
            if (text == name)
            {
                constraint = flag;
                return true;
            }
        }
        constraint = Constraint.None;
        return false;
    }
}
