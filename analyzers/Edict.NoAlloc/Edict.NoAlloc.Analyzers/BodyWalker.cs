// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Edict.NoAlloc.Analyzers;

/// <summary>
/// Walks one verified body once and reports every operation its contract forbids. Lambda bodies are never entered
/// (the lambda itself is the delegate creation); local functions are entered with their own contract, which
/// defaults to the enclosing one. Implicit calls (foreach, using, deconstruction, conversions, operators,
/// indexers) are checked like explicit ones.
/// </summary>
internal sealed class BodyWalker : OperationWalker
{
    private readonly NoAllocModel _model;
    private readonly FindingSink _sink;
    private readonly Stack<(ISymbol Owner, Contract Contract)> _scopes = new();

    public BodyWalker(NoAllocModel model, FindingSink sink, ISymbol owner, Contract contract)
    {
        _model = model;
        _sink = sink;
        _scopes.Push((owner, contract));
        if (contract.IsChecked)
            sink.Enter(owner, contract);
    }

    private ISymbol OwnerSymbol => _scopes.Peek().Owner;

    private string Owner => OwnerSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

    private Contract Current => _scopes.Peek().Contract;

    private bool Requires(Constraint constraint) => (Current.Required & constraint) != 0;

    /// <summary>Reports declaration-level violations: async and iterator state machines.</summary>
    public static void CheckDeclaration(IMethodSymbol method, Contract contract, FindingSink sink)
    {
        var name = method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
        var location = method.Locations.FirstOrDefault() ?? Location.None;
        var noAsync = method.IsAsync && (contract.Required & Constraint.NoAsync) != 0;
        if (noAsync)
            sink.Report(method, Constraint.NoAsync, NoAllocDiagnostics.Async, location, [name]);
        if (method.IsAsync && (contract.Required & Constraint.NoAlloc) != 0 && (!noAsync || sink.ReportsAll))
            sink.Report(method, Constraint.NoAlloc, NoAllocDiagnostics.Allocation, location, [name, "async state machine"]);
        if (IsIterator(method) && (contract.Required & Constraint.NoAlloc) != 0)
            sink.Report(method, Constraint.NoAlloc, NoAllocDiagnostics.Allocation, location, [name, "iterator state machine"]);
    }

