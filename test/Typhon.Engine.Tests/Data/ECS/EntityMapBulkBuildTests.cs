using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1665 — the crash open's bulk build of an EntityMap: emptied at its final bucket count and filled bucket by bucket, it holds every entry, never splits,
/// persists its counts, and splits one bucket at a time as usual once inserts resume.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class EntityMapBulkBuildTests : TestBase<EntityMapBulkBuildTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<BulkInsMark>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static RawValuePagedHashMap<long, PersistentStore> MapOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[ArchetypeRegistry.GetMetadata<BulkInsUnit>().ArchetypeId].EntityMap;

    private static long PayloadFor(long key) => (key * unchecked((long)0x9E3779B97F4A7C15UL)) ^ 0x5DEECE66DL;

    /// <summary>Sequential keys with gaps, and scattered ones: runs the hash keeps together and keys it spreads.</summary>
    private static long[] Keys(int count)
    {
        var keys = new HashSet<long>();
        var random = new Random(1665);
        for (var i = 0; keys.Count < count; i++)
        {
            keys.Add(i % 3 == 0 ? random.NextInt64(1, 1L << 40) : 1_000 + i);
        }

        return keys.ToArray();
    }

    /// <summary>Builds the map from <paramref name="keys"/>, in bucket order. Returns the bucket count.</summary>
    private static int Build(DatabaseEngine dbe, long[] keys)
    {
        var map = MapOf(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var changeSet = dbe.MMF.CreateChangeSet();
        var accessor = map.Segment.CreateChunkAccessor(changeSet);
        try
        {
            var bucketCount = map.BeginBulkBuild(keys.Length, changeSet);
            var sorted = keys.Select(k => (Key: k, Bucket: map.BucketIndexOf(k))).OrderBy(e => e.Bucket).ToArray();
            foreach (var (key, bucket) in sorted)
            {
                var value = map.BulkBuildAppend(key, bucket, ref accessor, changeSet);
                var payload = PayloadFor(key);
                new Span<byte>(value, map.ValueSize).Clear();
                MemoryMarshal.Write(new Span<byte>(value, map.ValueSize), in payload);
            }

            map.EndBulkBuild(ref accessor, changeSet);
            return bucketCount;
        }
        finally
        {
            accessor.Dispose();
            changeSet.SaveChanges();
        }
    }

    private static int Missing(DatabaseEngine dbe, IEnumerable<long> keys)
    {
        var map = MapOf(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = map.Segment.CreateChunkAccessor();
        var missing = 0;
        try
        {
            var value = stackalloc byte[map.ValueSize];
            foreach (var key in keys)
            {
                if (!map.TryGet(key, value, ref accessor) || MemoryMarshal.Read<long>(new ReadOnlySpan<byte>(value, map.ValueSize)) != PayloadFor(key))
                {
                    missing++;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return missing;
    }

    private static bool Verify(DatabaseEngine dbe)
    {
        var map = MapOf(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = map.Segment.CreateChunkAccessor();
        try
        {
            return map.VerifyIntegrity(ref accessor);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    public void ABulkBuild_HoldsEveryEntry_AtItsFinalSize()
    {
        using var dbe = SetupEngine();
        var map = MapOf(dbe);
        var keys = Keys(20_000);
        map.ResetDiagnostics();

        var bucketCount = Build(dbe, keys);

        Assert.That(map.BucketCount, Is.EqualTo(bucketCount), "the bucket count the build began with");
        Assert.That((double)keys.Length / ((long)bucketCount * map.BucketCapacity), Is.LessThanOrEqualTo(0.75).And.GreaterThan(0.6),
            "sized to the split threshold, no larger");
        Assert.That(map._splitCount, Is.Zero, "no split during the build");
        Assert.That(map._overflowChunksChained, Is.GreaterThan(0), "premise: some buckets overflowed into chained chunks");
        Assert.That(map.EntryCount, Is.EqualTo(keys.Length));
        Assert.That(Missing(dbe, keys), Is.Zero, "entries missing or with the wrong value");
        Assert.That(Verify(dbe), Is.True, "the map's integrity check");

        var changeSet = dbe.MMF.CreateChangeSet();
        Assert.That(map.FlushMeta(changeSet), Is.False, "the build persisted its counts: nothing stale to flush");
        changeSet.SaveChanges();
    }

    [Test]
    public void AfterABulkBuild_InsertsSplitAsUsual()
    {
        using var dbe = SetupEngine();
        var map = MapOf(dbe);
        var keys = Keys(12_000);
        var built = Build(dbe, keys[..10_000]);
        map.ResetDiagnostics();

        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            var accessor = map.Segment.CreateChunkAccessor();
            try
            {
                var value = stackalloc byte[map.ValueSize];
                foreach (var key in keys[10_000..])
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

        Assert.That(map._splitCount, Is.GreaterThan(0), "the inserts split the built map");
        Assert.That(map.BucketCount, Is.GreaterThan(built));
        Assert.That(Missing(dbe, keys), Is.Zero, "entries missing after the splits");
        Assert.That(Verify(dbe), Is.True, "the map's integrity check");
    }

    [Test]
    public void ABucketBelowTheLastFilled_IsRefused()
    {
        using var dbe = SetupEngine();
        var map = MapOf(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var changeSet = dbe.MMF.CreateChangeSet();
        var accessor = map.Segment.CreateChunkAccessor(changeSet);
        try
        {
            map.BeginBulkBuild(1_000, changeSet);
            var byBucket = Keys(1_000).Select(k => (Key: k, Bucket: map.BucketIndexOf(k))).OrderByDescending(e => e.Bucket).ToArray();
            var keys = new[] { byBucket[0], byBucket[^1] };
            Assert.That(keys[1].Bucket, Is.LessThan(keys[0].Bucket), "premise: two buckets");
            map.BulkBuildAppend(keys[0].Key, keys[0].Bucket, ref accessor, changeSet);
            var refused = false;
            try
            {
                map.BulkBuildAppend(keys[1].Key, keys[1].Bucket, ref accessor, changeSet);
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            Assert.That(refused, Is.True, "a bucket below the last one filled, refused");
        }
        finally
        {
            accessor.Dispose();
            changeSet.SaveChanges();
        }
    }
}
