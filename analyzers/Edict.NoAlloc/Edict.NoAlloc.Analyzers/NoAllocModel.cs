// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Edict.NoAlloc.Analyzers;

/// <summary>One [NoAlloc] or [NoAllocUnchecked] as written on a symbol.</summary>
internal sealed class Marker(Constraint constraints, Constraint allowed, bool allowSpin, bool @unchecked, string? reason)
{
    public Constraint Constraints { get; } = constraints;
    public Constraint Allowed { get; } = allowed;
    public bool AllowSpin { get; } = allowSpin;
    public bool Unchecked { get; } = @unchecked;
    public string? Reason { get; } = reason;

    /// <summary>A relaxation (Allow or [NoAllocUnchecked]) needs a reason (NA009).</summary>
    public bool ReasonMissing => (Unchecked || Allowed != Constraint.None || AllowSpin) && string.IsNullOrWhiteSpace(Reason);

    /// <summary>What the section is verified against and promises to callers.</summary>
    public Constraint Required => Unchecked ? Constraint.None : Constraints & ~Allowed;

    /// <summary>What the marker deliberately does not promise.</summary>
    public Constraint Relaxed => Unchecked ? Constraint.All : Allowed;
}

/// <summary>The effective contract of a method: what its body is verified against and what callers may rely on.</summary>
internal readonly struct Contract(Constraint required, Constraint relaxed, bool allowSpin, string? reason = null)
{
    public Constraint Required { get; } = required;
    public Constraint Relaxed { get; } = relaxed;
    public bool AllowSpin { get; } = allowSpin;

    /// <summary>The reason of the marker that relaxes the contract, if any.</summary>
    public string? Reason { get; } = reason;

    public bool IsChecked => Required != Constraint.None;
}

/// <summary>
/// What a callee promises, by source: its own contract, the allowlist (an assumption), or intrinsically (the runtime
/// helpers and trivially safe members every constraint holds for).
/// </summary>
internal readonly struct Promise(Constraint byContract, Constraint byAllowlist, bool intrinsic, Constraint relaxed)
{
    public Constraint ByContract { get; } = byContract;
    public Constraint ByAllowlist { get; } = byAllowlist;
    public bool Intrinsic { get; } = intrinsic;

    /// <summary>What the callee's contract deliberately does not promise.</summary>
    public Constraint Relaxed { get; } = relaxed;

    public Constraint Provided => Intrinsic ? Constraint.All : ByContract | ByAllowlist;
}

/// <summary>A member whose use is itself a violation of one constraint.</summary>
internal sealed class Forbidden(Constraint constraint, DiagnosticDescriptor descriptor, string what, bool isSpin)
{
    public Constraint Constraint { get; } = constraint;
    public DiagnosticDescriptor Descriptor { get; } = descriptor;
    public string What { get; } = what;
    public bool IsSpin { get; } = isSpin;
}

/// <summary>
/// Everything the analyzer knows about one compilation: markers, effective contracts, members forbidden per
/// constraint, intrinsic and allowlisted members. Every answer is cached per symbol. Runtime library types are
/// matched by metadata name and namespace at the callee, without building strings; allowlisted symbols by identity.
/// </summary>
/// <summary>
/// Markers assumed where none is written: every method of the named assemblies without a marker of its own, on
/// itself or its type, is verified as if it carried <c>[NoAlloc]</c> with <see cref="Required"/>. Only the
/// transitive verifier sets it, to measure code that has no markers.
/// </summary>
internal sealed class AssumedMarkers(Constraint required, ImmutableHashSet<string> assemblies)
{
    public Constraint Required { get; } = required;

    public ImmutableHashSet<string> Assemblies { get; } = assemblies;
}

internal sealed class NoAllocModel
{
    private const string AttributeNamespace = "Edict.NoAlloc";
    private const string AttributeNamespaceOuter = "Edict";
    private const string AttributeNamespaceInner = "NoAlloc";
    private const string MarkerName = "NoAllocAttribute";
    private const string UncheckedName = "NoAllocUncheckedAttribute";