    /// <summary>
    /// Whether the method's own body contains <c>yield</c>; nested local functions and lambdas are separate
    /// methods. Roslyn 4.14 has no public iterator flag, and this runs only for no-alloc methods.
    /// </summary>
    private static bool IsIterator(IMethodSymbol method)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var root = reference.GetSyntax();
            var hasYield = root
                .DescendantNodes(node => node == root || node is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
                .OfType<YieldStatementSyntax>()
                .Any();
            if (hasYield)
                return true;
        }
        return false;
    }

    // ---- scopes -------------------------------------------------------------------------------------------

    public override void VisitLocalFunction(ILocalFunctionOperation operation)
    {
        var contract = _model.GetContract(operation.Symbol);
        if (!contract.IsChecked)
            return;
        CheckDeclaration(operation.Symbol, contract, _sink);
        _scopes.Push((operation.Symbol, contract));
        _sink.Enter(operation.Symbol, contract);
        base.VisitLocalFunction(operation);
        _scopes.Pop();
    }

    public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation) => ReportLambda(operation, operation);

    public override void VisitDelegateCreation(IDelegateCreationOperation operation)
    {
        if (operation.Target is IAnonymousFunctionOperation lambda)
            ReportLambda(lambda, operation);
        else
            ReportAllocation(operation, "delegate creation from a method group");
    }

    /// <summary>A capturing lambda allocates its closure (NA002); any other lambda becomes a delegate (NA001).</summary>
    private void ReportLambda(IAnonymousFunctionOperation lambda, IOperation operation)
    {
        if (!Requires(Constraint.NoAlloc))
            return;
        if (Captures(lambda))
            Report(NoAllocDiagnostics.BoxingOrCapture, operation, "closure capture");
        else
            Report(NoAllocDiagnostics.Allocation, operation, "delegate creation from a lambda");
    }

    private static bool Captures(IAnonymousFunctionOperation lambda)
    {
        if (lambda.Symbol.IsStatic)
            return false;
        // The lambdas of a query expression have a clause as their syntax, which data flow analysis does not take.
        if (lambda.SemanticModel is { } semanticModel
            && lambda.Syntax is ExpressionSyntax or StatementSyntax
            && semanticModel.AnalyzeDataFlow(lambda.Syntax) is { Succeeded: true } flow && !flow.CapturedInside.IsEmpty)
            return true;
        return lambda.Body.Descendants().Any(operation => operation is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance });
    }

    public override void VisitAttribute(IAttributeOperation operation)
    {
        // Attribute arguments are metadata, not code that runs in a NoAlloc section.
    }

    public override void VisitNameOf(INameOfOperation operation)
    {
        // nameof is a compile-time constant; its argument is never evaluated.
    }

    // ---- calls --------------------------------------------------------------------------------------------

    /// <summary>
    /// A constructor declared without an initializer calls its base class's parameterless constructor implicitly;
    /// the operation tree does not show that call, so it is checked at the constructor's name.
    /// </summary>
    public void CheckImplicitBaseConstructor(IMethodSymbol constructor)
    {
        if (constructor is not { MethodKind: MethodKind.Constructor, IsStatic: false, ContainingType: { TypeKind: TypeKind.Class } type }
            || ImplicitBaseConstructor(type) is not { } callee)
            return;
        foreach (var reference in constructor.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is ConstructorDeclarationSyntax { Initializer: null } syntax)
                CheckCallee(callee, null, syntax.Identifier.GetLocation());
        }
    }

    /// <summary>The base constructor a constructor without initializer runs; <c>System.Object</c>'s does nothing.</summary>
    public static IMethodSymbol? ImplicitBaseConstructor(INamedTypeSymbol type) =>
        type.BaseType is { SpecialType: not SpecialType.System_Object } baseType
            ? baseType.InstanceConstructors.FirstOrDefault(constructor => constructor.Parameters.All(parameter => parameter.IsOptional || parameter.IsParams))
            : null;

    public override void VisitInvocation(IInvocationOperation operation)
    {
        if (operation.TargetMethod.MethodKind == MethodKind.DelegateInvoke)
            ReportUnknownTarget(operation, operation.TargetMethod.ContainingType, "a delegate");
        else
            CheckCallee(operation.TargetMethod, operation);
        CheckInitialization(operation.TargetMethod.IsStatic ? operation.TargetMethod.ContainingType : null, operation);
        base.VisitInvocation(operation);
    }

    public override void VisitFunctionPointerInvocation(IFunctionPointerInvocationOperation operation)
    {
        ReportUnknownTarget(operation, operation.GetFunctionPointerSignature(), "a function pointer");
        base.VisitFunctionPointerInvocation(operation);
    }

    /// <summary>Delegates and function pointers have no statically known target, so no contract (NA008).</summary>
    private void ReportUnknownTarget(IOperation operation, ISymbol? target, string what)
    {
        if (Current.Required != Constraint.None)
            ReportCall(NoAllocDiagnostics.NoContract, Current.Required, operation,
                $"{Display(target)} (the target of {what} is unknown)", Constraints.Describe(Current.Required));
    }

    public override void VisitFieldReference(IFieldReferenceOperation operation)
    {
        if (operation.Field.IsStatic && !operation.Field.IsConst)
            CheckInitialization(operation.Field.ContainingType, operation);
        base.VisitFieldReference(operation);
    }

    public override void VisitPropertyReference(IPropertyReferenceOperation operation)
    {
        var property = operation.Property;
        if (!property.ContainingType.IsAnonymousType)
        {
            var (reads, writes) = Usage(operation);
            if (reads && property.GetMethod is { } getter)
                CheckCallee(getter, operation);
            if (writes && property.SetMethod is { } setter)
                CheckCallee(setter, operation);
            if (property.IsStatic)
                CheckInitialization(property.ContainingType, operation);
        }
        base.VisitPropertyReference(operation);
    }

    public override void VisitEventAssignment(IEventAssignmentOperation operation)
    {
        if (operation.EventReference is IEventReferenceOperation { Event: var @event }
            && (operation.Adds ? @event.AddMethod : @event.RemoveMethod) is { } accessor)
            CheckCallee(accessor, operation);
        base.VisitEventAssignment(operation);
    }

    public override void VisitImplicitIndexerReference(IImplicitIndexerReferenceOperation operation)
    {
        CheckSymbolCallee(operation.LengthSymbol, operation);
        CheckSymbolCallee(operation.IndexerSymbol, operation);
        base.VisitImplicitIndexerReference(operation);
    }

    public override void VisitForEachLoop(IForEachLoopOperation operation)
    {
        if (operation.Syntax is CommonForEachStatementSyntax syntax
            && !IsLoweredToIndexing(Unconverted(operation.Collection).Type)
            && operation.SemanticModel?.GetForEachStatementInfo(syntax) is { } info)
        {
            // One finding per loop: the first enumerator member that is not allowed names the problem. A sink
            // that wants every finding gets each member checked.
            IMethodSymbol?[] members =
            [
                info.GetEnumeratorMethod,
                info.MoveNextMethod,
                info.CurrentProperty?.GetMethod,
                info.DisposeMethod is { } dispose ? ResolveImplementation(info.GetEnumeratorMethod?.ReturnType, dispose) : null,
            ];
            if (_sink.ReportsAll)
            {
                foreach (var member in members)
                {
                    if (member is not null)
                        CheckCallee(member, operation.Collection);
                }
            }
            else if (members.FirstOrDefault(member => member is not null && !Satisfies(member)) is { } offending)
            {
                CheckCallee(offending, operation.Collection);
            }
        }
        base.VisitForEachLoop(operation);
    }

    public override void VisitUsing(IUsingOperation operation)
    {
        CheckDispose(operation.Resources);
        base.VisitUsing(operation);
    }

    public override void VisitUsingDeclaration(IUsingDeclarationOperation operation)
    {
        CheckDispose(operation.DeclarationGroup);
        base.VisitUsingDeclaration(operation);
    }

    public override void VisitDeconstructionAssignment(IDeconstructionAssignmentOperation operation)
    {
        if (operation.Syntax is AssignmentExpressionSyntax syntax && operation.SemanticModel is { } semanticModel)
            CheckDeconstruction(semanticModel.GetDeconstructionInfo(syntax), operation);
        base.VisitDeconstructionAssignment(operation);
    }

    public override void VisitRecursivePattern(IRecursivePatternOperation operation)
    {
        CheckSymbolCallee(operation.DeconstructSymbol, operation);
        base.VisitRecursivePattern(operation);
    }

    // ---- operators and conversions ------------------------------------------------------------------------

    public override void VisitBinaryOperator(IBinaryOperation operation)
    {
        if (operation.OperatorMethod is { } method)
            CheckCallee(method, operation);
        else if (operation.Type?.SpecialType == SpecialType.System_String && !operation.ConstantValue.HasValue)
            ReportAllocation(operation, "string concatenation");
        base.VisitBinaryOperator(operation);
    }

    public override void VisitUnaryOperator(IUnaryOperation operation)
    {
        if (operation.OperatorMethod is { } method)
            CheckCallee(method, operation);
        base.VisitUnaryOperator(operation);
    }

    public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
    {
        if (operation.OperatorMethod is { } method)
            CheckCallee(method, operation);
        else if (operation.Type?.SpecialType == SpecialType.System_String)
            ReportAllocation(operation, "string concatenation");
        CheckSymbolCallee(operation.GetInConversion().MethodSymbol, operation);
        CheckSymbolCallee(operation.GetOutConversion().MethodSymbol, operation);
        base.VisitCompoundAssignment(operation);
    }

    public override void VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation)
    {
        if (operation.OperatorMethod is { } method)
            CheckCallee(method, operation);
        base.VisitIncrementOrDecrement(operation);
    }

    public override void VisitConversion(IConversionOperation operation)
    {
        var conversion = operation.GetConversion();
        if (conversion.IsBoxing && Requires(Constraint.NoAlloc))
            Report(NoAllocDiagnostics.BoxingOrCapture, operation, $"boxing of {Display(operation.Operand.Type)}");
        if (conversion.IsUserDefined && conversion.MethodSymbol is { } method)
            CheckCallee(method, operation);
        base.VisitConversion(operation);
    }

    public override void VisitDeclarationPattern(IDeclarationPatternOperation operation)
    {
        CheckPatternBoxing(operation.InputType, operation.NarrowedType, operation);
        base.VisitDeclarationPattern(operation);
    }

    public override void VisitTypePattern(ITypePatternOperation operation)
    {
        CheckPatternBoxing(operation.InputType, operation.NarrowedType, operation);
        base.VisitTypePattern(operation);
    }

    // ---- allocations --------------------------------------------------------------------------------------

    public override void VisitObjectCreation(IObjectCreationOperation operation)
    {
        if (operation.Type is { IsReferenceType: true } type)
            ReportAllocation(operation, $"new {Display(type)}");
        else if (operation.Constructor is { } constructor)
            CheckCallee(constructor, operation);
        CheckInitialization(operation.Type as INamedTypeSymbol, operation);
        base.VisitObjectCreation(operation);
    }

    public override void VisitTypeParameterObjectCreation(ITypeParameterObjectCreationOperation operation)
    {
        if (operation.Type is not { IsValueType: true })
            ReportAllocation(operation, $"new {Display(operation.Type)}()");
        base.VisitTypeParameterObjectCreation(operation);
    }

    public override void VisitAnonymousObjectCreation(IAnonymousObjectCreationOperation operation)
    {
        ReportAllocation(operation, "anonymous object");
        base.VisitAnonymousObjectCreation(operation);
    }

    public override void VisitArrayCreation(IArrayCreationOperation operation)
    {
        if (operation is { IsImplicit: true, Parent: IArgumentOperation { ArgumentKind: ArgumentKind.ParamArray } })
        {
            // A params call without arguments uses a cached empty array.
            if (!(operation.DimensionSizes.Length == 1 && operation.DimensionSizes[0].ConstantValue is { HasValue: true, Value: 0 }))
                ReportAllocation(operation, "params array");
        }
        else
        {
            ReportAllocation(operation, "array creation");
        }
        base.VisitArrayCreation(operation);
    }

    public override void VisitArrayElementReference(IArrayElementReferenceOperation operation)
    {
        if (operation.Indices.Any(index => IsSystemType(index.Type, "Range")))
            ReportAllocation(operation, "array range copy");
        base.VisitArrayElementReference(operation);
    }

    public override void VisitInterpolatedString(IInterpolatedStringOperation operation)
    {
        if (!operation.ConstantValue.HasValue && operation.Parent is not IInterpolatedStringHandlerCreationOperation)
            ReportAllocation(operation, "string interpolation");
        base.VisitInterpolatedString(operation);
    }

    public override void VisitInterpolatedStringHandlerCreation(IInterpolatedStringHandlerCreationOperation operation)
    {
        // Lowered to the default handler, the result is a new string; a custom handler is checked like any
        // other struct: its constructor and Append calls must be vetted.
        if (IsSystemType(operation.HandlerCreation.Type, "DefaultInterpolatedStringHandler", "System", "Runtime", "CompilerServices"))
        {
            ReportAllocation(operation, "string interpolation");
            // Still check the interpolated expressions themselves, not the handler plumbing around them.
            foreach (var part in operation.Content.ChildOperations)
            {
                if (part is IInterpolatedStringAppendOperation { AppendCall: IInvocationOperation append }
                    && part.Kind == OperationKind.InterpolatedStringAppendFormatted)
                {
                    foreach (var argument in append.Arguments)
                        Visit(argument.Value);
                }
            }
            return;
        }
        base.VisitInterpolatedStringHandlerCreation(operation);
    }

    public override void VisitWith(IWithOperation operation)
    {
        if (operation.Type is { IsReferenceType: true })
            ReportAllocation(operation, "with expression on a reference type");
        base.VisitWith(operation);
    }

    public override void VisitCollectionExpression(ICollectionExpressionOperation operation)
    {
        if (!IsSpan(operation.Type))
            ReportAllocation(operation, $"collection expression of type {Display(operation.Type)}");
        base.VisitCollectionExpression(operation);
    }

    public override void DefaultVisit(IOperation operation)
    {
        if (operation.Kind is OperationKind.DynamicInvocation or OperationKind.DynamicMemberReference
            or OperationKind.DynamicObjectCreation or OperationKind.DynamicIndexerAccess)
        {
            var noDynamic = Requires(Constraint.NoDynamic);
            if (noDynamic)
                Report(NoAllocDiagnostics.Dynamic, operation, "dynamic binding");
            if (!noDynamic || _sink.ReportsAll)
                ReportAllocation(operation, "dynamic binding");
        }
        base.DefaultVisit(operation);
    }

    // ---- throw and lock -----------------------------------------------------------------------------------

    public override void VisitThrow(IThrowOperation operation)
    {
        if (Requires(Constraint.NoThrow))
        {
            // The thrown object is part of the same violation; reporting its allocation too would only add noise.
            _sink.Report(OwnerSymbol, Constraint.NoThrow, NoAllocDiagnostics.Throw, operation.Syntax.GetLocation(), [Owner]);
            if (!_sink.ReportsAll)
                return;
        }
        base.VisitThrow(operation);
    }

    public override void VisitLock(ILockOperation operation)
    {
        if (Requires(Constraint.NoLock))
            Report(NoAllocDiagnostics.Lock, operation.LockedValue, "lock statement");
        base.VisitLock(operation);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reports what calling <paramref name="callee"/> violates under the current contract: a forbidden member of a
    /// required constraint (a proven operation), a promise relaxed by Allow or [NoAllocUnchecked] (NA013), or a
    /// missing promise (NA008). The analyzer reports the first of these only; a sink that wants every finding gets
    /// all of them. What the callee does promise is passed to the sink as a call.
    /// </summary>
    private void CheckCallee(IMethodSymbol callee, IOperation operation) => CheckCallee(callee, operation, null);

    /// <param name="at">Where the call is when <paramref name="operation"/> does not show it (an implicit base constructor call).</param>
    private void CheckCallee(IMethodSymbol callee, IOperation? operation, Location? at)
    {
        var required = Current.Required;
        if (required == Constraint.None)
            return;

        var promise = _model.GetPromise(callee);
        var provided = promise.Provided;
        var bySpin = Constraint.None;
        if (_model.GetForbidden(callee) is { } forbidden && (required & forbidden.Constraint) != 0)
        {
            if (forbidden.IsSpin && Current.AllowSpin)
            {
                bySpin = forbidden.Constraint & ~provided;
            }
            else
            {
                _sink.Report(OwnerSymbol, forbidden.Constraint, forbidden.Descriptor, at ?? operation!.Syntax.GetLocation(),
                    [Owner, $"'{Display(callee)}' ({forbidden.What})"]);
                if (!_sink.ReportsAll)
                    return;
            }
            provided |= forbidden.Constraint;
        }

        var missing = required & ~provided;
        var relaxed = missing & promise.Relaxed;
        if (relaxed != Constraint.None)
        {
            _sink.Report(OwnerSymbol, relaxed, NoAllocDiagnostics.RelaxedCallee, at ?? operation!.Syntax.GetLocation(),
                [Owner, Display(callee), Constraints.Describe(relaxed), RelaxedBy(callee)]);
        }
        if (missing != relaxed && (relaxed == Constraint.None || _sink.ReportsAll))
        {
            _sink.Report(OwnerSymbol, missing & ~relaxed, NoAllocDiagnostics.NoContract, at ?? operation!.Syntax.GetLocation(),
                [Owner, Display(callee), Constraints.Describe(missing & ~relaxed)]);
        }

        var kept = required & ~missing;
        if (_sink.RecordsCalls && !promise.Intrinsic && kept != Constraint.None)
        {
            _sink.Call(OwnerSymbol, callee, at ?? operation!.Syntax.GetLocation(), operation is not null && IsDispatched(callee, operation),
                kept & promise.ByContract, kept & promise.ByAllowlist & ~promise.ByContract, bySpin);
        }
    }

    /// <summary>
    /// Whether the call binds at run time to an override or implementation: a virtual, abstract or interface member
    /// called neither through <c>base</c> nor on a sealed type or a value type.
    /// </summary>
    private static bool IsDispatched(IMethodSymbol callee, IOperation operation)
    {
        if (!(callee.IsVirtual || callee.IsAbstract || callee.IsOverride) || callee.IsSealed)
            return false;
        if (callee.IsStatic)
            return true;
        if (callee.ContainingType is { IsSealed: true } or { IsValueType: true })
            return false;
        if (operation is IInvocationOperation invocation)
            return invocation.IsVirtual;
        return operation.Syntax is not MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax }
            and not ElementAccessExpressionSyntax { Expression: BaseExpressionSyntax };
    }

    /// <summary>Whether calling <paramref name="callee"/> violates nothing under the current contract.</summary>
    private bool Satisfies(IMethodSymbol callee)
    {
        var required = Current.Required;
        var provided = _model.GetPromise(callee).Provided;
        if (_model.GetForbidden(callee) is { } forbidden && (required & forbidden.Constraint) != 0)
        {
            if (!(forbidden.IsSpin && Current.AllowSpin))
                return false;
            provided |= forbidden.Constraint;
        }
        return (required & ~provided) == Constraint.None;
    }

    private void ReportCall(DiagnosticDescriptor descriptor, Constraint constraints, IOperation operation, params object[] arguments) =>
        _sink.Report(OwnerSymbol, constraints, descriptor, operation.Syntax.GetLocation(), [Owner, .. arguments]);

    private string RelaxedBy(IMethodSymbol callee)
    {
        bool IsUnchecked(ISymbol? symbol) => symbol is not null && _model.GetMarker(symbol) is { Unchecked: true };
        return IsUnchecked(callee) || IsUnchecked(callee.AssociatedSymbol) || IsUnchecked(callee.ContainingType)
            ? "[NoAllocUnchecked]"
            : "Allow";
    }

    /// <summary>NA011: a static member or a constructor of a source type with an explicit static constructor, outside that type.</summary>
    private void CheckInitialization(INamedTypeSymbol? type, IOperation operation)
    {
        if (type is null || !Requires(Constraint.NoInit) || !HasExplicitStaticConstructor(type))
            return;
        for (var owner = _scopes.Peek().Owner.ContainingType; owner is not null; owner = owner.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(owner.OriginalDefinition, type.OriginalDefinition))
                return;
        }
        _sink.Report(OwnerSymbol, Constraint.NoInit, NoAllocDiagnostics.TypeInitialization, operation.Syntax.GetLocation(), [Owner, Display(type)]);
    }

    private static bool HasExplicitStaticConstructor(INamedTypeSymbol type) =>
        type.StaticConstructors.Any(constructor => !constructor.IsImplicitlyDeclared && !constructor.DeclaringSyntaxReferences.IsEmpty);

    private void ReportAllocation(IOperation operation, string detail)
    {
        if (Requires(Constraint.NoAlloc))
            Report(NoAllocDiagnostics.Allocation, operation, detail);
    }

    private void CheckSymbolCallee(ISymbol? symbol, IOperation operation)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                CheckCallee(method, operation);
                break;
            case IPropertySymbol { GetMethod: { } getter }:
                CheckCallee(getter, operation);
                break;
        }
    }

    private void CheckDispose(IOperation resources)
    {
        var disposable = resources.SemanticModel?.Compilation.GetSpecialType(SpecialType.System_IDisposable);
        var interfaceDispose = disposable?.GetMembers("Dispose").OfType<IMethodSymbol>().FirstOrDefault();

        if (resources is IVariableDeclarationGroupOperation group)
        {
            foreach (var declarator in group.Declarations.SelectMany(declaration => declaration.Declarators))
                CheckDisposeOf(declarator.Symbol.Type, declarator, interfaceDispose);
        }
        else
        {
            CheckDisposeOf(resources.Type, resources, interfaceDispose);
        }
    }

    private void CheckDisposeOf(ITypeSymbol? type, IOperation operation, IMethodSymbol? interfaceDispose)
    {
        // Ref structs dispose by pattern; everything else through IDisposable.
        var dispose = type is null ? null
            : type.IsRefLikeType ? type.GetMembers("Dispose").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.IsEmpty && !m.IsStatic)
            : interfaceDispose is not null ? ResolveImplementation(type, interfaceDispose)
            : null;
        if (dispose is not null)
            CheckCallee(dispose, operation);
    }

    private void CheckDeconstruction(DeconstructionInfo info, IOperation operation)
    {
        if (info.Method is { } method)
            CheckCallee(method, operation);
        if (info.Conversion is { IsUserDefined: true, MethodSymbol: { } conversion })
            CheckCallee(conversion, operation);
        foreach (var nested in info.Nested)
            CheckDeconstruction(nested, operation);
    }

    private void CheckPatternBoxing(ITypeSymbol inputType, ITypeSymbol narrowedType, IOperation operation)
    {
        if (inputType is { IsValueType: true } && narrowedType is { IsReferenceType: true } && Requires(Constraint.NoAlloc))
            Report(NoAllocDiagnostics.BoxingOrCapture, operation, $"boxing of {Display(inputType)}");
    }

    private void Report(DiagnosticDescriptor descriptor, IOperation operation, string detail) =>
        Report(descriptor, NoAllocDiagnostics.ConstraintOf(descriptor), operation, detail);

    private void Report(DiagnosticDescriptor descriptor, Constraint constraint, IOperation operation, string detail) =>
        _sink.Report(OwnerSymbol, constraint, descriptor, operation.Syntax.GetLocation(), [Owner, detail]);

    /// <summary>Which accessors a property reference runs: assignments write, compound assignments do both.</summary>
    private static (bool Reads, bool Writes) Usage(IPropertyReferenceOperation operation) => operation.Parent switch
    {
        ISimpleAssignmentOperation assignment when assignment.Target == operation => (false, true),
        ICompoundAssignmentOperation compound when compound.Target == operation => (true, true),
        ICoalesceAssignmentOperation coalesce when coalesce.Target == operation => (true, true),
        IIncrementOrDecrementOperation increment when increment.Target == operation => (true, true),
        _ => (true, false),
    };

    /// <summary>The foreach collection arrives wrapped in an implicit conversion to the enumerable type.</summary>
    private static IOperation Unconverted(IOperation operation) =>
        operation is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : operation;

    private static IMethodSymbol ResolveImplementation(ITypeSymbol? type, IMethodSymbol interfaceMethod) =>
        type?.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol ?? interfaceMethod;

    /// <summary>foreach over arrays and strings compiles to an index loop without an enumerator.</summary>
    private static bool IsLoweredToIndexing(ITypeSymbol? type) =>
        type is IArrayTypeSymbol { IsSZArray: true } || type?.SpecialType == SpecialType.System_String;

    private static bool IsSpan(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Arity: 1 } named
        && (IsSystemType(named, "Span") || IsSystemType(named, "ReadOnlySpan"));

    private static bool IsSystemType(ITypeSymbol? type, string name, params string[] namespaces)
    {
        if (type is null || type.Name != name || type.ContainingType is not null)
            return false;
        var expected = namespaces.Length == 0 ? ["System"] : namespaces;
        var current = type.ContainingNamespace;
        for (var i = expected.Length - 1; i >= 0; i--)
        {
            if (current is null || current.Name != expected[i])
                return false;
            current = current.ContainingNamespace;
        }
        return current is { IsGlobalNamespace: true };
    }

    private static string Display(ISymbol? symbol) =>
        symbol?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat) ?? "?";
}
