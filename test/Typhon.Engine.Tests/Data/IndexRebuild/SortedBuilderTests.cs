using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The bottom-up builder (#1588, design §5.1, IXB-01): a tree it builds passes every consistency validator and holds exactly what ordered <c>Add</c> of the
/// same stream holds — same keys, same values, same element-id targets — for every size around the node-capacity edges and every fill, and the live insert
/// path keeps working on it.
/// </summary>
[TestFixture]
internal sealed unsafe class SortedBuilderTests
{
    private IServiceProvider _serviceProvider;

    private static readonly int[] Sizes = [0, 1, 2, 18, 19, 20, 37, 38, 39, 39 * 19, 39 * 19 + 1, 10_000];

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                var raw = $"sortedbuilder_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(4_096 * PagedMMF.PageSize);
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    [Test]
    [VerifiesRule("IXB-01")]
    public void UniqueLongTree_MatchesOrderedAdd_AtEverySizeAndFill(
        [ValueSource(nameof(Sizes))] int size, [Values(50, 90, 100)] int fill)
        => Check((segment, load) => new LongSingleBTree<PersistentStore>(segment, load), sizeof(Index64Chunk), (_, _, _) => [size], fill, multi: false);

    [Test]
    [VerifiesRule("IXB-01")]
    public void UniqueIntTree_MatchesOrderedAdd_AtEverySizeAndFill(
        [ValueSource(nameof(Sizes))] int size, [Values(50, 90, 100)] int fill)
        => Check((segment, load) => new IntSingleBTree<PersistentStore>(segment, load), sizeof(Index32Chunk), (_, _, _) => [size], fill, multi: false);

    [Test]
    [VerifiesRule("IXB-01")]
    public void MultiValueLongTree_MatchesOrderedAdd(
        [ValueSource(nameof(Sizes))] int size, [Values(50, 90, 100)] int fill)
        => Check((segment, load) => new LongMultipleBTree<PersistentStore>(segment, load), sizeof(Index64Chunk), (_, _, _) => [size], fill, multi: true);

    [Test]
    [VerifiesRule("IXB-01")]
    public void MultiValueIntTree_MatchesOrderedAdd(
        [ValueSource(nameof(Sizes))] int size, [Values(50, 90, 100)] int fill)
        => Check((segment, load) => new IntMultipleBTree<PersistentStore>(segment, load), sizeof(Index32Chunk), (_, _, _) => [size], fill, multi: true);

    [Test]
    [VerifiesRule("IXB-01")]
    public void UniqueString64Tree_MatchesOrderedAdd(
        [ValueSource(nameof(Sizes))] int size, [Values(50, 90, 100)] int fill)
        => Check((segment, load) => new String64SingleBTree<PersistentStore>(segment, load), sizeof(IndexString64Chunk), (_, _, _) => [size], fill,
            multi: false);

    /// <summary>
    /// A built tree takes the live path's concurrency (IXB-01: "a tree the live insert path could have produced"): writers insert between the built keys and
    /// remove half of them while readers look up keys nobody touches; the tree then holds exactly what they left and passes every validator.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("IXB-01")]
    public void ABuiltTreeSurvivesConcurrentWriters([Values(false, true)] bool multi, [Values(90, 100)] int fill)
    {
        const int keysPerThread = 2_000;   // built keys 0, 2, 4, …: each thread owns a slice, inserts its odd keys, removes its keys ≡ 2 mod 4
        var writers = Math.Clamp(Environment.ProcessorCount, 4, 16);
        var builtKeys = writers * keysPerThread;

        using var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 64, sizeof(Index32Chunk));
        BTree<int, PersistentStore> tree = multi ? new IntMultipleBTree<PersistentStore>(segment) : new IntSingleBTree<PersistentStore>(segment);
        var builtIds = new int[builtKeys];
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var sorted = tree.BeginSortedBuild(fill, null);
                for (var i = 0; i < builtKeys; i++)
                {
                    builtIds[i] = sorted.Append(i * 2, i, ref accessor);
                }

                sorted.Finish(ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
        }

        using var start = new ManualResetEventSlim(false);
        var readErrors = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var tasks = new List<System.Threading.Tasks.Task>();
        for (var w = 0; w < writers; w++)
        {
            var slice = w;
            tasks.Add(System.Threading.Tasks.Task.Factory.StartNew(() => Guard(failures, () =>
            {
                var d = epochManager.EnterScope();
                var a = segment.CreateChunkAccessor();
                try
                {
                    start.Wait();
                    var key = stackalloc byte[sizeof(int)];
                    for (var i = slice * keysPerThread; i < (slice + 1) * keysPerThread; i++)
                    {
                        tree.Add(i * 2 + 1, 1_000_000 + i, ref a, out _);
                        if (i % 2 == 1)
                        {
                            *(int*)key = i * 2;
                            var removed = multi ? tree.RemoveValue(key, builtIds[i], i, ref a) : tree.Remove(key, out _, ref a);
                            if (!removed)
                            {
                                throw new InvalidOperationException($"built key {i * 2} was not found for removal");
                            }
                        }
                    }
                }
                finally
                {
                    a.Dispose();
                    epochManager.ExitScope(d);
                }
            }), System.Threading.Tasks.TaskCreationOptions.LongRunning));
        }

        for (var r = 0; r < Math.Max(2, writers / 2); r++)
        {
            var seed = r;
            tasks.Add(System.Threading.Tasks.Task.Factory.StartNew(() => Guard(failures, () =>
            {
                var d = epochManager.EnterScope();
                var a = segment.CreateChunkAccessor();
                try
                {
                    start.Wait();
                    var random = new Random(seed);
                    for (var n = 0; n < 5_000; n++)
                    {
                        var i = random.Next(builtKeys / 2) * 2;   // an even index: its key ≡ 0 mod 4, never removed
                        var found = tree.TryGet(i * 2, ref a);
                        if (!found.IsSuccess || (!multi && found.Value != i))
                        {
                            Interlocked.Increment(ref readErrors);
                        }
                    }
                }
                finally
                {
                    a.Dispose();
                    epochManager.ExitScope(d);
                }
            }), System.Threading.Tasks.TaskCreationOptions.LongRunning));
        }

        start.Set();
        System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        Assert.That(readErrors, Is.Zero, "a key no writer touched read wrong");

        depth = epochManager.EnterScope();
        try
        {
            var expected = new List<string>();
            for (var i = 0; i < builtKeys; i++)
            {
                if (i % 2 == 0)
                {
                    expected.Add($"{i * 2}:{i}");
                }

                expected.Add($"{i * 2 + 1}:{1_000_000 + i}");
            }

            Assert.That(Contents(tree, multi), Is.EqualTo(expected), "exactly the keys the writers left");
            var accessor = segment.CreateChunkAccessor();
            try
            {
                tree.CheckConsistency(ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    private static void Guard(System.Collections.Concurrent.ConcurrentQueue<Exception> failures, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            failures.Enqueue(ex);
        }
    }

    /// <summary>A long key as a String64 whose byte order is the long's order: fixed width, offset positive.</summary>
    private static String64 Str(long key) => (key + 100_000_000).ToString("D10");

    /// <summary>Writes <paramref name="key"/> in the tree's key representation, for the pointer API.</summary>
    private static void KeyBytes(BTreeBase<PersistentStore> tree, long key, byte* buffer)
    {
        switch (tree)
        {
            case BTree<long, PersistentStore>:
                *(long*)buffer = key;
                break;
            case BTree<int, PersistentStore>:
                *(int*)buffer = (int)key;
                break;
            case BTree<String64, PersistentStore>:
                *(String64*)buffer = Str(key);
                break;
            default:
                throw new NotSupportedException(tree.GetType().Name);
        }
    }

    /// <summary>
    /// The sizes that sit on a tree's own shape edges, from its node capacity and fills: one leaf full and one more, two leaves, the first internal
    /// level full and one more (height 2 → 3), and the second (height 3 → 4) while it stays under <paramref name="limit"/> keys: every size of a case
    /// is built twice in one scope of the test's 32 MiB cache, which no checkpoint drains.
    /// </summary>
    private static IEnumerable<int> ShapeEdges(int capacity, int leafFill, int fanout, int limit)
    {
        var edges = new SortedSet<int> { capacity - 1, capacity, capacity + 1, leafFill, leafFill + 1, 2 * leafFill, 2 * leafFill + 1 };
        foreach (var level in new long[] { (long)leafFill * fanout, (long)leafFill * fanout * fanout })
        {
            if (level <= limit)
            {
                edges.Add((int)level - 1);
                edges.Add((int)level);
                edges.Add((int)level + 1);
                edges.Add((int)level + leafFill / 2);   // a last leaf under half, balanced with the one before it
            }
        }

        return edges.Where(n => n > 0);
    }

    /// <summary>Every tree kind at the edges of its own shape: the fixed size list is tuned to one capacity, and each key type has its own.</summary>
    [Test]
    [VerifiesRule("IXB-01")]
    public void EveryTreeKind_AtTheEdgesOfItsOwnShape([Values(0, 1, 2, 3, 4)] int kind, [Values(50, 90, 100)] int fill)
    {
        var multi = kind is 3 or 4;

        // A multi-value key holds up to 150 values: fewer keys fit the cache.
        IEnumerable<int> Edges(int capacity, int leafFill, int fanout) => ShapeEdges(capacity, leafFill, fanout, multi ? 3_000 : 30_000);

        switch (kind)
        {
            case 0:
                Check((segment, load) => new LongSingleBTree<PersistentStore>(segment, load), sizeof(Index64Chunk), Edges, fill, multi);
                break;
            case 1:
                Check((segment, load) => new IntSingleBTree<PersistentStore>(segment, load), sizeof(Index32Chunk), Edges, fill, multi);
                break;
            case 2:
                Check((segment, load) => new String64SingleBTree<PersistentStore>(segment, load), sizeof(IndexString64Chunk), Edges, fill, multi);
                break;
            case 3:
                Check((segment, load) => new LongMultipleBTree<PersistentStore>(segment, load), sizeof(Index64Chunk), Edges, fill, multi);
                break;
            default:
                Check((segment, load) => new IntMultipleBTree<PersistentStore>(segment, load), sizeof(Index32Chunk), Edges, fill, multi);
                break;
        }
    }

    /// <summary>
    /// A built value buffer at its exact chunk edges: the root one short of full, full, and one past; a chunk after it the same; and two. Each holds the
    /// values ordered Add puts there, names the chunk of each, and is a buffer the live path and crash recovery take for one of theirs.
    /// </summary>
    [Test]
    [VerifiesRule("IXB-01")]
    public void AValueBuffer_AtItsExactChunkEdges([Values(false, true)] bool wide)
    {
        var stride = wide ? sizeof(Index64Chunk) : sizeof(Index32Chunk);
        var inRoot = (stride - sizeof(VariableSizedBufferRootHeader)) / sizeof(int);
        var perChunk = (stride - sizeof(VariableSizedBufferChunkHeader)) / sizeof(int);
        int[] holders = [1, inRoot - 1, inRoot, inRoot + 1, inRoot + perChunk - 1, inRoot + perChunk, inRoot + perChunk + 1, inRoot + 2 * perChunk + 1];
        var stream = new List<(long Key, int Location)>();
        var location = 0;
        for (var k = 0; k < holders.Length; k++)
        {
            for (var h = 0; h < holders[k]; h++)
            {
                stream.Add((k * 10L, location++));
            }
        }

        using var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var depth = epochManager.EnterScope();
        try
        {
            Func<ChunkBasedSegment<PersistentStore>, bool, BTreeBase<PersistentStore>> create = wide
                ? (segment, load) => new LongMultipleBTree<PersistentStore>(segment, load)
                : (segment, load) => new IntMultipleBTree<PersistentStore>(segment, load);
            var built = Build(mmf, create, stride, stream, 100, builder: true, out var builtIds);
            var oracle = Build(mmf, create, stride, stream, 100, builder: false, out _);
            Assert.That(Contents(built, multi: true), Is.EqualTo(Contents(oracle, multi: true)), "the values ordered Add puts in each buffer");
            AssertElementIdsNameTheirChunks(built, stream, builtIds);
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void AKeyBelowThePreviousOne_Throws([Values(false, true)] bool multi)
        => AssertAppendThrows(multi, [5, 3], "below the previous one");

    [Test]
    public void AUniqueTree_RefusesADuplicateKey()
        => AssertAppendThrows(false, [5, 5], "duplicate key");

    private void AssertAppendThrows(bool multi, int[] keys, string message)
    {
        using var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 8, sizeof(Index32Chunk));
        BTree<int, PersistentStore> tree = multi ? new IntMultipleBTree<PersistentStore>(segment) : new IntSingleBTree<PersistentStore>(segment);
        var depth = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            var sorted = tree.BeginSortedBuild(100, null);
            sorted.Append(keys[0], 1, ref accessor);
            var second = keys[1];
            var thrown = Assert.Throws<InvalidOperationException>(() =>
            {
                var a = segment.CreateChunkAccessor();
                try
                {
                    sorted.Append(second, 2, ref a);
                }
                finally
                {
                    a.Dispose();
                }
            });
            Assert.That(thrown.Message, Does.Contain(message));
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(depth);
        }
    }

    private void Check(Func<ChunkBasedSegment<PersistentStore>, bool, BTreeBase<PersistentStore>> create, int stride,
        Func<int, int, int, IEnumerable<int>> sizes, int fill, bool multi)
    {
        using var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var depth = epochManager.EnterScope();
        try
        {
            // The tree's own node capacity and fills, read from a builder over a scratch tree that is never fed.
            var probe = create(mmf.AllocateChunkBasedSegment(PageBlockType.None, 8, stride), false);
            var shape = probe switch
            {
                BTree<long, PersistentStore> longs => Shape(longs, fill),
                BTree<int, PersistentStore> ints => Shape(ints, fill),
                BTree<String64, PersistentStore> strings => Shape(strings, fill),
                _ => throw new NotSupportedException(probe.GetType().Name),
            };
            foreach (var size in sizes(shape.Capacity, shape.LeafFill, shape.InternalFill))
            {
                CheckOne(mmf, create, stride, size, fill, multi);
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    private static (int Capacity, int LeafFill, int InternalFill) Shape<TKey>(BTree<TKey, PersistentStore> tree, int fill) where TKey : unmanaged
    {
        var sorted = Begin(tree, fill);
        return (sorted.NodeCapacity, sorted.LeafFill, sorted.InternalFill);
    }

    private static void CheckOne(ManagedPagedMMF mmf, Func<ChunkBasedSegment<PersistentStore>, bool, BTreeBase<PersistentStore>> create, int stride,
        int size, int fill, bool multi)
    {
        {
            // Keys with gaps, negatives included; a multi-value tree gets one to four locations per key, and every 97th key 150, a buffer of several
            // chunks.
            var random = new Random(size * 7 + fill);
            var stream = new List<(long Key, int Location)>();
            var location = 0;
            for (var i = 0; i < size; i++)
            {
                var streamKey = (long)i * 3 - size;
                var holders = !multi ? 1 : i % 97 == 96 ? 150 : random.Next(1, 5);
                for (var h = 0; h < holders; h++)
                {
                    stream.Add((streamKey, location++));
                }
            }

            var built = Build(mmf, create, stride, stream, fill, builder: true, out var builtIds);
            var oracle = Build(mmf, create, stride, stream, fill, builder: false, out var oracleIds);
            var reloaded = create(built.Segment, true);

            Assert.Multiple(() =>
            {
                Assert.That(built.EntryCount, Is.EqualTo(oracle.EntryCount), $"size {size}: EntryCount");
                Assert.That(Contents(built, multi), Is.EqualTo(Contents(oracle, multi)), $"size {size}: the same keys and values as ordered Add");
                Assert.That(builtIds.Count, Is.EqualTo(stream.Count), $"size {size}: an element id per entry");
                Assert.That(reloaded.EntryCount, Is.EqualTo(built.EntryCount), $"size {size}: the directory entry reloads the count");
                Assert.That(Contents(reloaded, multi), Is.EqualTo(Contents(built, multi)), $"size {size}: the directory entry reloads the tree");
            });

            if (multi)
            {
                AssertElementIdsNameTheirChunks(built, stream, builtIds);
            }

            // The live path keeps working on a built tree: inserts between and beyond the built keys, then removes of half the built ones.
            var accessor = built.Segment.CreateChunkAccessor();
            var key = stackalloc byte[sizeof(String64)];
            try
            {
                for (var i = 0; i < Math.Min(size, 500); i++)
                {
                    KeyBytes(built, (long)i * 3 - size + 1, key);
                    built.Add(key, 1_000_000 + i, ref accessor);
                }

                for (var i = 0; i < Math.Min(size, 500); i += 2)
                {
                    var wanted = (long)i * 3 - size;
                    // A multi-value tree already lost every third entry to AssertElementIdsNameTheirChunks: remove one it still holds.
                    var at = stream.FindIndex(e => e.Key == wanted);
                    while (multi && at < stream.Count && stream[at].Key == wanted && at % 3 == 0)
                    {
                        at++;
                    }

                    if (at >= stream.Count || stream[at].Key != wanted)
                    {
                        continue;
                    }

                    KeyBytes(built, wanted, key);
                    if (multi)
                    {
                        Assert.That(built.RemoveValue(key, builtIds[at], stream[at].Location, ref accessor), Is.True, $"live remove of key {wanted}");
                    }
                    else
                    {
                        Assert.That(built.Remove(key, out _, ref accessor), Is.True, $"live remove of key {wanted}");
                    }
                }

                built.CheckConsistency(ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    private static BTreeBase<PersistentStore> Build(ManagedPagedMMF mmf, Func<ChunkBasedSegment<PersistentStore>, bool, BTreeBase<PersistentStore>> create,
        int stride, List<(long Key, int Location)> stream, int fill, bool builder, out List<int> elementIds)
    {
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 64, stride);
        var tree = create(segment, false);
        elementIds = [];
        var accessor = segment.CreateChunkAccessor();
        try
        {
            switch (tree)
            {
                case BTree<long, PersistentStore> longs:
                    Feed(longs, stream.Select(e => (e.Key, e.Location)), fill, builder, elementIds, ref accessor);
                    break;
                case BTree<int, PersistentStore> ints:
                    Feed(ints, stream.Select(e => ((int)e.Key, e.Location)), fill, builder, elementIds, ref accessor);
                    break;
                case BTree<String64, PersistentStore> strings:
                    Feed(strings, stream.Select(e => (Str(e.Key), e.Location)), fill, builder, elementIds, ref accessor);
                    break;
            }

            tree.CheckConsistency(ref accessor);
        }
        finally
        {
            accessor.Dispose();
        }

        return tree;
    }

    private static void Feed<TKey>(BTree<TKey, PersistentStore> tree, IEnumerable<(TKey Key, int Location)> stream, int fill, bool builder,
        List<int> elementIds, ref ChunkAccessor<PersistentStore> accessor) where TKey : unmanaged
    {
        if (!builder)
        {
            foreach (var (key, location) in stream)
            {
                elementIds.Add(tree.Add(key, location, ref accessor, out _));
            }

            return;
        }

        var sorted = Begin(tree, fill);
        foreach (var (key, location) in stream)
        {
            elementIds.Add(sorted.Append(key, location, ref accessor));
        }

        sorted.Finish(ref accessor);
        AssertEveryNonRootNodeAtLeastHalfFull(tree, ref accessor);
        AssertInternalNodesKeepTheFill(tree, sorted.InternalFill - 1, ref accessor);
        if (tree.AllowMultiple)
        {
            AssertBuffersAsTheLivePathLeavesThem(tree, ref accessor);
        }
    }

    /// <summary>
    /// Every value buffer the build wrote directly is one the live path could continue: the root as AllocateBuffer leaves it (referenced once, no free
    /// chunk, lock free), its append cursor the chain's last chunk, every chunk but the last full, and the counts summing to its total.
    /// </summary>
    private static void AssertBuffersAsTheLivePathLeavesThem<TKey>(BTree<TKey, PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor)
        where TKey : unmanaged
    {
        if (!tree.DiagnosticRoot.IsValid)
        {
            return;
        }

        var stride = tree.Segment.Stride;
        var rootCapacity = (stride - sizeof(VariableSizedBufferRootHeader)) / sizeof(int);
        var chunkCapacity = (stride - sizeof(VariableSizedBufferChunkHeader)) / sizeof(int);
        var leaf = tree.DiagnosticRoot;
        while (!leaf.GetIsLeaf(ref accessor))
        {
            leaf = leaf.GetChild(-1, ref accessor);
        }

        for (; leaf.IsValid; leaf = leaf.GetNext(ref accessor))
        {
            var count = leaf.GetCount(ref accessor);
            for (var i = 0; i < count; i++)
            {
                var bufferId = leaf.GetItem(i, ref accessor).Value;
                var root = accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId);
                Assert.That((root.RefCounter, root.FirstFreeChunkId, root.TotalFreeChunk), Is.EqualTo(((short)1, 0, (short)0)), $"buffer {bufferId}: root");
                Assert.That(new ReadOnlySpan<byte>(&root.Lock, sizeof(AccessControl)).IndexOfAnyExcept((byte)0), Is.EqualTo(-1),
                    $"buffer {bufferId}: the lock word as AllocateBuffer resets it");
                Assert.That(VariableSizedBufferRecovery.IsBufferRoot(ref accessor, tree.Segment, bufferId), Is.True,
                    $"buffer {bufferId}: a root to crash recovery");
                int chunk = bufferId, last = bufferId, total = 0;
                while (chunk != 0)
                {
                    var header = accessor.GetChunk<VariableSizedBufferChunkHeader>(chunk);
                    var capacity = chunk == bufferId ? rootCapacity : chunkCapacity;
                    if (header.NextChunkId != 0)
                    {
                        Assert.That(header.ElementCount, Is.EqualTo(capacity), $"buffer {bufferId}: chunk {chunk} full before the next");
                    }

                    total += header.ElementCount;
                    last = chunk;
                    chunk = header.NextChunkId;
                }

                Assert.That((root.FirstStoredChunkId, root.TotalCount), Is.EqualTo((last, total)), $"buffer {bufferId}: cursor and total");
            }
        }
    }

    /// <summary>
    /// A multi-value tree's leaves are always full (D2), its internal nodes at the fill under test, as the rebuild builds them; a unique tree's both at it.
    /// </summary>
    private static BTree<TKey, PersistentStore>.SortedBuilder Begin<TKey>(BTree<TKey, PersistentStore> tree, int fill) where TKey : unmanaged
        => tree.BeginSortedBuild(tree.AllowMultiple ? 100 : fill, null, fill);

    /// <summary>
    /// Internal nodes keep their fill: each holds exactly that many keys, but the last two of its level, which the balance may merge or split.
    /// </summary>
    private static void AssertInternalNodesKeepTheFill<TKey>(BTree<TKey, PersistentStore> tree, int fill, ref ChunkAccessor<PersistentStore> accessor)
        where TKey : unmanaged
    {
        if (!tree.DiagnosticRoot.IsValid || tree.DiagnosticRoot.GetIsLeaf(ref accessor))
        {
            return;
        }

        // Level by level from the root; the last node of a level is its tail.
        var level = new List<BTree<TKey, PersistentStore>.NodeWrapper> { tree.DiagnosticRoot };
        while (!level[0].GetIsLeaf(ref accessor))
        {
            var below = new List<BTree<TKey, PersistentStore>.NodeWrapper>();
            for (var n = 0; n < level.Count; n++)
            {
                var count = level[n].GetCount(ref accessor);
                if (n < level.Count - 2 && count != fill)
                {
                    Assert.Fail($"internal node {level[n].ChunkId} holds {count} keys, not the fill of {fill}");
                }

                for (var i = -1; i < count; i++)
                {
                    below.Add(level[n].GetChild(i, ref accessor));
                }
            }

            level = below;
        }
    }

    /// <summary>
    /// The builder balances each level's last two nodes: no node but the root holds fewer than ⌊cap/2⌋ keys, leaves and internal nodes alike.
    /// </summary>
    private static void AssertEveryNonRootNodeAtLeastHalfFull<TKey>(BTree<TKey, PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor)
        where TKey : unmanaged
    {
        if (!tree.DiagnosticRoot.IsValid)
        {
            return;
        }

        var pending = new Stack<(BTree<TKey, PersistentStore>.NodeWrapper Node, bool Root)>();
        pending.Push((tree.DiagnosticRoot, true));
        while (pending.Count > 0)
        {
            var (node, root) = pending.Pop();
            var count = node.GetCount(ref accessor);
            var half = node.GetCapacity() / 2;
            if (!root && count < half)
            {
                var kind = node.GetIsLeaf(ref accessor) ? "leaf" : "internal";
                Assert.Fail($"node {node.ChunkId} ({kind}) holds {count} keys, under half of {node.GetCapacity()}");
            }

            if (!node.GetIsLeaf(ref accessor))
            {
                for (var i = -1; i < count; i++)
                {
                    pending.Push((node.GetChild(i, ref accessor), false));
                }
            }
        }
    }

    /// <summary>Every (key, values) of the tree in leaf-chain order, keys as long.</summary>
    private static List<string> Contents(BTreeBase<PersistentStore> tree, bool multi)
    {
        var rows = new List<string>();
        switch (tree)
        {
            case BTree<long, PersistentStore> longs when multi:
            {
                var e = longs.EnumerateRangeMultiple(long.MinValue, long.MaxValue);
                try
                {
                    while (e.MoveNextKey())
                    {
                        rows.Add($"{e.CurrentKey}:{Values(ref e)}");
                    }
                }
                finally
                {
                    e.Dispose();
                }

                break;
            }
            case BTree<int, PersistentStore> ints when multi:
            {
                var e = ints.EnumerateRangeMultiple(int.MinValue, int.MaxValue);
                try
                {
                    while (e.MoveNextKey())
                    {
                        rows.Add($"{e.CurrentKey}:{Values(ref e)}");
                    }
                }
                finally
                {
                    e.Dispose();
                }

                break;
            }
            case BTree<long, PersistentStore> longs:
                foreach (var kv in longs.EnumerateRange(long.MinValue, long.MaxValue))
                {
                    rows.Add($"{kv.Key}:{kv.Value}");
                }

                break;
            case BTree<int, PersistentStore> ints:
                foreach (var kv in ints.EnumerateRange(int.MinValue, int.MaxValue))
                {
                    rows.Add($"{kv.Key}:{kv.Value}");
                }

                break;
            case BTree<String64, PersistentStore> strings:
                foreach (var kv in strings.EnumerateLeaves())
                {
                    rows.Add($"{kv.Key}:{kv.Value}");
                }

                break;
        }

        return rows;
    }

    private static string Values<TKey>(ref BTree<TKey, PersistentStore>.RangeMultipleEnumerator e) where TKey : unmanaged
    {
        var values = new List<int>();
        do
        {
            foreach (var v in e.CurrentValues)
            {
                values.Add(v);
            }
        }
        while (e.NextChunk());

        values.Sort();
        return string.Join(',', values);
    }

    /// <summary>Each element id returned is the buffer chunk that holds its location: removing by it removes exactly that value.</summary>
    private static void AssertElementIdsNameTheirChunks(BTreeBase<PersistentStore> tree, List<(long Key, int Location)> stream, List<int> ids)
    {
        var accessor = tree.Segment.CreateChunkAccessor();
        try
        {
            var key = stackalloc byte[sizeof(String64)];
            for (var i = 0; i < stream.Count; i += 3)
            {
                KeyBytes(tree, stream[i].Key, key);
                Assert.That(tree.RemoveValue(key, ids[i], stream[i].Location, ref accessor), Is.True, $"entry {i}: its element id finds its value");
            }

            tree.CheckConsistency(ref accessor);
        }
        finally
        {
            accessor.Dispose();
        }
    }
}
