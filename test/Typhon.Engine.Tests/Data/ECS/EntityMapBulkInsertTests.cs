using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema

[Component("Typhon.Test.BulkIns.Mark", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct BulkInsMark
{
    public int Value;

    public BulkInsMark(int value) => Value = value;
}

[Archetype]
class BulkInsUnit : Archetype<BulkInsUnit>
{
    public static readonly Comp<BulkInsMark> Mark = Register<BulkInsMark>();
}

/// <summary>
/// A second archetype over the same component, so the two arms get two independent <c>EntityMap</c>s inside ONE engine.
/// </summary>
/// <remarks>
/// Two engines would have been the obvious shape and does not work: <c>TestBase</c>'s provider hands out one engine per test, and asking it for a second
/// reopens the same database file, which NREs in <c>LoadPersistedArchetypes</c>. Two archetypes start from identical empty maps with identical geometry,
/// which is what the comparison actually needs.
/// </remarks>
[Archetype]
class BulkInsUnitB : Archetype<BulkInsUnitB>
{
    public static readonly Comp<BulkInsMark> Mark = Register<BulkInsMark>();
}

#endregion

/// <summary>
/// #1100 — <c>InsertNewBulk</c> produces the same map a run of <c>InsertNew</c> would, and refuses rather than splits when the hash state was not advanced
/// for the batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the refusal matters as much as the equivalence.</b> The method exists so a parallel region can own disjoint buckets, and that only holds while
/// nothing inside the region changes the structure. A split would — it takes the global split lock and rewrites the round-robin bucket <c>Next</c> plus the
/// directory and the meta, none of which belongs to the partition doing the inserting. So a batch that would cross the load-factor threshold has to be
/// refused loudly, not absorbed: absorbing it would corrupt the partitioning silently, which is the worst available outcome.
/// </para>
/// <para>
/// <b>The comparison is over content and shape, not bytes.</b> Every key's value, the entry count, the bucket count and the chained overflow chunks. A
/// byte-identical chunk layout would additionally pin allocation ORDER, which the two paths have no reason to share — a serial run interleaves its splits
/// with its inserts while the bulk run has none — so asserting it would fail for a reason that is not a defect.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class EntityMapBulkInsertTests : TestBase<EntityMapBulkInsertTests>
{
    private const int BatchSize = 3000;

    /// <summary>One staged insert: the key, the bucket the caller resolved it to, and the value bytes.</summary>
    private struct Entry
    {
        public long Key;
        public int Bucket;
        public long Payload;
    }

    /// <summary>
    /// Writes <see cref="Entry.Payload"/> into the map's value slot, zero-padded to the value size.
    /// </summary>
    /// <remarks>
    /// The destination is a span over page-cache memory the engine owns; the source is this managed struct. That direction is the point — see the pointer
    /// note on <see cref="IRawBulkInserter{TKey,TEntry}"/>.
    /// </remarks>
    private struct Inserter : IRawBulkInserter<long, Entry>
    {
        public long KeyOf(in Entry entry) => entry.Key;

        public int BucketOf(in Entry entry) => entry.Bucket;

        public void WriteValue(in Entry entry, Span<byte> destination)
        {
            destination.Clear();
            MemoryMarshal.Write(destination, in entry.Payload);
        }
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<BulkInsMark>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static RawValuePagedHashMap<long, PersistentStore> MapOf<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch> =>
        dbe._archetypeStates[ArchetypeRegistry.GetMetadata<TArch>().ArchetypeId].EntityMap;

    /// <summary>Keys well clear of anything the engine itself issued, so the two arms cannot collide with real entities.</summary>
    private static long[] Keys(int count)
    {
        var keys = new long[count];
        for (var i = 0; i < count; i++)
        {
            keys[i] = 1_000_000 + i;
        }

        return keys;
    }

    private static long PayloadFor(long key) => (key * unchecked((long)0x9E3779B97F4A7C15UL)) ^ 0x5DEECE66DL;

    /// <summary>Inserts the batch one key at a time through the ordinary path, splits and all.</summary>
    private static void InsertSerially<TArch>(DatabaseEngine dbe, long[] keys) where TArch : Archetype<TArch>
    {
        var map = MapOf<TArch>(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = map.Segment.CreateChunkAccessor(null);
        try
        {
            var value = stackalloc byte[map.ValueSize];
            foreach (var key in keys)
            {
                new Span<byte>(value, map.ValueSize).Clear();
                var payload = PayloadFor(key);
                MemoryMarshal.Write(new Span<byte>(value, map.ValueSize), in payload);
                map.InsertNew(key, value, ref accessor, null);
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>Advances the hash state for the whole batch, then inserts it bucket-sorted in one call.</summary>
    private static int InsertInBulk<TArch>(DatabaseEngine dbe, long[] keys) where TArch : Archetype<TArch>
    {
        var map = MapOf<TArch>(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);

        var advanced = map.AdvanceHashStateFor(keys.Length);

        // Bucket indices are resolved AFTER the advance and before the insert, which is the only window in which they are stable — the advance moves them
        // and the insert must not.
        var batch = new Entry[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            batch[i] = new Entry { Key = keys[i], Bucket = map.BucketIndexOf(keys[i]), Payload = PayloadFor(keys[i]) };
        }

        Array.Sort(batch, (a, b) => a.Bucket.CompareTo(b.Bucket));

        var accessor = map.Segment.CreateChunkAccessor(null);
        try
        {
            map.InsertNewBulk<Entry, Inserter>(batch, ref accessor, null);
        }
        finally
        {
            accessor.Dispose();
        }

        return advanced;
    }

    /// <summary>
    /// Walks every bucket and returns the overflow chunks LIVE in the map and the deepest chain in it.
    /// </summary>
    /// <remarks>
    /// <b>This replaces comparing <c>_overflowChunksChained</c>, which is a cumulative counter and not a property of the map.</b> The serial arm chains 151
    /// and the bulk arm 78 for the same final content, and the bulk arm is not wrong — it is doing less work. A serial run interleaves inserts with splits,
    /// so a bucket fills and chains, a later split redistributes it and frees the chunk, and the next inserts chain again; the bulk arm has every split done
    /// before the first insert, so the buckets are already wide when the entries land. What has to match is the structure the two arrive at.
    /// </remarks>
    private static (int LiveOverflow, int DeepestChain) WalkChains<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch>
    {
        var map = MapOf<TArch>(dbe);
        var liveOverflow = 0;
        var deepest = 0;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = map.Segment.CreateChunkAccessor(null);
        try
        {
            for (var bucket = 0; bucket < map.LiveBucketCount; bucket++)
            {
                var chunkId = map.GetBucketChunkIdForTest(bucket, ref accessor);
                var depth = 0;
                while (chunkId != -1)
                {
                    chunkId = RawValuePagedHashMap<long, PersistentStore>.BucketOverflowChunkIdForTest(chunkId, ref accessor);
                    if (chunkId != -1)
                    {
                        liveOverflow++;
                        depth++;
                    }
                }

                if (depth > deepest)
                {
                    deepest = depth;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (liveOverflow, deepest);
    }

    private static Dictionary<long, long> ReadBack<TArch>(DatabaseEngine dbe, long[] keys) where TArch : Archetype<TArch>
    {
        var map = MapOf<TArch>(dbe);
        var found = new Dictionary<long, long>(keys.Length);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = map.Segment.CreateChunkAccessor(null);
        try
        {
            var value = stackalloc byte[map.ValueSize];
            foreach (var key in keys)
            {
                if (map.TryGet(key, value, ref accessor))
                {
                    found[key] = MemoryMarshal.Read<long>(new ReadOnlySpan<byte>(value, map.ValueSize));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return found;
    }

    /// <summary>
    /// The equivalence: same keys, same values, same entry count, same bucket count, same chained overflow — and zero splits inside the bulk insert.
    /// </summary>
    [Test]
    public void ABulkInsertProducesTheSameMapAsASerialOne()
    {
        using var dbe = SetupEngine();
        var keys = Keys(BatchSize);

        var serialMap = MapOf<BulkInsUnit>(dbe);
        var bulkMap = MapOf<BulkInsUnitB>(dbe);
        serialMap.ResetDiagnostics();
        bulkMap.ResetDiagnostics();

        InsertSerially<BulkInsUnit>(dbe, keys);
        var advanced = InsertInBulk<BulkInsUnitB>(dbe, keys);

        var serialContent = ReadBack<BulkInsUnit>(dbe, keys);
        var bulkContent = ReadBack<BulkInsUnitB>(dbe, keys);
        var serialChains = WalkChains<BulkInsUnit>(dbe);
        var bulkChains = WalkChains<BulkInsUnitB>(dbe);

        TestContext.Out.WriteLine($"serial: entries={serialMap.EntryCount} buckets={serialMap.LiveBucketCount} splits={serialMap._splitCount} "
            + $"chained={serialMap._overflowChunksChained} liveOverflow={serialChains.LiveOverflow} deepest={serialChains.DeepestChain}");
        TestContext.Out.WriteLine($"bulk:   entries={bulkMap.EntryCount} buckets={bulkMap.LiveBucketCount} splits={bulkMap._splitCount} "
            + $"(of which pre-advance {advanced}) chained={bulkMap._overflowChunksChained} liveOverflow={bulkChains.LiveOverflow} "
            + $"deepest={bulkChains.DeepestChain}");

        Assert.Multiple(() =>
        {
            // Content first: a map with the right counts and the wrong bytes is a different defect from one with the wrong counts.
            Assert.That(serialContent, Has.Count.EqualTo(keys.Length), "every key inserted serially must be readable back");
            Assert.That(bulkContent, Has.Count.EqualTo(keys.Length), "and so must every key inserted in bulk");
            foreach (var kv in serialContent)
            {
                Assert.That(bulkContent.TryGetValue(kv.Key, out var bulkValue), Is.True, $"key {kv.Key} is missing from the bulk map");
                Assert.That(bulkValue, Is.EqualTo(kv.Value), $"key {kv.Key} holds a different value");
            }

            // Then shape.
            Assert.That(bulkMap.EntryCount, Is.EqualTo(serialMap.EntryCount), "entry counts must agree");
            Assert.That(bulkMap.LiveBucketCount, Is.EqualTo(serialMap.LiveBucketCount), "and the maps must have grown to the same width");
            // The LIVE chains, not the cumulative chain counter: see WalkChains for why the two arms legitimately differ on the latter.
            Assert.That(bulkChains.LiveOverflow, Is.EqualTo(serialChains.LiveOverflow), "and hold the same number of live overflow chunks");
            Assert.That(bulkChains.DeepestChain, Is.EqualTo(serialChains.DeepestChain), "with the same deepest chain, so no bucket is worse off");

            // And the property the method exists for: the splits all happened in the pre-advance, none during the insert.
            Assert.That(advanced, Is.GreaterThan(0), "precondition: a 3000-entry batch must need the hash state advanced, or this proves nothing");
            Assert.That(bulkMap._splitCount, Is.EqualTo(advanced), "every split must belong to the pre-advance — the bulk insert itself performs none");
        });
    }

    /// <summary>
    /// The mutation guard: skip the pre-advance and the bulk insert must refuse, naming what to call. This is the case that fails if someone ever "fixes"
    /// the refusal by letting the insert split instead — which would pass an equivalence test and silently break every caller's partitioning.
    /// </summary>
    [Test]
    public void WithoutThePreAdvanceTheBulkInsertRefuses()
    {
        using var dbe = SetupEngine();
        var map = MapOf<BulkInsUnit>(dbe);
        var keys = Keys(BatchSize);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var batch = new Entry[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            batch[i] = new Entry { Key = keys[i], Bucket = map.BucketIndexOf(keys[i]), Payload = PayloadFor(keys[i]) };
        }

        Array.Sort(batch, (a, b) => a.Bucket.CompareTo(b.Bucket));

        var accessor = map.Segment.CreateChunkAccessor(null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                var local = map.Segment.CreateChunkAccessor(null);
                try
                {
                    map.InsertNewBulk<Entry, Inserter>(batch, ref local, null);
                }
                finally
                {
                    local.Dispose();
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(ex.Message, Does.Contain("AdvanceHashStateFor"), "the message must name the call that fixes it");
                Assert.That(ex.Message, Does.Contain("never splits"), "and say why it cannot just absorb the batch");
                Assert.That(map.EntryCount, Is.Zero, "and nothing may have been inserted — the refusal is before the first write");
            });
        }
        finally
        {
            accessor.Dispose();
        }
    }
}
