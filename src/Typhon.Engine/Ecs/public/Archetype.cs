using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// CRTP base class for ECS archetypes. Concrete archetypes inherit from this and declare components
/// as <c>static readonly Comp&lt;T&gt;</c> fields via <see cref="Register{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Finalization (slot assignment, validation, metadata creation) is lazy — triggered on first access to <see cref="Metadata"/>.
/// This avoids static constructor ordering issues between the base class and derived class field initializers.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The concrete archetype type (CRTP pattern).</typeparam>
[PublicAPI]
public abstract class Archetype<TSelf> where TSelf : Archetype<TSelf>
{
    // ReSharper disable once StaticMemberInGenericType
    // ReSharper disable once InconsistentNaming
    private static ArchetypeMetadata _metadata;

    /// <summary>Metadata populated on first access via lazy finalization.</summary>
    internal static ArchetypeMetadata Metadata
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _metadata ?? EnsureFinalized();
    }

    private static ArchetypeMetadata EnsureFinalized()
    {
        ArchetypeRegistry.EnsureFinalized(typeof(TSelf));
        _metadata = ArchetypeRegistry.GetMetadata<TSelf>();
        return _metadata;
    }

    /// <summary>
    /// Declare a component for this archetype. Must be called as a static field initializer.
    /// </summary>
    protected static Comp<T> Register<T>() where T : unmanaged => ArchetypeRegistry.DeclareComponent<TSelf, T>();

    /// <summary>Force static initialization of this archetype type (triggers field initializers and finalization).</summary>
    public static void Touch()
    {
        // Accessing any static member triggers the CLR type initializer.
        // After that, EnsureFinalized() can be called safely.
        RuntimeHelpers.RunClassConstructor(typeof(TSelf).TypeHandle);
        _ = Metadata;
    }
}

/// <summary>
/// CRTP base class for archetypes with a parent. Inherited components get lower slot indices (parent-first ordering).
/// Single parent only — no diamond inheritance.
/// </summary>
/// <typeparam name="TSelf">The concrete archetype type.</typeparam>
/// <typeparam name="TParent">The parent archetype type.</typeparam>
[PublicAPI]
public abstract class Archetype<TSelf, TParent> : Archetype<TSelf> where TSelf : Archetype<TSelf, TParent> where TParent : class
{
}
