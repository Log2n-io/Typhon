using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// Proves each <c>CheckConsistency</c> validator can actually reject something (#765 S1).
/// </summary>
/// <remarks>
/// Every test here breaks one invariant on purpose and requires the validator responsible for it to name the break. That is not belt-and-braces: this subsystem
/// spent 160 days with a checker that reported PASSED on trees carrying three separate defects, and the reason nobody caught it is that a green check is
/// indistinguishable from a check that cannot go red. Adding assertions without adding these is repeating the mistake with more code.
/// <para>
/// The corruptions go through <c>NodeWrapper</c>'s ordinary mutators against a tree built by the ordinary API, so what they produce is a state the engine's own
/// data structures can represent — not a hand-forged struct that proves only that the validator can read a field.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class BTreeConsistencyValidatorTests
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
                var raw = $"cv_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    /// <summary>
    /// A leaf holding <c>[… , 900, 5]</c> passes every pre-S1 assertion, because they only ever read a node's first and last key.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("IXS-04")]
    public unsafe void Mutant_KeysOutOfOrderWithinALeaf_AreReported()
    {
        Corrupt(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var leaf = tree.DiagnosticLeafChainHead;
            int count = leaf.GetCount(ref accessor);
            // Append a key BELOW the leaf's first, so the endpoints still look sane to any check that reads only GetFirst/GetLast.
            leaf.Insert(count, new IntSingleBTree<PersistentStore>.KeyValueItem(leaf.GetFirst(ref accessor).Key - 1, 0), ref accessor);
        },
        expectedMarker: "hold keys out of order",
        validator: static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) => tree.ValidateNodeKeyOrder(ref accessor));
    }

    /// <summary>
    /// An item placed in a leaf without <c>IncCount</c> is a key the tree holds and does not know it holds.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("IXS-05")]
    public unsafe void Mutant_EntryCountDisagreeingWithTheChain_IsReported()
    {
        Corrupt(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // In-order insert, so ONLY the counter is wrong — this must be caught by the counter check and by nothing else.
            var leaf = tree.DiagnosticLeafChainHead;
            int count = leaf.GetCount(ref accessor);
            var last = leaf.GetItem(count - 1, ref accessor).Key;
            var next = leaf.GetItem(count - 2, ref accessor).Key;
            leaf.Insert(count - 1, new IntSingleBTree<PersistentStore>.KeyValueItem(next + (last - next) / 2, 0), ref accessor);
        },
        expectedMarker: "but the leaf chain holds",
        validator: static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) => tree.ValidateEntryCountMatchesChain(ref accessor));
    }

    /// <summary>
    /// A leaf still write-locked after every worker has joined is a write path that returned without unlocking — the shape #695 had.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("IXW-02")]
    public unsafe void Mutant_ALatchLeftHeldAtQuiescence_IsReported()
    {
        Corrupt(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var leaf = tree.DiagnosticLeafChainHead;
            Assert.That(leaf.GetLatch(ref accessor).TryWriteLock(), Is.True, "the mutant must actually acquire the lock it then abandons");
            // Deliberately never unlocked: that IS the mutation.
        },
        expectedMarker: "still locked or obsolete",
        validator: static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) => tree.ValidateNoLatchResidue(ref accessor));
    }

    /// <summary>
    /// Leaves that are on the chain but under no ancestor hold keys the descent cannot reach — #297's "present key reported missing", one hop from permanent.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("IXS-05")]
    public unsafe void Mutant_LeavesOnTheChainButUnreachableByDescent_AreReported()
    {
        Corrupt(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            // Drop the root's leftmost subtree pointer. The leaves stay on the chain and stay perfectly ordered; they simply stop being reachable from the root,
            // which is precisely the state no pre-S1 check could see because every one of them walked one structure or the other, never both.
            var root = tree.DiagnosticRoot;
            Assert.That(root.GetIsLeaf(ref accessor), Is.False, "the tree must have interior nodes for this mutation to mean anything");
            Assert.That(root.GetLeft(ref accessor).IsValid, Is.True, "the root must have a left pointer to drop");
            root.SetLeft(default, ref accessor);
        },
        expectedMarker: "unreachable by descent",
        validator: static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) => tree.ValidateDescentAndChainAgree(ref accessor));
    }

    private delegate void TreeMutation(IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    private delegate string TreeValidator(IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    // Build a healthy multi-leaf tree, assert the validator is silent on it, break one thing, assert the validator names it. The first assertion is what stops
    // this degenerating into "the validator always complains".
    private unsafe void Corrupt(TreeMutation mutation, string expectedMarker, TreeValidator validator)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            for (int i = 1; i <= 2000; i++)
            {
                tree.Add(i * 10, i, ref accessor);
            }

            Assert.That(validator(tree, ref accessor), Is.Null, "the validator must be silent on a healthy tree, or the mutant below proves nothing");
            tree.CheckConsistency(ref accessor);

            mutation(tree, ref accessor);

            var detail = validator(tree, ref accessor);
            accessor.Dispose();

            Assert.That(detail, Is.Not.Null, "the corrupted tree went unreported — this validator cannot fail");
            Assert.That(detail, Does.Contain(expectedMarker), $"reported, but not by the check under test: {detail}");
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }
}
