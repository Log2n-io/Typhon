using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// A parallel system's handle on its own worker slot of the tick's entity-command buffer (#1099). Queue a spawn or a destroy from inside a parallel
/// chunk, without a transaction; the engine applies the buffer in one fence phase.
/// </summary>
/// <remarks>
/// <para>
/// <b>The id you get back is real and final.</b> <see cref="Spawn{TArch}"/> returns the <see cref="EntityId"/> the entity will have, assigned from this
/// producer's key block, so it can be written straight into another entity's component with no remap pass anywhere in the engine — the corpse-and-loot
/// case, which placeholder schemes reach only through a playback remap that has to cover ids embedded in recorded payloads. The link also survives the
/// tick boundary, which a placeholder does not.
/// </para>
/// <para>
/// <b>But the entity is not alive yet.</b> Until the apply phase runs, the id is valid and permanently reserved while the entity is not alive, not
/// openable and not query-visible. None of those answers throws — they answer <c>false</c> or empty. See the validity contract on
/// <see cref="Spawn{TArch}"/>.
/// </para>
/// <para>
/// <b>Stack-only by construction.</b> A <c>ref struct</c> cannot be captured into a lambda, boxed or stored on the heap, so a handle cannot outlive its
/// system body or be smuggled onto another thread — the compiler enforces the slot-disjointness invariant the no-atomics design depends on.
/// </para>
/// <para>Obtain one from <c>ctx.Commands</c>, which supplies the caller's worker slot and chunk index.</para>
/// </remarks>
[PublicAPI]
public ref struct EntityCommands
{
    private readonly EntityCommandBuffer _buffer;
    private readonly int _slot;
    private readonly int _chunkIndex;

    // The slot's state, resolved once — its address is stable for the buffer's lifetime.
    private readonly ref EntityCommandSegmentState _state;

    // The BUFFERS, by contrast, are re-read on every command. Caching them by value is the silent-corruption bug EventWriter documents: two live handles
    // on one slot leave one holding an orphaned array after the other grows the segment, and a reset then lets the stale handle's fast path succeed into
    // it. The outer arrays' identity is fixed at bind, so caching THOSE and indexing per command is correct and nearly free.
    private readonly EntityCommandHeader[][] _headers;
    private readonly ComponentValue[][] _payloads;

    internal EntityCommands(EntityCommandBuffer buffer, int slot, int chunkIndex = 0)
    {
        _buffer = buffer;
        _slot = slot;
        _chunkIndex = chunkIndex;
        _state = ref buffer.SlotState(slot);
        _headers = buffer.SlotHeaders();
        _payloads = buffer.SlotPayloads();
    }

    /// <summary>False for a <c>default</c> handle — a lifecycle hook, or a runtime with no database engine. Every method on an invalid handle is a no-op.</summary>
    public readonly bool IsValid => _buffer != null;

    /// <summary>Commands accepted into this slot so far this tick. Zero for an invalid handle.</summary>
    public readonly int Pending => _buffer == null ? 0 : _state.Count;

    /// <summary>
    /// Queues one spawn of <typeparamref name="TArch"/> and returns the new entity's final id.
    /// </summary>
    /// <typeparam name="TArch">The archetype to spawn. Must be registered with the database and initialised.</typeparam>
    /// <param name="values">Initial component values. Components not covered are zero-initialised and disabled, as with <c>Transaction.Spawn</c>.</param>
    /// <returns>
    /// The entity's final id, or <see cref="EntityId.Null"/> when the command was <b>not</b> queued — this slot is at its ceiling, the archetype is not
    /// registered, the realm the values name cannot hold it, or more than 255 values were supplied. Never throws: this runs inside parallel chunks, where
    /// an exception becomes a system failure and, under a strict tick-abort policy, can cancel the rest of the tick.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>A null id is a lost game action, and an application has to treat it as one</b> — do not decrement the loot budget, do not mark the corpse
    /// looted. Nothing else is required: no retry, no backoff. The idiomatic shape is to check <see cref="EntityId.IsNull"/> on the value you were going
    /// to store anyway. Even the ignored case degrades safely: an <see cref="EntityId.Null"/> written into a component is what <c>IsAlive</c> already
    /// answers <c>false</c> for, so a discarded failure becomes an unowned loot drop rather than a dangling reference.
    /// </para>
    /// <para>
    /// <b>The validity contract.</b> Between this call and the apply, the returned id is final and permanently reserved; the entity is not alive, cannot
    /// be opened, and does not appear in any query or spatial result; and none of those operations throws.
    /// </para>
    /// </remarks>
    public EntityId Spawn<TArch>(params scoped ReadOnlySpan<ComponentValue> values) where TArch : Archetype<TArch>
    {
        Span<EntityId> one = stackalloc EntityId[1];
        return SpawnMany<TArch>(1, one, values) == 1 ? one[0] : EntityId.Null;
    }

    /// <summary>
    /// Queues <paramref name="count"/> spawns of <typeparamref name="TArch"/> sharing one value set — one header and one payload copy for the whole run —
    /// and writes their ids into <paramref name="ids"/>.
    /// </summary>
    /// <typeparam name="TArch">The archetype to spawn.</typeparam>
    /// <param name="count">Entities to create. Must be positive and no larger than one key block; a larger request is refused.</param>
    /// <param name="ids">Destination for the new ids. Must hold at least <paramref name="count"/>; a shorter span refuses the whole command.</param>
    /// <param name="values">Initial component values, shared by every entity in the run.</param>
    /// <returns>The number queued — <paramref name="count"/>, or 0 if nothing was. Never partial: a run is queued whole or not at all.</returns>
    /// <remarks>
    /// Returns a count and fills a caller span rather than returning a range, because a range would be a lie in the general case: ids are contiguous
    /// within one key block, and the block boundary is not something a caller can see. The run itself IS contiguous — a request that would straddle a
    /// boundary takes a fresh block rather than splitting — but that is an implementation guarantee, not a shape the API should bake in.
    /// </remarks>
    public int SpawnMany<TArch>(int count, scoped Span<EntityId> ids, params scoped ReadOnlySpan<ComponentValue> values) where TArch : Archetype<TArch>
    {
        if (_buffer == null || count < 1 || ids.Length < count)
        {
            return 0;
        }

        if (values.Length > EntityCommandBuffer.MaxValuesPerCommand)
        {
            _state.Rejected++;
            return 0;
        }

        var meta = Archetype<TArch>.Metadata;
        if (!_buffer.Engine.CanSpawnDeferred(meta, values, out var routingId))
        {
            _state.Rejected++;
            return 0;
        }

        var baseKey = _buffer.Keys.Reserve(meta.ArchetypeId, _chunkIndex, count);
        if (baseKey < 0)
        {
            // Out of key blocks for this archetype this tick, or a chunk index outside the stride. Both are the engine declining, not the caller erring,
            // so both count as overflow — see the type's note on why a reported zero has to mean nothing was lost.
            _state.Overflow++;
            return 0;
        }

        if (!Append(EntityCommandKind.SpawnMany, meta.ArchetypeId, routingId, count, baseKey, values))
        {
            return 0;
        }

        for (var i = 0; i < count; i++)
        {
            ids[i] = EntityId.FromParts(baseKey + i, routingId);
        }

        return count;
    }

    /// <summary>
    /// Queues one destroy.
    /// </summary>
    /// <param name="id">The entity to destroy. <see cref="EntityId.Null"/> is refused.</param>
    /// <returns><c>true</c> if queued; <c>false</c> if the slot is at its ceiling or the id was null.</returns>
    /// <remarks>
    /// Idempotent at apply: a second command for the same id, from this or any other slot, is a no-op. Destroying an entity spawned earlier in the same
    /// tick works — spawns apply before destroys — so the spawn-then-destroy pair collapses to nothing observable rather than to a dangling row.
    /// </remarks>
    public bool Destroy(EntityId id)
    {
        if (_buffer == null)
        {
            return false;
        }

        if (id.IsNull)
        {
            _state.Rejected++;
            return false;
        }

        return Append(EntityCommandKind.Destroy, -1, id.ArchetypeId, 0, (long)id.RawValue, default);
    }

    /// <summary>Writes one header and its payload into this slot, after reserving room for both. Slot-owner-only; no atomics.</summary>
    private bool Append(EntityCommandKind kind, int internalArchetypeId, ushort routingId, int count, long key,
        scoped ReadOnlySpan<ComponentValue> values)
    {
        var headers = _headers;
        if (headers == null)
        {
            return false;
        }

        var buffer = headers[_slot];
        var n = _state.Count;

        // Fast path: room for the header, and room for the values in the payload pool.
        if (buffer == null || (uint)n >= (uint)buffer.Length || _state.PayloadCount + values.Length > _payloads[_slot].Length)
        {
            if (!_buffer.TryReserveRoom(_slot, values.Length))
            {
                return false;
            }

            buffer = headers[_slot];
            n = _state.Count;
        }

        var payloadOffset = -1;
        if (values.Length > 0)
        {
            payloadOffset = _state.PayloadCount;
            values.CopyTo(_payloads[_slot].AsSpan(payloadOffset));
            _state.PayloadCount = payloadOffset + values.Length;
        }

        buffer[n] = new EntityCommandHeader
        {
            Kind = kind,
            ValueCount = (byte)values.Length,
            ArchetypeId = routingId,
            InternalArchetypeId = internalArchetypeId,
            Count = count,
            PayloadOffset = payloadOffset,
            EntityKey = key,
        };

        _state.Count = n + 1;
        _state.SpawnedEntities += count;
        if (n == 0)
        {
            // Raise the buffer's O(1) emptiness gate only on this segment's 0 -> 1 transition, as EventWriter does: doing it per command costs a volatile
            // read for no added information.
            _buffer.MarkProduced();
        }

        return true;
    }
}