    /// <summary>Types whose members are all assumed to satisfy every constraint.</summary>
    private static readonly string[] IntrinsicTypeNames =
    [
        "System.Math",
        "System.MathF",
        "System.Numerics.BitOperations",
        "System.Runtime.CompilerServices.Unsafe",
        "System.Threading.Interlocked",
        "System.Threading.Volatile",
    ];

    /// <summary>Members whose use violates a constraint; <c>null</c> members means every member of the type.</summary>
    private static readonly (string Type, string[]? Members, Constraint Constraint, bool Spin)[] ForbiddenMemberNames =
    [
        ("System.Threading.Monitor", null, Constraint.NoLock, false),
        ("System.Threading.Lock", null, Constraint.NoLock, false),
        ("System.Threading.Mutex", null, Constraint.NoLock, false),
        ("System.Threading.SemaphoreSlim", ["Wait"], Constraint.NoLock, false),
        ("System.Threading.Semaphore", ["WaitOne"], Constraint.NoLock, false),
        ("System.Threading.ReaderWriterLock", null, Constraint.NoLock, false),
        ("System.Threading.ReaderWriterLockSlim", null, Constraint.NoLock, false),
        ("System.Threading.SpinLock", ["Enter", "TryEnter"], Constraint.NoLock, true),
        ("System.Threading.SpinWait", ["SpinOnce", "SpinUntil"], Constraint.NoLock, true),
        ("System.Threading.Thread", ["SpinWait"], Constraint.NoLock, true),
        ("System.Threading.Thread", ["Sleep", "Join"], Constraint.NoBlock, false),
        ("System.Threading.ManualResetEventSlim", ["Wait"], Constraint.NoBlock, false),
        ("System.Threading.CountdownEvent", ["Wait"], Constraint.NoBlock, false),
        ("System.Threading.Barrier", ["SignalAndWait"], Constraint.NoBlock, false),
        ("System.Threading.WaitHandle", ["WaitOne", "WaitAny", "WaitAll", "SignalAndWait"], Constraint.NoBlock, false),
        ("System.Threading.Tasks.Task", ["Wait", "WaitAll", "WaitAny", "RunSynchronously"], Constraint.NoBlock, false),
        ("System.Threading.Tasks.Task`1", ["get_Result"], Constraint.NoBlock, false),
        ("System.Runtime.CompilerServices.TaskAwaiter", ["GetResult"], Constraint.NoBlock, false),
        ("System.Runtime.CompilerServices.TaskAwaiter`1", ["GetResult"], Constraint.NoBlock, false),
        ("System.Runtime.CompilerServices.ValueTaskAwaiter", ["GetResult"], Constraint.NoBlock, false),
        ("System.Runtime.CompilerServices.ValueTaskAwaiter`1", ["GetResult"], Constraint.NoBlock, false),
        ("System.Collections.Concurrent.BlockingCollection`1", ["Add", "Take", "CompleteAdding", "GetConsumingEnumerable"], Constraint.NoBlock, false),
        ("System.IO.Stream", ["Read", "ReadByte", "ReadExactly", "ReadAtLeast", "Write", "WriteByte", "Flush", "CopyTo"], Constraint.NoBlock, false),
        ("System.IO.File", null, Constraint.NoBlock, false),
        ("System.IO.TextReader", ["Read", "ReadLine", "ReadToEnd", "ReadBlock"], Constraint.NoBlock, false),
        ("System.IO.TextWriter", ["Write", "WriteLine", "Flush"], Constraint.NoBlock, false),
        ("System.Console", ["Read", "ReadLine", "ReadKey", "Write", "WriteLine"], Constraint.NoBlock, false),
        ("System.Net.Sockets.Socket", ["Accept", "Connect", "Receive", "ReceiveFrom", "Send", "SendTo", "Poll", "Select"], Constraint.NoBlock, false),
        ("System.Activator", null, Constraint.NoDynamic, false),
        ("System.Type", ["GetType", "GetMethod", "GetMethods", "GetProperty", "GetProperties", "GetField", "GetFields",
            "GetMember", "GetMembers", "GetConstructor", "GetConstructors", "GetEvent", "GetEvents", "InvokeMember"], Constraint.NoDynamic, false),
        ("System.Delegate", ["DynamicInvoke"], Constraint.NoDynamic, false),
        ("System.Buffers.ArrayPool`1", ["Rent"], Constraint.NoAlloc, false),
        ("System.Buffers.MemoryPool`1", ["Rent"], Constraint.NoAlloc, false),
    ];

