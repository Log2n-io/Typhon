using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Typhon.Analyzers;

/// <summary>
/// Analyzer that detects IDisposable instances returned from method calls that are not properly disposed.
/// This addresses limitations in CA2000 which lacks inter-procedural analysis and misses many patterns.
///
/// Critical Typhon types (<see cref="OwnershipCatalog"/>: marked <c>[MustDispose]</c>, or owning one by value) are reported as errors because failing to
/// dispose them causes system-level bugs: pages pinned for good, a checkpoint that never covers them again and a WAL that is never recycled (CP-13).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class DisposableNotDisposedAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON004";
    private const string Category = "Reliability";

    // Diagnostic for discarded disposable results (Method(); or _ = Method();)
    private static readonly LocalizableString DiscardedTitle =
        "IDisposable result is discarded";

    private static readonly LocalizableString DiscardedMessageFormat =
        "'{0}' returns '{1}' which is discarded without being disposed{2}";

    private static readonly LocalizableString DiscardedDescription =
        "Method calls that return IDisposable instances must have their results captured and disposed. " +
        "Discarding the result causes a resource leak.";

    private static readonly DiagnosticDescriptor DiscardedRule = new DiagnosticDescriptor(
        DiagnosticId,
        DiscardedTitle,
        DiscardedMessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: DiscardedDescription);

    // Diagnostic for a critical value used once as a receiver and dropped (tx.For<T>().TryOpen(...))
    private static readonly DiagnosticDescriptor ReceiverRule = new DiagnosticDescriptor(
        DiagnosticId,
        "IDisposable result used as a receiver and dropped",
        "'{0}' returns '{1}', which is used once and dropped without being disposed{2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A critical value created only to call a member on it is never disposed. Take it into a 'using' local first.");

    // Diagnostic for variables that are never disposed
    private static readonly LocalizableString UndisposedTitle =
        "IDisposable variable is never disposed";

    private static readonly LocalizableString UndisposedMessageFormat =
        "Variable '{0}' of type '{1}' is never disposed{2}";

    private static readonly LocalizableString UndisposedDescription =
        "Variables holding IDisposable instances must be disposed before going out of scope. " +
        "Use a 'using' statement/declaration or call Dispose() explicitly.";

    private static readonly DiagnosticDescriptor UndisposedRule = new DiagnosticDescriptor(
        DiagnosticId,
        UndisposedTitle,
        UndisposedMessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: UndisposedDescription);

    // Diagnostic for reassignment without disposing first
    private static readonly LocalizableString ReassignmentTitle =
        "IDisposable reassigned without disposing previous value";

    private static readonly LocalizableString ReassignmentMessageFormat =
        "Variable '{0}' is reassigned without disposing the previous '{1}' value{2}";

    private static readonly LocalizableString ReassignmentDescription =
        "When reassigning a variable that holds an IDisposable, the previous value must be disposed first " +
        "to prevent resource leaks.";

    private static readonly DiagnosticDescriptor ReassignmentRule = new DiagnosticDescriptor(
        DiagnosticId,
        ReassignmentTitle,
        ReassignmentMessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: ReassignmentDescription);


    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiscardedRule, UndisposedRule, ReassignmentRule, ReceiverRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Use operation block analysis to track disposables across the entire method body
        context.RegisterCompilationStartAction(start =>
        {
            var catalog = new OwnershipCatalog(start.Compilation);
            start.RegisterOperationBlockAction(block => AnalyzeOperationBlock(block, catalog));
        });
    }

    private static void AnalyzeOperationBlock(OperationBlockAnalysisContext context, OwnershipCatalog catalog)
    {
        // Only analyze method bodies
        if (context.OwningSymbol is not IMethodSymbol)
            return;

        // Track state for each local variable holding a disposable
        var tracker = new DisposableTracker(catalog);

        foreach (var block in context.OperationBlocks)
        {
            AnalyzeOperationsRecursively(block, tracker, context);
        }

        // Report any variables that were never disposed
        foreach (var undisposed in tracker.GetUndisposedVariables())
        {
            var consequence = GetConsequenceMessage(undisposed.Type, catalog);
            var diagnostic = Diagnostic.Create(
                UndisposedRule,
                undisposed.Location,
                undisposed.Name,
                undisposed.TypeName,
                consequence);
            context.ReportDiagnostic(diagnostic);
        }
    }

    private static void AnalyzeOperationsRecursively(
        IOperation operation,
        DisposableTracker tracker,
        OperationBlockAnalysisContext context)
    {
        switch (operation)
        {
            case IUsingOperation usingOp:
                // using statement - resources are automatically disposed
                // Process body but don't track the declared variable
                if (usingOp.Body != null)
                {
                    AnalyzeOperationsRecursively(usingOp.Body, tracker, context);
                }
                return; // Don't recurse into children normally - we handled the body

            case IUsingDeclarationOperation:
                // using declaration - automatically disposed at end of scope
                return;

            case IVariableDeclarationGroupOperation declGroup:
                foreach (var decl in declGroup.Declarations)
                {
                    AnalyzeVariableDeclaration(decl, tracker, context);
                }
                break;

            case IExpressionStatementOperation exprStmt:
                // Check for discarded disposable results (e.g., Method();)
                AnalyzeExpressionStatement(exprStmt, tracker, context);
                break;

            case ISimpleAssignmentOperation assignment:
                AnalyzeAssignment(assignment, tracker, context);
                break;

            case IInvocationOperation invocation:
                // Check if this is a Dispose() call
                AnalyzeInvocation(invocation, tracker);
                AnalyzeReceiverTemporary(invocation, tracker.Catalog, context);
                break;

            case IReturnOperation returnOp:
                // Returning a disposable transfers ownership to caller
                AnalyzeReturn(returnOp, tracker);
                break;

            case IFieldReferenceOperation fieldRef when IsAssignmentTarget(fieldRef):
                // Assigning to a field transfers ownership to containing object
                AnalyzeFieldAssignment(fieldRef, tracker);
                break;

            case IPropertyReferenceOperation propRef when IsAssignmentTarget(propRef):
                // Assigning to a property transfers ownership
                AnalyzePropertyAssignment(propRef, tracker);
                break;

            case IForEachLoopOperation forEach when Unwrap(forEach.Collection) is ILocalReferenceOperation collection:
                // foreach disposes the enumerator it takes: an enumerator that is its own enumerable (GetEnumerator() => this) hands itself over, and a
                // 'using' on the local as well would dispose it twice
                tracker.MarkOwnershipTransferred(collection.Local);
                break;

            case IArgumentOperation argument:
                // Passing to out/ref parameter or to a method that takes ownership
                AnalyzeArgument(argument, tracker);
                break;
        }

        // Recurse into children
        foreach (var child in operation.ChildOperations)
        {
            AnalyzeOperationsRecursively(child, tracker, context);
        }
    }

    private static void AnalyzeVariableDeclaration(
        IVariableDeclarationOperation declaration,
        DisposableTracker tracker,
        OperationBlockAnalysisContext context)
    {
        foreach (var declarator in declaration.Declarators)
        {
            if (declarator.Initializer?.Value == null)
                continue;

            var initialValue = declarator.Initializer.Value;
            var local = declarator.Symbol;

            // Check if the type is critical
            if (!IsDisposableType(local.Type, tracker.Catalog))
                continue;

            // A ref local aliases a value someone else owns (a rented, cached accessor): no copy, nothing to dispose here
            if (local.RefKind != RefKind.None)
                continue;

            // Check if this is inside a using declaration
            if (IsInsideUsingDeclaration(declarator))
                continue;

            // Skip traversal patterns - these are not new allocations
            // e.g., var cur = Head; or var cur = node.Next;
            if (IsTraversalPattern(initialValue))
                continue;

            // `default` or `null` owns nothing: the value assigned later is what is tracked
            if (IsEmptyValue(initialValue))
                continue;

            // Track this variable
            tracker.TrackVariable(local, initialValue, declarator.Syntax.GetLocation());
        }
    }

    private static void AnalyzeExpressionStatement(
        IExpressionStatementOperation exprStmt,
        DisposableTracker tracker,
        OperationBlockAnalysisContext context)
    {
        var expr = exprStmt.Operation;

        // Unwrap conversion
        while (expr is IConversionOperation conversion)
        {
            expr = conversion.Operand;
        }

        // Check for invocation with discarded result
        if (expr is IInvocationOperation invocation)
        {
            var returnType = invocation.TargetMethod.ReturnType;
            if (invocation.TargetMethod.RefKind == RefKind.None && IsDisposableType(returnType, tracker.Catalog))
            {
                var consequence = GetConsequenceMessage(returnType, tracker.Catalog);
                var diagnostic = Diagnostic.Create(
                    DiscardedRule,
                    exprStmt.Syntax.GetLocation(),
                    invocation.TargetMethod.Name,
                    returnType.Name,
                    consequence);
                context.ReportDiagnostic(diagnostic);
            }
        }

        // Check for explicit discard: _ = Method();
        if (expr is ISimpleAssignmentOperation assignment &&
            assignment.Target is IDiscardOperation)
        {
            var value = assignment.Value;
            while (value is IConversionOperation conv)
            {
                value = conv.Operand;
            }

            if (value is IInvocationOperation discardedInvocation)
            {
                var returnType = discardedInvocation.TargetMethod.ReturnType;
                if (discardedInvocation.TargetMethod.RefKind == RefKind.None && IsDisposableType(returnType, tracker.Catalog))
                {
                    var consequence = GetConsequenceMessage(returnType, tracker.Catalog);
                    var diagnostic = Diagnostic.Create(
                        DiscardedRule,
                        exprStmt.Syntax.GetLocation(),
                        discardedInvocation.TargetMethod.Name,
                        returnType.Name,
                        consequence);
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
    }

    private static void AnalyzeAssignment(
        ISimpleAssignmentOperation assignment,
        DisposableTracker tracker,
        OperationBlockAnalysisContext context)
    {
        // Only handle assignments to local variables
        if (assignment.Target is not ILocalReferenceOperation localRef)
            return;

        var local = localRef.Local;

        // Check if we're assigning a disposable (a ref reassignment aliases, it does not own)
        if (assignment.IsRef || !IsDisposableType(local.Type, tracker.Catalog))
            return;

        // Skip traversal patterns - these are not new allocations
        // e.g., cur = cur.Next; or cur = node.Previous;
        if (IsTraversalPattern(assignment.Value))
        {
            // For traversal, just stop tracking - we don't own the value
            tracker.MarkOwnershipTransferred(local);
            return;
        }

        // Assigning `default` or `null` hands nothing over
        if (IsEmptyValue(assignment.Value))
            return;

        // Check if this variable already holds an undisposed value
        if (tracker.IsTrackedAndUndisposed(local))
        {
            var consequence = GetConsequenceMessage(local.Type, tracker.Catalog);
            var diagnostic = Diagnostic.Create(
                ReassignmentRule,
                assignment.Syntax.GetLocation(),
                local.Name,
                local.Type.Name,
                consequence);
            context.ReportDiagnostic(diagnostic);
        }

        // Track the new value
        tracker.TrackVariable(local, assignment.Value, assignment.Syntax.GetLocation());
    }

    private static void AnalyzeInvocation(IInvocationOperation invocation, DisposableTracker tracker)
    {
        // Check if this is a Dispose() call on a tracked variable
        if (invocation.TargetMethod.Name != "Dispose")
            return;

        if (invocation.Instance is ILocalReferenceOperation localRef)
        {
            tracker.MarkDisposed(localRef.Local);
        }

        // Also check for null-conditional: x?.Dispose()
        if (invocation.Instance is IConditionalAccessInstanceOperation)
        {
            // The parent should be a conditional access operation
            var parent = invocation.Parent;
            while (parent != null)
            {
                if (parent is IConditionalAccessOperation conditionalAccess &&
                    conditionalAccess.Operation is ILocalReferenceOperation condLocalRef)
                {
                    tracker.MarkDisposed(condLocalRef.Local);
                    break;
                }
                parent = parent.Parent;
            }
        }
    }

    /// <summary>
    /// A critical value returned by a call and used only as the receiver of another member — <c>tx.For&lt;T&gt;().TryOpen(...)</c> — is created and
    /// dropped in one expression: nothing can dispose it. A ref-returning call hands out an existing value, not a new one.
    /// </summary>
    private static void AnalyzeReceiverTemporary(IInvocationOperation invocation, OwnershipCatalog catalog, OperationBlockAnalysisContext context)
    {
        if (invocation.TargetMethod.RefKind != RefKind.None || !catalog.IsCritical(invocation.Type, out var consequence))
            return;

        IOperation self = invocation;
        var parent = invocation.Parent;
        while (parent is IConversionOperation)
        {
            self = parent;
            parent = parent.Parent;
        }

        var receiver = parent switch
        {
            IInvocationOperation call => call.Instance,
            IPropertyReferenceOperation property => property.Instance,
            IFieldReferenceOperation field => field.Instance,
            IEventReferenceOperation evt => evt.Instance,
            _ => null,
        };

        if (receiver != self)
            return;

        // The temporary's own Dispose() is the one receiver call that does dispose it
        if (parent is IInvocationOperation { TargetMethod.Name: "Dispose" })
            return;

        context.ReportDiagnostic(Diagnostic.Create(ReceiverRule, invocation.Syntax.GetLocation(), invocation.TargetMethod.Name, invocation.Type.Name, consequence));
    }

    private static bool IsEmptyValue(IOperation operation) =>
        Unwrap(operation) is IDefaultValueOperation || Unwrap(operation) is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }
        return operation;
    }

    private static void AnalyzeReturn(IReturnOperation returnOp, DisposableTracker tracker)
    {
        if (returnOp.ReturnedValue == null)
            return;

        var value = returnOp.ReturnedValue;
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        // Returning a local transfers ownership to caller
        if (value is ILocalReferenceOperation localRef)
        {
            tracker.MarkOwnershipTransferred(localRef.Local);
        }
    }

    private static void AnalyzeFieldAssignment(IFieldReferenceOperation fieldRef, DisposableTracker tracker)
    {
        // Find the assignment operation
        var parent = fieldRef.Parent;
        if (parent is ISimpleAssignmentOperation assignment && assignment.Value is ILocalReferenceOperation localRef)
        {
            tracker.MarkOwnershipTransferred(localRef.Local);
        }
    }

    private static void AnalyzePropertyAssignment(IPropertyReferenceOperation propRef, DisposableTracker tracker)
    {
        // Find the assignment operation
        var parent = propRef.Parent;
        if (parent is ISimpleAssignmentOperation assignment && assignment.Value is ILocalReferenceOperation localRef)
        {
            tracker.MarkOwnershipTransferred(localRef.Local);
        }
    }

    private static void AnalyzeArgument(IArgumentOperation argument, DisposableTracker tracker)
    {
        // Passing to out/ref transfers ownership
        if (argument.Parameter?.RefKind is RefKind.Out or RefKind.Ref)
        {
            if (argument.Value is ILocalReferenceOperation localRef)
            {
                tracker.MarkOwnershipTransferred(localRef.Local);
            }
        }

        // Check for [TransfersOwnership] attribute on the parameter
        if (argument.Parameter != null && HasTransfersOwnershipAttribute(argument.Parameter))
        {
            if (argument.Value is ILocalReferenceOperation localRef)
            {
                tracker.MarkOwnershipTransferred(localRef.Local);
            }
            return;
        }

        // Passing to a constructor parameter - assume ownership transfer (conservative)
        if (argument.Parent is IObjectCreationOperation)
        {
            if (argument.Value is ILocalReferenceOperation localRef)
            {
                tracker.MarkOwnershipTransferred(localRef.Local);
            }
        }
    }

    /// <summary>
    /// Checks if a parameter has the [TransfersOwnership] attribute.
    /// </summary>
    private static bool HasTransfersOwnershipAttribute(IParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            var attrName = attribute.AttributeClass?.Name;
            if (attrName == "TransfersOwnershipAttribute" || attrName == "TransfersOwnership")
            {
                // Verify it's from Typhon.Engine namespace
                var ns = attribute.AttributeClass?.ContainingNamespace?.ToDisplayString();
                if (ns == "Typhon.Engine" || ns == "global::Typhon.Engine")
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsAssignmentTarget(IOperation operation)
    {
        return operation.Parent is ISimpleAssignmentOperation assignment &&
               assignment.Target == operation;
    }

    private static bool IsInsideUsingDeclaration(IVariableDeclaratorOperation declarator)
    {
        // Check if this declarator is part of a using declaration
        var syntax = declarator.Syntax;
        while (syntax != null)
        {
            if (syntax is UsingStatementSyntax)
                return true;
            if (syntax is LocalDeclarationStatementSyntax localDecl && localDecl.UsingKeyword != default)
                return true;
            syntax = syntax.Parent;
        }
        return false;
    }

    /// <summary>
    /// Detects traversal patterns where a value is obtained from a property/field access
    /// rather than being newly created. These patterns don't represent new allocations
    /// and the caller doesn't own the value.
    /// Examples: var cur = Head; cur = cur.Next; var t = _transaction;
    /// </summary>
    private static bool IsTraversalPattern(IOperation operation)
    {
        var unwrapped = operation;
        while (unwrapped is IConversionOperation conversion)
        {
            unwrapped = conversion.Operand;
        }

        // Property access (e.g., node.Next, Head, Tail)
        if (unwrapped is IPropertyReferenceOperation)
            return true;

        // Field access (e.g., _head, _transaction)
        if (unwrapped is IFieldReferenceOperation)
            return true;

        // Local variable reference (e.g., var x = existingVar;)
        if (unwrapped is ILocalReferenceOperation)
            return true;

        // Parameter reference (e.g., var x = paramTransaction;)
        if (unwrapped is IParameterReferenceOperation)
            return true;

        // Array/indexer access (e.g., var x = transactions[i];)
        if (unwrapped is IArrayElementReferenceOperation)
            return true;

        // Unsafe.AsRef calls - these are reference casts, not new allocations
        // e.g., ref var owner = ref Unsafe.AsRef<ChunkAccessor>(_ownerPtr);
        if (unwrapped is IInvocationOperation invocation)
        {
            var methodName = invocation.TargetMethod.Name;
            var typeName = invocation.TargetMethod.ContainingType?.Name;
            if (typeName == "Unsafe" && (methodName == "AsRef" || methodName == "As" || methodName == "NullRef"))
                return true;
        }

        return false;
    }

    // ONLY track critical Typhon types to avoid false positives
    private static bool IsDisposableType(ITypeSymbol type, OwnershipCatalog catalog) => type != null && catalog.IsCritical(type);

    private static string GetConsequenceMessage(ITypeSymbol type, OwnershipCatalog catalog) => catalog.IsCritical(type, out var consequence) ? consequence : "";

    /// <summary>
    /// Tracks the disposal state of local variables within a method.
    /// </summary>
    private sealed class DisposableTracker
    {
        private readonly Dictionary<ILocalSymbol, TrackedVariable> _variables;

        public DisposableTracker(OwnershipCatalog catalog)
        {
            Catalog = catalog;
            _variables = new Dictionary<ILocalSymbol, TrackedVariable>(SymbolEqualityComparer.Default);
        }

        public OwnershipCatalog Catalog { get; }

        public void TrackVariable(ILocalSymbol local, IOperation initialValue, Location location)
        {
            _variables[local] = new TrackedVariable(local, initialValue, location);
        }

        public bool IsTrackedAndUndisposed(ILocalSymbol local)
        {
            return _variables.TryGetValue(local, out var tracked) &&
                   tracked.State == DisposalState.Tracked;
        }

        public void MarkDisposed(ILocalSymbol local)
        {
            if (_variables.TryGetValue(local, out var tracked))
            {
                tracked.State = DisposalState.Disposed;
            }
        }

        public void MarkOwnershipTransferred(ILocalSymbol local)
        {
            if (_variables.TryGetValue(local, out var tracked))
            {
                tracked.State = DisposalState.OwnershipTransferred;
            }
        }

        public IEnumerable<UndisposedInfo> GetUndisposedVariables()
        {
            foreach (var kvp in _variables)
            {
                if (kvp.Value.State == DisposalState.Tracked)
                {
                    yield return new UndisposedInfo(
                        kvp.Key.Name,
                        kvp.Key.Type.Name,
                        kvp.Key.Type,
                        kvp.Value.Location);
                }
            }
        }

        private enum DisposalState
        {
            Tracked,
            Disposed,
            OwnershipTransferred
        }

        private sealed class TrackedVariable
        {
            public ILocalSymbol Local { get; }
            public IOperation InitialValue { get; }
            public Location Location { get; }
            public DisposalState State { get; set; }

            public TrackedVariable(ILocalSymbol local, IOperation initialValue, Location location)
            {
                Local = local;
                InitialValue = initialValue;
                Location = location;
                State = DisposalState.Tracked;
            }
        }
    }

    public readonly struct UndisposedInfo
    {
        public string Name { get; }
        public string TypeName { get; }
        public ITypeSymbol Type { get; }
        public Location Location { get; }

        public UndisposedInfo(string name, string typeName, ITypeSymbol type, Location location)
        {
            Name = name;
            TypeName = typeName;
            Type = type;
            Location = location;
        }
    }
}
