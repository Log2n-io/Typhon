using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Typhon.Analyzers;

/// <summary>
/// TYPHON015 — a command sent by name when its contracts struct is in reach: <c>client.SendCommandAsync("MoveCmd", values)</c> where a
/// <c>[ReplicatedMessage] struct MoveCmd</c> is visible. Send the struct itself (<c>client.Enqueue(in cmd)</c>, #1614).
/// </summary>
/// <remarks>
/// <para>
/// The name-keyed path finds every field by a string at run time: a misspelled field is found only when the command is sent, and a driver that
/// swallows the throw sends nothing for its whole life — the SWG <c>--god-tour</c> sent <c>"Realm"</c> for <c>"realm"</c> that way. It also allocates
/// per send (a dictionary, a boxed value per field), which at 30 Hz × thousands of bots is a GC load of its own. The typed path is a compile-time
/// contract, bound once against the negotiated catalog, and allocation-free.
/// </para>
/// <para>
/// Fires only when the struct is in reach — the name (a literal or a <c>nameof</c>) is that of a <c>[ReplicatedMessage]</c> struct declared in this
/// compilation or an assembly it references — so a built-in command (<c>ClientRegion</c>) or one with no contracts struct on this side
/// is left alone.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class TypedCommandAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON015";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        "Command sent by name although its contracts struct is in reach",
        "Command '{0}' is sent by name, but '{1}' is its [ReplicatedMessage] struct: send it typed with Enqueue(in {0}) and FlushCommandsAsync() — fields "
        + "checked at compile time, bound to the catalog once, no allocation per send",
        "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "The name-keyed SendCommandAsync finds fields by string at run time and allocates per send. A [ReplicatedMessage] struct sent with "
            + "TyphonClient.Enqueue is checked by the compiler and bound to the negotiated catalog once.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var attribute = start.Compilation.GetTypeByMetadataName("Typhon.Protocol.ReplicatedMessageAttribute");
            var client = start.Compilation.GetTypeByMetadataName("Typhon.Client.TyphonClient");
            if (attribute == null || client == null)
            {
                return;
            }

            // Built on first use: a compilation that never sends a command by name never walks its references.
            Dictionary<string, INamedTypeSymbol> messages = null;
            var gate = new object();
            start.RegisterOperationAction(operation =>
            {
                var invocation = (IInvocationOperation)operation.Operation;
                var method = invocation.TargetMethod;
                if (method.Name != "SendCommandAsync" || method.IsGenericMethod || !SymbolEqualityComparer.Default.Equals(method.ContainingType, client)
                    || invocation.Arguments.Length == 0)
                {
                    return;
                }

                var argument = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value;
                if (argument is not { ConstantValue: { HasValue: true, Value: string name } })
                {
                    return;
                }

                // A nameof(MoveCmd) is a constant too, so one lookup covers it and a literal alike.
                lock (gate)
                {
                    messages ??= Collect(start.Compilation, attribute);
                }

                if (!messages.TryGetValue(name, out var message))
                {
                    return;
                }

                operation.ReportDiagnostic(Diagnostic.Create(Rule, argument.Syntax.GetLocation(), name, message.ToDisplayString()));
            }, OperationKind.Invocation);
        });
    }

    // A [ReplicatedMessage] struct the typed path can send: every public instance field a scalar it reads (MessageFieldType) — a bool, an integer, a float
    // or an enum. A struct with a shape or another struct field could not be bound, and a warning nobody can act on is noise.
    private static bool IsMessage(INamedTypeSymbol type, INamedTypeSymbol attribute) =>
        type.TypeKind == TypeKind.Struct
        && type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute))
        && type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && f.DeclaredAccessibility == Accessibility.Public).All(f => IsScalar(f.Type));

    private static bool IsScalar(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
        {
            type = underlying;
        }

        return type.SpecialType is SpecialType.System_Boolean or SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16
            or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double;
    }

    // Every [ReplicatedMessage] struct by simple name, in this compilation and in the assemblies it references that themselves reference Typhon.Protocol:
    // a contracts assembly does, and nothing else can declare one.
    private static Dictionary<string, INamedTypeSymbol> Collect(Compilation compilation, INamedTypeSymbol attribute)
    {
        var found = new Dictionary<string, INamedTypeSymbol>();
        Walk(compilation.Assembly.GlobalNamespace, attribute, found);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            // The SDK and the engine declare no application command, and walking them is most of the cost.
            var name = assembly.Identity.Name;
            if (name is not ("Typhon.Client" or "Typhon.Engine" or "Typhon.Protocol")
                && assembly.Modules.Any(m => m.ReferencedAssemblies.Any(r => r.Name == "Typhon.Protocol")))
            {
                Walk(assembly.GlobalNamespace, attribute, found);
            }
        }

        // Only a struct this compilation can name could be sent typed: an internal or private one elsewhere is not in reach.
        var reachable = new Dictionary<string, INamedTypeSymbol>();
        foreach (var pair in found)
        {
            if (compilation.IsSymbolAccessibleWithin(pair.Value, compilation.Assembly))
            {
                reachable[pair.Key] = pair.Value;
            }
        }

        found = reachable;

        return found;
    }

    private static void Walk(INamespaceSymbol ns, INamedTypeSymbol attribute, Dictionary<string, INamedTypeSymbol> found)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            WalkType(type, attribute, found);
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            Walk(child, attribute, found);
        }
    }

    private static void WalkType(INamedTypeSymbol type, INamedTypeSymbol attribute, Dictionary<string, INamedTypeSymbol> found)
    {
        if (IsMessage(type, attribute) && !found.ContainsKey(type.Name))
        {
            found[type.Name] = type;
        }

        foreach (var nested in type.GetTypeMembers())
        {
            WalkType(nested, attribute, found);
        }
    }
}
