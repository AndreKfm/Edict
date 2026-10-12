// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.CodeAnalysis;

namespace Edict.NoAlloc.Analyzers;

/// <summary>
/// Receives what a body walk finds. The analyzer turns findings into diagnostics; the transitive verifier also
/// records which scopes were verified and which calls a scope makes, and asks for every violated constraint
/// instead of the most telling one per operation.
/// </summary>
internal abstract class FindingSink
{
    /// <summary>
    /// Whether every violated constraint is reported: an async method under NoAsync and NoAlloc, a thrown object
    /// under NoThrow and NoAlloc, a forbidden callee that also lacks other promises, every enumerator member of a
    /// loop. The analyzer reports the most telling finding only; the verifier needs one per constraint.
    /// </summary>
    public virtual bool ReportsAll => false;

    /// <summary>A finding inside <paramref name="owner"/> that violates <paramref name="constraints"/>.</summary>
    public abstract void Report(ISymbol owner, Constraint constraints, DiagnosticDescriptor descriptor, Location location, object[] arguments);

    /// <summary>A scope whose code is verified under <paramref name="contract"/>.</summary>
    public virtual void Enter(ISymbol owner, Contract contract)
    {
    }

    /// <summary>Whether <see cref="Call"/> is wanted; the analyzer does not pay for locations it never uses.</summary>
    public virtual bool RecordsCalls => false;

    /// <summary>
    /// A call from <paramref name="owner"/> whose required constraints the callee promises without a finding:
    /// <paramref name="byContract"/> through its own contract, <paramref name="byAllowlist"/> through the allowlist,
    /// <paramref name="bySpinAllowance"/> because the caller's <c>Allow.Spin</c> lifts a spin operation. Constraints
    /// promised intrinsically are in none of them. <paramref name="dispatched"/>: the call binds at run time to an
    /// override or implementation.
    /// </summary>
    public virtual void Call(ISymbol owner, IMethodSymbol callee, Location location, bool dispatched, Constraint byContract, Constraint byAllowlist, Constraint bySpinAllowance)
    {
    }
}

/// <summary>
/// Observes one analyzer run for the transitive verifier: the model of the compilation, a sink per walked body,
/// and every declared method, property and type. Called concurrently.
/// </summary>
internal abstract class AnalysisObserver
{
    /// <summary>The model and the compilation the analyzer runs on, whose symbols every later callback uses.</summary>
    public abstract void Start(NoAllocModel model, Compilation compilation);

    /// <summary>Markers to assume where none is written (<see cref="AssumedMarkers"/>).</summary>
    public virtual AssumedMarkers? Assumed => null;

    /// <summary>A sink for one body; <paramref name="report"/> reports a diagnostic to the compiler.</summary>
    public abstract FindingSink CreateSink(Action<Diagnostic> report);

    public abstract void Declared(ISymbol symbol);
}

/// <summary>The analyzer's sink: every finding becomes a diagnostic, nothing else is kept.</summary>
internal sealed class DiagnosticSink(Action<Diagnostic> report) : FindingSink
{
    public override void Report(ISymbol owner, Constraint constraints, DiagnosticDescriptor descriptor, Location location, object[] arguments) =>
        report(Diagnostic.Create(descriptor, location, arguments));
}
