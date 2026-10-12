using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.ECS.VoidRace.Wallet", 1)]
[StructLayout(LayoutKind.Sequential)]
struct VrWallet
{
    public long Credits;
    public long Pad;
}

[Archetype]
class VrWalletArch : Archetype<VrWalletArch>
{
    public static readonly Comp<VrWallet> Wallet = Register<VrWallet>();
}

/// <summary>
/// Rule <b>CHAIN-01</b> (<c>rules/durability.md</c>, #1233): a transaction voids its own revision entry — at rollback, or as a last-writer-wins conflict's
/// orphan — under the chain's exclusive lock, found by its identity, and nothing else of the chain moves with it.
/// </summary>
/// <remarks>
/// The staged races run on one thread: <see cref="DatabaseEngine.ChainVoidProbe"/> compacts the chain at the point where another thread's cleanup could,
/// between the moment the transaction located its entry and the void. The compaction drops the oldest committed entry, so every later entry moves one slot
/// toward the root — and the newer commit lands where the voiding transaction's entry was.
/// </remarks>
[TestFixture]
class VersionedVoidRaceTests : TestBase<VersionedVoidRaceTests>
{
    private const string Marker = "CHAIN-01 violated";

    private static DatabaseEngine OpenEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<VrWallet>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Write(Transaction tx, EntityId id, long credits)
    {
        var target = tx.OpenMut(id);
        var wallet = target.Read(VrWalletArch.Wallet);
        wallet.Credits = credits;
        target.Set(VrWalletArch.Wallet, wallet);
    }

    /// <summary>Commits one write and returns its commit stamp, the TSN its published revision carries (VIS-04).</summary>
    private static long Commit(DatabaseEngine dbe, EntityId id, long credits)
    {
        using var tx = dbe.CreateQuickTransaction();
        Write(tx, id, credits);
        Assert.That(tx.Commit(), Is.True);
        return tx.CommitStamp;
    }

    /// <summary>
    /// Compacts one revision chain the way the deferred cleanup does — past every snapshot, or below <paramref name="nextMinTsn"/> — and returns how many
    /// entries it removed.
    /// </summary>
    private static int Compact(ComponentTable table, int firstChunkId, long nextMinTsn = long.MaxValue)
    {
        var revisions = table.CompRevTableSegment.CreateChunkAccessor();
        var contents = table.ComponentSegment.CreateChunkAccessor();
        try
        {
            ref var header = ref revisions.GetChunk<CompRevStorageHeader>(firstChunkId, true);
            var before = header.ItemCount;
            Assert.That(header.Control.TryEnterExclusiveAccess(), Is.True, "premise: nothing holds the chain where the void is about to happen");
            try
            {
                ComponentRevisionManager.CleanUpUnusedEntriesCore(table, firstChunkId, nextMinTsn, ref revisions, ref contents);
            }
            finally
            {
                header = ref revisions.GetChunk<CompRevStorageHeader>(firstChunkId);
                header.Control.ExitExclusiveAccess();
            }

            return before - revisions.GetChunk<CompRevStorageHeader>(firstChunkId).ItemCount;
        }
        finally
        {
            contents.Dispose();
            revisions.Dispose();
        }
    }

