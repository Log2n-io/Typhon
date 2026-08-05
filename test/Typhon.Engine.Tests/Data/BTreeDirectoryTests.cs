using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// The chunk-0 B+Tree directory that lets several trees share one segment — issue #657.
/// </summary>
/// <remarks>
/// <para>
/// Every tree on a shared segment owns one <c>BTreeDirectoryEntry</c> holding its root chunk and count, found on reopen by key. The key used to be
/// <c>StableId</c> (the field id) alone, which is not unique once a segment is shared across component slots: field ids restart at 0 per component, so two
/// components in one archetype each indexing their field #0 register the same key twice. It is now the pair (StableId, Slot).
/// </para>
/// <para>
/// These tests work directly on a raw segment — the failure lives in the directory, below the ECS. The end-to-end reopen consequence is covered by
/// <c>ClusterIndexDirectoryTests</c>.
/// </para>
/// </remarks>
[TestFixture]
class BTreeDirectoryTests
{
    private IServiceProvider _serviceProvider;

    // The option validator caps database names at 63 UTF-8 bytes and these test names run long, so truncate rather than rename the tests.
    private static string CurrentDatabaseName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            return name.Length <= 50 ? name : name.Substring(0, 50);
        }
    }

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
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)PagedMMF.MinimumMemPageCount * PagedMMF.PageSize;
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    /// <summary>
    /// The cap must be exactly what the four reserved directory chunks hold. One more entry and <c>ComputeEntryLocation</c> returns chunk 4 — the first
    /// NODE chunk — so the directory would write over tree data.
    /// </summary>
    [Test]
    public void MaxDirectoryEntries_IsDerivedFromStride_NotHardcoded()
    {
        // 256-byte node stride: chunk 0 holds (256 - 2) / 12 = 21 after its header, chunks 1-3 hold 256 / 12 = 21 each.
        Assert.That(BTreeBase<PersistentStore>.MaxDirectoryEntriesFor(256), Is.EqualTo(21 + 3 * 21), "84 entries fit at the 256-byte stride");

        // A wider stride (the String64 node size) must scale, not stay pinned to the old hardcoded 20.
        Assert.That(BTreeBase<PersistentStore>.MaxDirectoryEntriesFor(356), Is.GreaterThan(BTreeBase<PersistentStore>.MaxDirectoryEntriesFor(256)),
            "a wider chunk holds more directory entries");
    }

    /// <summary>
    /// A duplicate key creates fine — each instance caches its own entry offset — and only bites on reopen, where both trees resolve to the first entry's
    /// root. Registration must refuse it instead.
    /// </summary>
    [Test]
    public unsafe void RegisterInDirectory_DuplicateKey_Throws()
    {
        using var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            _ = new IntSingleBTree<PersistentStore>(segment, false, new BTreeStableKey(7, 1));

            // Same pair — rejected.
            var ex = Assert.Throws<InvalidOperationException>(() => _ = new IntSingleBTree<PersistentStore>(segment, false, new BTreeStableKey(7, 1)));
            Assert.That(ex.Message, Does.Contain("already registered"));

            // Same stableId on a DIFFERENT slot is a different tree, and must still be accepted — that is the whole point of widening the key.
            Assert.DoesNotThrow(() => _ = new IntSingleBTree<PersistentStore>(segment, false, new BTreeStableKey(7, 2)));
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// Two trees keyed on the same stableId but different slots must keep separate roots across a reload — the directory-level form of the #657 defect.
    /// </summary>
    [Test]
    public unsafe void FindInDirectory_SameStableIdDifferentSlots_ResolveToTheirOwnTree()
    {
        using var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var slot0 = new IntSingleBTree<PersistentStore>(segment, false, new BTreeStableKey(0, 0));
                var slot1 = new IntSingleBTree<PersistentStore>(segment, false, new BTreeStableKey(0, 1));

                slot0.Add(100, 1000, ref accessor);
                slot1.Add(200, 2000, ref accessor);

                // Reload both from the directory — the step where a shared key hands both trees the same root.
                var reloaded0 = new IntSingleBTree<PersistentStore>(segment, true, new BTreeStableKey(0, 0));
                var reloaded1 = new IntSingleBTree<PersistentStore>(segment, true, new BTreeStableKey(0, 1));

                Assert.That(reloaded0.TryGet(100, ref accessor).Value, Is.EqualTo(1000), "slot 0's tree must reload its own root");
                Assert.That(reloaded1.TryGet(200, ref accessor).Value, Is.EqualTo(2000), "slot 1's tree must reload its own root");
                Assert.That(reloaded1.TryGet(100, ref accessor).IsSuccess, Is.False, "slot 1 must not see slot 0's key");
                Assert.That(reloaded0.TryGet(200, ref accessor).IsSuccess, Is.False, "slot 0 must not see slot 1's key");
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

    /// <summary>
    /// Fill the directory to capacity, then overflow it. The last accepted entry must be a working tree — proof it landed in a directory chunk and not in
    /// node storage.
    /// </summary>
    [Test]
    public unsafe void RegisterInDirectory_AtCapacity_LastEntryWorks_AndOverflowThrows()
    {
        using var mmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 40, sizeof(Index32Chunk));
        var capacity = BTreeBase<PersistentStore>.MaxDirectoryEntriesFor(segment.Stride);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                IntSingleBTree<PersistentStore> last = null;
                for (var i = 0; i < capacity; i++)
                {
                    last = new IntSingleBTree<PersistentStore>(segment, false, new BTreeStableKey((short)i, 0));
                }

                // The boundary entry must be usable and reload correctly — a mis-computed capacity puts it in a node chunk, where the write corrupts a node.
                last.Add(42, 4242, ref accessor);
                var reloadedLast = new IntSingleBTree<PersistentStore>(segment, true, new BTreeStableKey((short)(capacity - 1), 0));
                Assert.That(reloadedLast.TryGet(42, ref accessor).Value, Is.EqualTo(4242), "the last in-capacity tree must round-trip through the directory");

                var ex = Assert.Throws<InvalidOperationException>(
                    () => _ = new IntSingleBTree<PersistentStore>(segment, false, new BTreeStableKey((short)capacity, 0)));
                Assert.That(ex.Message, Does.Contain("Maximum number of BTree indexes"));
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
}
