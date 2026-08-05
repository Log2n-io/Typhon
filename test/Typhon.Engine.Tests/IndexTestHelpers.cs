using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Test-side access to the per-archetype index home.
/// </summary>
/// <remarks>
/// <para>
/// Indexes live on the archetype, not on the <see cref="ComponentTable"/> (#666). A test that reads
/// <c>table.IndexStats</c> or <c>table.IndexedFieldInfos[i].Index</c> is reading a tree with no entries in it, so its
/// assertions describe nothing — and pass or fail for reasons unrelated to what they claim to check.
/// </para>
/// <para>
/// The arrays returned here are parallel to <c>ComponentTable.IndexedFieldInfos</c>, so a call site usually only has to
/// swap the accessor and keep its indices.
/// </para>
/// </remarks>
static class IndexTestHelpers
{
    /// <summary>The per-archetype <see cref="IndexStatistics"/> array for the slot of <paramref name="table"/>'s component in <typeparamref name="TArchetype"/>.</summary>
    public static IndexStatistics[] ArchetypeIndexStats<TArchetype>(DatabaseEngine dbe, ComponentTable table)
        => ResolveSlot<TArchetype>(dbe, table).Stats;

    /// <summary>The per-archetype B+Tree for one indexed field — the counterpart of <c>IndexedFieldInfos[fieldIndex].Index</c>.</summary>
    public static IBTreeIndex ArchetypeIndex<TArchetype>(DatabaseEngine dbe, ComponentTable table, int fieldIndex)
        => ResolveSlot<TArchetype>(dbe, table).Fields[fieldIndex].Index;

