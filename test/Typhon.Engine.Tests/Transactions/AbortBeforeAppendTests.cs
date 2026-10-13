using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A commit that fails after PREPARE and before its WAL append rolls back, and must leave no trace: PREPARE maintains a Versioned write's index entries
/// and appends the view deltas of its writes and destroys, and the rollback reverts revisions only. Each test aborts such a commit through AP-03's own
/// check — the same transaction also spawns a key a committed entity holds — then reads the index, refreshes a view, and commits the same change again.
/// </summary>
[TestFixture]
class AbortBeforeAppendTests : TestBase<AbortBeforeAppendTests>
{
    private const string Marker = "abort before the append left a trace";

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>The entity the unique index on <c>CompD.B</c> maps <paramref name="key"/> to, or <c>default</c>.</summary>
    private static unsafe int IndexHolder(DatabaseEngine dbe, int key)
    {
        var cs = dbe._archetypeStates[Archetype<CompDArch>.Metadata.ArchetypeId].ClusterState;
        foreach (var ixSlot in cs.IndexSlots)
        {
            foreach (var field in ixSlot.Fields)
            {
                if (field.AllowMultiple)
                {
                    continue;
                }

                using var guard = EpochGuard.Enter(dbe.EpochManager);
                var accessor = field.Index.Segment.CreateChunkAccessor();
                try
                {
                    var found = field.Index.TryGet(&key, ref accessor);
                    return found.IsSuccess ? found.Value : -1;
                }
                finally
                {
                    accessor.Dispose();
                }
            }
        }

        Assert.Fail("CompD has no unique index");
        return -1;
    }

