using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// A walk of every entity of <typeparamref name="TArch"/> visible at its transaction's snapshot, one batch at a time: <c>foreach (var batch in
/// tx.Walk&lt;TArch&gt;())</c>. Created by <see cref="Transaction.Walk{TArch}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it yields.</b> Each entity visible at the snapshot exactly once, across any number of tick fences (WALK-01), minus the transaction's own pending
/// destroys. Its own uncommitted updates and spawns are not shown, as a field query does not show them (VIS-12). Versioned components read their value at
/// the snapshot; SingleVersion and Transient ones read the current value, which is all those storage modes promise.
/// </para>
/// <para>
/// <b>What it holds.</b> A bounded window of pages, at any data size (EP-02), and between batches no epoch pin: a consumer that pauses between batches does
/// not keep the pages other threads touch meanwhile (EP-03). Its snapshot stays held for as long as the transaction lives.
/// </para>
/// <para>
/// <b>Order.</b> Storage order (one cluster per batch) on an archetype without a spatial index, the fastest walk the engine has. On a spatial archetype,
/// whose fence moves entities between clusters, entity-map order with up to 64 entities per batch, gathered into the walk's buffers.
/// </para>
/// </remarks>
[MustDispose(Reason = "holds scan accessors and native column buffers: foreach disposes it, a hand-driven walk must")]
[PublicAPI]
public ref struct ArchetypeWalk<TArch> where TArch : class
{
    private readonly SnapshotWalker _walker;

    internal ArchetypeWalk(SnapshotWalker walker) => _walker = walker;

    /// <summary>Returns this walk: <c>foreach</c> drives it directly.</summary>
    public readonly ArchetypeWalk<TArch> GetEnumerator() => this;

    /// <summary>Advances to the next batch holding at least one entity; false once the walk has visited every one.</summary>
    public readonly bool MoveNext() => _walker != null && _walker.MoveNext();

    /// <summary>The current batch, valid until the next <see cref="MoveNext"/> or <see cref="Dispose"/>.</summary>
    public readonly WalkBatch<TArch> Current => new(_walker, _walker.Step);

    /// <summary>Releases the walk's pages and buffers. Idempotent.</summary>
    public readonly void Dispose() => _walker?.Dispose();
}

/// <summary>
/// One batch of a <see cref="ArchetypeWalk{TArch}"/>: up to 64 entities, addressed by slot. Iterate the set bits of <see cref="Rows"/>; each indexes
/// <see cref="EntityIds"/> and every span <see cref="GetReadOnlySpan{T}"/> returns.
/// </summary>
/// <remarks>
/// Valid until the walk's next <c>MoveNext</c> or <c>Dispose</c>: using it after throws, and so does any span it handed out read after that point only by
/// convention — the memory behind it is the walk's or a page the walk let go of. Values are copies the caller may not write through.
/// </remarks>
[PublicAPI]
public readonly unsafe ref struct WalkBatch<TArch> where TArch : class
{
#pragma warning disable TYPHON005 // Borrowed: the walk's, which its ArchetypeWalk disposes
    private readonly SnapshotWalker _walker;
#pragma warning restore TYPHON005
    private readonly int _step;

    internal WalkBatch(SnapshotWalker walker, int step)
    {
        _walker = walker;
        _step = step;
    }

    /// <summary>One bit per slot holding an entity of this batch. Never zero.</summary>
    public ulong Rows
    {
        get
        {
            CheckStep();
            return _walker.Rows;
        }
    }

    /// <summary>The number of entities in this batch.</summary>
    public int Count => BitOperations.PopCount(Rows);

    /// <summary>The raw entity id of every slot; meaningful only at the slots <see cref="Rows"/> names.</summary>
    public ReadOnlySpan<long> EntityIds
    {
        get
        {
            CheckStep();
            return _walker.EntityIds();
        }
    }

    /// <summary>The id of the entity at <paramref name="slot"/>, a set bit of <see cref="Rows"/>.</summary>
    public EntityId GetEntityId(int slot) => EntityId.FromRaw(EntityIds[slot]);

    /// <summary>
    /// The values of component <paramref name="comp"/> by slot, as this walk reads them: at the snapshot for a Versioned component, the current value
    /// otherwise.
    /// </summary>
    /// <exception cref="InvalidOperationException">The batch's step is over, or <typeparamref name="T"/> does not stride the component's column.</exception>
    public ReadOnlySpan<T> GetReadOnlySpan<T>(Comp<T> comp) where T : unmanaged
    {
        CheckStep();
        return _walker.Column<T>(comp._componentTypeId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckStep()
    {
        if (_walker == null || _walker.Step != _step)
        {
            ThrowStepOver();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStepOver() =>
        ThrowHelper.ThrowInvalidOp("A WalkBatch was used after its walk moved on or was disposed: a batch is valid for one step of the walk only.");
}
