// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.CodeAnalysis;

namespace Edict.NoAlloc.Analyzers;

/// <summary>
/// NA010: an override or interface implementation inherits the marker of the member it overrides or implements and
/// may only tighten it. Without a marker of its own it is verified under the inherited contract; with a marker,
/// [NoAllocUnchecked] or Allow that drops an inherited constraint it is reported. Implementations a base class
/// provides for an interface the type declares are reported on the type.
/// </summary>
internal static class ContractChecker
{
    /// <summary>Implementations a base class provides for interfaces the type declares; only for types with a marked interface.</summary>
    public static void CheckType(INamedTypeSymbol type, NoAllocModel model, Action<Diagnostic> report)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || !model.HasContractRelevantInterface(type))
            return;

        foreach (var @interface in type.Interfaces)
        {
            foreach (var implemented in @interface.AllInterfaces.Add(@interface))
            {
                if (model.IsContractRelevant(implemented))
                    CheckBaseImplementations(type, implemented, model, report);
            }
        }
    }

    /// <summary>An override or implementation whose own contract drops an inherited constraint.</summary>
    public static void CheckMethod(IMethodSymbol method, NoAllocModel model, Action<Diagnostic> report)
    {
        if (method.IsAbstract)
            return;
        // Nothing inherited, nothing to relax: most methods end here without resolving their contract.
        var inherited = model.GetInherited(method);
        if (inherited == Constraint.None)
            return;
        var dropped = inherited & ~model.GetContract(method).Required;
        if (dropped == Constraint.None)
            return;
        var source = (ISymbol?)method.OverriddenMethod ?? method.ExplicitInterfaceImplementations.FirstOrDefault() ?? (ISymbol)method.ContainingType;
        report(Create(method.Locations.FirstOrDefault(), method, dropped, source));
    }

    private static void CheckBaseImplementations(INamedTypeSymbol type, INamedTypeSymbol @interface, NoAllocModel model, Action<Diagnostic> report)
    {
        foreach (var member in @interface.GetMembers())
        {
            if (member is not IMethodSymbol { IsStatic: false } interfaceMethod
                || !(interfaceMethod.IsAbstract || interfaceMethod.IsVirtual)
                || model.GetContract(interfaceMethod).Required is var demanded && demanded == Constraint.None
                || type.FindImplementationForInterfaceMember(interfaceMethod) is not IMethodSymbol implementation
                || SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type))
                continue;

            var dropped = demanded & ~model.GetPromise(implementation).Provided;
            if (dropped != Constraint.None)
                report(Create(type.Locations.FirstOrDefault(), implementation, dropped, interfaceMethod));
        }
    }

    private static Diagnostic Create(Location? location, IMethodSymbol implementation, Constraint dropped, ISymbol source) =>
        Diagnostic.Create(
            NoAllocDiagnostics.RelaxedOverride,
            location ?? Location.None,
            implementation.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
            Constraints.Describe(dropped),
            source.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat));
}
