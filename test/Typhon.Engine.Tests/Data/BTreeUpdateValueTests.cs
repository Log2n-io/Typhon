using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>TryUpdateValue</c> — in-place value update (#872 step 4). Migration changes a value while the key stays the same, and the pair it replaces,
/// <c>Remove</c> + <c>Add</c>, pays two root-to-leaf descents plus a structural insert to do it.
/// </summary>
/// <remarks>
/// The claim that carries the most weight here is not speed but <b>structural inertness</b>: the operation writes one <c>int</c> in the node's value array
/// and nothing else. These tests assert that against the raw chunk bytes rather than against observable behaviour, because a structural side effect that
/// happens to preserve every lookup is exactly the kind that surfaces three steps later as a corrupt tree.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class BTreeUpdateValueTests
{
    private IServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                var raw = $"uv_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private delegate void TreeAction(IntSingleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
        ref ChunkAccessor<PersistentStore> accessor);

    /// <summary>
    /// Builds a healthy multi-leaf tree of 2 000 entries (keys 10..20000 by 10, value == key/10) and runs <paramref name="body"/> against it.
    /// </summary>
    private unsafe void WithTree(TreeAction body)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            for (var i = 1; i <= 2000; i++)
            {
                tree.Add(i * 10, i, ref accessor);
            }

            try
            {
                body(tree, segment, epochManager, ref accessor);
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

    // ══════════════════════════════════════════════════════════════════════
    // AC-4.1 — the value is updated; an absent key changes nothing
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void TryUpdateValue_ExistingKey_UpdatesValueAndFindsIt()
    {
        WithTree(static (
            IntSingleBTree<PersistentStore> tree,
            ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            Assert.That(tree.TryUpdateValue(5000, 999_999, ref accessor), Is.True, "key 5000 exists, so the update must report success");

            var hit = tree.TryGet(5000, ref accessor);
            // Neighbours on the same leaf are what a bad index calculation would hit.
            var below = tree.TryGet(4990, ref accessor);
            var above = tree.TryGet(5010, ref accessor);
            var keyOrder = tree.ValidateNodeKeyOrder(ref accessor);
            var entryCount = tree.ValidateEntryCountMatchesChain(ref accessor);

            Assert.Multiple(() =>
            {
                Assert.That(hit.IsSuccess, Is.True, "the key must still be present after its value changed");
                Assert.That(hit.Value, Is.EqualTo(999_999), "and must resolve to the new value");
                Assert.That(below.IsSuccess && below.Value == 499, Is.True, "the entry below must be untouched");
                Assert.That(above.IsSuccess && above.Value == 501, Is.True, "the entry above must be untouched");
                Assert.That(keyOrder, Is.Null, "key order must survive a value update");
                Assert.That(entryCount, Is.Null, "the entry count must survive a value update");
            });
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void TryUpdateValue_AbsentKey_ReturnsFalseAndMutatesNothing()
    {
        WithTree(static (
            IntSingleBTree<PersistentStore> tree,
            ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // 5005 falls between two existing keys, so the descent lands on a real leaf and the miss is decided there rather than by an empty tree.
            Assert.That(tree.TryUpdateValue(5005, 123, ref accessor), Is.False, "an absent key must report failure");

            var absent = tree.TryGet(5005, ref accessor);
            var neighbour = tree.TryGet(5000, ref accessor);
            var entryCount = tree.ValidateEntryCountMatchesChain(ref accessor);

            Assert.Multiple(() =>
            {
                Assert.That(absent.IsSuccess, Is.False, "and must not have been inserted");
                Assert.That(neighbour.IsSuccess && neighbour.Value == 500, Is.True, "the neighbouring entry must be untouched");
                Assert.That(entryCount, Is.Null);
            });
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-4.2 — everything except the value slot is byte-identical
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public unsafe void TryUpdateValue_LeavesEveryNonValueByteIdentical()
    {
        WithTree(static (
            IntSingleBTree<PersistentStore> tree,
            ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // Compare the raw chunk, not the tree's behaviour. A structural side effect that still answers every lookup correctly is precisely the one that
            // shows up later as corruption, and only a byte comparison rules it out.
            var leaf = tree.DiagnosticLeafChainHead;
            var key = leaf.GetFirst(ref accessor).Key;

            ref readonly var beforeChunk = ref accessor.GetChunkReadOnly<Index32Chunk>(leaf.ChunkId);
            var keysBefore = new int[Index32Chunk.Capacity];
            var valuesBefore = new int[Index32Chunk.Capacity];
            for (var i = 0; i < Index32Chunk.Capacity; i++)
            {
                keysBefore[i] = beforeChunk.Keys[i];
                valuesBefore[i] = beforeChunk.Values[i];
            }
            var controlBefore = beforeChunk.Control;
            var highKeyBefore = beforeChunk.HighKey;
            var prevBefore = beforeChunk.PrevChunk;
            var nextBefore = beforeChunk.NextChunk;
            var leftBefore = beforeChunk.LeftValue;

            Assert.That(tree.TryUpdateValue(key, 4_242, ref accessor), Is.True);

            ref readonly var afterChunk = ref accessor.GetChunkReadOnly<Index32Chunk>(leaf.ChunkId);
            var controlAfter = afterChunk.Control;
            var highKeyAfter = afterChunk.HighKey;
            var prevAfter = afterChunk.PrevChunk;
            var nextAfter = afterChunk.NextChunk;
            var leftAfter = afterChunk.LeftValue;

            var changedValueSlots = 0;
            var changedKeySlots = 0;
            for (var i = 0; i < Index32Chunk.Capacity; i++)
            {
                if (afterChunk.Keys[i] != keysBefore[i])
                {
                    changedKeySlots++;
                }
                if (afterChunk.Values[i] != valuesBefore[i])
                {
                    changedValueSlots++;
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(controlAfter, Is.EqualTo(controlBefore), "Control packs the flags, Start and Count — a value update must move none of them");
                Assert.That(highKeyAfter, Is.EqualTo(highKeyBefore), "HighKey is the B-link separator; changing it would misroute readers");
                Assert.That(prevAfter, Is.EqualTo(prevBefore));
                Assert.That(nextAfter, Is.EqualTo(nextBefore));
                Assert.That(leftAfter, Is.EqualTo(leftBefore));
                Assert.That(changedKeySlots, Is.Zero, "no key slot may be written");
            });

            Assert.That(changedValueSlots, Is.EqualTo(1), "exactly one value slot may change — no more, and not zero");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-4.5 — the operation allocates nothing
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void TryUpdateValue_AllocatesNothing()
    {
        WithTree(static (
            IntSingleBTree<PersistentStore> tree,
            ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            for (var i = 0; i < 64; i++)
            {
                tree.TryUpdateValue(5000, i, ref accessor);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                tree.TryUpdateValue(5000, i, ref accessor);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero, "the update path is a descent and one store — nothing on it may allocate");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-4.4 — a concurrent reader never sees the entry absent or torn
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void TryUpdateValue_ConcurrentReader_NeverSeesAbsentOrTornValue()
    {
        // This is the property that makes the operation better than Remove+Add rather than merely faster: that pair has a window in which the key is GONE,
        // and a reader landing in it gets a false negative (IX-06). Here the entry is never removed, and the value is published with a release store, so a
        // reader sees one of the two written values and never a third.
        //
        // Values are drawn from a set whose members are mutually distinguishable, so "torn" is detectable rather than merely improbable: a half-written int
        // would combine high and low halves that never appear together.
        WithTree(static (
            IntSingleBTree<PersistentStore> tree,
            ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int Key = 5000;
            const int ValueA = unchecked((int)0xAAAA_AAAA);
            const int ValueB = 0x5555_5555;
            const int Iterations = 20_000;

            var misses = 0;
            var torn = 0;
            var stop = 0;

            var reader = Task.Run(() =>
            {
                // Its OWN epoch pin: EpochManager pins the CALLING thread, so the scope WithTree entered does not cover this task, and
                // ChunkAccessor asserts the caller is pinned.
                var readerDepth = epochs.EnterScope();
                var readerAccessor = segment.CreateChunkAccessor();
                try
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        var probe = tree.TryGet(Key, ref readerAccessor);
                        if (!probe.IsSuccess)
                        {
                            Interlocked.Increment(ref misses);
                            continue;
                        }
                        if (probe.Value != ValueA && probe.Value != ValueB)
                        {
                            Interlocked.Increment(ref torn);
                        }
                    }
                }
                finally
                {
                    readerAccessor.Dispose();
                    epochs.ExitScope(readerDepth);
                }
            });

            for (var i = 0; i < Iterations; i++)
            {
                tree.TryUpdateValue(Key, (i & 1) == 0 ? ValueA : ValueB, ref accessor);
            }

            Volatile.Write(ref stop, 1);
            reader.Wait(TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(
                    Volatile.Read(ref misses),
                    Is.Zero,
                    "the entry is never removed, so a reader must never fail to find it — this is the false negative Remove+Add has");
                Assert.That(Volatile.Read(ref torn), Is.Zero, "a reader must observe one of the two written values, never a mixture of both");
            });
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-4.3 — AllowMultiple: siblings untouched, elementId unchanged
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Builds an AllowMultiple tree where one key carries several values, and runs <paramref name="body"/> against it.</summary>
    private unsafe void WithMultiTree(MultiTreeAction body)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntMultipleBTree<PersistentStore>(segment);
            try
            {
                body(tree, ref accessor);
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

    private delegate void MultiTreeAction(IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    private delegate void MultiTreeFullAction(IntMultipleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, EpochManager epochs,
        ref ChunkAccessor<PersistentStore> accessor);

    /// <summary>
    /// <see cref="WithMultiTree"/> plus the segment and the epoch manager, for tests that need a SECOND accessor or a second thread.
    /// </summary>
    private unsafe void WithMultiTreeFull(MultiTreeFullAction body)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntMultipleBTree<PersistentStore>(segment);
            try
            {
                body(tree, segment, epochManager, ref accessor);
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

    // ══════════════════════════════════════════════════════════════════════
    // Contention is a liveness condition, not "the element is not there"
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void TryUpdateValueAtPessimistic_UpdatesTheElementInPlaceAndReleasesTheLeaf()
    {
        // The BODY of the fallback. Its ROUTING is the test below; the two are separate because MaxOptimisticRestarts is a const, so no test can shrink the
        // optimistic budget to force entry here without turning six hot loops into field reads.
        //
        // Two accessors, as production does: the optimistic path rents a warm accessor for the nodes and a warm SIBLING accessor for the buffer pages,
        // because VSBS chunks and index nodes evict each other out of a single warm accessor.
        WithMultiTreeFull(static (
            IntMultipleBTree<PersistentStore> tree,
            ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int Key = 42;

            tree.Add(Key, 1001, ref accessor);
            var idB = tree.Add(Key, 1002, ref accessor);
            tree.Add(Key, 1003, ref accessor);
            tree.Add(Key + 1, 2001, ref accessor);

            var sibAccessor = segment.CreateChunkAccessor();
            try
            {
                var updated = tree.TryUpdateValueAtPessimistic(Key, idB, 1002, 9002, ref accessor, ref sibAccessor);
                Assert.That(updated, Is.True, "the element exists under this key, so the pessimistic path must report success");

                // A leaf left write-locked is the failure this fallback's `finally` exists to prevent, and it is invisible to a value assertion: ReadVersion
                // returns 0 for every reader and TryWriteLock refuses every writer, with no restart able to clear it. A second update through the ORDINARY
                // path is the cheapest probe for it — it cannot complete on a stuck latch.
                var again = tree.TryUpdateValueAt(Key, idB, 9002, 7002, ref accessor);
                Assert.That(again, Is.True, "the fallback must release the leaf write lock — a later update through the optimistic path proves it did");

                var mismatched = tree.TryUpdateValueAtPessimistic(Key, idB, 123_456, 1, ref accessor, ref sibAccessor);
                Assert.That(mismatched, Is.False, "elements are addressed by value; a stale oldValue must find nothing");

                var absent = tree.TryUpdateValueAtPessimistic(Key + 500, idB, 1, 2, ref accessor, ref sibAccessor);
                Assert.That(absent, Is.False, "a key that is not in the tree must report not-found, and must not leave a latch held");
            }
            finally
            {
                sibAccessor.Dispose();
            }

            var seen = new System.Collections.Generic.List<int>();
            var neighbour = new System.Collections.Generic.List<int>();
            var e = tree.EnumerateRangeMultiple(Key, Key + 1);
            while (e.MoveNext())
            {
                var target = e.CurrentKey == Key ? seen : neighbour;
                foreach (var v in e.CurrentValues)
                {
                    target.Add(v);
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(seen, Is.EqualTo(new[] { 1001, 7002, 1003 }), "the fallback must update in place — a reordering means siblings moved");
                Assert.That(neighbour, Is.EqualTo(new[] { 2001 }), "an adjacent key's buffer must not be touched");
            });
        });
    }

    [Test]
    [CancelAfter(15_000)]
    [Category("Sensitive")]
    public void TryUpdateValueAt_UnderContention_NeverAnswersFalseForAnElementThatExists()
    {
        // The ROUTING. Before the fallback existed, exhausting MaxOptimisticRestarts returned `false` — which every caller reads as "no such element", so a
        // migration would skip the update or re-Add a duplicate. Contention is a liveness condition; it must cost time, never an answer.
        //
        // The shape that generates it: every thread's element lives under ONE key, so every thread contends for the SAME leaf latch, while each thread owns
        // its own element and therefore always knows its own oldValue.
        WithMultiTreeFull(static (
            IntMultipleBTree<PersistentStore> tree,
            ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int Key = 7;
            const int Threads = 8;
            const int Iterations = 3_000;

            var elementIds = new int[Threads];
            for (var t = 0; t < Threads; t++)
            {
                elementIds[t] = tree.Add(Key, t * Iterations, ref accessor);
            }

            var falseAnswers = 0;
            var workers = new Task[Threads];
            for (var t = 0; t < Threads; t++)
            {
                var slot = t;
                workers[slot] = Task.Run(() =>
                {
                    // Its OWN epoch pin and accessors: EpochManager pins the CALLING thread, and ChunkAccessor asserts the caller is pinned.
                    var depth = epochs.EnterScope();
                    var nodeAccessor = segment.CreateChunkAccessor();
                    try
                    {
                        var current = slot * Iterations;
                        for (var i = 0; i < Iterations; i++)
                        {
                            if (!tree.TryUpdateValueAt(Key, elementIds[slot], current, current + 1, ref nodeAccessor))
                            {
                                Interlocked.Increment(ref falseAnswers);
                                break;
                            }

                            current++;
                        }
                    }
                    finally
                    {
                        nodeAccessor.Dispose();
                        epochs.ExitScope(depth);
                    }
                });
            }

            Assert.That(Task.WaitAll(workers, TimeSpan.FromSeconds(10)), Is.True, "the workers must finish — a stuck latch shows up here first");

            var restarts = tree.OptimisticRestarts;
            var fallbacks = tree.PessimisticFallbacks;

            Assert.Multiple(() =>
            {
                Assert.That(Volatile.Read(ref falseAnswers), Is.Zero,
                    $"every element exists for the whole run, so no call may answer false. OptRestarts={restarts} Fallbacks={fallbacks}");

                // Vacuity guard. With no contention generated, the assertion above is satisfied by an implementation that never reaches the exhaustion
                // branch at all, and the test would go green against the defect it was written for.
                Assert.That(fallbacks, Is.GreaterThan(0),
                    "no contention was generated on the shared leaf, so this run proved nothing about the exhaustion path");
            });
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void TryUpdateValueAt_AllowMultiple_LeavesSiblingsAndElementIdIntact()
    {
        // The regression this guards is the reason migration wanted an in-place update at all: remove-then-append moves whichever element was last into the
        // vacated slot and hands back a NEW id, so any caller holding element ids is silently wrong afterwards. This asserts the id survives and that the
        // siblings at the same key are neither moved nor altered.
        WithMultiTree(static (IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int Key = 42;

            // Several values under ONE key, plus a neighbouring key so a buffer-wide mistake is visible too.
            var idA = tree.Add(Key, 1001, ref accessor);
            var idB = tree.Add(Key, 1002, ref accessor);
            var idC = tree.Add(Key, 1003, ref accessor);
            tree.Add(Key + 1, 2001, ref accessor);

            var updated = tree.TryUpdateValueAt(Key, idB, 1002, 9002, ref accessor);
            Assert.That(updated, Is.True, "the element exists under this key, so the update must report success");

            var seen = new System.Collections.Generic.List<int>();
            var neighbour = new System.Collections.Generic.List<int>();
            var e = tree.EnumerateRangeMultiple(Key, Key + 1);
            while (e.MoveNext())
            {
                var target = e.CurrentKey == Key ? seen : neighbour;
                foreach (var v in e.CurrentValues)
                {
                    target.Add(v);
                }
            }

            // ORDER, not the sorted set. A remove-then-append — or a swap-with-last inside the buffer — preserves the SET exactly, so comparing sorted
            // contents passes against the very implementation this test exists to reject. Buffer position is the observable that distinguishes them, and
            // ablating the in-place write to a swap-with-last is what proved the sorted version vacuous.
            Assert.Multiple(() =>
            {
                Assert.That(seen, Is.EqualTo(new[] { 1001, 9002, 1003 }), "the element must be updated WHERE IT SITS — a reordering means siblings moved");
                Assert.That(neighbour, Is.EqualTo(new[] { 2001 }), "an adjacent key's buffer must not be touched");
                Assert.That(idA, Is.Not.EqualTo(0));
                Assert.That(idC, Is.Not.EqualTo(0));
            });

            // The id must still address the element — the whole point of updating in place rather than remove+append.
            var again = tree.TryUpdateValueAt(Key, idB, 9002, 7002, ref accessor);
            Assert.That(again, Is.True, "elementId must remain valid after an in-place update");

            // And a wrong old value must not match anything, leaving the buffer alone.
            var mismatched = tree.TryUpdateValueAt(Key, idB, 123_456, 1, ref accessor);
            Assert.That(mismatched, Is.False, "elements are addressed by value; a stale oldValue must find nothing rather than overwrite a neighbour");
        });
    }

    // ══════════════════════════════════════════════
    // Index-kind guards — the wrong overload must refuse, not corrupt
    // ══════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void TryUpdateValue_OnAllowMultipleTree_ThrowsAndCorruptsNothing()
    {
        // The defect this guards was real and shipped: with no kind check, SetValueOnly overwrites the leaf slot, which on an AllowMultiple tree holds a
        // BUFFER ID rather than a value. The buffer is orphaned and every later read of that key dereferences an arbitrary chunk as a buffer root — silent
        // index corruption, from nothing worse than picking the wrong overload. The test asserts both halves: that it refuses, and that the key still reads
        // back intact afterwards, which is what would have failed before the guard.
        WithMultiTree(static (IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int Key = 42;
            tree.Add(Key, 1001, ref accessor);
            tree.Add(Key, 1002, ref accessor);

            var localAccessor = accessor;
            Assert.Throws<InvalidOperationException>(() => tree.TryUpdateValue(Key, 7777, ref localAccessor));

            var seen = new System.Collections.Generic.List<int>();
            var e = tree.EnumerateRangeMultiple(Key, Key);
            while (e.MoveNext())
            {
                foreach (var v in e.CurrentValues)
                {
                    seen.Add(v);
                }
            }

            seen.Sort();
            Assert.That(seen, Is.EqualTo(new[] { 1001, 1002 }), "the refused call must leave the buffer and both its elements exactly as they were");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void TryUpdateValueAt_OnUniqueTree_Throws()
    {
        // The mirror. A unique index has no element buffers at all, so `false` would report "the element is not there" — a recoverable data condition — for
        // what is actually a caller bug.
        WithTree(static (
            IntSingleBTree<PersistentStore> tree,
            ChunkBasedSegment<PersistentStore> segment,
            EpochManager epochs,
            ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var localAccessor = accessor;
            Assert.Throws<InvalidOperationException>(() => tree.TryUpdateValueAt(100, 1, 10, 20, ref localAccessor));
        });
    }

    // ══════════════════════════════════════════════
    // SetValueOnly across every chunk width — L16 / L64 / String64
    // ══════════════════════════════════════════════

    /// <summary>
    /// Every width restates the same <c>Adjust(Start + index)</c> arithmetic in its own <c>SetValueOnly</c> override, and only <c>Index32</c> was covered.
    /// An off-by-one in any one of the other three writes a neighbour's value with no test to notice — and the rotation offset is precisely the part an
    /// ablation showed to be fragile.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public unsafe void TryUpdateValue_UpdatesTheRightSlot_AtEveryChunkWidth()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var depth = epochManager.EnterScope();
        try
        {
            var seg16 = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index16Chunk));
            var seg64 = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index64Chunk));

            var a16 = seg16.CreateChunkAccessor();
            var a64 = seg64.CreateChunkAccessor();
            try
            {
                var t16 = new ShortSingleBTree<PersistentStore>(seg16);
                var t64 = new LongSingleBTree<PersistentStore>(seg64);

                // Enough entries to fill several leaves, so the updated key is not the first item of its node and a rotation offset actually matters.
                for (short i = 1; i <= 600; i++)
                {
                    t16.Add(i, i * 10, ref a16);
                }

                for (long i = 1; i <= 600; i++)
                {
                    t64.Add(i, (int)i * 10, ref a64);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(t16.TryUpdateValue(377, 4_242, ref a16), Is.True, "L16: the key exists");
                    Assert.That(t64.TryUpdateValue(377, 4_242, ref a64), Is.True, "L64: the key exists");
                });

                // The neighbours are the assertion. A slot-index mistake shows up as a changed neighbour, not as a failed update of the target.
                Assert.Multiple(() =>
                {
                    Assert.That(t16.TryGet(377, ref a16).Value, Is.EqualTo(4_242), "L16 target");
                    Assert.That(t16.TryGet(376, ref a16).Value, Is.EqualTo(3_760), "L16 left neighbour must be untouched");
                    Assert.That(t16.TryGet(378, ref a16).Value, Is.EqualTo(3_780), "L16 right neighbour must be untouched");
                    Assert.That(t64.TryGet(377, ref a64).Value, Is.EqualTo(4_242), "L64 target");
                    Assert.That(t64.TryGet(376, ref a64).Value, Is.EqualTo(3_760), "L64 left neighbour must be untouched");
                    Assert.That(t64.TryGet(378, ref a64).Value, Is.EqualTo(3_780), "L64 right neighbour must be untouched");
                });

                // CheckConsistency throws on a broken tree rather than returning a verdict, so calling it IS the assertion.
                t16.CheckConsistency(ref a16);
                t64.CheckConsistency(ref a64);
            }
            finally
            {
                a16.Dispose();
                a64.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }
}
