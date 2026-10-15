// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Edict.NoAlloc;

/// <summary>
/// The no-alloc marker: the member, or every member declared in the type, promises <see cref="Constraints.NoAlloc"/>
/// and the constraints given, and may only call members that promise them too.
/// </summary>
/// <remarks>
/// Overrides and interface implementations inherit the marker and may only tighten it. A member-level marker wins
/// over a type-level one. The analyzer recognizes the attribute by its full name, so this public copy and the copy
/// the analyzer injects into each compilation are interchangeable.
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface
    | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property,
    Inherited = false, AllowMultiple = false)]
public sealed class NoAllocAttribute : Attribute
{
    /// <summary>Promises <see cref="Constraints.NoAlloc"/>.</summary>
    public NoAllocAttribute()
        : this(Constraints.NoAlloc)
    {
    }

    /// <summary>Promises <see cref="Constraints.NoAlloc"/> and <paramref name="constraints"/>.</summary>
    public NoAllocAttribute(Constraints constraints) => Constraints = constraints | Constraints.NoAlloc;

    /// <summary>The promised constraints, <see cref="Constraints.NoAlloc"/> included.</summary>
    public Constraints Constraints { get; }

    /// <summary>Constraints lifted for this section; callers that require them get a contract breach.</summary>
    public Allow Allow { get; set; }

    /// <summary>Why the constraints in <see cref="Allow"/> are lifted; required when <see cref="Allow"/> is set.</summary>
    public string? Reason { get; set; }
}
