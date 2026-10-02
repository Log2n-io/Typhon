using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// Applies a tick's deferred entity commands (#1102): one pass over the buffer, grouped by archetype, through the ordinary spawn path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serial, on the tick driver, immediately before the fence opens — and that is a deliberate retreat from the design.</b> ENG-01 specified a seventh
/// fence exec system partitioned by destination cell. The measurements that were supposed to justify it did the opposite: a spawn costs about 1 us and an
/// indexed field about 0.2 us more, measured over a hundred 3 000-entity bursts and 300 000 entities, so the realistic mass-death burst is roughly 3 ms
/// rather than the 14 ms an earlier and badly-built harness reported. Three milliseconds inside a 20 ms tick is worth removing eventually and is not worth
/// new concurrency in the B+Tree and the EntityMap to remove now.
/// </para>
/// <para>
/// <b>What that retreat buys, concretely.</b> No new exec system, no change to <c>FenceExecBundle</c> or the fence DAG, and one insertion point instead of
/// two — the parallel and serial fence arms both pass through here, so a drain placed inside the DAG would have had to be duplicated for hosts running with
/// <c>EnableParallelFence</c> off. The pieces built for the parallel version (<c>AdvanceHashStateFor</c>, <c>InsertNewBulk</c>, <c>AddBulkSorted</c>) are
/// additive and sit unused; nothing has to be unpicked to adopt them later.
/// </para>
/// <para>
/// <b>Why it still runs before the fence rather than after it.</b> An entity queued this tick has to be cluster-slotted and index-visible by the time the
/// fence's own phases run, or it would be a tick late to everything the fence does — the AABB refresh, the index merge, the spatial maintenance. Before
/// <c>FenceWindow.Open()</c> also means this is ordinary engine code holding an ordinary transaction, with no licence taken and no invariant suspended.
/// </para>
/// </remarks>
internal sealed class EntityCommandDrain
{
    private readonly DatabaseEngine _engine;

    // Reused across ticks: the drain runs every tick and a per-tick allocation here would be a per-tick allocation on the tick driver.
    private readonly List<DrainedCommand> _commands = new();
    private long[] _keys = new long[256];
    private EntityId[] _ids = new EntityId[256];

    internal EntityCommandDrain(DatabaseEngine engine) => _engine = engine;

    /// <summary>Entities applied by the most recent drain. Zero on a tick that queued nothing.</summary>
    internal int LastAppliedEntities { get; private set; }

    /// <summary>Commands applied by the most recent drain.</summary>
    internal int LastAppliedCommands { get; private set; }

    /// <summary>One drained header, flattened out of its slot so the whole tick can be sorted by archetype in one go.</summary>
    private struct DrainedCommand
    {
        public int InternalArchetypeId;
        public int Slot;
        public int Index;
        public long EntityKey;
        public int Count;
        public int PayloadOffset;
        public byte ValueCount;
        public EntityCommandKind Kind;
    }

    /// <summary>
    /// Applies everything in <paramref name="buffer"/> and clears it. Call once per tick, on the driver thread, before the fence window opens.
    /// </summary>
    internal void Apply(EntityCommandBuffer buffer)
    {
        LastAppliedEntities = 0;
        LastAppliedCommands = 0;
        if (buffer == null || buffer.IsEmpty)
        {
            return;
        }

        Collect(buffer);
        if (_commands.Count == 0)
        {
            return;
        }

        // Grouped by archetype, then by key within it. Archetype first because the spawn path resolves per-archetype state once per call; key second so a
        // run is contiguous, which is what lets one call cover many commands and what makes the applied order independent of which worker produced them.
        _commands.Sort(static (a, b) => a.InternalArchetypeId != b.InternalArchetypeId
            ? a.InternalArchetypeId.CompareTo(b.InternalArchetypeId)
            : a.EntityKey.CompareTo(b.EntityKey));

        using var tx = _engine.CreateQuickTransaction();

        var start = 0;
        while (start < _commands.Count)
        {
            var archetype = _commands[start].InternalArchetypeId;
            var end = start + 1;
            while (end < _commands.Count && _commands[end].InternalArchetypeId == archetype)
            {
                end++;
            }

            ApplyArchetype(tx, buffer, archetype, start, end);
            start = end;
        }

        tx.Commit();
    }

