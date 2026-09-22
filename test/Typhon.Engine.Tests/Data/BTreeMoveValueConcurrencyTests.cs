using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>BTree.MoveValue</c> — the AllowMultiple key-change — under concurrent writers, with a CENSUS oracle rather than a presence probe (#887, IXW-06).
/// </summary>
/// <remarks>
/// <para>
/// This is the operation the tick fence's shadow drain runs from W workers at once since #886 sliced Prep.
/// <c>OlcBTreeStressTests.Stress_MoveValueTailConsistency</c> hammered it for months and missed the defect, because its assertion is "the destination key is
/// readable" — an element removed from its old buffer and never appended to the new one passes that test. The fence saw exactly that shape: an entity the
/// index no longer lists, and a leaf entry naming a slot nobody occupies.
/// </para>
/// <para>
/// So this fixture owns the whole population: every (key, value) pair is moved by exactly one thread, to a destination other threads also write, and the final
/// census must be the permutation those moves define — every value present, exactly once, under the key its move named, and the move itself must have
/// reported success. Both defects lived in the pessimistic fallback; with <c>MaxOptimisticRestarts</c> at 3 and the thread count below, that fallback carries
/// about two moves in three, which is why the loss was 8 runs in 12 before the fix and why these arms are real guards rather than formalities.
/// </para>
/// <para>
/// They are probabilistic in the way <c>OlcBTreeStressTests</c> is: a green run is weak evidence, a red one is conclusive. The serial arms run the identical
/// code on one thread and must always pass — they pin the oracle, so a day when a concurrent arm reddens is a day the tree changed, not a day the census did.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class BTreeMoveValueConcurrencyTests
{
    private IServiceProvider _serviceProvider;

    /// <summary>
    /// One regime of the census. <c>Rotation</c> is the original: 64 keys of 8 values, every key both a source and a destination, so moves mostly APPEND to a
    /// key that exists. <c>UniqueToFresh</c> is the tick fence's: thousands of keys of ONE value each, every move EMPTIES its old key and INSERTS a key that
    /// did not exist — so every move is a key removal plus a key insertion, splits and merges are continuous, and the tree is several levels deep.
    /// <c>Spill</c> is the buffer regime: 8 keys of 128 values, so every buffer runs past the 56-element root chunk and the multi-chunk
    /// <c>RemoveFromBuffer</c> / <c>Append</c> paths and the enumerator's chunk hop actually execute. The first regime found the pessimistic fallback's
    /// unlatched buffer reads; the second is what PrepSliceEquivalenceTests kept failing on after that was fixed.
    /// </summary>
    private readonly record struct Scenario(string Name, int KeyCount, int ValuesPerKey, Func<int, int> DestinationOf);

    private static readonly Scenario Rotation = new("Rotation", 64, 8, key => 1 + ((key + 7) % 64));
    private static readonly Scenario UniqueToFresh = new("UniqueToFresh", 4096, 1, key => key + 100_000);
    private static readonly Scenario Spill = new("Spill", 8, 128, key => 1 + ((key + 3) % 8));

    // Oversubscribed on purpose, for the reason OlcBTreeStressTests scales with the box: the interleavings that matter happen when a thread is preempted
    // inside the optimistic read/validate window — and the ones that matter MOST here happen when three of those send a move down the pessimistic fallback.
    // 2 x CPU, clamped to [8, 32]: 32 on the dev box and the gate runner, 8 on the 3-core arm64 nightly, which is still 2.7x oversubscribed.
    private static readonly int Threads = Math.Clamp(Environment.ProcessorCount * 2, 8, 32);

    [SetUp]
    public void Setup()
    {
        var name = $"btmvc_{TestContext.CurrentContext.Test.Name}";
        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = name[..Math.Min(63, name.Length)];
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    /// <summary>Values are <c>key * 1000 + v</c> with <c>v &lt; 1000</c>, so the census recovers the original key exactly as <c>value / 1000</c>.</summary>
    private static int ValueOf(int key, int v) => (key * 1000) + v;

    private readonly record struct Pair(int Key, int Value, int ElementId);

    /// <summary>The population: the scenario's keys and values, every one recorded with the element id the insert gave it.</summary>
    private static List<Pair> Populate(IntMultipleBTree<PersistentStore> tree, in Scenario scenario, ref ChunkAccessor<PersistentStore> accessor)
    {
        var pairs = new List<Pair>(scenario.KeyCount * scenario.ValuesPerKey);
        for (var k = 1; k <= scenario.KeyCount; k++)
        {
            for (var v = 0; v < scenario.ValuesPerKey; v++)
            {
                pairs.Add(new Pair(k, ValueOf(k, v), tree.Add(k, ValueOf(k, v), ref accessor)));
            }
        }

        return pairs;
    }

    /// <summary>
    /// Moves every pair to its destination, pair <c>i</c> on thread <c>i mod threadCount</c>, so no two threads ever move the same element and a negative
    /// return is a LOST move rather than a race with a peer. Returns how many moves reported the element missing, and the new element ids by pair index.
    /// <paramref name="skipIndex"/> leaves one pair unmoved — the second mutant's lever.
    /// </summary>
    private static (int NotFound, int[] NewElementIds) MoveAll(IntMultipleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment,
        EpochManager epochManager, Scenario scenario, List<Pair> pairs, int threadCount, int skipIndex = -1)
    {
        var notFound = 0;
        var newElementIds = new int[pairs.Count];
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                var scope = epochManager.EnterScope();
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    barrier.SignalAndWait();
                    for (var i = threadId; i < pairs.Count; i += threadCount)
                    {
                        if (i == skipIndex)
                        {
                            continue;
                        }

                        var (key, value, elementId) = pairs[i];
                        var moved = tree.MoveValue(key, scenario.DestinationOf(key), elementId, value, ref accessor, out _, out _);
                        newElementIds[i] = moved;
                        if (moved < 0)
                        {
                            Interlocked.Increment(ref notFound);
                        }
                    }
                }
                finally
                {
                    // In a finally, not at the end of the try: a throw from MoveValue would otherwise leak this accessor's slot refcounts and the next
                    // assertion would be about the leak rather than about the tree.
                    accessor.Dispose();
                    epochManager.ExitScope(scope);
                }
            }, TaskCreationOptions.LongRunning);
        }

        // [CancelAfter] is cooperative — a latch deadlock inside MoveValue would otherwise hang the shard until the CI-level timeout, with the attribute
        // unable to fire. Handing the token to the wait is what makes the timeout real.
        Task.WaitAll(tasks, TestContext.CurrentContext.CancellationToken);
        return (notFound, newElementIds);
    }

    /// <summary>
    /// What the tree holds against what the moves define: every value exactly once, under <c>DestinationOf</c> its original key. Walked the two-level way
    /// <c>EnumerateRangeMultiple</c> requires — <c>CurrentValues</c> is one chunk of the key's buffer, not the whole of it.
    /// </summary>
    private static List<string> Census(IntMultipleBTree<PersistentStore> tree, in Scenario scenario, List<Pair> pairs)
    {
        var problems = new List<string>();
        var seen = new HashSet<int>();
        using var e = tree.EnumerateRangeMultiple(int.MinValue, int.MaxValue);
        while (e.MoveNextKey())
        {
            do
            {
                var values = e.CurrentValues;
                for (var i = 0; i < values.Length; i++)
                {
                    var value = values[i];
                    if (!seen.Add(value))
                    {
                        problems.Add($"value {value} appears more than once (last under key {e.CurrentKey})");
                    }

                    var wanted = scenario.DestinationOf(value / 1000);
                    if (e.CurrentKey != wanted)
                    {
                        problems.Add($"value {value} is listed under key {e.CurrentKey}, but its move named {wanted}");
                    }
                }
            }
            while (e.NextChunk());
        }

        foreach (var (key, value, _) in pairs)
        {
            if (!seen.Contains(value))
            {
                problems.Add($"value {value} (moved {key} -> {scenario.DestinationOf(key)}) is GONE from the tree");
            }
        }

        return problems;
    }

    /// <summary>
    /// Plain asserts, not <c>Assert.Multiple</c>: the mutants run this through <c>RuleMutants.AssertDetects</c>, which recognises the verifier's own
    /// rejection by catching <c>AssertionException</c>, and a Multiple block surfaces as a different exception type. The census message is the one that
    /// carries the evidence, so it goes first.
    /// </summary>
    private static void AssertCensus(List<string> problems, int notFound, string diag)
    {
        if (problems.Count > 0)
        {
            Assert.Fail($"the tree lost or misplaced elements ({problems.Count} findings, {diag}):{Environment.NewLine}"
                + string.Join(Environment.NewLine, problems.GetRange(0, Math.Min(20, problems.Count))));
        }

        Assert.That(notFound, Is.EqualTo(0), "every pair is owned by exactly one thread, so no move may report the element missing");
    }

    private unsafe void RunMoveCensus(in Scenario scenario, int threadCount)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var tree = new IntMultipleBTree<PersistentStore>(segment);
            List<Pair> pairs;
            var setup = segment.CreateChunkAccessor();
            try
            {
                pairs = Populate(tree, scenario, ref setup);
            }
            finally
            {
                setup.Dispose();
            }

            var (notFound, _) = MoveAll(tree, segment, epochManager, scenario, pairs, threadCount);
            var problems = Census(tree, scenario, pairs);

            // The census is blind to a key whose buffer emptied but was never dropped — an allocated-empty buffer yields an empty span and the enumerator
            // skips it — so the tree's own structural validator runs beside it. Before AssertCensus, because a census failure is the more informative report.
            var check = segment.CreateChunkAccessor();
            try
            {
                tree.CheckConsistency(ref check);
            }
            finally
            {
                check.Dispose();
            }

            AssertCensus(problems, notFound, $"{scenario.Name}: restarts={tree.OptimisticRestarts} fallbacks={tree.PessimisticFallbacks}");
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// The control arms and the cases under test share every line: one thread must produce the exact census the oracle predicts, which is what makes a
    /// failure at <c>Threads</c> a CONCURRENCY defect rather than a wrong expectation.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void SerialMoveValue_KeepsEveryElement_UnderItsNewKey() => RunMoveCensus(Rotation, 1);

    [Test]
    [CancelAfter(30_000)]
    public void SerialMoveValue_UniqueKeysToFreshKeys_KeepsEveryElement() => RunMoveCensus(UniqueToFresh, 1);

    [Test]
    [CancelAfter(30_000)]
    public void SerialMoveValue_SpillingBuffers_KeepsEveryElement() => RunMoveCensus(Spill, 1);

    /// <summary>
    /// The rotation regime: ~350 of 512 moves down the pessimistic fallback. Red 8 runs in 12 before #887's fix, 0 in 20 after with the fallback still taken
    /// as often. When it fails it fails large — whole runs of one key's values gone.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("IXW-06")]
    public void ConcurrentMoveValue_KeepsEveryElement_UnderItsNewKey() => RunMoveCensus(Rotation, Threads);

    /// <summary>
    /// The fence's regime: every move a key removal and a fresh-key insertion, with splits and merges live throughout. Before the authority check in
    /// <c>RemoveElementLatched</c> it returned -1 for 1 to 5 present keys per run, 7 runs in 8, with the tree untouched for them; 0 in 10 after, and 8 in 8
    /// with every move forced pessimistic.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("IXW-06")]
    public void ConcurrentMoveValue_UniqueKeysToFreshKeys_KeepsEveryElement() => RunMoveCensus(UniqueToFresh, Threads);

    /// <summary>Buffers past the root chunk: the multi-chunk remove and append paths under the same contention as the other two arms.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("IXW-06")]
    public void ConcurrentMoveValue_SpillingBuffers_KeepsEveryElement() => RunMoveCensus(Spill, Threads);

    /// <summary>
    /// Proves the census is not vacuous for the LOSS shape: a tree from which one moved element has been taken behind its back must be reported, with the
    /// census's own "GONE" marker. A verifier that could not go red is worth less than none — it also stops anyone looking (the IXW-04 mutant's argument).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [RuleMutant("IXW-06")]
    public unsafe void Mutant_AnElementRemovedBehindTheCensus_IsReported()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            var tree = new IntMultipleBTree<PersistentStore>(segment);
            var pairs = Populate(tree, Rotation, ref accessor);
            var (notFound, newElementIds) = MoveAll(tree, segment, epochManager, Rotation, pairs, 1);

            // Green first — otherwise the mutant proves nothing about the mutation.
            Assert.That(notFound, Is.EqualTo(0));
            Assert.That(Census(tree, Rotation, pairs), Is.Empty, "the unmutated tree must be clean, or this mutant tests nothing");

            // The mutation: one element the census expects under its new key is gone from it.
            var victimIndex = Rotation.KeyCount * 3;
            var victim = pairs[victimIndex];
            Assert.That(tree.RemoveValue(Rotation.DestinationOf(victim.Key), newElementIds[victimIndex], victim.Value, ref accessor), Is.True,
                "the element chosen for removal must have been present");

            var problems = Census(tree, Rotation, pairs);
            RuleMutants.AssertDetects("IXW-06", "GONE", () => AssertCensus(problems, notFound, "mutant"));
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// Proves the census is not vacuous for the NOT-FOUND shape either: a move that never happened leaves its value under the old key, and the census must
    /// say so with its own "its move named" marker — the face #887's second defect wore, with <c>MoveValue</c> returning -1 and the tree untouched.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [RuleMutant("IXW-06")]
    public unsafe void Mutant_AMoveThatNeverHappened_IsReported()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        var accessor = segment.CreateChunkAccessor();
        try
        {
            var tree = new IntMultipleBTree<PersistentStore>(segment);
            var pairs = Populate(tree, UniqueToFresh, ref accessor);

            // The mutation is in the run itself: one pair is never moved, exactly as if MoveValue had returned -1 for it and the caller had shrugged.
            var (notFound, _) = MoveAll(tree, segment, epochManager, UniqueToFresh, pairs, 1, skipIndex: 1234);
            Assert.That(notFound, Is.EqualTo(0), "the moves that did run must all have succeeded, or the mutant is testing something else");

            var problems = Census(tree, UniqueToFresh, pairs);
            RuleMutants.AssertDetects("IXW-06", "its move named", () => AssertCensus(problems, notFound, "mutant"));
        }
        finally
        {
            accessor.Dispose();
            epochManager.ExitScope(depth);
        }
    }
}
