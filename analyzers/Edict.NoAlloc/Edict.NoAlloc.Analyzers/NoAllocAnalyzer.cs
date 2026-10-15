// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Edict.NoAlloc.Analyzers;

/// <summary>
/// Verifies [NoAlloc] code: one operation walk per body with a contract, inheritance checks per type, marker and
/// allowlist validation. Registers nothing when the compilation cannot see the marker attributes.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoAllocAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        NoAllocDiagnostics.Allocation,
        NoAllocDiagnostics.BoxingOrCapture,
        NoAllocDiagnostics.Lock,
        NoAllocDiagnostics.Blocking,
        NoAllocDiagnostics.Async,
        NoAllocDiagnostics.Throw,
        NoAllocDiagnostics.Dynamic,
        NoAllocDiagnostics.NoContract,
        NoAllocDiagnostics.MissingReason,
        NoAllocDiagnostics.RelaxedOverride,
        NoAllocDiagnostics.TypeInitialization,
        NoAllocDiagnostics.PoolRent,
        NoAllocDiagnostics.RelaxedCallee,
        NoAllocDiagnostics.UnresolvedAllowlistEntry,
        NoAllocDiagnostics.MalformedAllowlistEntry,
    ];

    private readonly AnalysisObserver? _observer;

    public NoAllocAnalyzer()
    {
    }

    /// <summary>An analyzer whose findings and calls also go to <paramref name="observer"/> (the transitive verifier).</summary>
    internal NoAllocAnalyzer(AnalysisObserver observer) => _observer = observer;

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var model = NoAllocModel.TryCreate(context.Compilation, context.Options.AdditionalFiles, context.CancellationToken, _observer?.Assumed);
        if (model is null)
            return;
        _observer?.Start(model, context.Compilation);

        context.RegisterOperationBlockAction(block => AnalyzeBlock(block, model));
        context.RegisterSyntaxNodeAction(node => AnalyzeLocalFunction(node, model), SyntaxKind.LocalFunctionStatement);
        context.RegisterSymbolAction(symbol => AnalyzeSymbol(symbol, model), SymbolKind.Method, SymbolKind.Property, SymbolKind.NamedType);
        context.RegisterAdditionalFileAction(file => ValidateAllowlist(file, context.Compilation));
    }

    private FindingSink CreateSink(Action<Diagnostic> report) =>
        _observer?.CreateSink(report) ?? new DiagnosticSink(report);

    private void AnalyzeBlock(OperationBlockAnalysisContext context, NoAllocModel model)
    {
        // Abstract and extern members have no code to verify; their blocks are at most their attributes.
        if (context.OperationBlocks.All(block => block is IAttributeOperation))
            return;
        switch (context.OwningSymbol)
        {
            case IMethodSymbol method when model.GetContract(method) is { IsChecked: true } contract:
            {
                var sink = CreateSink(context.ReportDiagnostic);
                BodyWalker.CheckDeclaration(method, contract, sink);
                Walk(context, model, sink, method, contract);
                break;
            }

            // Instance field and property initializers run inside the constructors of the type.
            case IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false }
                when InitializerContract(context.OwningSymbol.ContainingType, model) is { IsChecked: true } contract:
                Walk(context, model, CreateSink(context.ReportDiagnostic), context.OwningSymbol, contract);
                break;
        }
    }

    private static void Walk(OperationBlockAnalysisContext context, NoAllocModel model, FindingSink sink, ISymbol owner, Contract contract)
    {
        var walker = new BodyWalker(model, sink, owner, contract);
        if (owner is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            walker.CheckImplicitBaseConstructor(constructor);
        foreach (var block in context.OperationBlocks)
        {
            if (block is not IAttributeOperation)
                walker.Visit(block);
        }
    }

    /// <summary>
    /// A marked local function inside code that is not verified: the enclosing block walk does not cover it.
    /// The attribute-list check keeps this a cheap syntax filter for the vast majority of local functions.
    /// </summary>
    private void AnalyzeLocalFunction(SyntaxNodeAnalysisContext context, NoAllocModel model)
    {
        var syntax = (LocalFunctionStatementSyntax)context.Node;
        if (syntax.AttributeLists.Count == 0
            || context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not IMethodSymbol symbol)
            return;

        CheckMarker(symbol, model, context.ReportDiagnostic);
        var contract = model.GetContract(symbol);
        if (!contract.IsChecked || model.GetContextContract(symbol.ContainingSymbol).IsChecked)
            return;

        if (context.SemanticModel.GetOperation(syntax, context.CancellationToken) is { } operation)
            new BodyWalker(model, CreateSink(context.ReportDiagnostic), symbol.ContainingSymbol, default).Visit(operation);
    }

    /// <summary>One action per declared symbol: NA009 for its marker, NA010 for methods and for types' base implementations.</summary>
    private void AnalyzeSymbol(SymbolAnalysisContext context, NoAllocModel model)
    {
        _observer?.Declared(context.Symbol);
        CheckMarker(context.Symbol, model, context.ReportDiagnostic);
        switch (context.Symbol)
        {
            case IMethodSymbol method:
                ContractChecker.CheckMethod(method, model, context.ReportDiagnostic);
                break;
            case INamedTypeSymbol type:
                ContractChecker.CheckType(type, model, context.ReportDiagnostic);
                break;
        }
    }

    /// <summary>NA009: Allow without Reason, or [NoAllocUnchecked] without a reason.</summary>
    private static void CheckMarker(ISymbol symbol, NoAllocModel model, Action<Diagnostic> report)
    {
        if (model.GetMarker(symbol) is not { ReasonMissing: true } marker)
            return;
        report(Diagnostic.Create(
            NoAllocDiagnostics.MissingReason,
            symbol.Locations.FirstOrDefault() ?? Location.None,
            marker.Unchecked ? "[NoAllocUnchecked]" : "Allow",
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
    }

    /// <summary>The constraints instance initializers run under: those of the type's marker and of its verified constructors.</summary>
    internal static Contract InitializerContract(INamedTypeSymbol type, NoAllocModel model)
    {
        var required = model.GetMarker(type)?.Required ?? Constraint.None;
        var allowSpin = false;
        foreach (var constructor in type.InstanceConstructors)
        {
            if (constructor.IsImplicitlyDeclared)
                continue;
            var contract = model.GetContract(constructor);
            required |= contract.Required;
            allowSpin |= contract.AllowSpin;
        }
        return new Contract(required, Constraint.None, allowSpin);
    }

    private static void ValidateAllowlist(AdditionalFileAnalysisContext context, Compilation compilation)
    {
        var path = context.AdditionalFile.Path;
        if (!Allowlist.IsAllowlistPath(path) || context.AdditionalFile.GetText(context.CancellationToken) is not { } text)
            return;

        foreach (var entry in Allowlist.Parse(text))
        {
            var location = Location.Create(path, entry.Span, text.Lines.GetLinePositionSpan(entry.Span));
            if (entry.Error is { } error)
                context.ReportDiagnostic(Diagnostic.Create(NoAllocDiagnostics.MalformedAllowlistEntry, location, error));
            else if (Allowlist.AppliesTo(entry, compilation) && Allowlist.Resolve(entry, compilation).IsEmpty)
                context.ReportDiagnostic(Diagnostic.Create(NoAllocDiagnostics.UnresolvedAllowlistEntry, location, entry.Symbol));
        }
    }
}
