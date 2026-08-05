using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Where a cross-archetype scan deposits the entities it finds.
/// </summary>
/// <remarks>
/// <para>
/// The archetype loop is identical for one-shot execution and for view population, but the two want different containers: <c>Execute()</c> returns a
/// <see cref="HashSet{T}"/> of <see cref="EntityId"/>, while a view's entity set is a <c>HashMap&lt;long&gt;</c> of raw PKs. Rather than keep two copies of
/// the loop — which is exactly how the per-archetype index home came to be handled at one of five call sites and forgotten at the other four (#663) — the
/// scan is generic over this sink.
/// </para>
/// <para>
/// A <c>struct</c> constraint, not an interface reference: the JIT specialises the scan per sink type, so <see cref="Add"/> devirtualises and inlines into
/// the inner loop and nothing is allocated per entity. Same shape as <c>RawValuePagedHashMap.IEntryAction&lt;T&gt;</c>.
/// </para>
/// </remarks>
internal interface IEntityIdSink
{
    void Add(EntityId id);
}

/// <summary>Deposits into a <see cref="HashSet{T}"/> of <see cref="EntityId"/> — the one-shot <c>Execute()</c> shape.</summary>
internal readonly struct EntityIdSetSink : IEntityIdSink
{
    private readonly HashSet<EntityId> _target;

    public EntityIdSetSink(HashSet<EntityId> target) => _target = target;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(EntityId id) => _target.Add(id);
}

/// <summary>Deposits the raw PK into a <c>HashMap&lt;long&gt;</c> — the shape a view's entity set uses.</summary>
internal readonly struct PkMapSink : IEntityIdSink
{
    private readonly HashMap<long> _target;

    public PkMapSink(HashMap<long> target) => _target = target;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(EntityId id) => _target.TryAdd((long)id.RawValue);
}
