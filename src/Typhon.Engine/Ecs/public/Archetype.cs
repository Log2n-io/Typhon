using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// CRTP base class for ECS archetypes. Concrete archetypes inherit from this and declare components
/// as <c>static readonly</c> <see cref="Comp{T}"/> fields via <see cref="Register{T}"/>.
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

    /// <summary>
    /// This archetype's <b>catalog id</b>: the engine-assigned number that identifies it among all archetypes registered in this process, and the id every
    /// per-archetype engine API takes — <see cref="DatabaseEngine.GetSpatialTelemetry(int)"/> among them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not <see cref="EntityId.ArchetypeId"/>.</b> Typhon has two archetype id spaces and they are easy to confuse: the <i>catalog</i> id here is
    /// process-global and assigned when the archetype registers, while the low 16 bits of an <see cref="EntityId"/> hold the <i>routing</i> id, which is
    /// per-database, numbered from 1 in registration order within that database, and persisted as <c>ArchetypeR1.RoutingId</c> so a reopen can re-match it by
    /// name. Comparing one to the other compiles — both are <see cref="ushort"/> — and silently never matches.
    /// </para>
    /// <para>
    /// <b>They may coincide by accident, which is the dangerous case.</b> An application that registers its archetypes in one process and routes them all
    /// into one database gives both counters the same order, so the two ids commonly agree — and code that confuses them appears to work until a second
    /// database, or an archetype registered and not routed, pulls them apart. Use this value only where a catalog id is asked for.
    /// </para>
    /// <para>
    /// Reading it finalizes the archetype if that has not happened yet, so it is safe from any thread at any time — but it is a dictionary key and a table
    /// index, not a hot-path value: cache it in a field rather than re-reading it per entity.
    /// </para>
    /// <para>
    /// <b>The id is assigned by the engine, not by the author</b> (#514 D1), and it reflects this process's registration order. Do not persist it or hard-code
    /// a literal — use the archetype's name for anything durable.
    /// </para>
    /// </remarks>
    public static ushort CatalogId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Metadata.ArchetypeId;
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
