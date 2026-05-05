using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

[TestFixture]
public class HashMapTests
{
    // Reuse BitmapTestServices — same DI setup (ResourceRegistry + MemoryAllocator)
    private static IMemoryAllocator Allocator => BitmapTestServices.MemoryAllocator;
    private static IResource Parent => BitmapTestServices.AllocationResource;

    private static HashMap<int> CreateSet(int initialBuckets = 64) =>
        new(initialBuckets);

    private static HashMap<int, int> CreateMap(int initialBuckets = 64) =>
        new(initialBuckets);

    private static HashMap<int, string> CreateManagedMap(int initialBuckets = 64) =>
        new(initialBuckets);

    // ═══════════════════════════════════════════════════════════════════════
    // HashUtils tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ComputeHash_Int_IsDeterministic()
    {
        uint h1 = HashUtils.ComputeHash(42);
        uint h2 = HashUtils.ComputeHash(42);
        Assert.That(h1, Is.EqualTo(h2));
    }

    [Test]
    public void ComputeHash_Long_IsDeterministic()
    {
        uint h1 = HashUtils.ComputeHash(42L);
        uint h2 = HashUtils.ComputeHash(42L);
        Assert.That(h1, Is.EqualTo(h2));
    }

    [Test]
    public void ComputeHash_DifferentKeys_ProduceDifferentHashes()
    {
        uint h1 = HashUtils.ComputeHash(1);
        uint h2 = HashUtils.ComputeHash(2);
        Assert.That(h1, Is.Not.EqualTo(h2));
    }

    [Test]
    public void ComputeHash_NonTrivialOutput()
    {
        // Hash should avalanche — not be identity
        Assert.That(HashUtils.ComputeHash(42), Is.Not.EqualTo(42u));
        Assert.That(HashUtils.ComputeHash(0), Is.Not.EqualTo(0u));
    }

