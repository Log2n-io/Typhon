using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// An incremental view (<c>WhereField</c>) folds the commits' index deltas from its ring. These are the ring's defects independent of MVCC (step 1.5 of the
/// commit-path plan): what a view must end up holding — the entities whose COMMITTED value passes the predicate — however the deltas arrive.
/// </summary>
[TestFixture]
class ViewDeltaRingDefectTests : TestBase<ViewDeltaRingDefectTests>
{
    private const string Marker = "the view lost a committed change";

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void SetB(Transaction tx, EntityId id, int b)
    {
        var mut = tx.OpenMut(id);
        var d = mut.Read(CompDArch.D);
        d.B = b;
        mut.Set(CompDArch.D, d);
    }

    /// <summary>
    /// A commit's delta reaches the ring (here: is dropped, the ring full) before its value reaches the cluster slot the full re-scan reads. A view that
    /// re-scans in between misses the value, and must re-scan once more: it ends holding Z, whose committed B passes. With the ring Reset and the
    /// overflow cleared, the next refresh found nothing to do.
    /// </summary>
    [Test]
    [VerifiesRule("VDELTA-01")]
    [CancelAfter(10_000)]
    public void AFullRefreshBetweenACommitsDeltaAndItsValue_IsFollowedByAnother()
    {
        using var dbe = SetupEngine();
        EntityId z;
        var fillers = new EntityId[8];
        using (var tx = dbe.CreateQuickTransaction())
        {
            z = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 10, 1.0)));
            for (var i = 0; i < fillers.Length; i++)
            {
                fillers[i] = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 1_000 + i, 1.0)));
            }

            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView(bufferCapacity: 4);
        Assert.That(view.Contains(z), Is.False, "premise: Z starts outside the view");

        // Overflow the 4-entry ring.
        for (var i = 0; i < fillers.Length; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            SetB(tx, fillers[i], 2_000 + i);
            tx.Commit();
        }

        // Z's commit, held between its delta and its value.
        using var held = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        var commit = System.Threading.Tasks.Task.Run(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            SetB(tx, z, 60);
            dbe.PublishAfterIndexesProbe = () =>
            {
                dbe.PublishAfterIndexesProbe = null;
                held.Set();
                release.Wait(System.TimeSpan.FromSeconds(5));
            };
            return tx.Commit();
        });

        try
        {
            Assert.That(held.Wait(System.TimeSpan.FromSeconds(5)), Is.True, "premise: Z's commit reached its publish");
            using var during = dbe.CreateQuickTransaction();
            view.Refresh(during);
        }
        finally
        {
            release.Set();
        }

        Assert.That(commit.Wait(System.TimeSpan.FromSeconds(5)) && commit.Result, Is.True, "premise: Z's commit completed");
        using (var after = dbe.CreateQuickTransaction())
        {
            view.Refresh(after);
            Assert.That(view.Contains(z), Is.True, $"{Marker}: Z's move, missed by the re-scan and dropped from the ring, never reached the view");
        }
    }

    /// <summary>
    /// Two last-writer-wins updates of one entity, both from the value 60 (in the view): the first commits 30 (out), the second 70 (in). Each delta's
    /// "before" is its writer's read value, 60 — so the second does not say the entity was out when it applies. The view must end holding X: its
    /// newest committed value, 70, passes.
    /// </summary>
    [Test]
    [VerifiesRule("VDELTA-01")]
    [CancelAfter(10_000)]
    public void TwoConcurrentWritersOfOneEntity_LeaveTheViewOnTheNewestValue([Values] ViewShape shape)
    {
        using var dbe = SetupEngine();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            x = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 60, 1.0)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        // The OR view keeps one bit per branch, and decided each branch's change from the same stale BeforeKey.
        using var view = shape switch
        {
            ViewShape.Or => txView.Query<CompDArch>().WhereField<CompD>(d => d.B <= 20 || d.B >= 50).ToView(),
            ViewShape.MultiField => txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50 && d.A >= 0.5f).ToView(),
            _ => txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView(),
        };
        Assert.That(view.Contains(x), Is.True, "premise: X starts in the view");

        using (var first = dbe.CreateQuickTransaction())
        using (var second = dbe.CreateQuickTransaction())
        {
            SetB(first, x, 30);
            SetB(second, x, 70);
            Assert.That(first.Commit(), Is.True);
            Assert.That(second.Commit(), Is.True);
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            Assert.That(r.Open(x).Read(CompDArch.D).B, Is.EqualTo(70), "premise: the second writer's value is the newest committed");
            view.Refresh(r);
            Assert.That(view.Contains(x), Is.True, $"{Marker}: X's newest committed value passes, and the view does not hold it");
        }
    }

    /// <summary>A destroy of an entity in the view reports it removed once (a suspected double delete, not reproduced: kept as a guard).</summary>
    [Test]
    [CancelAfter(10_000)]
    public void ADestroy_IsReportedOnce()
    {
        using var dbe = SetupEngine();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            x = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 60, 1.0)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(x);
            tx.Commit();
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            view.Refresh(r);
            Assert.That(view.Contains(x), Is.False);
            Assert.That(view.Removed, Is.EquivalentTo(new[] { x }), "a destroy is one removal");
            Assert.That(view.Count, Is.Zero);
        }
    }

    /// <summary>
    /// A navigation view whose ring overflowed rebuilds its set at the next refresh: every qualifying source is in it. Its full refresh cleared the set and
    /// computed the deltas without re-scanning, so an overflow emptied the view.
    /// </summary>
    [Test]
    [VerifiesRule("VDELTA-01")]
    [CancelAfter(10_000)]
    public void AnOverflowedNavigationView_RebuildsItsSet()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SvNavGuild>();
        dbe.RegisterComponentFromAccessor<SvNavPlayer>();
        dbe.InitializeArchetypes();
        using var engine = dbe;

        EntityId guild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild = tx.Spawn<SvNavGuildArch>(SvNavGuildArch.Guild.Set(new SvNavGuild(50, 100)));
            tx.Spawn<SvNavPlayerArch>(SvNavPlayerArch.Player.Set(new SvNavPlayer((long)guild.RawValue, 90)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<SvNavPlayerArch>()
            .NavigateField<SvNavPlayer, SvNavGuild>(p => p.GuildId)
            .Where((p, g) => p.Score >= 50 && g.Level >= 30)
            .ToView(bufferCapacity: 4);
        Assert.That(view.Count, Is.EqualTo(1), "premise");

        for (var i = 0; i < 8; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Spawn<SvNavPlayerArch>(SvNavPlayerArch.Player.Set(new SvNavPlayer((long)guild.RawValue, 60 + i)));
            tx.Commit();
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            view.Refresh(r);
        }

        Assert.That(view.Count, Is.EqualTo(9), $"{Marker}: the overflowed navigation view does not hold every qualifying source");
    }

    /// <summary>The view shapes whose membership a delta decides differently.</summary>
    public enum ViewShape
    {
        SingleField,
        MultiField,
        Or,
    }

    /// <summary>
    /// A range on one field (<c>B &gt;= 10 &amp;&amp; B &lt;= 20</c>) is two evaluators of one field: a value passes only if it passes both. An entity below
    /// the range stays out when it moves within the area below, enters when it crosses into the range, and leaves above it.
    /// </summary>
    [Test]
    [VerifiesRule("VDELTA-01")]
    [CancelAfter(10_000)]
    public void ARangeOnOneField_IsDecidedByBothBounds()
    {
        using var dbe = SetupEngine();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            x = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 5, 1.0)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 10 && d.B <= 20).ToView();
        Assert.That(view.Contains(x), Is.False, "premise");

        void MoveAndCheck(int b, bool expected, string what)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                SetB(tx, x, b);
                tx.Commit();
            }

            using var r = dbe.CreateQuickTransaction();
            view.Refresh(r);
            Assert.That(view.Contains(x), Is.EqualTo(expected), $"{Marker}: {what}");
        }

        MoveAndCheck(6, false, "a move below the range put X in the view");
        MoveAndCheck(15, true, "a move into the range left X out");
        MoveAndCheck(25, false, "a move above the range kept X in");
    }

    /// <summary>
    /// Disabling an entity's component takes it out of the view; enabling it — with or without a new value — brings it back, decided by its value. The
    /// enable's ring entry carries no key: it is evaluated from what the view reads, not from a zero key.
    /// </summary>
    [Test]
    [VerifiesRule("VDELTA-01")]
    [CancelAfter(10_000)]
    public void DisableThenEnable_BringsTheEntityBack([Values] bool withValue)
    {
        using var dbe = SetupEngine();
        EntityId x;
        using (var tx = dbe.CreateQuickTransaction())
        {
            x = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 60, 1.0)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView();
        Assert.That(view.Contains(x), Is.True, "premise");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(x).Disable(CompDArch.D);
            tx.Commit();
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            view.Refresh(r);
            Assert.That(view.Contains(x), Is.False, "a disabled component takes X out");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            if (withValue)
            {
                tx.OpenMut(x).Enable(CompDArch.D, new CompD(1f, 70, 1.0));
            }
            else
            {
                tx.OpenMut(x).Enable(CompDArch.D);
            }

            tx.Commit();
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            view.Refresh(r);
            Assert.That(view.Contains(x), Is.True, $"{Marker}: the enabled component's value passes, and the view does not hold X");
        }
    }

    /// <summary>
    /// The full refresh an overflow owes meets a commit between its delta and its value too: that refresh folds the entries it covers rather than
    /// discarding them, so the commit's delta still reaches the view.
    /// </summary>
    [Test]
    [VerifiesRule("VDELTA-01")]
    [CancelAfter(10_000)]
    public void TheOwedFullRefresh_FoldsACommitBetweenItsDeltaAndItsValue()
    {
        using var dbe = SetupEngine();
        EntityId z;
        var fillers = new EntityId[8];
        using (var tx = dbe.CreateQuickTransaction())
        {
            z = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 10, 1.0)));
            for (var i = 0; i < fillers.Length; i++)
            {
                fillers[i] = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 1_000 + i, 1.0)));
            }

            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ToView(bufferCapacity: 4);
        for (var i = 0; i < fillers.Length; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            SetB(tx, fillers[i], 2_000 + i);
            tx.Commit();
        }

        // The overflow's refresh, which owes the next one a full refresh too.
        using (var first = dbe.CreateQuickTransaction())
        {
            view.Refresh(first);
        }

        using var held = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        var commit = System.Threading.Tasks.Task.Run(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            SetB(tx, z, 60);
            dbe.PublishAfterIndexesProbe = () =>
            {
                dbe.PublishAfterIndexesProbe = null;
                held.Set();
                release.Wait(System.TimeSpan.FromSeconds(5));
            };
            return tx.Commit();
        });

        try
        {
            Assert.That(held.Wait(System.TimeSpan.FromSeconds(5)), Is.True, "premise: Z's commit reached its publish");
            using var second = dbe.CreateQuickTransaction();
            view.Refresh(second);
        }
        finally
        {
            release.Set();
        }

        Assert.That(commit.Wait(System.TimeSpan.FromSeconds(5)) && commit.Result, Is.True, "premise: Z's commit completed");
        using (var after = dbe.CreateQuickTransaction())
        {
            view.Refresh(after);
            Assert.That(view.Contains(z), Is.True, $"{Marker}: Z's move, between its delta and its value during the owed refresh, never reached the view");
        }
    }

    /// <summary>
    /// An OR view over two fields keeps one bit per branch. A spawn's ring entries — one per field — each add their branch to the same bits; when one
    /// branch then fails, the entity stays while the other holds.
    /// </summary>
    [Test]
    [VerifiesRule("VDELTA-01")]
    [CancelAfter(10_000)]
    public void AnOrViewOverTwoFields_KeepsEveryBranchOfASpawn([Values] bool failTheBBranch)
    {
        using var dbe = SetupEngine();
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B <= 20 || d.A >= 5f).ToView();

        EntityId y;
        using (var tx = dbe.CreateQuickTransaction())
        {
            y = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(10f, 10, 1.0)));   // both branches pass
            tx.Commit();
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            view.Refresh(r);
            Assert.That(view.Contains(y), Is.True, "premise: Y passes both branches");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            // One branch fails, the other still holds — both ways, since the spawn's entries arrive in one order and a lost bit shows only when its
            // branch is the one left holding.
            var mut = tx.OpenMut(y);
            var d = mut.Read(CompDArch.D);
            if (failTheBBranch)
            {
                d.B = 30;
            }
            else
            {
                d.A = 1f;
            }

            mut.Set(CompDArch.D, d);
            tx.Commit();
        }

        using (var r = dbe.CreateQuickTransaction())
        {
            view.Refresh(r);
            Assert.That(view.Contains(y), Is.True, $"{Marker}: Y left the view though its other branch still holds");
        }
    }
}
