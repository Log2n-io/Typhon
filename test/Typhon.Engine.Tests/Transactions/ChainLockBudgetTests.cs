using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// CX-06: a revision-chain lock's wait budget starts when the wait does. A transaction older than <c>RevisionChainLockTimeout</c> still waits out a brief
/// hold on a chain it walks — the budget used to run from the transaction's first locked walk, and a reader holding its snapshot longer than the timeout
/// threw on its first contended walk (MarketHardeningTests' long readers, on the desks' chains).
/// </summary>
[TestFixture]
[NonParallelizable] // Mutates the static TimeoutOptions.Current, which every engine reads.
class ChainLockBudgetTests : TestBase<ChainLockBudgetTests>
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(15);

    [Test]
    [VerifiesRule("CX-06")]
    [CancelAfter(5000)]
    public void AReaderOlderThanTheChainLockTimeout_WaitsOutABriefHold([Values(false, true)] bool inAUnitOfWork) =>
        AssertALateContendedWalkWaits(cacheTheBudget: false, inAUnitOfWork);

    [Test]
    [RuleMutant("CX-06")]
    [CancelAfter(5000)]
    public void Mutant_ABudgetCachedForTheTransaction_ThrowsOnALateHold() =>
        RuleMutants.AssertDetects("CX-06", "threw instead of waiting", () => AssertALateContendedWalkWaits(cacheTheBudget: true, inAUnitOfWork: false));

    /// <param name="inAUnitOfWork">
    /// The reader is a quick transaction, whose budget composes with its unit of work's deadline, instead of a read-only one with none.
    /// </param>
    private void AssertALateContendedWalkWaits(bool cacheTheBudget, bool inAUnitOfWork)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        EntityId first, second;
        using (var tx = dbe.CreateQuickTransaction())
        {
            first = tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1)));
            second = tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(2)));
            tx.Commit();
        }

        // After the engine's construction, which sets TimeoutOptions.Current.
        var saved = TimeoutOptions.Current;
        TimeoutOptions.Current = new TimeoutOptions
        {
            DefaultLockTimeout = saved.DefaultLockTimeout,
            PageCacheLockTimeout = saved.PageCacheLockTimeout,
            BTreeLockTimeout = saved.BTreeLockTimeout,
            TransactionChainLockTimeout = saved.TransactionChainLockTimeout,
            RevisionChainLockTimeout = Budget,
            SegmentAllocationLockTimeout = saved.SegmentAllocationLockTimeout,
            DefaultCommitTimeout = saved.DefaultCommitTimeout,
            DefaultUowTimeout = saved.DefaultUowTimeout,
            PageCacheBackpressureTimeout = saved.PageCacheBackpressureTimeout,
            WalBackPressureTimeout = saved.WalBackPressureTimeout,
        };
        try
        {
            using var reader = inAUnitOfWork ? dbe.CreateQuickTransaction() : dbe.CreateReadOnlyTransaction();
            if (cacheTheBudget)
            {
                // The mutant: the budget composed at the transaction's first locked walk, and reused — what CX-06 forbids.
                var composed = false;
                var budget = default(WaitContext);
                reader.ChainLockWaitContextProbe = () =>
                {
                    if (!composed)
                    {
                        budget = WaitContext.FromTimeout(Budget);
                        composed = true;
                    }

                    return budget;
                };
            }

            // A commit after the reader began, on both entities: its snapshot is behind each chain's head, so reading either walks under the chain lock.
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.OpenMut(first).Set(CompAArch.A, new CompA(10));
                tx.OpenMut(second).Set(CompAArch.A, new CompA(20));
                tx.Commit();
            }

            // The first walk is contended too: a budget is composed only when a walk has to wait, so this is where a per-transaction cache would take
            // its deadline.
            var firstRoot = LookupRevisionChunkId(dbe, dbe.GetComponentTable<CompA>(), first);
            using (var firstHeld = new ManualResetEventSlim(false))
            {
                var firstHolder = Task.Run(() => HoldTheChain(dbe, firstRoot, firstHeld));
                Assert.That(firstHeld.Wait(TimeSpan.FromSeconds(5)), Is.True, "the holder never took the first chain's lock");
                Assert.That(reader.Open(first).Read(CompAArch.A).A, Is.EqualTo(1), "premise: the reader walks to its snapshot's revision");
                Assert.That(firstHolder.Result, Is.True, "premise: the first walk waited on the held lock");
            }

            var age = Stopwatch.StartNew();
            while (age.Elapsed < Budget * 1.5)
            {
                Thread.SpinWait(64);
            }

            var root = LookupRevisionChunkId(dbe, dbe.GetComponentTable<CompA>(), second);
            using var held = new ManualResetEventSlim(false);
            var holder = Task.Run(() => HoldTheChain(dbe, root, held));
            Assert.That(held.Wait(TimeSpan.FromSeconds(5)), Is.True, "the holder never took the second chain's lock");
            try
            {
                Assert.That(reader.Open(second).Read(CompAArch.A).A, Is.EqualTo(2));
            }
            catch (LockTimeoutException e)
            {
                Assert.Fail($"the reader, {age.Elapsed.TotalMilliseconds:F0} ms old, threw instead of waiting out a {Hold.TotalMilliseconds:F0} ms hold " +
                            $"against a {Budget.TotalMilliseconds:F0} ms budget: {e.Message}");
            }
            finally
            {
                Assert.That(holder.Result, Is.True, "premise: the second walk waited on the held lock");
            }
        }
        finally
        {
            TimeoutOptions.Current = saved;
        }
    }

    /// <summary>
    /// Holds the chain's lock exclusively, as a committing writer does for a moment: until a reader is seen waiting on it — the contention flag is set
    /// only by an acquisition that has to wait, never by the holder taking an idle lock — then <see cref="Hold"/> more. Returns whether a reader waited
    /// (false after 2 s without one): the test's premise, so a walk that slipped past the hold cannot pass for one that waited.
    /// </summary>
    private static bool HoldTheChain(DatabaseEngine dbe, int root, ManualResetEventSlim held)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = dbe.GetComponentTable<CompA>().CompRevTableSegment.CreateChunkAccessor();
            try
            {
                ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root, false);
                header.EnterControlLockForTest();
                held.Set();
                var waiting = Stopwatch.StartNew();
                var contended = false;
                while (!(contended = header.Control.WasContended) && waiting.Elapsed < TimeSpan.FromSeconds(2))
                {
                    Thread.SpinWait(64);
                }

                var hold = Stopwatch.StartNew();
                while (hold.Elapsed < Hold)
                {
                    Thread.SpinWait(64);
                }

                header.ExitControlLockForTest();
                return contended;
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>The root chunk of <paramref name="entityId"/>'s revision chain for the component of <paramref name="ct"/>, from its entity record.</summary>
    private static unsafe int LookupRevisionChunkId(DatabaseEngine dbe, ComponentTable ct, EntityId entityId)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var meta = dbe.GetMetaByRouting(entityId.ArchetypeId);
            var es = dbe._archetypeStates[meta.ArchetypeId];
            var slot = -1;
            for (var s = 0; s < meta.ComponentCount; s++)
            {
                if (ReferenceEquals(es.SlotToComponentTable[s], ct))
                {
                    slot = s;
                    break;
                }
            }

            Assert.That(slot, Is.GreaterThanOrEqualTo(0), "the component's slot");
            var record = stackalloc byte[meta._entityRecordSize];
            var accessor = es.EntityMap.Segment.CreateChunkAccessor();
            var found = es.EntityMap.TryGet(entityId.EntityKey, record, ref accessor);
            accessor.Dispose();
            Assert.That(found, Is.True, "the entity's record");
            if (meta.IsClusterEligible && es.ClusterState != null)
            {
                // A cluster record keeps one chain root per Versioned slot, by versioned index — as Transaction.ResolveEntity reads it.
                return ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, es.ClusterState.Layout.SlotToVersionedIndex[slot]);
            }

            return EntityRecordAccessor.GetLocation(record, slot);
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }
}
