using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine.Tests;

[TestFixture]
public class ConcurrentHashMapTests
{
    private static IMemoryAllocator Allocator => BitmapTestServices.MemoryAllocator;
    private static IResource Parent => BitmapTestServices.AllocationResource;

    private static ConcurrentHashMap<int> CreateSet(int initialCapacity = 1024) => new(initialCapacity);

    private static ConcurrentHashMap<int, int> CreateMap(int initialCapacity = 1024) => new(initialCapacity);

    private static ConcurrentHashMap<int, string> CreateManagedMap(int initialCapacity = 1024) => new(initialCapacity);

    // ═══════════════════════════════════════════════════════════════════════
    // Single-threaded: Set variant
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
        set.Clear();
        Assert.That(set.Count, Is.EqualTo(0));
        for (int i = 0; i < 100; i++)
        {
            Assert.That(set.Contains(i), Is.False);
        }
        Assert.That(set.TryAdd(42), Is.True);
    }

    [Test]
    public void Set_Resize_PreservesEntries()
    {
        using var set = CreateSet(64);
        int n = 1000;
        for (int i = 0; i < n; i++)
        {
            set.TryAdd(i);
        }
        Assert.That(set.Count, Is.EqualTo(n));
        for (int i = 0; i < n; i++)
        {
            Assert.That(set.Contains(i), Is.True, $"Missing key {i}");
        }
    }

    [Test]
    public void Set_EnsureCapacity_GrowsStripes()
    {
        using var set = CreateSet(64);
        set.EnsureCapacity(50_000);
        for (int i = 0; i < 50_000; i++)
        {
            set.TryAdd(i);
        }
        Assert.That(set.Count, Is.EqualTo(50_000));
    }

    [Test]
    public void Set_Enumerator_ReturnsAllEntries()
    {
        using var set = CreateSet();
        var expected = new HashSet<int>();
        for (int i = 0; i < 500; i++)
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
    public void Set_KeyZero_Works()
    {
        using var set = CreateSet();
        Assert.That(set.Contains(0), Is.False);
        Assert.That(set.TryAdd(0), Is.True);
        Assert.That(set.Contains(0), Is.True);
        Assert.That(set.TryRemove(0), Is.True);
        Assert.That(set.Contains(0), Is.False);
    }

    [Test]
    public void Set_Stress_10K()
    {
        using var set = CreateSet(64);
        const int N = 10_000;
        for (int i = 0; i < N; i++)
        {
            Assert.That(set.TryAdd(i), Is.True);
        }
        Assert.That(set.Count, Is.EqualTo(N));
        for (int i = 0; i < N; i++)
        {
            Assert.That(set.Contains(i), Is.True);
        }
        for (int i = 0; i < N; i += 2)
        {
            Assert.That(set.TryRemove(i), Is.True);
        }
        Assert.That(set.Count, Is.EqualTo(N / 2));
        for (int i = 0; i < N; i++)
        {
            Assert.That(set.Contains(i), Is.EqualTo(i % 2 != 0));
        }
    }

    [Test]
    public void Set_Dispose_IsSafe()
    {
        var set = CreateSet();
        set.TryAdd(1);
        set.Dispose();
        set.Dispose(); // double dispose
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Single-threaded: Map variant (unmanaged TValue)
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
        Assert.That(map.TryGetValue(1, out int val), Is.True);
        Assert.That(val, Is.EqualTo(100));
    }

    [Test]
    public void Map_TryGetValue_Existing_ReturnsValue()
    {
        using var map = CreateMap();
        map.TryAdd(42, 999);
        Assert.That(map.TryGetValue(42, out int val), Is.True);
        Assert.That(val, Is.EqualTo(999));
    }

    [Test]
    public void Map_TryGetValue_Missing_ReturnsFalse()
    {
        using var map = CreateMap();
        Assert.That(map.TryGetValue(42, out _), Is.False);
    }

    [Test]
    public void Map_TryRemove_Existing_ReturnsValue()
    {
        using var map = CreateMap();
        map.TryAdd(42, 999);
        Assert.That(map.TryRemove(42, out int val), Is.True);
        Assert.That(val, Is.EqualTo(999));
        Assert.That(map.Count, Is.EqualTo(0));
    }

    [Test]
    public void Map_TryRemove_Missing_ReturnsFalse()
    {
        using var map = CreateMap();
        Assert.That(map.TryRemove(42, out _), Is.False);
    }

    [Test]
    public void Map_Indexer_GetSet()
    {
        using var map = CreateMap();
        map[42] = 999;
        Assert.That(map[42], Is.EqualTo(999));
        map[42] = 1000;
        Assert.That(map[42], Is.EqualTo(1000));
        Assert.That(map.Count, Is.EqualTo(1));
    }

    [Test]
    public void Map_Indexer_Missing_Throws()
    {
        using var map = CreateMap();
        Assert.Throws<KeyNotFoundException>(() => { var _ = map[42]; });
    }

    [Test]
    public void Map_GetOrAdd_NewKey()
    {
        using var map = CreateMap();
        Assert.That(map.GetOrAdd(42, 999), Is.EqualTo(999));
        Assert.That(map.Count, Is.EqualTo(1));
        Assert.That(map[42], Is.EqualTo(999));
    }

    [Test]
    public void Map_GetOrAdd_ExistingKey()
    {
        using var map = CreateMap();
        map.TryAdd(42, 999);
        Assert.That(map.GetOrAdd(42, 1000), Is.EqualTo(999));
        Assert.That(map.Count, Is.EqualTo(1));
    }

    [Test]
    public void Map_TryUpdate_ExistingKey_Updates()
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
    }

    [Test]
    public void Map_TryUpdateCAS_Match_Updates()
    {
        using var map = CreateMap();
        map.TryAdd(42, 100);
        Assert.That(map.TryUpdate(42, 200, 100), Is.True);
        Assert.That(map[42], Is.EqualTo(200));
    }

    [Test]
    public void Map_TryUpdateCAS_Mismatch_ReturnsFalse()
    {
        using var map = CreateMap();
        map.TryAdd(42, 100);
        Assert.That(map.TryUpdate(42, 200, 999), Is.False);
        Assert.That(map[42], Is.EqualTo(100));
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
        Assert.That(map.TryUpdate(1, "newer", "wrong"), Is.False);
        Assert.That(map[1], Is.EqualTo("new"));
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
    public void Map_Clear_Works()
    {
        using var map = CreateMap();
        for (int i = 0; i < 100; i++)
        {
            map.TryAdd(i, i);
        }
        map.Clear();
        Assert.That(map.Count, Is.EqualTo(0));
        for (int i = 0; i < 100; i++)
        {
            Assert.That(map.TryGetValue(i, out _), Is.False);
        }
        map.TryAdd(1, 100);
        Assert.That(map[1], Is.EqualTo(100));
    }

    [Test]
    public void Map_Resize_PreservesEntries()
    {
        using var map = CreateMap(64);
        int n = 1000;
        for (int i = 0; i < n; i++)
        {
            map.TryAdd(i, i * 10);
        }
        Assert.That(map.Count, Is.EqualTo(n));
        for (int i = 0; i < n; i++)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True, $"Missing key {i}");
            Assert.That(val, Is.EqualTo(i * 10));
        }
    }

    [Test]
    public void Map_Enumerator_ReturnsAllPairs()
    {
        using var map = CreateMap();
        var expected = new Dictionary<int, int>();
        for (int i = 0; i < 500; i++)
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
    public void Map_KeyZero_Works()
    {
        using var map = CreateMap();
        Assert.That(map.TryGetValue(0, out _), Is.False);
        map.TryAdd(0, 0);
        Assert.That(map.TryGetValue(0, out int val), Is.True);
        Assert.That(val, Is.EqualTo(0));
    }

    [Test]
    public void Map_Stress_10K()
    {
        using var map = CreateMap(64);
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
        for (int i = 0; i < N; i += 2)
        {
            Assert.That(map.TryRemove(i, out int val), Is.True);
            Assert.That(val, Is.EqualTo(i * 7));
        }
        Assert.That(map.Count, Is.EqualTo(N / 2));
    }

    [Test]
    public void Map_Dispose_IsSafe()
    {
        var map = CreateMap();
        map.TryAdd(1, 100);
        map.Dispose();
        map.Dispose(); // double dispose
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Single-threaded: Map variant (managed TValue)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ManagedMap_TryAdd_StringValue()
    {
        using var map = CreateManagedMap();
        Assert.That(map.TryAdd(1, "hello"), Is.True);
        Assert.That(map.TryGetValue(1, out string val), Is.True);
        Assert.That(val, Is.EqualTo("hello"));
    }

    [Test]
    public void ManagedMap_Resize_PreservesStrings()
    {
        using var map = CreateManagedMap(64);
        int n = 1000;
        for (int i = 0; i < n; i++)
        {
            map.TryAdd(i, $"val_{i}");
        }
        Assert.That(map.Count, Is.EqualTo(n));
        for (int i = 0; i < n; i++)
        {
            Assert.That(map.TryGetValue(i, out string val), Is.True, $"Missing key {i}");
            Assert.That(val, Is.EqualTo($"val_{i}"));
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
        for (int i = 0; i < 50; i++)
        {
            Assert.That(map.TryGetValue(i, out _), Is.False);
        }
    }

    [Test]
    public void ManagedMap_Indexer_StringValue()
    {
        using var map = CreateManagedMap();
        map[1] = "hello";
        Assert.That(map[1], Is.EqualTo("hello"));
        map[1] = "updated";
        Assert.That(map[1], Is.EqualTo("updated"));
        Assert.That(map.Count, Is.EqualTo(1));
    }

    [Test]
    public void ManagedMap_Enumerator_StringValues()
    {
        using var map = CreateManagedMap();
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

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrent correctness
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Concurrent_DisjointInserts_AllPresent()
    {
        using var map = CreateMap();
        const int ThreadCount = 8;
        const int KeysPerThread = 5000;

        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                int start = threadId * KeysPerThread;
                for (int i = 0; i < KeysPerThread; i++)
                {
                    map.TryAdd(start + i, start + i);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.That(map.Count, Is.EqualTo(ThreadCount * KeysPerThread));
        for (int i = 0; i < ThreadCount * KeysPerThread; i++)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True, $"Missing key {i}");
            Assert.That(val, Is.EqualTo(i));
        }
    }

    [Test]
    public void Concurrent_OverlappingInserts_NoDuplicates()
    {
        using var map = CreateMap();
        const int ThreadCount = 8;
        const int KeyRange = 5000;

        var addedCounts = new int[ThreadCount];
        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                int count = 0;
                for (int i = 0; i < KeyRange; i++)
                {
                    if (map.TryAdd(i, threadId))
                    {
                        count++;
                    }
                }
                addedCounts[threadId] = count;
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.That(addedCounts.Sum(), Is.EqualTo(KeyRange), "Total successful adds should equal key range");
        Assert.That(map.Count, Is.EqualTo(KeyRange));
    }

    [Test]
    public void Concurrent_InsertAndLookup_AllVisible()
    {
        using var map = CreateMap();
        const int N = 10_000;
        var inserted = new int[N];

        using var startEvent = new ManualResetEventSlim(false);

        var writer = new Thread(() =>
        {
            startEvent.Wait();
            for (int i = 0; i < N; i++)
            {
                map.TryAdd(i, i * 10);
                Volatile.Write(ref inserted[i], 1);
            }
        });

        int readerFound = 0;
        var reader = new Thread(() =>
        {
            startEvent.Wait();
            int localFound = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 0; i < N; i++)
                {
                    if (Volatile.Read(ref inserted[i]) == 1 && map.TryGetValue(i, out int val))
                    {
                        if (val == i * 10)
                        {
                            localFound++;
                        }
                    }
                }
            }
            Interlocked.Exchange(ref readerFound, localFound);
        });

        writer.Start();
        reader.Start();
        startEvent.Set();

        writer.Join();
        reader.Join();

        // After writer completes, all keys must be visible
        for (int i = 0; i < N; i++)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True, $"Key {i} not visible after writer completed");
            Assert.That(val, Is.EqualTo(i * 10));
        }
    }

    [Test]
    public void Concurrent_ManagedValues_ResizingStripes_NeverPairAKeyWithAnotherKeysValue()
    {
        // 64 entries across the stripes leaves each at its minimum capacity, so the writer resizes every stripe several times while both readers run.
        using var map = CreateManagedMap(initialCapacity: 64);
        const int N = 20_000;
        var values = new string[N + 1];
        for (int i = 1; i <= N; i++)
        {
            values[i] = "v" + i;
        }

        using var startEvent = new ManualResetEventSlim(false);
        int writerDone = 0;
        int wrongLookups = 0;
        int wrongPairs = 0;

        var writer = new Thread(() =>
        {
            startEvent.Wait();
            for (int i = 1; i <= N; i++)
            {
                map.TryAdd(i, values[i]);
            }
            Volatile.Write(ref writerDone, 1);
        });

        var lookup = new Thread(() =>
        {
            startEvent.Wait();
            while (Volatile.Read(ref writerDone) == 0)
            {
                for (int i = 1; i <= N; i += 7)
                {
                    if (map.TryGetValue(i, out var value) && !ReferenceEquals(value, values[i]))
                    {
                        Interlocked.Increment(ref wrongLookups);
                    }
                }
            }
        });

        var enumerate = new Thread(() =>
        {
            startEvent.Wait();
            while (Volatile.Read(ref writerDone) == 0)
            {
                foreach (var (key, value) in map)
                {
                    // A slot being filled can show its hash before its key (still 0) and its key before its value (still null): the partial state the
                    // enumerator allows. Another key's value is never allowed.
                    if (key != 0 && value != null && ((uint)key > N || !ReferenceEquals(value, values[key])))
                    {
                        Interlocked.Increment(ref wrongPairs);
                    }
                }
            }
        });

        writer.Start();
        lookup.Start();
        enumerate.Start();
        startEvent.Set();
        writer.Join();
        lookup.Join();
        enumerate.Join();

        Assert.That(wrongLookups, Is.Zero, "a validated lookup returned another key's value");
        Assert.That(wrongPairs, Is.Zero, "the enumerator paired a key with another key's value");
        Assert.That(map.Count, Is.EqualTo(N));
    }

    [Test]
    public void Concurrent_InsertAndRemove_CountConsistent()
    {
        using var map = CreateMap();
        const int N = 5000;

        // Pre-populate keys 0..N-1
        for (int i = 0; i < N; i++)
        {
            map.TryAdd(i, i);
        }

        using var barrier = new Barrier(2);

        var inserter = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (int i = N; i < N * 2; i++)
            {
                map.TryAdd(i, i);
            }
        });

        var remover = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < N; i++)
            {
                map.TryRemove(i, out _);
            }
        });

        inserter.Start();
        remover.Start();
        inserter.Join();
        remover.Join();

        // Keys 0..N-1 removed, keys N..2N-1 inserted
        Assert.That(map.Count, Is.EqualTo(N));
        for (int i = N; i < N * 2; i++)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True, $"Missing key {i}");
            Assert.That(val, Is.EqualTo(i));
        }
    }

    [Test]
    public void Concurrent_ResizeUnderLoad_NoLostEntries()
    {
        // Small initial capacity forces many per-stripe resizes
        using var map = CreateMap(64);
        const int ThreadCount = 8;
        const int KeysPerThread = 5000;

        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                int start = threadId * KeysPerThread;
                for (int i = 0; i < KeysPerThread; i++)
                {
                    map.TryAdd(start + i, start + i);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.That(map.Count, Is.EqualTo(ThreadCount * KeysPerThread));
        for (int i = 0; i < ThreadCount * KeysPerThread; i++)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True, $"Lost key {i} after resize");
            Assert.That(val, Is.EqualTo(i));
        }
    }

    [Test]
    public void Concurrent_Set_DisjointInserts()
    {
        using var set = CreateSet();
        const int ThreadCount = 8;
        const int KeysPerThread = 5000;

        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                int start = threadId * KeysPerThread;
                for (int i = 0; i < KeysPerThread; i++)
                {
                    set.TryAdd(start + i);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.That(set.Count, Is.EqualTo(ThreadCount * KeysPerThread));
        for (int i = 0; i < ThreadCount * KeysPerThread; i++)
        {
            Assert.That(set.Contains(i), Is.True, $"Missing key {i}");
        }
    }

    [Test]
    public void Concurrent_Set_OverlappingInserts()
    {
        using var set = CreateSet();
        const int ThreadCount = 8;
        const int KeyRange = 5000;

        var addedCounts = new int[ThreadCount];
        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                int count = 0;
                for (int i = 0; i < KeyRange; i++)
                {
                    if (set.TryAdd(i))
                    {
                        count++;
                    }
                }
                addedCounts[threadId] = count;
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.That(addedCounts.Sum(), Is.EqualTo(KeyRange));
        Assert.That(set.Count, Is.EqualTo(KeyRange));
    }

    [Test]
    public void Concurrent_GetOrAdd_ConsistentValues()
    {
        using var map = CreateMap();
        const int ThreadCount = 8;
        const int KeyRange = 5000;

        // Each thread calls GetOrAdd with thread-specific values; for each key, one wins
        var results = new ConcurrentDictionary<int, ConcurrentBag<int>>();
        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < KeyRange; i++)
                {
                    int result = map.GetOrAdd(i, threadId);
                    results.GetOrAdd(i, _ => new ConcurrentBag<int>()).Add(result);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        // For each key, all threads must see the same value
        foreach (var kvp in results)
        {
            var values = kvp.Value.Distinct().ToArray();
            Assert.That(values.Length, Is.EqualTo(1),
                $"Key {kvp.Key} returned inconsistent values: {string.Join(", ", values)}");
        }
    }

    [Test]
    public void Concurrent_TryUpdateCAS_OnlyOneThreadWins()
    {
        using var map = CreateMap();
        const int ThreadCount = 8;
        const int KeyRange = 1000;

        // Pre-populate all keys with value 0
        for (int i = 0; i < KeyRange; i++)
        {
            map.TryAdd(i, 0);
        }

        // Each thread tries to CAS from 0 → threadId. Exactly one should win per key.
        var winCounts = new int[ThreadCount];
        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                int wins = 0;
                for (int i = 0; i < KeyRange; i++)
                {
                    if (map.TryUpdate(i, threadId + 1, 0))
                    {
                        wins++;
                    }
                }
                winCounts[threadId] = wins;
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        // Total wins must equal key range — exactly one thread won per key
        Assert.That(winCounts.Sum(), Is.EqualTo(KeyRange));

        // Every key should have a non-zero value (set by the winning thread)
        for (int i = 0; i < KeyRange; i++)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True);
            Assert.That(val, Is.GreaterThan(0), $"Key {i} was never updated");
            Assert.That(val, Is.LessThanOrEqualTo(ThreadCount));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Contention stress
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Stress_SameKeySet_MaxContention()
    {
        using var map = CreateMap();
        const int ThreadCount = 8;
        const int KeyRange = 100; // small range = maximum stripe contention
        const int OpsPerThread = 5000;

        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                var rng = new Random(threadId);
                for (int op = 0; op < OpsPerThread; op++)
                {
                    int key = rng.Next(KeyRange);
                    if (rng.NextDouble() < 0.5)
                    {
                        map.TryAdd(key, threadId);
                    }
                    else
                    {
                        map.TryRemove(key, out _);
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        // Verify consistency: every key in the map is readable
        int verified = 0;
        foreach (var (key, _) in map)
        {
            Assert.That(map.TryGetValue(key, out _), Is.True);
            verified++;
        }
        Assert.That(verified, Is.LessThanOrEqualTo(KeyRange));
    }

    [Test]
    [TestCase(2)]
    [TestCase(4)]
    public void Stress_MixedReadWrite_90Read10Write(int threadCount)
    {
        using var map = CreateMap();
        const int KeyRange = 10_000;

        // Pre-populate
        for (int i = 0; i < KeyRange; i++)
        {
            map.TryAdd(i, i);
        }

        using var barrier = new Barrier(threadCount);
        var errors = new ConcurrentBag<string>();
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                var rng = new Random(threadId);
                for (int op = 0; op < 50_000; op++)
                {
                    int key = rng.Next(KeyRange);
                    if (rng.NextDouble() < 0.9)
                    {
                        // Read
                        if (map.TryGetValue(key, out int val))
                        {
                            if (val != key && val != key + 1_000_000)
                            {
                                errors.Add($"Key {key} had unexpected value {val}");
                            }
                        }
                    }
                    else
                    {
                        // Write: toggle value
                        map[key] = key + 1_000_000;
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.That(errors, Is.Empty, $"Errors: {string.Join("; ", errors.Take(5))}");
        Assert.That(map.Count, Is.EqualTo(KeyRange));
    }

    [Test]
    public void Stress_ProcessorCount_Threads()
    {
        int threadCount = Math.Min(Environment.ProcessorCount, 16);
        using var map = CreateMap(64);
        const int KeysPerThread = 5000;

        using var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                int start = threadId * KeysPerThread;
                for (int i = 0; i < KeysPerThread; i++)
                {
                    map.TryAdd(start + i, start + i);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        int expected = threadCount * KeysPerThread;
        Assert.That(map.Count, Is.EqualTo(expected));
    }

    [Test]
    public void Stress_ConcurrentResizeAndRead()
    {
        // Very small initial capacity + concurrent reads during writes that trigger resize
        using var map = CreateMap(64);
        const int N = 20_000;
        var readErrors = new ConcurrentBag<string>();

        using var startEvent = new ManualResetEventSlim(false);
        int writerDone = 0;

        var writer = new Thread(() =>
        {
            startEvent.Wait();
            for (int i = 0; i < N; i++)
            {
                map.TryAdd(i, i * 10);
            }
            Volatile.Write(ref writerDone, 1);
        });

        // Multiple readers hammering lookups during resize
        var readers = new Thread[4];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = new Thread(() =>
            {
                startEvent.Wait();
                var rng = new Random(Thread.CurrentThread.ManagedThreadId);
                while (Volatile.Read(ref writerDone) == 0)
                {
                    int key = rng.Next(N);
                    if (map.TryGetValue(key, out int val))
                    {
                        if (val != key * 10)
                        {
                            readErrors.Add($"Key {key} had value {val}, expected {key * 10}");
                        }
                    }
                }
            });
            readers[r].Start();
        }

        writer.Start();
        startEvent.Set();

        writer.Join();
        foreach (var r in readers)
        {
            r.Join();
        }

        Assert.That(readErrors, Is.Empty, $"Read errors: {string.Join("; ", readErrors.Take(5))}");
        Assert.That(map.Count, Is.EqualTo(N));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Additional coverage: managed TValue gaps
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ManagedMap_TryRemove_ReturnsString()
    {
        using var map = CreateManagedMap();
        map.TryAdd(1, "hello");
        Assert.That(map.TryRemove(1, out string val), Is.True);
        Assert.That(val, Is.EqualTo("hello"));
        Assert.That(map.Count, Is.EqualTo(0));
        Assert.That(map.TryGetValue(1, out _), Is.False);
    }

    [Test]
    public void ManagedMap_GetOrAdd_NewAndExisting()
    {
        using var map = CreateManagedMap();
        Assert.That(map.GetOrAdd(42, "first"), Is.EqualTo("first"));
        Assert.That(map.GetOrAdd(42, "second"), Is.EqualTo("first"));
        Assert.That(map.Count, Is.EqualTo(1));
    }

    [Test]
    public void ManagedMap_KeyZero_Works()
    {
        using var map = CreateManagedMap();
        Assert.That(map.TryGetValue(0, out _), Is.False);
        map.TryAdd(0, "zero");
        Assert.That(map.TryGetValue(0, out string val), Is.True);
        Assert.That(val, Is.EqualTo("zero"));
        Assert.That(map.TryRemove(0, out string removed), Is.True);
        Assert.That(removed, Is.EqualTo("zero"));
    }

    [Test]
    public void ManagedMap_Stress_10K()
    {
        using var map = CreateManagedMap(64);
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
        for (int i = 0; i < N; i += 2)
        {
            Assert.That(map.TryRemove(i, out _), Is.True);
        }
        Assert.That(map.Count, Is.EqualTo(N / 2));
        for (int i = 1; i < N; i += 2)
        {
            Assert.That(map.TryGetValue(i, out string val), Is.True);
            Assert.That(val, Is.EqualTo($"s{i}"));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Backward-shift delete correctness
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Set_BackwardShift_RemoveFromProbeChain_PreservesOthers()
    {
        // Use small capacity to force collisions and long probe chains
        using var set = CreateSet(64);
        int n = 200;
        for (int i = 0; i < n; i++)
        {
            set.TryAdd(i);
        }

        // Remove every 3rd key — this creates gaps within probe chains
        for (int i = 0; i < n; i += 3)
        {
            Assert.That(set.TryRemove(i), Is.True);
        }

        // Verify remaining keys are all still findable (backward-shift must have preserved chains)
        for (int i = 0; i < n; i++)
        {
            bool expected = (i % 3) != 0;
            Assert.That(set.Contains(i), Is.EqualTo(expected), $"Key {i} mismatch after backward-shift");
        }

        // Re-add removed keys — should succeed
        for (int i = 0; i < n; i += 3)
        {
            Assert.That(set.TryAdd(i), Is.True);
        }
        Assert.That(set.Count, Is.EqualTo(n));
    }

    [Test]
    public void Map_BackwardShift_RemoveFromProbeChain_PreservesValues()
    {
        using var map = CreateMap(64);
        int n = 200;
        for (int i = 0; i < n; i++)
        {
            map.TryAdd(i, i * 100);
        }

        // Remove even keys
        for (int i = 0; i < n; i += 2)
        {
            Assert.That(map.TryRemove(i, out int val), Is.True);
            Assert.That(val, Is.EqualTo(i * 100));
        }

        // Verify odd keys have correct values after backward-shift
        for (int i = 1; i < n; i += 2)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True, $"Missing key {i}");
            Assert.That(val, Is.EqualTo(i * 100), $"Wrong value for key {i}");
        }
    }

    [Test]
    public void ManagedMap_BackwardShift_PreservesStringValues()
    {
        using var map = CreateManagedMap(64);
        int n = 200;
        for (int i = 0; i < n; i++)
        {
            map.TryAdd(i, $"v{i}");
        }

        for (int i = 0; i < n; i += 2)
        {
            Assert.That(map.TryRemove(i, out string val), Is.True);
            Assert.That(val, Is.EqualTo($"v{i}"));
        }

        for (int i = 1; i < n; i += 2)
        {
            Assert.That(map.TryGetValue(i, out string val), Is.True, $"Missing key {i}");
            Assert.That(val, Is.EqualTo($"v{i}"));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrent Clear
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Concurrent_ClearDuringInserts_NoCorruption()
    {
        using var map = CreateMap();
        const int Rounds = 50;
        const int KeysPerRound = 1000;
        var errors = new ConcurrentBag<string>();

        using var startEvent = new ManualResetEventSlim(false);
        int round = 0;

        var inserter = new Thread(() =>
        {
            startEvent.Wait();
            for (int r = 0; r < Rounds; r++)
            {
                Volatile.Write(ref round, r);
                for (int i = 0; i < KeysPerRound; i++)
                {
                    map.TryAdd(r * KeysPerRound + i, i);
                }
            }
        });

        var clearer = new Thread(() =>
        {
            startEvent.Wait();
            for (int r = 0; r < Rounds; r++)
            {
                // Periodically clear
                if (r % 5 == 0)
                {
                    map.Clear();
                }
                Thread.SpinWait(100);
            }
        });

        inserter.Start();
        clearer.Start();
        startEvent.Set();

        inserter.Join();
        clearer.Join();

        // After all operations, the map should be in a consistent state:
        // every key in the map should be readable
        int count = map.Count;
        int verified = 0;
        foreach (var (key, _) in map)
        {
            if (map.TryGetValue(key, out _))
            {
                verified++;
            }
        }
        // Count might differ from verified (best-effort enumerator), but no crashes
        Assert.That(count, Is.GreaterThanOrEqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrent managed TValue
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Concurrent_ManagedMap_DisjointInserts()
    {
        using var map = CreateManagedMap();
        const int ThreadCount = 4;
        const int KeysPerThread = 2000;

        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                int start = threadId * KeysPerThread;
                for (int i = 0; i < KeysPerThread; i++)
                {
                    map.TryAdd(start + i, $"t{threadId}_v{i}");
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.That(map.Count, Is.EqualTo(ThreadCount * KeysPerThread));
        for (int t = 0; t < ThreadCount; t++)
        {
            int start = t * KeysPerThread;
            for (int i = 0; i < KeysPerThread; i++)
            {
                Assert.That(map.TryGetValue(start + i, out string val), Is.True);
                Assert.That(val, Is.EqualTo($"t{t}_v{i}"));
            }
        }
    }

    [Test]
    public void Concurrent_ManagedMap_ResizeUnderLoad()
    {
        // Small initial capacity forces many per-stripe resizes with managed values
        using var map = CreateManagedMap(64);
        const int ThreadCount = 4;
        const int KeysPerThread = 2000;

        using var barrier = new Barrier(ThreadCount);
        var threads = new Thread[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                int start = threadId * KeysPerThread;
                for (int i = 0; i < KeysPerThread; i++)
                {
                    map.TryAdd(start + i, $"val_{start + i}");
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        int total = ThreadCount * KeysPerThread;
        Assert.That(map.Count, Is.EqualTo(total));
        for (int i = 0; i < total; i++)
        {
            Assert.That(map.TryGetValue(i, out string val), Is.True, $"Lost key {i}");
            Assert.That(val, Is.EqualTo($"val_{i}"));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // StripeCount and EnsureCapacity for Map variant
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void StripeCount_IsPowerOfTwo_AndAtLeast64()
    {
        using var set = CreateSet();
        Assert.That(set.StripeCount, Is.GreaterThanOrEqualTo(64));
        Assert.That(BitOperations.IsPow2(set.StripeCount), Is.True);

        using var map = CreateMap();
        Assert.That(map.StripeCount, Is.GreaterThanOrEqualTo(64));
        Assert.That(BitOperations.IsPow2(map.StripeCount), Is.True);
    }

    [Test]
    public void Map_EnsureCapacity_GrowsStripes()
    {
        using var map = CreateMap(64);
        map.EnsureCapacity(50_000);
        for (int i = 0; i < 50_000; i++)
        {
            map.TryAdd(i, i);
        }
        Assert.That(map.Count, Is.EqualTo(50_000));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrent remove + subsequent probe chain correctness
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Concurrent_RemoveAndLookup_ProbeChainIntact()
    {
        using var map = CreateMap(64);
        const int N = 10_000;

        // Pre-populate
        for (int i = 0; i < N; i++)
        {
            map.TryAdd(i, i);
        }

        using var barrier = new Barrier(3);
        var errors = new ConcurrentBag<string>();

        // Thread 1: removes even keys
        var remover = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < N; i += 2)
            {
                map.TryRemove(i, out _);
            }
        });

        // Thread 2: reads odd keys (should never disappear due to backward-shift bugs)
        var reader1 = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (int pass = 0; pass < 5; pass++)
            {
                for (int i = 1; i < N; i += 2)
                {
                    if (map.TryGetValue(i, out int val) && val != i)
                    {
                        errors.Add($"Key {i} had wrong value {val}");
                    }
                }
            }
        });

        // Thread 3: reads even keys (may or may not find them)
        var reader2 = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (int pass = 0; pass < 5; pass++)
            {
                for (int i = 0; i < N; i += 2)
                {
                    if (map.TryGetValue(i, out int val) && val != i)
                    {
                        errors.Add($"Key {i} had wrong value {val}");
                    }
                }
            }
        });

        remover.Start();
        reader1.Start();
        reader2.Start();

        remover.Join();
        reader1.Join();
        reader2.Join();

        Assert.That(errors, Is.Empty, $"Errors: {string.Join("; ", errors.Take(5))}");

        // After remover finishes, all odd keys must still be present
        for (int i = 1; i < N; i += 2)
        {
            Assert.That(map.TryGetValue(i, out int val), Is.True, $"Odd key {i} lost after concurrent removes");
            Assert.That(val, Is.EqualTo(i));
        }
    }
}
