using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Typhon.Analyzers;

/// <summary>
/// TYPHON012 — a non-<c>readonly</c> member of <c>EntityRef</c> / <c>EntityRefMut</c> called on a read-only receiver.
/// </summary>
/// <remarks>
/// <para>
/// C# silently copies a struct before calling a member that is not <c>readonly</c> on a receiver it may not modify — an <c>in</c> or
/// <c>ref readonly</c> parameter, a <c>ref readonly</c> local, a <c>foreach</c> or <c>using</c> variable, a <c>readonly</c> field, <c>this</c> inside a
/// <c>readonly</c> member, a <c>ref readonly</c> return. For the entity handles that copy is ~200 bytes on the hottest path in the engine, and for
/// <c>EntityRefMut.Write</c> / <c>Enable</c> / <c>Disable</c> it is also wrong: the handle state they update (a Versioned slot's new location, the
/// enabled bits) lands on the copy and is lost.
/// </para>
/// <para>
/// Every non-writing member of both handles is <c>readonly</c> (#997, pinned by <c>EntityRefMutTests.EveryNonWritingMember_IsReadonly</c>), so today this
/// fires only for the three mutators. It also covers any non-<c>readonly</c> member added later.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class EntityHandleDefensiveCopyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON012";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        "Non-readonly member of an entity handle called on a read-only receiver",
        "'{0}' is not a readonly member of '{1}', and '{2}' is read-only here: the compiler copies the handle before the call, and any state the call updates is lost with the copy",
        "Performance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "EntityRef and EntityRefMut are ~200-byte ref structs. Calling a member that is not readonly on a read-only receiver (in / ref readonly " +
            "parameter or local, foreach or using variable, readonly field, 'this' in a readonly member, ref readonly return) makes a defensive copy first. " +
            "Hold the handle in a mutable local (var e = tx.OpenMut(id)) or pass it by ref.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Resolve the two handle types once per compilation; a compilation that has neither (anything not referencing the engine) registers nothing.
            var entityRef = start.Compilation.GetTypeByMetadataName("Typhon.Engine.EntityRef");
            var entityRefMut = start.Compilation.GetTypeByMetadataName("Typhon.Engine.EntityRefMut");
            if (entityRef == null && entityRefMut == null)
            {
                return;
            }

            var handles = new Handles(entityRef, entityRefMut);
            start.RegisterOperationAction(c => AnalyzeInvocation(c, handles), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzePropertyReference(c, handles), OperationKind.PropertyReference);
        });
    }

    private sealed class Handles
    {
        private readonly INamedTypeSymbol _entityRef;
        private readonly INamedTypeSymbol _entityRefMut;

        public Handles(INamedTypeSymbol entityRef, INamedTypeSymbol entityRefMut)
        {
            _entityRef = entityRef;
            _entityRefMut = entityRefMut;
        }

        public bool Contains(ITypeSymbol type) =>
            SymbolEqualityComparer.Default.Equals(type, _entityRef) || SymbolEqualityComparer.Default.Equals(type, _entityRefMut);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, Handles handles)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.IsStatic || method.IsReadOnly)
        {
            return;
        }
        Check(context, handles, invocation.Instance, method.Name);
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context, Handles handles)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        var property = reference.Property;
        if (property.IsStatic)
        {
            return;
        }

        // A read goes through the getter, a write through the setter; a property with a readonly accessor for the use at hand is fine.
        var accessor = reference.Parent is ISimpleAssignmentOperation assignment && assignment.Target == reference ? property.SetMethod : property.GetMethod;
        if (accessor == null || accessor.IsReadOnly)
        {
            return;
        }
        Check(context, handles, reference.Instance, property.Name);
    }

    private static void Check(OperationAnalysisContext context, Handles handles, IOperation instance, string memberName)
    {
        var type = instance?.Type;
        if (type == null || !handles.Contains(type))
        {
            return;
        }

        var receiver = ReadOnlyReceiverName(instance, context.ContainingSymbol);
        if (receiver == null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), memberName, type.Name, receiver));
    }

    /// <summary>A display name for <paramref name="instance"/> when the compiler treats it as read-only, else <c>null</c>.</summary>
    private static string ReadOnlyReceiverName(IOperation instance, ISymbol containingSymbol)
    {
        switch (instance)
        {
            case IParameterReferenceOperation parameter:
                return parameter.Parameter.RefKind is RefKind.In or RefKind.RefReadOnlyParameter ? parameter.Parameter.Name : null;

            case ILocalReferenceOperation local:
                // `foreach (ref var e in …)` is a writable ref local; only a by-value foreach variable is read-only.
                var isReadOnlyLocal = local.Local.RefKind == RefKind.RefReadOnly
                    || (local.Local.IsForEach && local.Local.RefKind != RefKind.Ref)
                    || local.Local.IsUsing;
                return isReadOnlyLocal ? local.Local.Name : null;

            case IFieldReferenceOperation field:
                if (field.Field.IsReadOnly)
                {
                    return field.Field.Name;
                }
                // A field of a read-only receiver is read-only too: `this` in a readonly member, a field of an `in` parameter...
                if (field.Instance is IInstanceReferenceOperation)
                {
                    return containingSymbol is IMethodSymbol { IsReadOnly: true } ? field.Field.Name : null;
                }
                return field.Instance != null && ReadOnlyReceiverName(field.Instance, containingSymbol) != null ? field.Field.Name : null;

            case IInstanceReferenceOperation:
                return containingSymbol is IMethodSymbol { IsReadOnly: true } ? "this" : null;

            case IInvocationOperation invocation:
                return invocation.TargetMethod.RefKind == RefKind.RefReadOnly ? invocation.TargetMethod.Name + "()" : null;

            case IPropertyReferenceOperation property:
                return property.Property.RefKind == RefKind.RefReadOnly ? property.Property.Name : null;

            default:
                return null;
        }
    }
}
