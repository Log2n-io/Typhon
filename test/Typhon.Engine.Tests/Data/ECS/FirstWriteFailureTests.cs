using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.ECS.FirstWriteFailure.Wallet", 1)]
[StructLayout(LayoutKind.Sequential)]
struct FwWallet
{
    public long Credits;
    public long Pad;
}

[Archetype]
class FwWalletArch : Archetype<FwWalletArch>
{
    public static readonly Comp<FwWallet> Wallet = Register<FwWallet>();
}

/// <summary>
/// #1252: a transaction's first write of a Versioned component that fails — its revision never added, here because the chain's lock is held past a short
/// <c>RevisionChainLockTimeout</c> — leaves the transaction claiming nothing. Its cached entry used to say "written" while still naming the committed revision:
/// a rollback then freed and voided that revision, and a retried write in the same transaction was handed the committed revision's chunk and wrote into it,
/// in place, visible to every reader.
/// </summary>
[TestFixture]
[NonParallelizable] // Mutates the static TimeoutOptions.Current, which every engine reads.
class FirstWriteFailureTests : TestBase<FirstWriteFailureTests>
{
    private const string Marker = "#1252";
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

    private static DatabaseEngine OpenEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<FwWallet>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static long Read(DatabaseEngine dbe, EntityId id)
    {
        using var r = dbe.CreateReadOnlyTransaction();
        return r.Open(id).Read(FwWalletArch.Wallet).Credits;
    }

    private static void Write(Transaction tx, EntityId id, long credits)
    {
        var target = tx.OpenMut(id);
        var wallet = target.Read(FwWalletArch.Wallet);
        wallet.Credits = credits;
        target.Set(FwWalletArch.Wallet, wallet);
    }

    /// <summary>What the transaction does after its write fails.</summary>
    public enum After
    {
        /// <summary>It rolls back.</summary>
        Rollback,

        /// <summary>It writes again, then commits.</summary>
        Retry,
    }

    /// <summary>
    /// CHAIN-01's backstop: a first write fails with its entry already claiming the committed revision (the order CHAIN-02 forbids, forced), and the
    /// transaction rolls back. The rollback finds no pending entry of its own and touches nothing: the entity reads its value, and the revision still names
    /// the same content chunk.
    /// </summary>
    [Test]
    [VerifiesRule("CHAIN-01")]
    [CancelAfter(20_000)]
    public void AFailedFirstWriteThatClaimedTheCommittedRevision_ThenARollback_LeavesIt() => Run(After.Rollback, claimBeforeAppend: true);

    /// <summary>
    /// The first write fails on the chain lock; the same transaction writes again once the lock is free. Until it commits, no other transaction sees that
    /// write: the retry gets a revision of its own, not the committed one to write into.
    /// </summary>
    [Test]
    [VerifiesRule("CHAIN-02")]
    [CancelAfter(20_000)]
    public void AFailedFirstWrite_ThenARetry_WritesItsOwnRevision() => Run(After.Retry, claimBeforeAppend: false);

    /// <summary>The <see cref="RuleMutantAttribute"/> companion: a first write that marks its entry written before appending its revision.</summary>
    [Test]
    [RuleMutant("CHAIN-02")]
    [CancelAfter(20_000)]
    public void AFirstWriteThatClaimsBeforeAppending_LeaksItsRetry() =>
        RuleMutants.AssertDetects("CHAIN-02", Marker, () => Run(After.Retry, claimBeforeAppend: true));

    /// <summary>
    /// A destroy's tombstone is appended at its commit; the append fails on the chain lock, and the commit throws. Committed again once the lock is free,
    /// the destroy writes its tombstone and the entity is gone; until then every other transaction still sees it.
    /// </summary>
    [Test]
    [VerifiesRule("CHAIN-02")]
    [CancelAfter(20_000)]
    public void ADestroyWhoseTombstoneFails_CommitsWhenRetried()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        var id = Seed(dbe, out var table, out var root);
        using (OverrideChainLockTimeout(Budget))
        {
            using var writer = dbe.CreateQuickTransaction();
            writer.Destroy(id);
            FailUnderASharedHold(dbe, table, root, () => writer.Commit());
            using (var r = dbe.CreateReadOnlyTransaction())
            {
                Assert.That(r.TryOpen(id, out _), Is.True, "the failed commit destroyed nothing");
            }

            Assert.That(writer.Commit(), Is.True, $"{Marker}: the retried destroy does not commit");
        }