    [Test]
    public unsafe void ComputeHash_16ByteKey_UsesGenericPath()
    {
        // Guid is 16 bytes — exercises XxHash32_Bytes fallback
        var guid = new Guid(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        uint h1 = HashUtils.ComputeHash(guid);
        uint h2 = HashUtils.ComputeHash(guid);
        Assert.That(h1, Is.EqualTo(h2));
        Assert.That(h1, Is.Not.EqualTo(0u));
    }

    [Test]
    public void PackUnpackMeta_Roundtrip()
    {
        long packed = HashUtils.PackMeta(3, 100, 500);
        var (level, next, bucketCount) = HashUtils.UnpackMeta(packed);
        Assert.That(level, Is.EqualTo(3));
        Assert.That(next, Is.EqualTo(100));
        Assert.That(bucketCount, Is.EqualTo(500));
    }

    [Test]
    public void PackUnpackMeta_MaxValues()
    {
        long packed = HashUtils.PackMeta(255, 0x00FFFFFF, int.MaxValue);
        var (level, next, bucketCount) = HashUtils.UnpackMeta(packed);
        Assert.That(level, Is.EqualTo(255));
        Assert.That(next, Is.EqualTo(0x00FFFFFF));
        Assert.That(bucketCount, Is.EqualTo(int.MaxValue));
    }

    [Test]
    public void ResolveBucket_BeforeNext_UsesCoarseModulus()
    {
        // level=0, next=2, n0=8 → mod=8
        // bucket = hash & 7. If bucket >= 2, use coarse modulus
        uint hash = 5; // 5 & 7 = 5 ≥ 2 → coarse
        int bucket = HashUtils.ResolveBucket(hash, 0, 2, 8);
        Assert.That(bucket, Is.EqualTo(5));
    }

    [Test]
    public void ResolveBucket_AfterNext_UsesFineModulus()
    {
        // level=0, next=2, n0=8 → mod=8
        // bucket = hash & 7. If bucket < 2, use fine modulus (hash & 15)
        uint hash = 1; // 1 & 7 = 1 < 2 → fine: 1 & 15 = 1
        int bucket = HashUtils.ResolveBucket(hash, 0, 2, 8);
        Assert.That(bucket, Is.EqualTo(1));

        // hash=9: 9 & 7 = 1 < 2 → fine: 9 & 15 = 9
        int bucket2 = HashUtils.ResolveBucket(9, 0, 2, 8);
        Assert.That(bucket2, Is.EqualTo(9));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HashMap<TKey> — Set variant
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Set_TryAdd_NewKey_ReturnsTrue()
    {
        using var set = CreateSet();
        Assert.That(set.TryAdd(42), Is.True);
        Assert.That(set.Count, Is.EqualTo(1));
    }

    [Test]
    public void Set_TryAdd_DuplicateKey_ReturnsFalse()
    {
        using var set = CreateSet();
        set.TryAdd(42);
        Assert.That(set.TryAdd(42), Is.False);
        Assert.That(set.Count, Is.EqualTo(1));
    }

    [Test]
    public void Set_Contains_ExistingKey_ReturnsTrue()
    {
        using var set = CreateSet();
        set.TryAdd(42);
        Assert.That(set.Contains(42), Is.True);
    }

    [Test]
    public void Set_Contains_NonExistingKey_ReturnsFalse()
    {
        using var set = CreateSet();
        Assert.That(set.Contains(42), Is.False);
    }

    [Test]
    public void Set_TryRemove_ExistingKey_ReturnsTrue()
    {
        using var set = CreateSet();
        set.TryAdd(42);
        Assert.That(set.TryRemove(42), Is.True);
        Assert.That(set.Count, Is.EqualTo(0));
        Assert.That(set.Contains(42), Is.False);
    }

    [Test]
    public void Set_TryRemove_NonExistingKey_ReturnsFalse()
    {
        using var set = CreateSet();
        Assert.That(set.TryRemove(42), Is.False);
    }

    [Test]
    public void Set_Count_ReflectsOperations()
    {
        using var set = CreateSet();
        Assert.That(set.Count, Is.EqualTo(0));

        for (int i = 0; i < 10; i++)
        {
            set.TryAdd(i);
        }
        Assert.That(set.Count, Is.EqualTo(10));

        set.TryRemove(5);
        Assert.That(set.Count, Is.EqualTo(9));
    }

    [Test]
    public void Set_Clear_ResetsToEmpty()
    {
        using var set = CreateSet();
        for (int i = 0; i < 100; i++)
        {
            set.TryAdd(i);
        }
        Assert.That(set.Count, Is.GreaterThan(0));

        set.Clear();
        Assert.That(set.Count, Is.EqualTo(0));

        // Verify keys are gone
        for (int i = 0; i < 100; i++)
        {
            Assert.That(set.Contains(i), Is.False);
        }

        // Verify can add again
        Assert.That(set.TryAdd(42), Is.True);
        Assert.That(set.Contains(42), Is.True);
    }

    [Test]
    public void Set_Split_PreservesAllEntries()
    {
        using var set = CreateSet(8);
        int n = 200; // Enough to trigger resizes from initial capacity 8

        for (int i = 0; i < n; i++)
        {
            set.TryAdd(i);
        }

        Assert.That(set.Capacity, Is.GreaterThan(8), "Should have resized at least once");
        Assert.That(set.Count, Is.EqualTo(n));

        // Verify all entries present
        for (int i = 0; i < n; i++)
        {
            Assert.That(set.Contains(i), Is.True, $"Missing key {i} after split");
        }
    }

    [Test]
    public void Set_MultipleSplits_AllEntriesPreserved()
    {
        using var set = CreateSet(4);
        int n = 500; // Forces many splits from initial 4 buckets

        for (int i = 0; i < n; i++)
        {
            set.TryAdd(i);
        }

        Assert.That(set.Capacity, Is.GreaterThan(4));
        Assert.That(set.Count, Is.EqualTo(n));

        for (int i = 0; i < n; i++)
        {
            Assert.That(set.Contains(i), Is.True);
        }
    }

    [Test]
    public void Set_EnsureCapacity_ForcesSplits()
    {
        using var set = CreateSet(8);
        int initialCapacity = set.Capacity;

        set.EnsureCapacity(1000);
        Assert.That(set.Capacity, Is.GreaterThan(initialCapacity));
    }

    [Test]
    public void Set_Enumerator_ReturnsAllEntries()
    {
        using var set = CreateSet(8);
        var expected = new HashSet<int>();
        for (int i = 0; i < 100; i++)
        {
            set.TryAdd(i);
            expected.Add(i);
        }

        var actual = new HashSet<int>();
        foreach (var key in set)
        {
            actual.Add(key);
        }

        Assert.That(actual, Is.EquivalentTo(expected));
    }

    [Test]
    public void Set_Overflow_HandledCorrectly()
    {
        // Use small initial buckets to force overflow within a bucket
        using var set = CreateSet(4);
        // Insert many keys that will hash to same bucket (statistical — just insert enough)
        int n = 50; // Enough entries to exercise probing
        for (int i = 0; i < n; i++)
        {
            set.TryAdd(i);
        }

        Assert.That(set.Count, Is.EqualTo(n));
        for (int i = 0; i < n; i++)
        {
            Assert.That(set.Contains(i), Is.True);
        }
    }

    [Test]
    public void Set_TryRemove_FromOverflow_UnlinksEmptySlot()
    {
        using var set = CreateSet(4);
        int n = 100;
        for (int i = 0; i < n; i++)
        {
            set.TryAdd(i);
        }

        // Remove all — should unlink empty overflow slots
        for (int i = 0; i < n; i++)
        {
            Assert.That(set.TryRemove(i), Is.True);
        }
        Assert.That(set.Count, Is.EqualTo(0));

        // Re-add should work
        for (int i = 0; i < n; i++)
        {
            Assert.That(set.TryAdd(i), Is.True);
        }
        Assert.That(set.Count, Is.EqualTo(n));
    }

    [Test]
    public void Set_Dispose_ReleasesResources()
    {
        var set = CreateSet();
        set.TryAdd(1);
        set.TryAdd(2);
        set.Dispose();
        // Double dispose should be safe
        set.Dispose();
    }

    [Test]
    public void Set_Stress_10K_Keys()
    {
        using var set = CreateSet(16);
        const int N = 10_000;

        // Insert
        for (int i = 0; i < N; i++)
        {
            Assert.That(set.TryAdd(i), Is.True);
        }
        Assert.That(set.Count, Is.EqualTo(N));

        // Verify
        for (int i = 0; i < N; i++)
        {
            Assert.That(set.Contains(i), Is.True);
        }

        // Remove half
        for (int i = 0; i < N; i += 2)
        {
            Assert.That(set.TryRemove(i), Is.True);
        }
        Assert.That(set.Count, Is.EqualTo(N / 2));

        // Verify remaining
        for (int i = 0; i < N; i++)
        {
            Assert.That(set.Contains(i), Is.EqualTo(i % 2 != 0));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HashMap<TKey, TValue> — Map variant (unmanaged TValue)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Map_TryAdd_NewKV_ReturnsTrue()
    {
        using var map = CreateMap();
        Assert.That(map.TryAdd(1, 100), Is.True);
        Assert.That(map.Count, Is.EqualTo(1));
    }

    [Test]
    public void Map_TryAdd_DuplicateKey_ReturnsFalse()
    {
        using var map = CreateMap();
        map.TryAdd(1, 100);
        Assert.That(map.TryAdd(1, 200), Is.False);
        Assert.That(map.Count, Is.EqualTo(1));

        // Original value preserved
        Assert.That(map.TryGetValue(1, out int val), Is.True);
        Assert.That(val, Is.EqualTo(100));
    }

    [Test]
    public void Map_TryGetValue_ExistingKey_ReturnsValue()
    {
        using var map = CreateMap();
        map.TryAdd(42, 999);
        Assert.That(map.TryGetValue(42, out int val), Is.True);
        Assert.That(val, Is.EqualTo(999));
    }

    [Test]
    public void Map_TryGetValue_NonExistingKey_ReturnsFalse()
    {
        using var map = CreateMap();
        Assert.That(map.TryGetValue(42, out int val), Is.False);
        Assert.That(val, Is.EqualTo(default(int)));
    }

    [Test]
    public void Map_TryRemove_ExistingKey_ReturnsValueAndTrue()
    {
        using var map = CreateMap();
        map.TryAdd(42, 999);
        Assert.That(map.TryRemove(42, out int val), Is.True);
        Assert.That(val, Is.EqualTo(999));
        Assert.That(map.Count, Is.EqualTo(0));
    }

    [Test]
    public void Map_TryRemove_NonExistingKey_ReturnsFalse()
    {
        using var map = CreateMap();
        Assert.That(map.TryRemove(42, out _), Is.False);
    }

    [Test]
    public void Map_Indexer_Get_ExistingKey_ReturnsValue()
    {
        using var map = CreateMap();
        map.TryAdd(42, 999);
        Assert.That(map[42], Is.EqualTo(999));
    }

    [Test]
    public void Map_Indexer_Get_NonExistingKey_ThrowsKeyNotFound()
    {
        using var map = CreateMap();
        Assert.Throws<KeyNotFoundException>(() => { var _ = map[42]; });
    }

    [Test]
    public void Map_Indexer_Set_NewKey_Adds()
    {
        using var map = CreateMap();
        map[42] = 999;
        Assert.That(map.Count, Is.EqualTo(1));
        Assert.That(map[42], Is.EqualTo(999));
    }

    [Test]
    public void Map_Indexer_Set_ExistingKey_Updates()
    {
        using var map = CreateMap();
        map[42] = 999;
        map[42] = 1000;
        Assert.That(map.Count, Is.EqualTo(1));
        Assert.That(map[42], Is.EqualTo(1000));
    }

    [Test]
    public void Map_GetOrAdd_NewKey_AddsAndReturnsValue()
    {
        using var map = CreateMap();
        int result = map.GetOrAdd(42, 999);
        Assert.That(result, Is.EqualTo(999));
        Assert.That(map.Count, Is.EqualTo(1));
        Assert.That(map[42], Is.EqualTo(999));
    }

    [Test]
    public void Map_GetOrAdd_ExistingKey_ReturnsExisting()
    {
        using var map = CreateMap();
        map.TryAdd(42, 999);
        int result = map.GetOrAdd(42, 1000);
        Assert.That(result, Is.EqualTo(999));
        Assert.That(map.Count, Is.EqualTo(1));
    }

    [Test]
    public void Map_Count_ReflectsOperations()
    {
        using var map = CreateMap();
        for (int i = 0; i < 10; i++)
        {
            map.TryAdd(i, i * 10);
        }
        Assert.That(map.Count, Is.EqualTo(10));

        map.TryRemove(5, out _);
        Assert.That(map.Count, Is.EqualTo(9));
    }

    [Test]
    public void Map_Clear_ResetsToEmpty()
    {
        using var map = CreateMap();
        for (int i = 0; i < 100; i++)
        {
            map.TryAdd(i, i * 10);
        }

        map.Clear();
        Assert.That(map.Count, Is.EqualTo(0));

        for (int i = 0; i < 100; i++)
        {
            Assert.That(map.TryGetValue(i, out _), Is.False);
        }

        // Can add again
        map.TryAdd(1, 100);
        Assert.That(map[1], Is.EqualTo(100));
    }

    [Test]
    public void Map_Split_PreservesAllEntries()
    {
        using var map = CreateMap(8);
        int n = 200;

        for (int i = 0; i < n; i++)
        {
            map.TryAdd(i, i * 10);
        }

        Assert.That(map.Capacity, Is.GreaterThan(8));
        Assert.That(map.Count, Is.EqualTo(n));

        for (int i = 0; i < n; i++)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True, $"Missing key {i}");
            Assert.That(val, Is.EqualTo(i * 10), $"Wrong value for key {i}");
        }
    }

    [Test]
    public void Map_SwapWithLast_OnRemove_PreservesOtherEntries()
    {
        using var map = CreateMap(4);
        // Add enough to fill a slot
        int n = 50;
        for (int i = 0; i < n; i++)
        {
            map.TryAdd(i, i * 100);
        }

        // Remove from middle
        map.TryRemove(0, out _);
        map.TryRemove(2, out _);

        // Verify remaining
        for (int i = 0; i < n; i++)
        {
            if (i == 0 || i == 2)
            {
                Assert.That(map.TryGetValue(i, out _), Is.False);
            }
            else
            {
                Assert.That(map.TryGetValue(i, out int val), Is.True);
                Assert.That(val, Is.EqualTo(i * 100));
            }
        }
    }

    [Test]
    public void Map_Enumerator_ReturnsAllPairs()
    {
        using var map = CreateMap(8);
        var expected = new Dictionary<int, int>();
        for (int i = 0; i < 100; i++)
        {
            map.TryAdd(i, i * 10);
            expected[i] = i * 10;
        }

        var actual = new Dictionary<int, int>();
        foreach (var (key, value) in map)
        {
            actual[key] = value;
        }

        Assert.That(actual.Count, Is.EqualTo(expected.Count));
        foreach (var kvp in expected)
        {
            Assert.That(actual[kvp.Key], Is.EqualTo(kvp.Value));
        }
    }

    [Test]
    public void Map_KeyZero_DistinguishableFromMiss()
    {
        using var map = CreateMap();
        // Key 0 with value 0 should be distinguishable from not-found (default 0)
        Assert.That(map.TryGetValue(0, out _), Is.False);

        map.TryAdd(0, 0);
        Assert.That(map.TryGetValue(0, out int val), Is.True);
        Assert.That(val, Is.EqualTo(0));
    }

    [Test]
    public void Map_Stress_10K_Pairs()
    {
        using var map = CreateMap(16);
        const int N = 10_000;

        for (int i = 0; i < N; i++)
        {
            Assert.That(map.TryAdd(i, i * 7), Is.True);
        }
        Assert.That(map.Count, Is.EqualTo(N));

        for (int i = 0; i < N; i++)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True);
            Assert.That(val, Is.EqualTo(i * 7));
        }

        // Remove evens
        for (int i = 0; i < N; i += 2)
        {
            Assert.That(map.TryRemove(i, out int val), Is.True);
            Assert.That(val, Is.EqualTo(i * 7));
        }
        Assert.That(map.Count, Is.EqualTo(N / 2));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HashMap<TKey, TValue> — TryUpdate
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Map_TryUpdate_ExistingKey_UpdatesValue()
    {
        using var map = CreateMap();
        map.TryAdd(42, 100);
        Assert.That(map.TryUpdate(42, 200), Is.True);
        Assert.That(map[42], Is.EqualTo(200));
        Assert.That(map.Count, Is.EqualTo(1));
    }

    [Test]
    public void Map_TryUpdate_MissingKey_ReturnsFalse()
    {
        using var map = CreateMap();
        Assert.That(map.TryUpdate(42, 200), Is.False);
        Assert.That(map.Count, Is.EqualTo(0));
    }

    [Test]
    public void Map_TryUpdateCAS_MatchingComparison_Updates()
    {
        using var map = CreateMap();
        map.TryAdd(42, 100);
        Assert.That(map.TryUpdate(42, 200, 100), Is.True);
        Assert.That(map[42], Is.EqualTo(200));
    }

    [Test]
    public void Map_TryUpdateCAS_MismatchComparison_ReturnsFalse()
    {
        using var map = CreateMap();
        map.TryAdd(42, 100);
        Assert.That(map.TryUpdate(42, 200, 999), Is.False);
        Assert.That(map[42], Is.EqualTo(100)); // unchanged
    }

    [Test]
    public void Map_TryUpdateCAS_MissingKey_ReturnsFalse()
    {
        using var map = CreateMap();
        Assert.That(map.TryUpdate(42, 200, 100), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HashMap<TKey, TValue> — Map variant (managed TValue)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ManagedMap_TryAdd_StringValue_Works()
    {
        using var map = CreateManagedMap();
        Assert.That(map.TryAdd(1, "hello"), Is.True);
        Assert.That(map.TryGetValue(1, out string val), Is.True);
        Assert.That(val, Is.EqualTo("hello"));
    }

    [Test]
    public void ManagedMap_TryGetValue_StringValue_Works()
    {
        using var map = CreateManagedMap();
        map.TryAdd(42, "world");
        Assert.That(map.TryGetValue(42, out string val), Is.True);
        Assert.That(val, Is.EqualTo("world"));
    }

    [Test]
    public void ManagedMap_TryRemove_StringValue_ClearsReference()
    {
        using var map = CreateManagedMap();
        map.TryAdd(1, "hello");
        Assert.That(map.TryRemove(1, out string val), Is.True);
        Assert.That(val, Is.EqualTo("hello"));
        Assert.That(map.Count, Is.EqualTo(0));
    }

    [Test]
    public void ManagedMap_Split_PreservesStringValues()
    {
        using var map = CreateManagedMap(8);
        int n = 200;

        for (int i = 0; i < n; i++)
        {
            map.TryAdd(i, $"val_{i}");
        }

        Assert.That(map.Capacity, Is.GreaterThan(8));
        Assert.That(map.Count, Is.EqualTo(n));

        for (int i = 0; i < n; i++)
        {
            Assert.That(map.TryGetValue(i, out string val), Is.True, $"Missing key {i}");
            Assert.That(val, Is.EqualTo($"val_{i}"), $"Wrong value for key {i}");
        }
    }

    [Test]
    public void ManagedMap_Enumerator_StringValues()
    {
        using var map = CreateManagedMap(8);
        var expected = new Dictionary<int, string>();
        for (int i = 0; i < 50; i++)
        {
            map.TryAdd(i, $"item_{i}");
            expected[i] = $"item_{i}";
        }

        var actual = new Dictionary<int, string>();
        foreach (var (key, value) in map)
        {
            actual[key] = value;
        }

        Assert.That(actual.Count, Is.EqualTo(expected.Count));
        foreach (var kvp in expected)
        {
            Assert.That(actual[kvp.Key], Is.EqualTo(kvp.Value));
        }
    }

    [Test]
    public void ManagedMap_Clear_ClearsReferences()
    {
        using var map = CreateManagedMap();
        for (int i = 0; i < 50; i++)
        {
            map.TryAdd(i, $"val_{i}");
        }

        map.Clear();
        Assert.That(map.Count, Is.EqualTo(0));

        // Verify keys are gone
        for (int i = 0; i < 50; i++)
        {
            Assert.That(map.TryGetValue(i, out _), Is.False);
        }

        // Can add again
        map.TryAdd(1, "new");
        Assert.That(map[1], Is.EqualTo("new"));
    }

    [Test]
    public void ManagedMap_Indexer_StringValue_Works()
    {
        using var map = CreateManagedMap();
        map[1] = "hello";
        Assert.That(map[1], Is.EqualTo("hello"));

        map[1] = "updated";
        Assert.That(map[1], Is.EqualTo("updated"));
        Assert.That(map.Count, Is.EqualTo(1));
    }

    [Test]
    public void ManagedMap_Stress_StringValues()
    {
        using var map = CreateManagedMap(16);
        const int N = 5_000;

        for (int i = 0; i < N; i++)
        {
            map.TryAdd(i, $"s{i}");
        }
        Assert.That(map.Count, Is.EqualTo(N));

        for (int i = 0; i < N; i++)
        {
            Assert.That(map.TryGetValue(i, out string val), Is.True);
            Assert.That(val, Is.EqualTo($"s{i}"));
        }

        // Remove half
        for (int i = 0; i < N; i += 2)
        {
            Assert.That(map.TryRemove(i, out _), Is.True);
        }
        Assert.That(map.Count, Is.EqualTo(N / 2));

        // Verify odd keys still present
        for (int i = 1; i < N; i += 2)
        {
            Assert.That(map.TryGetValue(i, out string val), Is.True);
            Assert.That(val, Is.EqualTo($"s{i}"));
        }
    }

    [Test]
    public void ManagedMap_TryUpdate_StringValue()
    {
        using var map = CreateManagedMap();
        map.TryAdd(1, "old");
        Assert.That(map.TryUpdate(1, "new"), Is.True);
        Assert.That(map[1], Is.EqualTo("new"));
    }

    [Test]
    public void ManagedMap_TryUpdateCAS_StringValue()
    {
        using var map = CreateManagedMap();
        map.TryAdd(1, "old");
        Assert.That(map.TryUpdate(1, "new", "old"), Is.True);
        Assert.That(map[1], Is.EqualTo("new"));

        // Mismatch
        Assert.That(map.TryUpdate(1, "newer", "wrong"), Is.False);
        Assert.That(map[1], Is.EqualTo("new")); // unchanged
    }
}