    /// <summary>
    /// The cluster state of whichever archetype owns an index slot for <paramref name="table"/>'s component — resolved by search, not by name.
    /// </summary>
    /// <remarks>
    /// Preferred over the generic overloads at call sites that handle several component types: guessing the archetype from the component name breaks the
    /// moment a fixture introduces a third one, which is exactly how the String64 case surfaced.
    /// </remarks>
    public static ArchetypeClusterState OwningCluster(DatabaseEngine dbe, ComponentTable table)
    {
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            var clusterState = engineState?.ClusterState;
            if (clusterState?.IndexSlots == null)
            {
                continue;
            }

            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (!ReferenceEquals(engineState.SlotToComponentTable[slot], table))
                {
                    continue;
                }

                for (var i = 0; i < clusterState.IndexSlots.Length; i++)
                {
                    if (clusterState.IndexSlots[i].Slot == slot && clusterState.IndexSlots[i].Fields != null)
                    {
                        return clusterState;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The B+Tree for one indexed field of <paramref name="table"/>, on whichever archetype owns an index slot for it — resolved by search, not by name.
    /// </summary>
    /// <remarks>
    /// For call sites that only have the <see cref="ComponentTable"/>. Prefer <see cref="ArchetypeIndex{TArchetype}"/> when the archetype is known: a search
    /// returns the FIRST owner, which is the wrong one the moment a second archetype holds the same component.
    /// </remarks>
    public static IBTreeIndex OwningIndex(DatabaseEngine dbe, ComponentTable table, int fieldIndex)
    {
        var clusterState = OwningCluster(dbe, table);
        if (clusterState == null)
        {
            return null;
        }

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            if (!ReferenceEquals(engineState?.ClusterState, clusterState))
            {
                continue;
            }

            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (!ReferenceEquals(engineState.SlotToComponentTable[slot], table))
                {
                    continue;
                }

                for (var i = 0; i < clusterState.IndexSlots.Length; i++)
                {
                    if (clusterState.IndexSlots[i].Slot == slot && clusterState.IndexSlots[i].Fields != null)
                    {
                        return clusterState.IndexSlots[i].Fields[fieldIndex].Index;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Per-archetype statistics for <paramref name="table"/>, or <see langword="null"/> when no archetype indexes it.</summary>
    public static IndexStatistics[] OwningStats(DatabaseEngine dbe, ComponentTable table)
    {
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var engineState = dbe._archetypeStates[meta.ArchetypeId];
            var clusterState = engineState?.ClusterState;
            if (clusterState?.IndexSlots == null)
            {
                continue;
            }

            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (!ReferenceEquals(engineState.SlotToComponentTable[slot], table))
                {
                    continue;
                }

                for (var i = 0; i < clusterState.IndexSlots.Length; i++)
                {
                    if (clusterState.IndexSlots[i].Slot == slot && clusterState.IndexSlots[i].Fields != null)
                    {
                        return clusterState.IndexSlots[i].Stats;
                    }
                }
            }
        }

        return null;
    }

    private static ClusterIndexSlot<PersistentStore> ResolveSlot<TArchetype>(DatabaseEngine dbe, ComponentTable table)
    {
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        Assert.That(meta, Is.Not.Null, $"archetype {typeof(TArchetype).Name} is not registered");

        var engineState = dbe._archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        Assert.That(clusterState?.IndexSlots, Is.Not.Null,
            $"{typeof(TArchetype).Name} has no per-archetype index slots — it is either not cluster-backed or has no indexed field");

        // A component sits at a different slot in each archetype, and index slots are keyed on the archetype's own slot numbering, so the table identity is
        // the only reliable join.
        for (var slot = 0; slot < meta.ComponentCount; slot++)
        {
            if (!ReferenceEquals(engineState.SlotToComponentTable[slot], table))
            {
                continue;
            }

            for (var i = 0; i < clusterState.IndexSlots.Length; i++)
            {
                if (clusterState.IndexSlots[i].Slot == slot && clusterState.IndexSlots[i].Fields != null)
                {
                    return clusterState.IndexSlots[i];
                }
            }
        }

        Assert.Fail($"{typeof(TArchetype).Name} owns no index slot for the given component table");
        return default;
    }

    /// <summary>
    /// Oracle: every UNIQUE per-archetype secondary index must agree with the cluster data it indexes. Walks the live cluster occupancy, derives the key each
    /// entity actually holds in its indexed field, and checks the B+Tree against it — every held key present, no key present that no entity holds, and the
    /// entry count equal to the distinct key count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the suite's index coverage asserted the wrong thing. Mutation tests checked the QUERY RESULT, and queries take the SoA scan, which
    /// reads component data and never consults the tree — so an index could be arbitrarily wrong while every assertion passed. Spawn and destroy had
    /// index-level assertions; update did not. Call this after a mutating operation and the tree gets checked without writing a bespoke assertion for it.
    /// </para>
    /// <para>
    /// A DUPLICATE key in the data on a unique index is reported as a violation in its own right. A unique index physically cannot represent two entities at one
    /// key, so if the write path admitted both, the index is unrepresentable rather than merely stale — that is the defect, not a symptom of one.
    /// </para>
    /// <para>
    /// AllowMultiple fields are SKIPPED, and say so rather than silently passing: their leaf value is a VSBS buffer-root id, not a cluster location, so checking
    /// them means walking the buffer. Doing that wrong would be worse than not doing it. Extending the oracle to cover them is worth doing.
    /// </para>
    /// </remarks>
    public static unsafe List<string> IndexDataMismatches(DatabaseEngine dbe, ArchetypeMetadata meta)
    {
        var violations = new List<string>();
        var engineState = dbe._archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState?.IndexSlots == null || clusterState.ClusterSegment == null)
        {
            return violations;
        }

        var layout = clusterState.Layout;
        // Own the epoch rather than relying on the caller's: the oracle is meant to be callable from anywhere in a test, including outside a transaction, and
        // ChunkAccessor creation asserts it is inside an epoch scope.
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var ixs = 0; ixs < clusterState.IndexSlots.Length; ixs++)
            {
                ref var ixSlot = ref clusterState.IndexSlots[ixs];
                if (ixSlot.Fields == null)
                {
                    continue;
                }

                var compSlot = ixSlot.Slot;
                var compOffset = layout.ComponentOffset(compSlot);
                var compSize = layout.ComponentSize(compSlot);

                for (var fi = 0; fi < ixSlot.Fields.Length; fi++)
                {
                    ref var field = ref ixSlot.Fields[fi];
                    if (field.Index == null)
                    {
                        continue;
                    }

                    var label = $"{meta.Name} slot{compSlot} field{fi} (off={field.FieldOffset} size={field.FieldSize})";
                    if (field.AllowMultiple || field.FieldSize > sizeof(long))
                    {
                        violations.Add($"SKIPPED {label}: {(field.AllowMultiple ? "AllowMultiple" : "key wider than 8 bytes")} not yet checkable");
                        continue;
                    }

                    // Truth comes from the DATA: key bytes -> the entity's cluster location.
                    var expected = new Dictionary<long, int>();
                    var dupes = new List<string>();
                    for (var c = 0; c < clusterState.ActiveClusterCount; c++)
                    {
                        var chunkId = clusterState.ActiveClusterIds[c];
                        var clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                        var occupancy = *(ulong*)clusterBase;
                        while (occupancy != 0)
                        {
                            var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                            occupancy &= occupancy - 1;

                            var fieldPtr = clusterBase + compOffset + slotIndex * compSize + field.FieldOffset;
                            long key = 0;
                            Buffer.MemoryCopy(fieldPtr, &key, sizeof(long), field.FieldSize);
                            var location = chunkId * 64 + slotIndex;

                            if (expected.TryGetValue(key, out var firstLoc))
                            {
                                dupes.Add($"key {key} held at cluster locations {firstLoc} AND {location}");
                                continue;
                            }

                            expected[key] = location;
                        }
                    }

                    foreach (var d in dupes)
                    {
                        violations.Add($"{label}: DUPLICATE key in data on a UNIQUE index — {d}. The tree cannot represent both, so one entity is unreachable "
                            + "through the index. Either the write path must reject the duplicate, or the field needs [Index(AllowMultiple = true)].");
                    }

                    var ixAccessor = field.Index.Segment.CreateChunkAccessor();
                    try
                    {
                        foreach (var kv in expected)
                        {
                            var k = kv.Key;
                            if (!field.Index.TryGet(&k, ref ixAccessor).IsSuccess)
                            {
                                violations.Add($"{label}: key {k} is held by the entity at cluster location {kv.Value} but is MISSING from the index.");
                            }
                        }

                        if (dupes.Count == 0 && field.Index.EntryCount != expected.Count)
                        {
                            violations.Add($"{label}: index EntryCount={field.Index.EntryCount} but the data holds {expected.Count} distinct keys — the index "
                                + "carries entries for values no entity holds (a stale key left by an update, or an entry not removed on destroy).");
                        }
                    }
                    finally
                    {
                        ixAccessor.Dispose();
                    }
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }

        return violations;
    }

    /// <summary>Asserts <see cref="IndexDataMismatches"/> found no disagreement for <typeparamref name="TArchetype"/>.</summary>
    public static void AssertIndexMatchesData<TArchetype>(DatabaseEngine dbe, string when = null)
        where TArchetype : Archetype<TArchetype>
    {
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        var v = IndexDataMismatches(dbe, meta).FindAll(x => !x.StartsWith("SKIPPED"));
        Assert.That(v, Is.Empty, $"index/data disagreement{(when == null ? "" : " " + when)}:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", v)}");
    }
}