        using (var r = dbe.CreateReadOnlyTransaction())
        {
            Assert.That(r.TryOpen(id, out _), Is.False, $"{Marker}: the retried destroy committed, and the entity is still there");
        }
    }

    /// <summary>
    /// A first write fails earlier, in its copy-on-write's resolve: the entity is opened and read first, then the chain is held exclusive, so the write's
    /// walk times out before anything is appended. The retry, once the lock is free, resolves again and writes a revision of its own — the failed attempt
    /// left no entry behind (a default one named chain 0, which the retry appended to).
    /// </summary>
    [Test]
    [VerifiesRule("CHAIN-02")]
    [CancelAfter(20_000)]
    public void AFailedResolve_ThenARetry_WritesItsOwnRevision()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        var id = Seed(dbe, out var table, out var root);
        var committedChunk = HeadChunk(dbe, table, root);
        using (OverrideChainLockTimeout(Budget))
        {
            using var writer = dbe.CreateQuickTransaction();
            var target = writer.OpenMut(id);
            var wallet = target.Read(FwWalletArch.Wallet);
            wallet.Credits = 555;
            using (var held = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                var holder = Task.Run(() => HoldTheChainExclusive(dbe, table, root, held, release));
                Assert.That(held.Wait(TimeSpan.FromSeconds(5)), Is.True, "premise: the chain's lock is held");
                try
                {
                    LockTimeoutException failure = null;
                    try
                    {
                        target.Set(FwWalletArch.Wallet, wallet);
                    }
                    catch (LockTimeoutException e)
                    {
                        failure = e;
                    }

                    Assert.That(failure, Is.Not.Null, "premise: the write fails on the chain's lock");
                    Assert.That(failure.Message, Does.Contain("RevisionChain/Enumerate"), "premise: it fails before appending, in its walk");
                }
                finally
                {
                    release.Set();
                    holder.Wait(TimeSpan.FromSeconds(5));
                }
            }

            Write(writer, id, 555);
            Assert.That(Read(dbe, id), Is.EqualTo(100), $"{Marker}: another transaction sees the retried write before it commits");
            Assert.That(writer.Commit(), Is.True, "the retried write commits");
        }

        Assert.That(Read(dbe, id), Is.EqualTo(555), $"{Marker}: the retried write did not land in the entity's chain");
        Assert.That(HeadChunk(dbe, table, root), Is.Not.EqualTo(committedChunk), "the commit is a new revision");
    }

    /// <summary>Holds the chain's lock exclusively, as a committing writer would, until released.</summary>
    private static void HoldTheChainExclusive(DatabaseEngine dbe, ComponentTable table, int root, ManualResetEventSlim held, ManualResetEventSlim release)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = table.CompRevTableSegment.CreateChunkAccessor();
            try
            {
                ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root, false);
                header.EnterControlLockForTest();
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                header = ref accessor.GetChunk<CompRevStorageHeader>(root, false);
                header.ExitControlLockForTest();
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

    /// <param name="after">What the transaction does after its write fails.</param>
    /// <param name="claimBeforeAppend">The mutant: <c>FirstWriteClaimsBeforeAppendForTest</c>.</param>
    private void Run(After after, bool claimBeforeAppend)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        dbe.FirstWriteClaimsBeforeAppendForTest = claimBeforeAppend;
        var id = Seed(dbe, out var table, out var root);
        var committedChunk = HeadChunk(dbe, table, root);
        using (OverrideChainLockTimeout(Budget))
        {
            using var writer = dbe.CreateQuickTransaction();
            FailUnderASharedHold(dbe, table, root, () => Write(writer, id, 555));
            if (after == After.Retry)
            {
                Write(writer, id, 555);
                Assert.That(Read(dbe, id), Is.EqualTo(100), $"{Marker}: another transaction sees the retried write before it commits");
                Assert.That(HeadChunk(dbe, table, root), Is.EqualTo(committedChunk), "the committed revision still names its chunk");
                Assert.That(writer.Commit(), Is.True, "the retried write commits");
                Assert.That(Read(dbe, id), Is.EqualTo(555), "the retried write commits");
            }
            else
            {
                writer.Rollback();
                Assert.That(Read(dbe, id), Is.EqualTo(100), $"{Marker}: the rollback took the committed value");
                Assert.That(HeadChunk(dbe, table, root), Is.EqualTo(committedChunk), $"{Marker}: the committed revision no longer names its chunk");
            }
        }
    }

    /// <summary>An entity worth 100, committed; its component table and revision-chain root.</summary>
    private static EntityId Seed(DatabaseEngine dbe, out ComponentTable table, out int root)
    {
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<FwWalletArch>(FwWalletArch.Wallet.Set(new FwWallet { Credits = 100 }));
            tx.Commit();
        }

        table = dbe.GetComponentTable<FwWallet>();
        root = RootOf(dbe, id);
        return id;
    }

    /// <summary>Runs <paramref name="act"/> while a reader holds the chain shared, and asserts it fails appending a revision — past its own walk.</summary>
    private static void FailUnderASharedHold(DatabaseEngine dbe, ComponentTable table, int root, Action act)
    {
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() => HoldTheChain(dbe, table, root, held, release));
        Assert.That(held.Wait(TimeSpan.FromSeconds(5)), Is.True, "premise: the chain's lock is held");
        try
        {
            var failure = Assert.Throws<LockTimeoutException>(() => act(), "premise: the write fails on the chain's lock");
            Assert.That(failure.Message, Does.Contain("RevisionChain/AddRevision"), "premise: it fails adding its revision, past its own walk");
        }
        finally
        {
            release.Set();
            holder.Wait(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Sets <c>RevisionChainLockTimeout</c> for the test's duration, every other timeout as it was; disposing restores them.</summary>
    private static TimeoutScope OverrideChainLockTimeout(TimeSpan timeout)
    {
        var saved = TimeoutOptions.Current;
        TimeoutOptions.Current = new TimeoutOptions
        {
            DefaultLockTimeout = saved.DefaultLockTimeout,
            PageCacheLockTimeout = saved.PageCacheLockTimeout,
            BTreeLockTimeout = saved.BTreeLockTimeout,
            TransactionChainLockTimeout = saved.TransactionChainLockTimeout,
            RevisionChainLockTimeout = timeout,
            SegmentAllocationLockTimeout = saved.SegmentAllocationLockTimeout,
            DefaultCommitTimeout = saved.DefaultCommitTimeout,
            DefaultUowTimeout = saved.DefaultUowTimeout,
            PageCacheBackpressureTimeout = saved.PageCacheBackpressureTimeout,
            WalBackPressureTimeout = saved.WalBackPressureTimeout,
        };
        return new TimeoutScope(saved);
    }

    private sealed class TimeoutScope(TimeoutOptions saved) : IDisposable
    {
        public void Dispose() => TimeoutOptions.Current = saved;
    }

    /// <summary>
    /// The storm: a 10 µs chain-lock timeout, so first writes fail on readers walking the chains (long readers keep them more than one entry long). Writers
    /// retry them in the same transaction, then commit transfers between 8 accounts — one in four rolls back instead — serialized per account, and record
    /// the committed ones. Every account must end where the committed transfers put it: a retried write leaking into a committed revision survives its
    /// rollback, a lost committed update goes missing.
    /// </summary>
    /// <remarks>
    /// No snapshot sums: an older writer's commit becomes visible mid-snapshot one entity at a time (#1146), so a sweep can see half a transfer.
    /// </remarks>
    [Test]
    [VerifiesRule("CHAIN-02")]
    [CancelAfter(30_000)]
    public void AStormOfFailingFirstWrites_LosesNoCommitAndKeepsNoRollback()
    {
        const int accounts = 8;
        const long each = 1000;
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        var ids = new EntityId[accounts];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < accounts; i++)
            {
                ids[i] = tx.Spawn<FwWalletArch>(FwWalletArch.Wallet.Set(new FwWallet { Credits = each }));
            }

            tx.Commit();
        }

        var timeouts = OverrideChainLockTimeout(TimeSpan.FromTicks(100));

        var locks = new object[accounts];
        for (var i = 0; i < accounts; i++)
        {
            locks[i] = new object();
        }

        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var expected = new long[accounts];
        Array.Fill(expected, each);
        long appendTimeouts = 0, transfers = 0, rolledBack = 0, sweeps = 0;
        using var stop = new ManualResetEventSlim(false);
        var threads = new System.Collections.Generic.List<Thread>();
        var stuck = 0;
        try
        {
            for (var w = 0; w < 3; w++)
            {
                var seed = 1252 + w;
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        var rng = new Random(seed);
                        while (!stop.IsSet && failures.IsEmpty)
                        {
                            var a = rng.Next(accounts);
                            var b = (a + 1 + rng.Next(accounts - 1)) % accounts;
                            lock (locks[Math.Min(a, b)])
                            {
                                lock (locks[Math.Max(a, b)])
                                {
                                    using var tx = dbe.CreateQuickTransaction();
                                    Move(tx, ids[a], -1, ref appendTimeouts);
                                    Move(tx, ids[b], +1, ref appendTimeouts);
                                    if (rng.Next(4) == 0)
                                    {
                                        tx.Rollback();
                                        Interlocked.Increment(ref rolledBack);
                                    }
                                    else
                                    {
                                        if (!tx.Commit())
                                        {
                                            failures.Enqueue("writer: a transfer's commit returned false");
                                            break;
                                        }

                                        expected[a]--;
                                        expected[b]++;
                                        Interlocked.Increment(ref transfers);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue($"writer: {ex.GetType().Name}: {ex.Message}");
                    }
                }) { IsBackground = true });
            }

            for (var r = 0; r < 4; r++)
            {
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        // A snapshot held across sweeps keeps the chains longer than one entry, so the sweeps walk them under the shared lock.
                        using var longReader = dbe.CreateReadOnlyTransaction();
                        while (!stop.IsSet && failures.IsEmpty)
                        {
                            foreach (var id in ids)
                            {
                                try
                                {
                                    longReader.Open(id).Read(FwWalletArch.Wallet);
                                }
                                catch (LockTimeoutException)
                                {
                                    // The readers' walks share the 10 µs budget: one that waits on a writer gives up, and the sweep goes on.
                                }
                            }

                            Interlocked.Increment(ref sweeps);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue($"reader: {ex.GetType().Name}: {ex.Message}");
                    }
                }) { IsBackground = true });
            }

            foreach (var t in threads)
            {
                t.Start();
            }

            stop.Wait(TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            // Every thread joined before anything is asserted: the engine is disposed when the test returns, and must not be under a running thread.
            stop.Set();
            foreach (var t in threads)
            {
                stuck += t.Join(TimeSpan.FromSeconds(10)) ? 0 : 1;
            }

            timeouts.Dispose();
        }

        Assert.That(stuck, Is.Zero, "threads did not stop");

        TestContext.Out.WriteLine(
            $"{transfers:N0} transfers committed, {rolledBack:N0} rolled back, {appendTimeouts:N0} appends timed out and retried, {sweeps:N0} sweeps");
        Assert.That(failures, Is.Empty);
        Assert.That(appendTimeouts, Is.GreaterThan(0), "premise: first writes did fail appending their revision");
        Assert.That(rolledBack, Is.GreaterThan(0), "premise: transfers rolled back");
        for (var i = 0; i < accounts; i++)
        {
            Assert.That(Read(dbe, ids[i]), Is.EqualTo(expected[i]),
                $"{Marker}: account {i} is not where the committed transfers put it — a rolled-back write survived, or a commit was lost");
        }
    }

    /// <summary>
    /// One side of a transfer: its write retried in the same transaction while it fails on the chain's lock, for up to 5 s. Counts the failures that came
    /// from appending the revision — the ones CHAIN-02 is about — not those of the resolve's walk.
    /// </summary>
    private static void Move(Transaction tx, EntityId id, long delta, ref long appendTimeouts)
    {
        var since = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var target = tx.OpenMut(id);
                var wallet = target.Read(FwWalletArch.Wallet);
                wallet.Credits += delta;
                target.Set(FwWalletArch.Wallet, wallet);
                return;
            }
            catch (LockTimeoutException e) when (since.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (e.Message.Contains("AddRevision"))
                {
                    Interlocked.Increment(ref appendTimeouts);
                }
            }
        }
    }

    /// <summary>
    /// Holds the chain's lock shared, as a reader walking it does, until released: the first write's own walk passes, and its revision's append — which
    /// needs the lock exclusive — times out (the starvation of #1253).
    /// </summary>
    private static void HoldTheChain(DatabaseEngine dbe, ComponentTable table, int root, ManualResetEventSlim held, ManualResetEventSlim release)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = table.CompRevTableSegment.CreateChunkAccessor();
            try
            {
                ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root, false);
                Assert.That(header.Control.TryEnterSharedAccess(), Is.True, "premise: nothing holds the chain exclusively");
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                header = ref accessor.GetChunk<CompRevStorageHeader>(root, false);
                header.Control.ExitSharedAccess();
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

    /// <summary>The content chunk of the chain's last committed, non-void entry.</summary>
    private static int HeadChunk(DatabaseEngine dbe, ComponentTable table, int root)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var revisions = table.CompRevTableSegment.CreateChunkAccessor();
        try
        {
            var chunk = -1;
            using var en = new RevisionEnumerator(ref revisions, root, false, true, WaitContext.FromTimeout(TimeSpan.FromSeconds(5)));
            while (en.MoveNext())
            {
                if (!en.Current.IsVoid && !en.Current.IsolationFlag)
                {
                    chunk = en.Current.ComponentChunkId;
                }
            }

            return chunk;
        }
        finally
        {
            revisions.Dispose();
        }
    }

    /// <summary>The root chunk of the entity's revision chain for <see cref="FwWallet"/>, from its entity record.</summary>
    private static unsafe int RootOf(DatabaseEngine dbe, EntityId entityId)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var ct = dbe.GetComponentTable<FwWallet>();
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

            var record = stackalloc byte[meta._entityRecordSize];
            var accessor = es.EntityMap.Segment.CreateChunkAccessor();
            var found = es.EntityMap.TryGet(entityId.EntityKey, record, ref accessor);
            accessor.Dispose();
            Assert.That(found, Is.True, "the entity's record");
            return meta.IsClusterEligible && es.ClusterState != null
                ? ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, es.ClusterState.Layout.SlotToVersionedIndex[slot])
                : EntityRecordAccessor.GetLocation(record, slot);
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }
}
