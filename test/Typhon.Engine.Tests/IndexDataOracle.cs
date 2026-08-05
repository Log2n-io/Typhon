using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The one place that checks a per-archetype secondary index against the data it indexes, in BOTH directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The suite had ~4 000 tests and none of them checked an index after a mutation. Every mutation test asserted the QUERY RESULT, and
/// queries take the SoA scan, which evaluates predicates against component data and never consults the tree — so the index could be arbitrarily wrong while
/// every assertion passed. Spawn and destroy had index-level assertions; update did not, and that empty cell was a real defect (#675).
/// </para>
/// <para>
/// <b>Both directions, because each catches a different failure.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Data → index</b> catches a SHORTFALL: an entity whose current value is missing from the tree, which is what a skipped or lost index update looks like.
/// </description></item>
/// <item><description>
/// <b>Index → data</b> catches an OVER-POPULATION and, more importantly, a MIS-POINTED leaf: a leaf that resolves to an unoccupied slot, or to a slot holding a
/// different entity. That second case is what a wrong-slot copy during a re-cluster produces, and no amount of "is the key present" checking finds it — the key
/// IS present, it just names the wrong entity. A one-direction oracle would pass.
/// </description></item>
/// </list>
/// <para>
/// <b>What it replaces.</b> The branch grew three separate index-walking helpers, one per fixture that needed one:
/// <c>SchemaEvolutionTests.IndexedEntities</c> (strict, and the model this generalises), <c>Probe671IdxMigTests.IndexedEntities</c> (lenient — it accepted a
/// leaf naming an unoccupied slot), and <c>RecoveryOracle.ClusterIndexEntityIds</c>. Three implementations of one invariant means three chances to write the
/// lenient one, and the lenient one is the one that passes when the code is broken.
/// </para>
/// <para>
/// <b>Duplicate keys on a unique index are reported as a violation in their own right.</b> A unique index physically cannot represent two entities at one key,
/// so if the write path admitted both, the tree is unrepresentable rather than merely stale — that is the defect, not a downstream symptom of one.
/// </para>
/// <para>
/// <b>Deliberate gaps, reported rather than silently passed.</b> Keys wider than 8 bytes and non-<c>int</c> AllowMultiple keys are skipped with a message. A
/// skipped check that returns success is indistinguishable from a passing one, which is the exact failure mode this file exists to remove.
/// </para>
/// </remarks>
internal static unsafe class IndexDataOracle
{
    /// <summary>Asserts every unique / int-keyed index on <paramref name="meta"/> agrees with its cluster data, in both directions.</summary>
    internal static void AssertIndexAgreesWithData<TArchetype>(DatabaseEngine dbe, string when = null)
        where TArchetype : Archetype<TArchetype>
        => AssertIndexAgreesWithData(dbe, ArchetypeRegistry.GetMetadata<TArchetype>(), when);