    /// <summary>Entries of the chain still isolated.</summary>
    private static int IsolatedEntries(DatabaseEngine dbe, ComponentTable table, int root)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var revisions = table.CompRevTableSegment.CreateChunkAccessor();
        try
        {
            var isolated = 0;
            using var en = new RevisionEnumerator(ref revisions, root, false, true, WaitContext.FromTimeout(TimeSpan.FromSeconds(5)));
            while (en.MoveNext())
            {
                isolated += en.Current.IsolationFlag ? 1 : 0;
            }

            return isolated;
        }
        finally
        {
            revisions.Dispose();
        }
    }

    /// <summary>The chain's entries, in order: TSN, content chunk, isolation.</summary>
    private static List<(long, int, bool)> Entries(DatabaseEngine dbe, ComponentTable table, int root)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var revisions = table.CompRevTableSegment.CreateChunkAccessor();
        try
        {
            var entries = new List<(long, int, bool)>();
            using var en = new RevisionEnumerator(ref revisions, root, false, true, WaitContext.FromTimeout(TimeSpan.FromSeconds(5)));
            while (en.MoveNext())
            {
                entries.Add((en.Current.TSN, en.Current.ComponentChunkId, en.Current.IsolationFlag));
            }

            return entries;
        }
        finally
        {
            revisions.Dispose();
        }
    }

    /// <summary>
    /// #1308: a compaction that throws midway leaves the chain as it was. It cleared each entry it dropped in place as it walked and rewrote the root only at
    /// the end, so a throw on an overflow chunk's page left the chain without those entries — the revision a reader at the horizon reads among them. Here
    /// the throw comes among the entries it drops, the chain spread over overflow chunks.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    public void ACompactionThatThrowsMidway_LeavesTheChainAsItWas()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenEngine(scope);
        var (id, table, root) = SpawnWallet(dbe);
        using (var reader = dbe.CreateReadOnlyTransaction())
        {
            var tsns = CommitTwelve(dbe, table, id, root);
            var before = Entries(dbe, table, root);
            var contentBefore = table.ComponentSegment.AllocatedChunkCount;

            // Past everything: all but the newest dropped. The throw comes at the fourth entry visited.
            var visited = 0;
            dbe.CompactionStepProbe = _ =>
            {
                if (++visited == 4)
                {
                    throw new InvalidOperationException("staged: a page fault midway through the walk");
                }
            };
            Assert.Throws<InvalidOperationException>(() => Compact(table, root), "premise: the compaction threw");
            dbe.CompactionStepProbe = null;

            Assert.That(Entries(dbe, table, root), Is.EqualTo(before), "the chain, as it was");
            Assert.That(table.ComponentSegment.AllocatedChunkCount, Is.EqualTo(contentBefore), "no content freed under the live chain");
            Assert.That(reader.Open(id).Read(VrWalletArch.Wallet).Credits, Is.EqualTo(0), "the reader at the spawn still reads it");
        }

        using (EpochGuard.Enter(dbe.EpochManager))
        {
            Assert.That(Compact(table, root), Is.EqualTo(12), "the next compaction does the whole job");
        }

        Assert.That(Read(dbe, id), Is.EqualTo(12L));
    }

    /// <summary>
    /// #1298: a compaction that would keep more than the chain's root holds is put off, the chain unchanged. It used to write what it kept past the root
    /// into fresh chunks after the checkpoint's barrier: a crash that wrote the root and not them recovered an older revision, the newest having been in
    /// the lost chunk. Here a reader at the spawn keeps the sentinel and seven newer commits — more than the root's room.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    public void ACompactionThatWouldKeepMoreThanTheRoot_IsPutOff()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenEngine(scope);
        var (id, table, root) = SpawnWallet(dbe);
        using (var reader = dbe.CreateReadOnlyTransaction())
        {
            var tsns = CommitTwelve(dbe, table, id, root);
            var before = Entries(dbe, table, root);
            var (revisionsBefore, contentBefore) = (table.CompRevTableSegment.AllocatedChunkCount, table.ComponentSegment.AllocatedChunkCount);
            var deferredBefore = Interlocked.Read(ref ComponentRevisionManager.CompactionsDeferred);
            Assert.That(Compact(table, root, tsns[5]), Is.Zero, "nothing removed");
            Assert.That(Entries(dbe, table, root), Is.EqualTo(before), "the chain, as it was");
            Assert.That((table.CompRevTableSegment.AllocatedChunkCount, table.ComponentSegment.AllocatedChunkCount),
                Is.EqualTo((revisionsBefore, contentBefore)), "nothing allocated or freed");
            Assert.That(Interlocked.Read(ref ComponentRevisionManager.CompactionsDeferred), Is.EqualTo(deferredBefore + 1), "counted as put off");
        }

        using (EpochGuard.Enter(dbe.EpochManager))
        {
            Assert.That(Compact(table, root), Is.EqualTo(12), "once the reader is gone, compacted into the root");
        }

        Assert.That(Read(dbe, id), Is.EqualTo(12L));
    }

    private static (EntityId Id, ComponentTable Table, int Root) SpawnWallet(DatabaseEngine dbe)
    {
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<VrWalletArch>(VrWalletArch.Wallet.Set(new VrWallet { Credits = 0 }));
            tx.Commit();
        }

        return (id, dbe.GetComponentTable<VrWallet>(), RootOf(dbe, id));
    }

    // Twelve commits past a reader at the spawn: a chain of thirteen entries over overflow chunks.
    private static long[] CommitTwelve(DatabaseEngine dbe, ComponentTable table, EntityId id, int root)
    {
        var tsns = new long[12];
        for (var k = 0; k < tsns.Length; k++)
        {
            tsns[k] = Commit(dbe, id, k + 1);
        }

        Assert.That(Entries(dbe, table, root), Has.Count.EqualTo(13), "premise: the spawn and twelve commits");
        return tsns;
    }

    /// <summary>Whether the chain holds a committed entry of <paramref name="tsn"/> that still names a content chunk.</summary>
    private static bool HoldsCommit(DatabaseEngine dbe, ComponentTable table, int root, long tsn)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var revisions = table.CompRevTableSegment.CreateChunkAccessor();
        try
        {
            using var en = new RevisionEnumerator(ref revisions, root, false, true, WaitContext.FromTimeout(TimeSpan.FromSeconds(5)));
            while (en.MoveNext())
            {
                if (!en.Current.IsolationFlag && en.Current.TSN == tsn && en.Current.ComponentChunkId != 0)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            revisions.Dispose();
        }
    }

    private static long Read(DatabaseEngine dbe, EntityId id)
    {
        using var r = dbe.CreateReadOnlyTransaction();
        return r.Open(id).Read(VrWalletArch.Wallet).Credits;
    }

    /// <summary>
    /// The chain the races run on: the spawn (0) and one update (1), kept by a reader held across the update; then a writer's pending entry (999), then a
    /// newer commit (2) appended after it — <c>[0, 1, pending, 2]</c>, the pending entry in the root chunk's last slot and the commit in an overflow chunk.
    /// </summary>
    private static (EntityId Id, long NewerTsn) BuildChain(DatabaseEngine dbe, out Transaction writer)
    {
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<VrWalletArch>(VrWalletArch.Wallet.Set(new VrWallet { Credits = 0 }));
            tx.Commit();
        }

        using (dbe.CreateReadOnlyTransaction())
        {
            Commit(dbe, id, 1);
        }

        writer = dbe.CreateQuickTransaction();
        Write(writer, id, 999);

        var newerTsn = Commit(dbe, id, 2);
        return (id, newerTsn);
    }

    /// <summary>
    /// #1273: a rollback waits for its chain's lock without a deadline — it has nothing to fail into — but in slices, each one that runs out reported with the
    /// lock's holder: a conflict handler that waits on another transaction of the entity while its commit holds the lock hangs the rollback, and the report
    /// names the thread. Released, the rollback voids its entry as before. The lock is held on another thread, as the handler's commit holds it.
    /// </summary>
    [Test]
    [VerifiesRule("CHAIN-01")]
    [CancelAfter(20_000)]
    public void ARollbackWaitingOnAHeldChainLock_ReportsItsHolder_AndVoidsOnceReleased()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = OpenEngine(scope);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<VrWalletArch>(VrWalletArch.Wallet.Set(new VrWallet { Credits = 0 }));
            tx.Commit();
        }

        var table = dbe.GetComponentTable<VrWallet>();
        var root = RootOf(dbe, id);
        dbe.RollbackVoidReportInterval = TimeSpan.FromMilliseconds(20);
        var writer = dbe.CreateQuickTransaction();
        Write(writer, id, 999);

        var holderId = 0;
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            holderId = Environment.CurrentManagedThreadId;
            using var guard = EpochGuard.Enter(dbe.EpochManager);
            var revisions = table.CompRevTableSegment.CreateChunkAccessor();
            try
            {
                ref var header = ref revisions.GetChunk<CompRevStorageHeader>(root, true);
                header.Control.EnterExclusiveAccess(ref WaitContext.Null);
                held.Set();

                // Held until the rollback has reported twice, or 10 s.
                var sw = Stopwatch.StartNew();
                while (dbe.RollbackVoidWaitReports < 2 && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Yield();
                }

                header = ref revisions.GetChunk<CompRevStorageHeader>(root);
                header.Control.ExitExclusiveAccess();
            }
            finally
            {
                revisions.Dispose();
            }
        });
        holder.Start();
        Assert.That(held.Wait(TimeSpan.FromSeconds(5)), Is.True, "premise: the other thread holds the chain's lock");

        writer.Dispose();
        holder.Join();
        Assert.That(dbe.RollbackVoidWaitReports, Is.GreaterThanOrEqualTo(2), "the rollback reported its wait while the lock was held");
        Assert.That((Read(dbe, id), IsolatedEntries(dbe, table, root)), Is.EqualTo((0L, 0)), "released, the rollback voided its entry");
    }

    /// <summary>
    /// A rollback after a newer commit was appended past its entry: the newer commit stays visible, and nothing stays isolated. No compaction — the void
    /// alone must not take the chain's last entry with it.
    /// </summary>
    [Test]
    [VerifiesRule("CHAIN-01")]
    public void ARollback_AfterANewerCommit_KeepsThatCommit()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        var (id, _) = BuildChain(dbe, out var writer);
        var table = dbe.GetComponentTable<VrWallet>();
        var root = RootOf(dbe, id);
        using (writer)
        {
            writer.Rollback();
        }

        Assert.That(Read(dbe, id), Is.EqualTo(2), $"{Marker}: the rollback took the commit appended after its entry");
        Assert.That(IsolatedEntries(dbe, table, root), Is.Zero, $"{Marker}: the rolled-back entry is still isolated");
    }

    /// <summary>A rollback whose chain is compacted between locating its entry and voiding it.</summary>
    [Test]
    [VerifiesRule("CHAIN-01")]
    public void ACompactionBeforeARollbacksVoid_LeavesTheNewerCommit() => RunCompactionBeforeARollbacksVoid(voidTheRecordedIndex: false);

    /// <summary>The <see cref="RuleMutantAttribute"/> companion: the same race against a rollback that voids the index it recorded, without the lock.</summary>
    [Test]
    [RuleMutant("CHAIN-01")]
    public void ARollbackThatVoidsItsRecordedIndex_LosesTheNewerCommit() =>
        RuleMutants.AssertDetects("CHAIN-01", Marker, () => RunCompactionBeforeARollbacksVoid(voidTheRecordedIndex: true));

    /// <param name="voidTheRecordedIndex">The mutant: <c>RollbackVoidsRecordedIndexForTest</c>.</param>
    private void RunCompactionBeforeARollbacksVoid(bool voidTheRecordedIndex)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        dbe.RollbackVoidsRecordedIndexForTest = voidTheRecordedIndex;
        var (id, _) = BuildChain(dbe, out var writer);
        var table = dbe.GetComponentTable<VrWallet>();
        var root = RootOf(dbe, id);
        var removed = -1;
        dbe.ChainVoidProbe = (t, r) =>
        {
            if (ReferenceEquals(t, table) && removed < 0)
            {
                removed = Compact(t, r);
            }
        };

        using (writer)
        {
            writer.Rollback();
        }

        dbe.ChainVoidProbe = null;
        Assert.That(removed, Is.GreaterThan(0), "premise: the compaction moved the chain's entries");
        Assert.That(Read(dbe, id), Is.EqualTo(2), $"{Marker}: the rollback voided the commit that moved into its entry's slot");
        Assert.That(IsolatedEntries(dbe, table, root), Is.Zero, $"{Marker}: the rolled-back entry is still isolated");
    }

    /// <summary>
    /// A last-writer-wins commit that finds a newer commit past its entry publishes that entry where it sits: its stamp is drawn after the newer commit
    /// published, so it is the newest by stamp (VIS-05). No entry is added or voided, the chain lock is not taken (the void probe never runs), and the
    /// newer commit stays in the chain for a reader whose snapshot falls between the two.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-05")]
    public void ALastWriterWinsCommitPastANewerCommit_PublishesItsEntryInPlace()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        var (id, newerTsn) = BuildChain(dbe, out var writer);
        var table = dbe.GetComponentTable<VrWallet>();
        var root = RootOf(dbe, id);
        var before = Entries(dbe, table, root);
        var conflictsBefore = (long)dbe.GetDebugProperties()["Transactions.Conflicts"];
        var probed = 0;
        dbe.ChainVoidProbe = (t, _) => probed += ReferenceEquals(t, table) ? 1 : 0;

        using (writer)
        {
            Assert.That(writer.Commit(), Is.True);
            dbe.ChainVoidProbe = null;
            Assert.That((long)dbe.GetDebugProperties()["Transactions.Conflicts"] - conflictsBefore, Is.EqualTo(1), "premise: the commit found the newer one past its read");

            var after = Entries(dbe, table, root);
            Assert.That(probed, Is.Zero, "the commit took the chain lock to resolve a conflict it has nothing to resolve");
            Assert.That(after.Count, Is.EqualTo(before.Count), "the commit added an entry to the chain");
            Assert.That(after.Select(e => e.Item2), Is.EqualTo(before.Select(e => e.Item2)), "the commit moved or voided an entry");
            Assert.That(IsolatedEntries(dbe, table, root), Is.Zero, "the commit's entry is still isolated");
            Assert.That(HoldsCommit(dbe, table, root, newerTsn), Is.True, "the newer commit left the chain");
            Assert.That(HoldsCommit(dbe, table, root, writer.CommitStamp), Is.True, "the commit's entry is not published with its stamp");
        }

        Assert.That(Read(dbe, id), Is.EqualTo(999), "the last writer wins");
    }

    /// <summary>
    /// Two writers' pending entries, rolled back in the reverse of their order: the older one's void becomes a hole, the newer one's pops the chain's last
    /// entry and leaves the hole last. The compaction that follows must keep the committed revision before them — a void is not an entry, and does not end
    /// the cleanup's skip.
    /// </summary>
    [Test]
    [VerifiesRule("CHAIN-01")]
    public void TwoRollbacksInReverseOrder_ThenACompaction_KeepTheCommittedRevision()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<VrWalletArch>(VrWalletArch.Wallet.Set(new VrWallet { Credits = 5 }));
            tx.Commit();
        }

        var table = dbe.GetComponentTable<VrWallet>();
        var root = RootOf(dbe, id);
        using var older = dbe.CreateQuickTransaction();
        using var newer = dbe.CreateQuickTransaction();
        Write(older, id, 111);
        Write(newer, id, 222);
        older.Rollback();
        newer.Rollback();
        Assert.That(Compact(table, root), Is.GreaterThan(0), "premise: the compaction removed something");

        Assert.That(Read(dbe, id), Is.EqualTo(5), $"{Marker}: the compaction freed the committed revision a trailing void followed");
        Assert.That(IsolatedEntries(dbe, table, root), Is.Zero);
    }

    /// <summary>
    /// A compaction of a chain holding a hole between pending entries: the newest revision by stamp, the one conflict detection weighs and a handler is
    /// handed as the committed value, is a committed revision, never the hole — a hole there would be a conflict that is not, and chunk 0 as the value.
    /// </summary>
    [Test]
    [VerifiesRule("CHAIN-01")]
    public void ACompactionOverAHole_LeavesTheLastCommitOnACommit()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<VrWalletArch>(VrWalletArch.Wallet.Set(new VrWallet { Credits = 5 }));
            tx.Commit();
        }

        var table = dbe.GetComponentTable<VrWallet>();
        var root = RootOf(dbe, id);
        using var first = dbe.CreateQuickTransaction();
        using var second = dbe.CreateQuickTransaction();
        using var third = dbe.CreateQuickTransaction();
        Write(first, id, 111);
        Write(second, id, 222);
        Write(third, id, 333);
        second.Rollback();
        Assert.That(Compact(table, root), Is.GreaterThan(0), "premise: the rollback left a hole, and the compaction removed something");

        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var revisions = table.CompRevTableSegment.CreateChunkAccessor();
            try
            {
                var newest = ComponentRevisionManager.FindNewestByStamp(ref revisions, root, dbe.CommitCards, 0, out _);
                Assert.That(newest, Is.GreaterThanOrEqualTo(0), "premise: the chain holds a commit");
                var element = ComponentRevisionManager.GetRevisionElement(ref revisions, root, newest).Element;
                Assert.That(element.IsVoid, Is.False, $"{Marker}: the newest revision is a hole");
                Assert.That(element.IsolationFlag, Is.False, "the newest revision is a commit");
            }
            finally
            {
                revisions.Dispose();
            }
        }
    }

    /// <summary>
    /// A conflict handler that throws aborts the commit: the rollback that follows finds only the commit's new entry, so the conflict's orphan must already
    /// be void — left isolated, no compaction would ever pass it. The newer commit stays.
    /// </summary>
    [Test]
    [VerifiesRule("CHAIN-01")]
    public void AConflictHandlerThatThrows_LeavesNoOrphan()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        var (id, _) = BuildChain(dbe, out var writer);
        var table = dbe.GetComponentTable<VrWallet>();
        var root = RootOf(dbe, id);
        using (writer)
        {
            Assert.That(() => writer.Commit((ref ConcurrencyConflictSolver _) => throw new InvalidOperationException("handler")), Throws.Exception,
                "premise: the handler ran, and threw");
        }

        Assert.That(IsolatedEntries(dbe, table, root), Is.Zero, $"{Marker}: the conflict's orphan is still isolated");
        Assert.That(Read(dbe, id), Is.EqualTo(2), "the newer commit stays");
    }

    /// <summary>The root chunk of the entity's revision chain for <see cref="VrWallet"/>, from its entity record.</summary>
    private static unsafe int RootOf(DatabaseEngine dbe, EntityId entityId)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var ct = dbe.GetComponentTable<VrWallet>();
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