    /// <summary>Flattens every slot's headers into one list.</summary>
    private void Collect(EntityCommandBuffer buffer)
    {
        _commands.Clear();
        var headers = buffer.SlotHeaders();
        for (var slot = 0; slot < buffer.SlotCount; slot++)
        {
            ref var state = ref buffer.SlotState(slot);
            var count = state.Count;
            if (count == 0 || headers[slot] == null)
            {
                continue;
            }

            var buf = headers[slot];
            for (var i = 0; i < count; i++)
            {
                ref var h = ref buf[i];
                if (h.Kind == EntityCommandKind.None)
                {
                    continue;
                }

                _commands.Add(new DrainedCommand
                {
                    InternalArchetypeId = h.InternalArchetypeId,
                    Slot = slot,
                    Index = i,
                    EntityKey = h.EntityKey,
                    Count = h.Count,
                    PayloadOffset = h.PayloadOffset,
                    ValueCount = h.ValueCount,
                    Kind = h.Kind,
                });
            }
        }
    }

    /// <summary>
    /// Applies one archetype's run: every spawn in one allocate call, then the values, then the destroys.
    /// </summary>
    /// <remarks>
    /// Spawns before destroys, which is the ordering the API promises: a destroy queued for an entity spawned earlier in the same tick collapses the pair to
    /// nothing observable rather than leaving a row nothing can reach.
    /// </remarks>
    private void ApplyArchetype(Transaction tx, EntityCommandBuffer buffer, int archetypeId, int start, int end)
    {
        var meta = ArchetypeRegistry.GetMetadata((ushort)archetypeId);
        if (meta == null)
        {
            return;
        }

        var entities = 0;
        for (var c = start; c < end; c++)
        {
            if (_commands[c].Kind != EntityCommandKind.Destroy)
            {
                entities += _commands[c].Count;
            }
        }

        if (entities > 0)
        {
            if (_keys.Length < entities)
            {
                _keys = new long[Math.Max(entities, _keys.Length * 2)];
                _ids = new EntityId[_keys.Length];
            }

            var n = 0;
            for (var c = start; c < end; c++)
            {
                if (_commands[c].Kind == EntityCommandKind.Destroy)
                {
                    continue;
                }

                // A SpawnMany's run is contiguous by construction — the key allocator never splits a request across generations — so its keys expand here
                // rather than being carried one per header.
                for (var k = 0; k < _commands[c].Count; k++)
                {
                    _keys[n++] = _commands[c].EntityKey + k;
                }
            }

            var baseIndex = tx.SpawnBatchAllocateRaw(meta, _keys.AsSpan(0, entities), _ids.AsSpan(0, entities));

            var payloads = buffer.SlotPayloads();
            // A span over the list, not ToArray(): the first draft called a helper that copied the whole list per command.
            var all = CollectionsMarshal.AsSpan(_commands);
            var written = 0;
            for (var c = start; c < end; c++)
            {
                ref var cmd = ref all[c];
                if (cmd.Kind == EntityCommandKind.Destroy)
                {
                    continue;
                }

                for (var k = 0; k < cmd.Count; k++, written++)
                {
                    // Every entity of a SpawnMany shares one payload run, which is the reason that kind exists.
                    for (var v = 0; v < cmd.ValueCount; v++)
                    {
                        tx.SpawnWriteValueRaw(baseIndex + written, in payloads[cmd.Slot][cmd.PayloadOffset + v]);
                    }
                }
            }

            LastAppliedEntities += entities;
        }

        for (var c = start; c < end; c++)
        {
            if (_commands[c].Kind == EntityCommandKind.Destroy)
            {
                // Idempotent: a second command for the same id, from any slot, finds it already pending and does nothing.
                tx.Destroy(EntityId.FromRawValue((ulong)_commands[c].EntityKey));
            }
        }

        LastAppliedCommands += end - start;
    }
}
