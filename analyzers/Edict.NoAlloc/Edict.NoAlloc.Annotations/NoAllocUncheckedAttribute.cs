// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Edict.NoAlloc;

/// <summary>
/// Takes a member, or every member declared in a type, out of no-alloc verification. The gap stays visible: the
/// member promises no constraint, and a caller that requires one gets a contract breach.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property,
    Inherited = false, AllowMultiple = false)]
public sealed class NoAllocUncheckedAttribute : Attribute
{
    /// <summary>Creates the attribute with the reason the member is not verified.</summary>
    public NoAllocUncheckedAttribute(string reason) => Reason = reason;

    /// <summary>Why the member is not verified.</summary>
    public string Reason { get; }
}