    /// <summary>Seeds X (B=60) in the view <c>B &gt;= 50</c>, and Y holding B=75, the key the aborting commit's spawn collides on.</summary>
    private static (EntityId X, EntityId Y) Seed(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var x = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 60, 1.0)));
        var y = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 75, 1.0)));
        tx.Commit();
        return (x, y);
    }

    private static void Abort(Transaction tx)
    {
        Assert.Throws<UniqueConstraintViolationException>(() =>
        {
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 75, 1.0)));
            tx.Commit();
        }, "premise: the commit is rejected after PREPARE, before its append");

        // The failed commit ended the transaction: retried, PREPARE would run on entries the first attempt consumed.
        Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.Rollbacked), $"{Marker}: a commit that failed after PREPARE left the transaction open");
        Assert.That(tx.Commit(), Is.False, $"{Marker}: a commit that failed after PREPARE can be committed again");
    }

    private static void SetB(Transaction tx, EntityId id, int b)
    {
        var mut = tx.OpenMut(id);
        var d = mut.Read(CompDArch.D);
        d.B = b;
        mut.Set(CompDArch.D, d);
    }

    /// <summary>An aborted write that moved X's key 60 → 30: the index still maps 60 to X and 30 to nothing, a view refreshed after it keeps X.</summary>
    [Test]
    [VerifiesRule("AP-07")]
    [CancelAfter(10_000)]
    public void AnAbortedKeyMove_LeavesTheIndexAndTheView() => RunAbortedKeyMove(prepareReconciles: false);

    /// <summary>The <see cref="RuleMutantAttribute"/> companion: a commit that reconciles its indexes and views in PREPARE, as before #1262.</summary>
    [Test]
    [RuleMutant("AP-07")]
    [CancelAfter(10_000)]
    public void APrepareThatReconcilesIndexes_LeavesTheAbortedKey() =>
        RuleMutants.AssertDetects("AP-07", Marker, () => RunAbortedKeyMove(prepareReconciles: true));

    private void RunAbortedKeyMove(bool prepareReconciles)
    {
        using var dbe = SetupEngine();
        dbe.PrepareReconcilesIndexesForTest = prepareReconciles;
        var (x, _) = Seed(dbe);
        var holderOf60 = IndexHolder(dbe, 60);
        Assert.That(holderOf60, Is.Not.EqualTo(-1), "premise: 60 is indexed");

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Contains(x), Is.True, "premise: X is in the view");

        using (var tx = dbe.CreateQuickTransaction())
        {
            var mut = tx.OpenMut(x);
            var d = mut.Read(CompDArch.D);
            d.B = 30;
            mut.Set(CompDArch.D, d);
            Abort(tx);
        }

        Assert.That(IndexHolder(dbe, 60), Is.EqualTo(holderOf60), $"{Marker}: the index lost X's committed key");
        Assert.That(IndexHolder(dbe, 30), Is.EqualTo(-1), $"{Marker}: the index holds the aborted key");

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
            Assert.That(view.Contains(x), Is.True, $"{Marker}: the view dropped X on an aborted write");
            Assert.That(view.Removed, Is.Empty, $"{Marker}: the view reported a removal no commit made");
        }

        // The same change, committed: it goes through and moves the key.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var mut = tx.OpenMut(x);
            var d = mut.Read(CompDArch.D);
            d.B = 30;
            mut.Set(CompDArch.D, d);
            Assert.That(tx.Commit(), Is.True, "the change commits once nothing collides");
        }

        Assert.That(IndexHolder(dbe, 30), Is.EqualTo(holderOf60), "the committed move maps 30 to X");
        Assert.That(IndexHolder(dbe, 60), Is.EqualTo(-1), "the committed move frees 60");
        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
            Assert.That(view.Contains(x), Is.False, "the committed move takes X out of the view");
        }
    }

    /// <summary>
    /// An aborted write that takes X out of a view over a non-unique field (<c>CompD.A</c>, AllowMultiple): a view refreshed after the abort keeps X and
    /// reports nothing. With the deltas appended in PREPARE, the refresh consumed the aborted transaction's delta as a committed one.
    /// </summary>
    [Test]
    [VerifiesRule("AP-07")]
    [CancelAfter(10_000)]
    public void AnAbortedWrite_LeavesNoPhantomInAView() => RunAbortedViewWrite(prepareReconciles: false);

    /// <summary>The <see cref="RuleMutantAttribute"/> companion for the views: the deltas appended in PREPARE.</summary>
    [Test]
    [RuleMutant("AP-07")]
    [CancelAfter(10_000)]
    public void APrepareThatAppendsViewDeltas_LeavesAPhantom() =>
        RuleMutants.AssertDetects("AP-07", Marker, () => RunAbortedViewWrite(prepareReconciles: true));

    private void RunAbortedViewWrite(bool prepareReconciles)
    {
        using var dbe = SetupEngine();
        dbe.PrepareReconcilesIndexesForTest = prepareReconciles;
        var (x, _) = Seed(dbe);   // X.A = 1
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.A >= 0.5f).ToView();
        Assert.That(view.Contains(x), Is.True, "premise: X is in the view");

        using (var tx = dbe.CreateQuickTransaction())
        {
            var mut = tx.OpenMut(x);
            var d = mut.Read(CompDArch.D);
            d.A = 0.1f;
            mut.Set(CompDArch.D, d);
            Abort(tx);
        }

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
            Assert.That(view.Contains(x), Is.True, $"{Marker}: the view dropped X on an aborted write");
            Assert.That(view.Removed, Is.Empty, $"{Marker}: the view reported a removal no commit made");
        }

        // The same write, committed, then X destroyed: the AllowMultiple entry the aborted commit never touched is moved, then removed, by its element id.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var mut = tx.OpenMut(x);
            var d = mut.Read(CompDArch.D);
            d.A = 0.1f;
            mut.Set(CompDArch.D, d);
            Assert.That(tx.Commit(), Is.True);
        }

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
            Assert.That(view.Contains(x), Is.False, "the committed write takes X out of the view");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(x);
            Assert.That(tx.Commit(), Is.True, "X's destroy removes its AllowMultiple entry");
        }
    }

    /// <summary>
    /// A conflict-handler commit retains each written chain's lock from PREPARE to its publish. Aborted before the append, it releases them before it rolls
    /// back: the rollback frees a spawn's chain — lock word included — and voids, with no deadline, on chains another aborting handler commit may hold.
    /// The commit's own failure is what surfaces, and the entity's chain is free afterwards.
    /// </summary>
    [Test]
    [VerifiesRule("AP-07")]
    [CancelAfter(15_000)]
    public void AnAbortedHandlerCommit_ReleasesItsChainLocksBeforeRollingBack()
    {
        using var dbe = SetupEngine();
        var (x, _) = Seed(dbe);
        using (var tx = dbe.CreateQuickTransaction())
        {
            SetB(tx, x, 30);
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 40, 1.0)));   // a chain created by this transaction, its lock retained too
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 75, 1.0)));   // the key Y holds: rejected after PREPARE
            Assert.Throws<UniqueConstraintViolationException>(() => tx.Commit((ref ConcurrencyConflictSolver _) => { }),
                $"{Marker}: the aborted handler commit surfaced something other than its own failure");
            Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.Rollbacked));
        }

        var other = Task.Run(() =>
        {
            using var t = dbe.CreateQuickTransaction();
            SetB(t, x, 31);
            return t.Commit();
        });
        Assert.That(other.Wait(TimeSpan.FromSeconds(5)), Is.True, $"{Marker}: X's chain lock stayed held after the aborted commit");
        Assert.That(other.Result, Is.True);
    }

    /// <summary>An aborted destroy of X: X stays indexed and in a view refreshed after it.</summary>
    [Test]
    [VerifiesRule("AP-07")]
    [CancelAfter(10_000)]
    public void AnAbortedDestroy_LeavesTheIndexAndTheView()
    {
        using var dbe = SetupEngine();
        var (x, _) = Seed(dbe);
        var holderOf60 = IndexHolder(dbe, 60);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Contains(x), Is.True, "premise: X is in the view");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(x);
            Abort(tx);
        }

        Assert.That(IndexHolder(dbe, 60), Is.EqualTo(holderOf60), $"{Marker}: the index lost the key of an entity whose destroy aborted");
        using (var txR = dbe.CreateQuickTransaction())
        {
            Assert.That(txR.TryOpen(x, out _), Is.True, "the aborted destroy destroyed nothing");
            view.Refresh(txR);
            Assert.That(view.Contains(x), Is.True, $"{Marker}: the view dropped X on an aborted destroy");
            Assert.That(view.Removed, Is.Empty, $"{Marker}: the view reported a removal no commit made");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(x);
            Assert.That(tx.Commit(), Is.True, "the destroy commits once nothing collides");
        }

        using (var txR = dbe.CreateQuickTransaction())
        {
            Assert.That(txR.TryOpen(x, out _), Is.False);
            view.Refresh(txR);
            Assert.That(view.Contains(x), Is.False, "the committed destroy takes X out of the view");
        }
    }

    /// <summary>
    /// A Versioned write onto a unique key another entity holds is rejected before the append (AP-03), with the documented exception: in PREPARE it threw
    /// <see cref="System.InvalidOperationException"/> (#1260). The index and the WAL are untouched, and the transaction is over.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [VerifiesRule("AP-07")]
    [CancelAfter(10_000)]
    public void AVersionedWriteOntoAHeldKey_IsRejectedBeforeTheAppend()
    {
        using var dbe = SetupEngine();
        var (x, y) = Seed(dbe);
        var holderOf60 = IndexHolder(dbe, 60);
        var holderOf75 = IndexHolder(dbe, 75);
        var lsnBefore = dbe.DurabilityLog.LastAppendedLsn;
        using (var tx = dbe.CreateQuickTransaction())
        {
            SetB(tx, x, 75);
            Assert.Throws<UniqueConstraintViolationException>(() => tx.Commit(), $"{Marker}: a Versioned write onto a held key is not rejected");
            Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.Rollbacked));
        }

        Assert.That(dbe.DurabilityLog.LastAppendedLsn, Is.EqualTo(lsnBefore), $"{Marker}: the rejected commit appended records");
        Assert.That(IndexHolder(dbe, 60), Is.EqualTo(holderOf60), $"{Marker}: the index lost X's key");
        Assert.That(IndexHolder(dbe, 75), Is.EqualTo(holderOf75), $"{Marker}: the index lost Y's key");
        using var r = dbe.CreateReadOnlyTransaction();
        Assert.That(r.Open(x).Read(CompDArch.D).B, Is.EqualTo(60));
        Assert.That(r.Open(y).Read(CompDArch.D).B, Is.EqualTo(75));
    }

    /// <summary>
    /// The Versioned writes' index moves publish one after another, before the spawns: a write may take the key a write before it moves away, and a spawn
    /// the key a write moves away. The other order — taking a key a later write frees — is rejected before the append, as the publish would reject it.
    /// </summary>
    [Test]
    [VerifiesRule("AP-03")]
    [CancelAfter(10_000)]
    public void VersionedKeyMoves_FollowThePublishOrder([Values] bool holderFirst)
    {
        using var dbe = SetupEngine();
        var (x, y) = Seed(dbe);   // X = 60, Y = 75
        var xLocation = IndexHolder(dbe, 60);
        var yLocation = IndexHolder(dbe, 75);
        using (var tx = dbe.CreateQuickTransaction())
        {
            if (holderFirst)
            {
                SetB(tx, y, 90);   // Y leaves 75 first …
                SetB(tx, x, 75);   // … so X may take it,
                tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 60, 1.0)));   // and a spawn the key X left.
                Assert.That(tx.Commit(), Is.True, $"{Marker}: moves onto keys freed earlier in the publish are rejected");
            }
            else
            {
                SetB(tx, x, 75);   // X takes 75 while Y still holds it,
                SetB(tx, y, 90);   // Y leaves it only afterwards.
                Assert.Throws<UniqueConstraintViolationException>(() => tx.Commit(), $"{Marker}: a move onto a key held until later is accepted");
            }
        }

        Assert.That(dbe.CommitPipelineFailure, Is.Null, $"{Marker}: the commit stopped the engine");
        using var r = dbe.CreateReadOnlyTransaction();
        Assert.That(r.Open(x).Read(CompDArch.D).B, Is.EqualTo(holderFirst ? 75 : 60));
        Assert.That(r.Open(y).Read(CompDArch.D).B, Is.EqualTo(holderFirst ? 90 : 75));
        if (holderFirst)
        {
            Assert.That(IndexHolder(dbe, 75), Is.EqualTo(xLocation), "X holds 75");
            Assert.That(IndexHolder(dbe, 90), Is.EqualTo(yLocation), "Y holds 90");
            Assert.That(IndexHolder(dbe, 60), Is.Not.EqualTo(-1).And.Not.EqualTo(xLocation).And.Not.EqualTo(yLocation), "the spawn holds 60");
        }
        else
        {
            Assert.That(IndexHolder(dbe, 60), Is.EqualTo(xLocation), "X still holds 60");
            Assert.That(IndexHolder(dbe, 75), Is.EqualTo(yLocation), "Y still holds 75");
            Assert.That(IndexHolder(dbe, 90), Is.EqualTo(-1), "nothing holds 90");
        }
    }
}