    /// <summary>
    /// Forbidden members by type metadata name (<c>Task`1</c>). Matching by name and namespace at the callee, cached
    /// per callee, avoids resolving dozens of types through every reference of every compilation.
    /// </summary>
    private static readonly Dictionary<string, (string Namespace, string[]? Members, Forbidden Forbidden)[]> ForbiddenByName =
        ForbiddenMemberNames
            .Select(entry =>
            {
                var dot = entry.Type.LastIndexOf('.');
                var name = entry.Type.Substring(dot + 1);
                var descriptor = entry.Constraint switch
                {
                    Constraint.NoLock => NoAllocDiagnostics.Lock,
                    Constraint.NoBlock => NoAllocDiagnostics.Blocking,
                    Constraint.NoDynamic => NoAllocDiagnostics.Dynamic,
                    _ => NoAllocDiagnostics.PoolRent,
                };
                return (Name: name, Entry: (entry.Type.Substring(0, dot), entry.Members, new Forbidden(entry.Constraint, descriptor, name, entry.Spin)));
            })
            .GroupBy(item => item.Name)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Entry).ToArray());

    private static readonly Dictionary<string, string> IntrinsicByName =
        IntrinsicTypeNames.ToDictionary(name => name.Substring(name.LastIndexOf('.') + 1), name => name.Substring(0, name.LastIndexOf('.')));

    private static readonly Forbidden Reflection = new(Constraint.NoDynamic, NoAllocDiagnostics.Dynamic, "reflection", isSpin: false);

    private static readonly Lazy<ImmutableArray<AllowlistEntry>> BaselineEntries = new(LoadBaseline);

    private readonly ConcurrentDictionary<ISymbol, Marker?> _markers = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<IMethodSymbol, Contract> _contracts = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<IMethodSymbol, Constraint> _inherited = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<IMethodSymbol, Forbidden?> _forbiddenCache = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _contractRelevant = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _relevantInterfaces = new(SymbolEqualityComparer.Default);
    private readonly ImmutableDictionary<ISymbol, Constraint> _allowed;
    private readonly AssumedMarkers? _assumed;

    // Factories are created once: a lambda or method group that captures this would allocate on every lookup.
    private readonly Func<IMethodSymbol, Constraint> _computeInherited;
    private readonly Func<INamedTypeSymbol, bool> _computeHasRelevantInterface;
    private readonly Func<INamedTypeSymbol, bool> _computeContractRelevant;
    private readonly Func<ISymbol, bool> _isContractRelevantInterface;

    private NoAllocModel(ImmutableDictionary<ISymbol, Constraint> allowed, AssumedMarkers? assumed)
    {
        _allowed = allowed;
        _assumed = assumed;
        _computeInherited = ComputeInherited;
        _computeHasRelevantInterface = type => type.AllInterfaces.Any(IsContractRelevant);
        _isContractRelevantInterface = member => member is IMethodSymbol or IPropertySymbol && GetMarker(member) is not null;
        _computeContractRelevant = type => GetMarker(type) is not null || type.GetMembers().Any(_isContractRelevantInterface);
    }

    /// <summary>
    /// Returns <c>null</c> when the compilation cannot see the marker attributes, so the analyzer registers nothing
    /// and costs nothing.
    /// </summary>
    public static NoAllocModel? TryCreate(
        Compilation compilation, ImmutableArray<AdditionalText> additionalFiles, CancellationToken cancellationToken, AssumedMarkers? assumed = null)
    {
        if (assumed is null
            && compilation.GetTypesByMetadataName($"{AttributeNamespace}.{MarkerName}").IsEmpty
            && compilation.GetTypesByMetadataName($"{AttributeNamespace}.{UncheckedName}").IsEmpty)
            return null;

        var allowed = new Dictionary<ISymbol, Constraint>(SymbolEqualityComparer.Default);
        void Add(AllowlistEntry entry)
        {
            foreach (var symbol in Allowlist.Resolve(entry, compilation))
                allowed[symbol] = allowed.TryGetValue(symbol, out var existing) ? existing | entry.Constraints : entry.Constraints;
        }
        foreach (var entry in BaselineEntries.Value)
            Add(entry);
        foreach (var file in additionalFiles)
        {
            if (!Allowlist.IsAllowlistPath(file.Path) || file.GetText(cancellationToken) is not { } text)
                continue;
            foreach (var entry in Allowlist.Parse(text))
                Add(entry);
        }

        var allowedBySymbol = ImmutableDictionary.CreateBuilder<ISymbol, Constraint>(SymbolEqualityComparer.Default);
        allowedBySymbol.AddRange(allowed);
        return new NoAllocModel(allowedBySymbol.ToImmutable(), assumed);
    }

    public static ImmutableArray<AllowlistEntry> Baseline => BaselineEntries.Value;

    /// <summary>The marker written on the symbol itself, or <c>null</c>.</summary>
    public Marker? GetMarker(ISymbol symbol) => _markers.GetOrAdd(symbol.OriginalDefinition, ReadMarker);

    /// <summary>
    /// The effective contract: the member's own marker (or its property's), else the marker of its containing type
    /// together with what it inherits, else what it inherits. Local functions without a marker take the contract of
    /// the method they are declared in; code inside lambdas is not verified.
    /// </summary>
    public Contract GetContract(IMethodSymbol method)
    {
        method = Normalize(method);
        if (_contracts.TryGetValue(method, out var cached))
            return cached;
        // Most methods have no contract; computing that from the cached markers is cheaper than caching it.
        var contract = ComputeContract(method);
        if (contract.Required != Constraint.None || contract.Relaxed != Constraint.None)
            _contracts.TryAdd(method, contract);
        return contract;
    }

    /// <summary>
    /// What overridden and implemented members demand of this one. Only overrides, explicit implementations and
    /// public instance methods of a type with a marked interface can inherit; everything else answers without a
    /// cache entry, which keeps unmarked code cheap.
    /// </summary>
    public Constraint GetInherited(IMethodSymbol method)
    {
        method = Normalize(method);
        if (!method.IsOverride && method.ExplicitInterfaceImplementations.IsEmpty
            && (method.IsStatic || method.DeclaredAccessibility != Accessibility.Public
                || method.ContainingType is not { } type || !HasContractRelevantInterface(type)))
            return Constraint.None;
        return _inherited.GetOrAdd(method, _computeInherited);
    }

    /// <summary>Whether any interface the type implements declares a marker.</summary>
    public bool HasContractRelevantInterface(INamedTypeSymbol type) =>
        _relevantInterfaces.GetOrAdd(type.OriginalDefinition, _computeHasRelevantInterface);

    /// <summary>The contract under which code directly inside <paramref name="symbol"/> runs.</summary>
    public Contract GetContextContract(ISymbol symbol) =>
        symbol is IMethodSymbol { MethodKind: not MethodKind.AnonymousFunction } method ? GetContract(method) : default;

    /// <summary>What a callee promises, by source, and what it deliberately relaxes.</summary>
    public Promise GetPromise(IMethodSymbol callee)
    {
        callee = Normalize(callee);
        var contract = GetContract(callee);
        var intrinsic = callee.ContainingType is { } type && IsIntrinsic(type) || IsTriviallySafe(callee);
        var assumed = Allowed(callee);
        if (callee.AssociatedSymbol is { } associated)
            assumed |= Allowed(associated);
        if (callee.ContainingType is { } containing)
            assumed |= Allowed(containing);
        return new Promise(contract.Required, assumed, intrinsic, contract.Relaxed);
    }

    /// <summary>The forbidden-member entry for a callee, if any; overrides of forbidden members are forbidden too.</summary>
    public Forbidden? GetForbidden(IMethodSymbol callee) => _forbiddenCache.GetOrAdd(Normalize(callee), FindForbidden);

    /// <summary>Whether a type declares any marker, so overrides and implementations may inherit from it.</summary>
    public bool IsContractRelevant(INamedTypeSymbol type) =>
        _contractRelevant.GetOrAdd(type.OriginalDefinition, _computeContractRelevant);

    private Constraint Allowed(ISymbol symbol) =>
        _allowed.TryGetValue(symbol.OriginalDefinition, out var constraints) ? constraints : Constraint.None;

    private Contract ComputeContract(IMethodSymbol method)
    {
        var own = GetMarker(method) ?? (method.AssociatedSymbol is { } associated ? GetMarker(associated) : null);
        if (own is not null)
            return new Contract(own.Required, own.Relaxed, own.AllowSpin, own.Reason);

        if (method.MethodKind == MethodKind.LocalFunction)
            return GetContextContract(method.ContainingSymbol);

        var inherited = GetInherited(method);
        if (method.ContainingType is { } type && GetMarker(type) is { } typeMarker)
            return new Contract(typeMarker.Required | (typeMarker.Unchecked ? Constraint.None : inherited), typeMarker.Relaxed, typeMarker.AllowSpin, typeMarker.Reason);
        if (_assumed is { } assumed && method.ContainingAssembly is { } assembly && assumed.Assemblies.Contains(assembly.Name))
            inherited |= assumed.Required;
        return new Contract(inherited, Constraint.None, allowSpin: false);
    }

    private Constraint ComputeInherited(IMethodSymbol method)
    {
        var inherited = Constraint.None;
        if (method.OverriddenMethod is { } overridden)
            inherited |= GetContract(overridden).Required;
        foreach (var explicitImplementation in method.ExplicitInterfaceImplementations)
            inherited |= GetContract(explicitImplementation).Required;

        if (method is { IsStatic: false, ContainingType: { } type } && method.ExplicitInterfaceImplementations.IsEmpty)
        {
            foreach (var @interface in type.AllInterfaces)
            {
                if (!IsContractRelevant(@interface))
                    continue;
                foreach (var member in @interface.GetMembers(method.Name))
                {
                    if (member is IMethodSymbol interfaceMethod
                        && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(interfaceMethod)?.OriginalDefinition, method))
                        inherited |= GetContract(interfaceMethod).Required;
                }
            }
        }
        return inherited;
    }

    private static Forbidden? FindForbidden(IMethodSymbol method)
    {
        for (var current = method; current is not null; current = current.OverriddenMethod)
        {
            if (current.ContainingType is { } type && ForbiddenByName.TryGetValue(type.MetadataName, out var entries))
            {
                foreach (var (@namespace, members, forbidden) in entries)
                {
                    if ((members is null || Array.IndexOf(members, current.Name) >= 0) && IsNamespace(type.ContainingNamespace, @namespace))
                        return forbidden;
                }
            }
            if (current.ContainingNamespace is { Name: "Reflection", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } })
                return Reflection;
        }
        return null;
    }

    private static bool IsIntrinsic(INamedTypeSymbol type) =>
        type.ContainingType is null
        && IntrinsicByName.TryGetValue(type.MetadataName, out var @namespace)
        && IsNamespace(type.ContainingNamespace, @namespace);

    /// <summary>Whether <paramref name="symbol"/> is the namespace <paramref name="dotted"/>, compared part by part from the end.</summary>
    private static bool IsNamespace(INamespaceSymbol? symbol, string dotted)
    {
        var end = dotted.Length;
        while (end > 0)
        {
            var start = dotted.LastIndexOf('.', end - 1) + 1;
            if (symbol is null || symbol.IsGlobalNamespace || symbol.Name.Length != end - start
                || string.CompareOrdinal(symbol.Name, 0, dotted, start, end - start) != 0)
                return false;
            symbol = symbol.ContainingNamespace;
            end = start - 1;
        }
        return symbol is { IsGlobalNamespace: true };
    }

    /// <summary>
    /// Members that satisfy every constraint by construction: implicit parameterless struct constructors and
    /// non-virtual auto-property accessors (in source: accessors without a body; in metadata: compiler-generated).
    /// </summary>
    private static bool IsTriviallySafe(IMethodSymbol method)
    {
        if (method is { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: true, Parameters.IsEmpty: true, ContainingType.IsValueType: true })
            return true;

        if (method.AssociatedSymbol is not IPropertySymbol
            || method.IsVirtual || method.IsAbstract || method.IsOverride
            || method.ContainingType?.TypeKind == TypeKind.Interface)
            return false;

        if (method.IsImplicitlyDeclared)
            return true;

        if (method.DeclaringSyntaxReferences.IsEmpty)
        {
            return method.GetAttributes().Any(attribute => attribute.AttributeClass is
            {
                Name: "CompilerGeneratedAttribute",
                ContainingNamespace: { Name: "CompilerServices", ContainingNamespace: { Name: "Runtime", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } },
            });
        }

        return method.DeclaringSyntaxReferences.All(reference =>
            reference.GetSyntax() is AccessorDeclarationSyntax { Body: null, ExpressionBody: null });
    }

    private static Marker? ReadMarker(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { ContainingType: null, ContainingNamespace: { Name: AttributeNamespaceInner, ContainingNamespace: { Name: AttributeNamespaceOuter, ContainingNamespace.IsGlobalNamespace: true } } } type)
                continue;

            if (type.Name == MarkerName)
            {
                var constraints = Constraint.NoAlloc;
                if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int value)
                    constraints |= (Constraint)value & Constraint.All;
                var allow = 0;
                string? reason = null;
                foreach (var argument in attribute.NamedArguments)
                {
                    if (argument is { Key: "Allow", Value.Value: int flags })
                        allow = flags;
                    else if (argument is { Key: "Reason", Value.Value: string text })
                        reason = text;
                }
                var allowed = (Constraint)allow & Constraint.All;
                var spin = (allow & Constraints.AllowSpin) != 0;
                return new Marker(constraints, allowed, spin, @unchecked: false, reason);
            }

            if (type.Name == UncheckedName)
            {
                var reason = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
                return new Marker(Constraint.None, Constraint.All, allowSpin: false, @unchecked: true, reason);
            }
        }
        return null;
    }

    private static IMethodSymbol Normalize(IMethodSymbol method) => (method.ReducedFrom ?? method).OriginalDefinition;

    private static ImmutableArray<AllowlistEntry> LoadBaseline()
    {
        using var stream = typeof(NoAllocModel).Assembly.GetManifestResourceStream("Edict.NoAlloc.bcl.noalloc-allowlist.toml")
            ?? throw new InvalidOperationException("Embedded baseline allowlist is missing.");
        return Allowlist.Parse(Microsoft.CodeAnalysis.Text.SourceText.From(stream));
    }
}
