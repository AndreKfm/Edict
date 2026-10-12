// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Edict.NoAlloc;

/// <summary>What a <see cref="NoAllocAttribute"/> section promises. <see cref="NoAlloc"/> is always included.</summary>
[Flags]
public enum Constraints
{
    /// <summary>No constraint beyond <see cref="NoAlloc"/>.</summary>
    None = 0,

    /// <summary>No managed heap allocation: no <c>new</c> of reference types, arrays or strings, no boxing, no capturing closures, no delegates, no iterators, no pool rent.</summary>
    NoAlloc = 1,

    /// <summary>No locks, monitors, mutexes, semaphore waits, reader-writer locks, spin locks or spin waits.</summary>
    NoLock = 2,

    /// <summary>No synchronous I/O, sleeps, task waits or blocking collections.</summary>
    NoBlock = 4,

    /// <summary>Not async and no await.</summary>
    NoAsync = 8,

    /// <summary>No explicit <c>throw</c>; every callee promises <see cref="NoThrow"/> too. Runtime exceptions are not covered.</summary>
    NoThrow = 16,

    /// <summary>No reflection, <c>dynamic</c>, <see cref="Activator"/> or late binding.</summary>
    NoDynamic = 32,

    /// <summary>No first access to a type with an explicit static constructor.</summary>
    NoInit = 64,

    /// <summary>Every constraint.</summary>
    All = NoAlloc | NoLock | NoBlock | NoAsync | NoThrow | NoDynamic | NoInit,
}

/// <summary>Constraints a <see cref="NoAllocAttribute"/> lifts; every flag needs a <see cref="NoAllocAttribute.Reason"/>.</summary>
[Flags]
public enum Allow
{
    /// <summary>Nothing is lifted.</summary>
    None = 0,

    /// <summary>Lifts <see cref="Constraints.NoAlloc"/>.</summary>
    Alloc = 1,

    /// <summary>Lifts <see cref="Constraints.NoLock"/> entirely.</summary>
    Lock = 2,

    /// <summary>Lifts <see cref="Constraints.NoBlock"/>.</summary>
    Block = 4,

    /// <summary>Lifts <see cref="Constraints.NoAsync"/>.</summary>
    Async = 8,

    /// <summary>Lifts <see cref="Constraints.NoThrow"/>.</summary>
    Throw = 16,

    /// <summary>Lifts <see cref="Constraints.NoDynamic"/>.</summary>
    Dynamic = 32,

    /// <summary>Lifts <see cref="Constraints.NoInit"/>.</summary>
    Init = 64,

    /// <summary>Lifts spin operations only (spin waits and spin locks) under <see cref="Constraints.NoLock"/>.</summary>
    Spin = 128,
}
