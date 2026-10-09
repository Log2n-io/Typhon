using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
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

    private static long Commit(DatabaseEngine dbe, EntityId id, long credits)
    {
        using var tx = dbe.CreateQuickTransaction();
        Write(tx, id, credits);
        Assert.That(tx.Commit(), Is.True);
        return tx.TSN;
    }

    /// <summary>Compacts one revision chain the way the deferred cleanup does, past every snapshot, and returns how many entries it removed.</summary>
    private static int Compact(ComponentTable table, int firstChunkId)
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
                ComponentRevisionManager.CleanUpUnusedEntriesCore(table, firstChunkId, long.MaxValue, ref revisions, ref contents);
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
    /// A last-writer-wins commit that finds a newer commit past its entry re-adds its value at the end and voids its first entry; the chain is compacted in
    /// between. The newer commit stays in the chain, committed and with its content — a reader whose snapshot falls between the two commits reads it — and
    /// the orphan does not stay isolated.
    /// </summary>
    /// <remarks>
    /// Checked on the chain, not through such a reader: the writer began before the newer commit, so its own commit is stamped with an older TSN and is
    /// visible to that reader anyway (#1146).
    /// </remarks>
    [Test]
    [VerifiesRule("CHAIN-01")]
    public void ACompactionBeforeALastWriterWinsVoid_LeavesTheNewerCommit()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenEngine(scope);
        var (id, newerTsn) = BuildChain(dbe, out var writer);
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
            Assert.That(writer.Commit(), Is.True);
            dbe.ChainVoidProbe = null;
            Assert.That(removed, Is.GreaterThan(0), "premise: the conflict path ran, and the compaction moved the chain's entries");

            // Before the writer leaves: as the chain's tail, its disposal trims what no snapshot needs any more, the newer commit included.
            Assert.That(HoldsCommit(dbe, table, root, newerTsn), Is.True, $"{Marker}: the conflict's void took the commit that moved into its orphan's slot");
            Assert.That(IsolatedEntries(dbe, table, root), Is.Zero, $"{Marker}: the conflict's orphan is still isolated");
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
    /// A compaction of a chain holding a hole between pending entries: <c>LastCommitRevisionIndex</c> names a committed revision, never the hole — a commit
    /// testing it would see a conflict that is not there, and a handler would be handed chunk 0 as the committed value.
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
                var lcri = revisions.GetChunk<CompRevStorageHeader>(root).LastCommitRevisionIndex;
                Assert.That(lcri, Is.GreaterThanOrEqualTo(0), "premise: the chain holds a commit");
                var element = ComponentRevisionManager.GetRevisionElement(ref revisions, root, lcri).Element;
                Assert.That(element.IsVoid, Is.False, $"{Marker}: LastCommitRevisionIndex names a hole");
                Assert.That(element.IsolationFlag, Is.False, "LastCommitRevisionIndex names a commit");
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