    /// <inheritdoc cref="AssertIndexAgreesWithData{TArchetype}"/>
    internal static void AssertIndexAgreesWithData(DatabaseEngine dbe, ArchetypeMetadata meta, string when = null)
    {
        var problems = Check(dbe, meta, out var skipped);
        if (problems.Count == 0)
        {
            return;
        }

        var ctx = when == null ? "" : " " + when;
        Assert.Fail($"index/data disagreement{ctx} on {meta.Name}:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", problems)}"
            + (skipped.Count == 0 ? "" : $"{Environment.NewLine}(not checked: {string.Join("; ", skipped)})"));
    }

    /// <summary>The findings, for a caller that wants to inspect rather than assert.</summary>
    internal static List<string> Check(DatabaseEngine dbe, ArchetypeMetadata meta, out List<string> skipped)
    {
        var problems = new List<string>();
        skipped = [];

        var engineState = dbe._archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState?.IndexSlots == null || clusterState.ClusterSegment == null)
        {
            return problems;
        }

        // Own the epoch: the oracle must be callable from anywhere in a test, including outside a transaction, and ChunkAccessor creation asserts it is inside
        // an epoch scope.
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var layout = clusterState.Layout;
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

                    var label = $"slot{compSlot}.field{fi}(off={field.FieldOffset},size={field.FieldSize})";
                    if (field.FieldSize > sizeof(long))
                    {
                        skipped.Add($"{label}: key wider than 8 bytes");
                        continue;
                    }

                    CheckField(clusterState, layout, ref clusterAccessor, ref field, compOffset, compSize, label, problems, skipped);
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }

        return problems;
    }

    private static void CheckField(ArchetypeClusterState clusterState, ArchetypeClusterInfo layout, ref ChunkAccessor<PersistentStore> clusterAccessor,
        ref ClusterIndexField<PersistentStore> field, int compOffset, int compSize, string label, List<string> problems, List<string> skipped)
    {
        // ── Truth, derived from the DATA: key -> the cluster locations of the entities currently holding it ──────────────────────────────────────────────
        var expected = new Dictionary<long, List<int>>();
        var locationToEntity = new Dictionary<int, long>();
        for (var c = 0; c < clusterState.ActiveClusterCount; c++)
        {
            var chunkId = clusterState.ActiveClusterIds[c];
            var clusterBase = clusterAccessor.GetChunkAddress(chunkId);
            var occupancy = *(ulong*)clusterBase;
            while (occupancy != 0)
            {
                var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;

                long key = 0;
                Buffer.MemoryCopy(clusterBase + compOffset + slotIndex * compSize + field.FieldOffset, &key, sizeof(long), field.FieldSize);
                var location = chunkId * 64 + slotIndex;
                (expected.TryGetValue(key, out var list) ? list : expected[key] = []).Add(location);
                locationToEntity[location] = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
            }
        }

        var idxAccessor = field.Index.Segment.CreateChunkAccessor();
        try
        {
            if (!field.AllowMultiple)
            {
                foreach (var kv in expected)
                {
                    if (kv.Value.Count > 1)
                    {
                        problems.Add($"{label}: DUPLICATE key {kv.Key} on a UNIQUE index — held at cluster locations {string.Join(" and ", kv.Value)}. The "
                            + "tree cannot represent both, so one entity is unreachable through the index. Either the write path must reject the duplicate, or "
                            + "the field needs [Index(AllowMultiple = true)].");
                        continue;
                    }

                    var k = kv.Key;
                    var found = field.Index.TryGet(&k, ref idxAccessor);
                    if (!found.IsSuccess)
                    {
                        problems.Add($"{label}: key {k} is held by the entity at cluster location {kv.Value[0]} but is MISSING from the index.");
                        continue;
                    }

                    // The leaf VALUE, not merely its presence. A re-cluster that placed an entity correctly but wrote the index entry for the wrong slot leaves
                    // the key present and pointing elsewhere — presence-only checking passes, and the query then returns another entity's row.
                    if (found.Value != kv.Value[0])
                    {
                        problems.Add($"{label}: key {k} resolves to cluster location {found.Value} but the entity holding that value is at {kv.Value[0]}"
                            + $" (entity {locationToEntity[kv.Value[0]]}). The leaf names the WRONG slot.");
                    }
                }

                if (field.Index.EntryCount != expected.Count)
                {
                    problems.Add($"{label}: index EntryCount={field.Index.EntryCount} but the data holds {expected.Count} distinct keys — the index carries "
                        + "entries for values no entity holds (a stale key left by an update, or an entry not removed on destroy).");
                }

                return;
            }

            // ── AllowMultiple: the leaf value is a VSBS buffer-root id, so the entries have to be walked rather than probed ──────────────────────────────
            if (field.Index is not BTree<int, PersistentStore> intTree)
            {
                skipped.Add($"{label}: AllowMultiple with a non-int key");
                return;
            }

            var seen = new Dictionary<long, List<int>>();
            var e = intTree.EnumerateRangeMultiple(int.MinValue, int.MaxValue);
            while (e.MoveNextKey())
            {
                var values = e.CurrentValues;
                for (var i = 0; i < values.Length; i++)
                {
                    var location = values[i];
                    var chunkId = location >> 6;
                    var slotIndex = location & 0x3F;
                    var clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                    if ((*(ulong*)clusterBase & (1UL << slotIndex)) == 0)
                    {
                        problems.Add($"{label}: leaf value {location} names cluster slot {chunkId}:{slotIndex}, which is NOT OCCUPIED — a stale entry left "
                            + "behind by a destroy or a re-cluster.");
                        continue;
                    }

                    long actual = 0;
                    Buffer.MemoryCopy(clusterBase + compOffset + slotIndex * compSize + field.FieldOffset, &actual, sizeof(long), field.FieldSize);
                    if (actual != e.CurrentKey)
                    {
                        problems.Add($"{label}: leaf key {e.CurrentKey} names cluster slot {chunkId}:{slotIndex}, but the entity there currently holds "
                            + $"{actual}. The leaf points at the WRONG slot.");
                        continue;
                    }

                    (seen.TryGetValue(e.CurrentKey, out var list) ? list : seen[e.CurrentKey] = []).Add(location);
                }
            }

            foreach (var kv in expected)
            {
                if (!seen.TryGetValue(kv.Key, out var got))
                {
                    problems.Add($"{label}: key {kv.Key} is held by {kv.Value.Count} entit(y|ies) but is MISSING from the index entirely.");
                    continue;
                }

                foreach (var loc in kv.Value)
                {
                    if (!got.Contains(loc))
                    {
                        problems.Add($"{label}: the entity at cluster location {loc} holds key {kv.Key} but the index does not list it there — a sibling of "
                            + "the same key was indexed and this one was not.");
                    }
                }
            }
        }
        finally
        {
            idxAccessor.Dispose();
        }
    }
}
