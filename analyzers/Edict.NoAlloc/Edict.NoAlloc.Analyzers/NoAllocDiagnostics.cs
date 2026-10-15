// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.CodeAnalysis;

namespace Edict.NoAlloc.Analyzers;

/// <summary>NA001–NA015 as README.md specifies them. NA001–NA007, NA011 and NA012 prove an operation; NA008 and NA013 are contract breaches.</summary>
internal static class NoAllocDiagnostics
{
    private const string Category = "NoAlloc";

    public static readonly DiagnosticDescriptor Allocation = Rule(
        "NA001", "Allocation in a NoAlloc section", "'{0}' allocates on the managed heap: {1}");

    public static readonly DiagnosticDescriptor BoxingOrCapture = Rule(
        "NA002", "Boxing or closure capture in a NoAlloc section", "'{0}' allocates: {1}");

    public static readonly DiagnosticDescriptor Lock = Rule(
        "NA003", "Lock, spin or wait in a NoLock section", "'{0}' locks or spins: {1}");

    public static readonly DiagnosticDescriptor Blocking = Rule(
        "NA004", "Blocking call in a NoBlock section", "'{0}' blocks: {1}");

    public static readonly DiagnosticDescriptor Async = Rule(
        "NA005", "Async in a NoAsync section", "'{0}' is async");

    public static readonly DiagnosticDescriptor Throw = Rule(
        "NA006", "Explicit throw in a NoThrow section", "'{0}' throws explicitly");

    public static readonly DiagnosticDescriptor Dynamic = Rule(
        "NA007", "Reflection or dynamic in a NoDynamic section", "'{0}' uses reflection or dynamic binding: {1}");

    public static readonly DiagnosticDescriptor NoContract = Rule(
        "NA008", "Callee without contract", "'{0}' calls '{1}', which has no verified or assumed contract for {2}");

    public static readonly DiagnosticDescriptor MissingReason = Rule(
        "NA009", "Relaxation without reason", "{0} on '{1}' needs a non-empty reason");

    public static readonly DiagnosticDescriptor RelaxedOverride = Rule(
        "NA010", "Override relaxes an inherited marker", "'{0}' does not keep {1} of the contract it inherits from '{2}'");

    public static readonly DiagnosticDescriptor TypeInitialization = Rule(
        "NA011", "Possible type initialization in a NoInit section", "'{0}' may run the static constructor of '{1}'");

    public static readonly DiagnosticDescriptor PoolRent = Rule(
        "NA012", "Pool rent in a NoAlloc section", "'{0}' rents from a pool: '{1}'");

    public static readonly DiagnosticDescriptor RelaxedCallee = Rule(
        "NA013", "Callee relaxed (contract breach)", "'{0}' calls '{1}', which does not promise {2}: it is relaxed by {3}");

    public static readonly DiagnosticDescriptor UnresolvedAllowlistEntry = Rule(
        "NA014", "Allowlist entry does not resolve", "Allowlist entry '{0}' does not resolve to any symbol in this compilation",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor MalformedAllowlistEntry = Rule(
        "NA015", "Allowlist entry is malformed", "Allowlist entry is malformed: {0}",
        DiagnosticSeverity.Warning);

    /// <summary>The constraint a proven operation violates.</summary>
    public static Constraint ConstraintOf(DiagnosticDescriptor descriptor) => descriptor.Id switch
    {
        "NA001" or "NA002" or "NA012" => Constraint.NoAlloc,
        "NA003" => Constraint.NoLock,
        "NA004" => Constraint.NoBlock,
        "NA005" => Constraint.NoAsync,
        "NA006" => Constraint.NoThrow,
        "NA007" => Constraint.NoDynamic,
        "NA011" => Constraint.NoInit,
        _ => Constraint.None,
    };

    private static DiagnosticDescriptor Rule(
        string id, string title, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        new(id, title, message, Category, severity, isEnabledByDefault: true);
}
