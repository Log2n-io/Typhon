using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;

namespace Typhon.Schema.Definition;

/// <summary>
/// Process-global sink for source-generated component schemas (feature #514). Each schema assembly's generated <c>[ModuleInitializer]</c> calls
/// <see cref="RegisterComponent"/> exactly once at assembly load; the engine reads specs via <see cref="TryGetComponentSpec"/> when building component
/// definitions — so the schema build is reflection-free without the component implementing an interface (hence without the component needing to be <c>partial</c>).
/// </summary>
/// <remarks>
/// <para>This lives in the schema-contract assembly — NOT the engine — so that schema-only assemblies (which reference only <c>Typhon.Schema.Definition</c>, never
/// <c>Typhon.Engine</c>) can still register their components from a generated module-init.</para>
/// <para>ALC-safe: backed by a <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed weakly on the component <see cref="Type"/>, so a collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> is never pinned by this static — the entry is reclaimed together with the Type when the ALC unloads.</para>
/// </remarks>
[PublicAPI]
public static class GeneratedSchemaRegistry
{
    // Boxes the readonly-struct spec so it can live in the reference-typed table.
    private sealed class SpecHolder
    {
        public ComponentSchemaSpec Spec;
    }

    private static readonly ConditionalWeakTable<Type, SpecHolder> Specs = new();

    /// <summary>
    /// Registers a component's source-generated <see cref="ComponentSchemaSpec"/>, keyed by its CLR type. Called by generated module-initializers — not intended
    /// for hand-written code. Idempotent: a later identical registration overwrites with the same data.
    /// </summary>
    /// <param name="componentType">The <c>[Component]</c> struct type the spec describes.</param>
    /// <param name="spec">The component's schema shape (identity, storage/durability defaults, ordered fields).</param>
    public static void RegisterComponent(Type componentType, ComponentSchemaSpec spec)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        Specs.AddOrUpdate(componentType, new SpecHolder { Spec = spec });
    }

    /// <summary>
    /// Looks up a source-generated component spec by CLR type. Returns false when none is registered — the engine then falls back to runtime reflection
    /// (hand-authored components, or schema-only assemblies whose module-init did not run).
    /// </summary>
    /// <param name="componentType">The component struct type.</param>
    /// <param name="spec">The registered spec, or <c>default</c> when not found.</param>
    /// <returns>True if a spec was registered for the type.</returns>
    public static bool TryGetComponentSpec(Type componentType, out ComponentSchemaSpec spec)
    {
        if (componentType != null && Specs.TryGetValue(componentType, out var holder))
        {
            spec = holder.Spec;
            return true;
        }
        spec = default;
        return false;
    }
}
