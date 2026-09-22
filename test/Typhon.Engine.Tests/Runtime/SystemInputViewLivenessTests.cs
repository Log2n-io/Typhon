using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Issue #718 — a View built from an unfiltered <c>Query&lt;T&gt;().ToView()</c> is a snapshot taken at construction, not a live set.
/// </summary>
/// <remarks>
/// <para>
/// <c>EcsQuery.ToPullView</c> populates the view once and registers it with no <c>ViewRegistry</c>, unlike <c>ToIncrementalView</c>, which does. A system
/// fed such a view therefore runs against the membership that existed when the view was built, for the entire life of the runtime — so no system ever
/// processes an entity spawned after startup. Silent: the system runs every tick and reports a plausible entity count.
/// </para>
/// <para>
/// Found while root-causing #631, and it is that issue's actual cause. Kept here rather than in the issue text because the whole class of defect exists
/// only because no fixture anywhere spawns an entity while a runtime is ticking — every view test spawns first and creates the view second.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SystemInputViewLivenessTests : TestBase<SystemInputViewLivenessTests>
{
    // Quarantined against #893: the spawn below is committed from the TEST thread against a runtime ticking at 1 kHz, which is an EW-01 violation by
    // construction — 5 runs in 40 in Release, green in Debug only because the timing rarely overlaps there. The guard is right and the fixture is the
    // messenger: there is no handshake today for an outside committer to be admitted between ticks. Un-quarantine when #893 settles which way that goes.
    [Test]
    [Category("Quarantine")]
    [VerifiesRule("BIND-04")]
    public void SystemInputView_SeesEntitiesSpawnedWhileTheRuntimeIsRunning()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();
        Assert.That(view.Count, Is.EqualTo(10), "PREMISE: the view is populated at creation");

        var ticksSeen = 0;
        var lastSeen = -1;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Walk", ctx => Volatile.Write(ref lastSeen, ctx.Entities.Count), input: () => view, parallel: true, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
            Assert.That(Volatile.Read(ref lastSeen), Is.EqualTo(10), "PREMISE: the system sees the entities that existed when its view was built");

            // Spawn while the runtime is ticking — the ordering a simulation actually produces, and the one no fixture covers.
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 10; i < 20; i++)
                {
                    var v = new TouchPos { X = i };
                    tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
                }

                tx.Commit();
            }

            var target = ticksSeen + 5;
            SpinWait.SpinUntil(() => ticksSeen >= target, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }

        Assert.That(Volatile.Read(ref lastSeen), Is.EqualTo(20),
            "a system's input view must include entities spawned while the runtime runs — measured: it stays at 10 forever, so those entities are "
            + "committed, durable and queryable, and no system will ever touch them");

        view.Dispose();
    }

    /// <summary>The same mechanism without a runtime: two views over one engine, differing only in when they were built.</summary>
    /// <remarks>
    /// <para>
    /// This pins the ENDPOINT, not the interim. The runtime re-queries the pull views it feeds to systems once per tick, which is what makes the test above
    /// pass; that does nothing for a view held by user code. #790's membership channel is what makes this one converge: an archetype-only query subscribes to
    /// its archetypes' membership channels, and a commit that spawns or destroys publishes to every subscriber.
    /// </para>
    /// <para>
    /// <b>It asserts convergence after ONE refresh, not without one.</b> The original form asserted the counts matched with no refresh at all, which #722
    /// withdrew: a view holds no transaction (ADR-042), so it has no snapshot to become live against, and giving it one would pin the TransactionChain for
    /// the view's whole lifetime. What was actually wrong was the COST of that refresh — a whole-archetype rescan plus a full set-difference — and the second
    /// assertion is what pins the fix, because convergence alone would still pass on the re-query this replaces.
    /// </para>
    /// </remarks>
    [Test]
    public void ViewCreatedBeforeTheSpawns_ConvergesWithOneCreatedAfter()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using var txEarly = dbe.CreateQuickTransaction();
        var early = txEarly.Query<TouchArch>().ToView();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var txLate = dbe.CreateQuickTransaction();
        var late = txLate.Query<TouchArch>().ToView();
        Assert.That(late.Count, Is.EqualTo(10), "PREMISE: a view built after the spawns sees them");

        QueryPathProbe.Reset();
        using (var txRefresh = dbe.CreateQuickTransaction())
        {
            early.Refresh(txRefresh);
        }

        Assert.Multiple(() =>
        {
            Assert.That(early.Count, Is.EqualTo(late.Count),
                "one refresh must converge a view built before the spawns with one built after — before #790 the early view stayed at 0 forever");
            Assert.That(QueryPathProbe.ViewRequeries, Is.EqualTo(1),
                "and it converges by RE-QUERYING, not by draining: the seed is taken at the creating transaction's snapshot and cannot be trusted, so the "
                + "first refresh resynchronises. Draining here would mean the view believed a seed it had no basis to believe");
        });

        // From here the view is anchored and the channel takes over — which is the half that has to be asserted separately, because convergence
        // alone is satisfied by the O(N) rescan this feature exists to remove.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var v = new TouchPos { X = 99 };
            tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        QueryPathProbe.Reset();
        using (var txRefresh2 = dbe.CreateQuickTransaction())
        {
            early.Refresh(txRefresh2);
        }

        Assert.Multiple(() =>
        {
            Assert.That(early.Count, Is.EqualTo(11), "the eleventh entity reaches the view");
            Assert.That(QueryPathProbe.MembershipDrains, Is.EqualTo(1), "via the channel");
            Assert.That(QueryPathProbe.ViewRequeries, Is.Zero, "with no rescan");
        });

        early.Dispose();
        late.Dispose();
    }

    /// <summary>A refresh over an archetype nothing has touched must not read the channel or the archetype at all.</summary>
    /// <remarks>
    /// The headline property of #790, and the one a timing assertion cannot pin: a simulation holds tens of views and most of their archetypes are untouched on
    /// any given tick, so what decides the per-tick cost is whether an idle view can answer "nothing changed" without looking at anything. Counting the branch
    /// is the only way to assert that durably — "it was fast" would still pass the day the gate stops firing.
    /// </remarks>
    [Test]
    public void MembershipRefresh_OnAQuietArchetype_TakesTheEpochGate()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();

        // First refresh may legitimately drain: the view subscribed before its own population scan, so entries can overlap what the scan already added.
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }

        QueryPathProbe.Reset();
        for (var i = 0; i < 5; i++)
        {
            using var quiet = dbe.CreateQuickTransaction();
            view.Refresh(quiet);
        }

        Assert.Multiple(() =>
        {
            Assert.That(view.Count, Is.EqualTo(10), "PREMISE: the view still holds the entities across the quiet refreshes");
            Assert.That(QueryPathProbe.MembershipGateHits, Is.EqualTo(5), "every refresh over an untouched archetype must short-circuit on the epoch");
            Assert.That(QueryPathProbe.MembershipDrains, Is.Zero, "a quiet refresh must not touch the ring buffer");
            Assert.That(QueryPathProbe.ViewRequeries, Is.Zero, "and must not re-run the query");
        });

        view.Dispose();
    }

    /// <summary>Spawn and destroy of the same entity inside one transaction nets to no membership change.</summary>
    [Test]
    public void MembershipRefresh_SpawnAndDestroyInOneTransaction_YieldsNoDelta()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();

        // Anchor first, so the assertions below observe the channel rather than the seeding resync.
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }
        view.ClearDelta();

        // A SURVIVOR alongside the netted-out pair. Without it every assertion is "is Zero", which a channel that publishes nothing at all
        // satisfies just as well as a correct one — "nothing happened" and "the right nothing happened" would be indistinguishable.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var doomed = new TouchPos { X = 1 };
            var id = tx.Spawn<TouchArch>(TouchArch.Pos.Set(in doomed));
            tx.Destroy(id);

            var survivor = new TouchPos { X = 2 };
            tx.Spawn<TouchArch>(TouchArch.Pos.Set(in survivor));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        QueryPathProbe.Reset();
        using (var refresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(refresh);
        }

        var delta = view.GetDelta();
        Assert.Multiple(() =>
        {
            Assert.That(view.Count, Is.EqualTo(1), "the survivor is in; the entity spawned and destroyed in one transaction never existed as far as "
                + "membership is concerned");
            Assert.That(delta.Added.Count, Is.EqualTo(1), "exactly one addition is reported");
            Assert.That(delta.Removed.Count, Is.Zero, "and no removal — the view never held the netted-out entity");
            Assert.That(QueryPathProbe.MembershipDrains, Is.EqualTo(1), "and the channel is what delivered it, not a rescan");
        });

        view.Dispose();
    }

    /// <summary>A destroy reaches a membership view, not just a spawn.</summary>
    [Test]
    public void MembershipRefresh_Destroy_RemovesFromTheView()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        var ids = new System.Collections.Generic.List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 6; i++)
            {
                var v = new TouchPos { X = i };
                ids.Add(tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();
        Assert.That(view.Count, Is.EqualTo(6), "PREMISE");

        // The first refresh always resynchronises (it is the seed's only guarantee of correctness), so anchor the view before measuring which
        // path the destroy takes.
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[0]);
            tx.Destroy(ids[1]);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        QueryPathProbe.Reset();
        view.ClearDelta();
        using (var refresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(refresh);
        }

        Assert.Multiple(() =>
        {
            Assert.That(view.Count, Is.EqualTo(4), "the two destroyed entities must leave the view");
            Assert.That(view.Contains(ids[0]), Is.False);
            Assert.That(view.Contains(ids[1]), Is.False);
            Assert.That(QueryPathProbe.ViewRequeries, Is.Zero, "via the channel, not a rescan");
        });

        view.Dispose();
    }

    /// <summary>A commit landing between the view transaction's snapshot and ToView() must not be lost.</summary>
    /// <remarks>
    /// Regression for the defect that made the first version of #790 unshippable. The seed runs at the CREATING transaction's fixed TSN, so a
    /// commit after that snapshot is not in it; the subscription happens later, so that commit is not in the buffer either. With the epoch
    /// recorded at subscription time, the gate then reported "nothing changed" and those entities were gone for the life of the view — the exact
    /// text of MEMB-01's on_violation, reached by the change that claims to fix #718. The fix is that the first refresh resynchronises.
    /// </remarks>
    [Test]
    [VerifiesRule("MEMB-01")]
    public void ViewBuiltAfterACommitItsSnapshotPredates_StillConverges()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 3; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Snapshot taken HERE — before the next nine exist.
        using var txView = dbe.CreateQuickTransaction();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 3; i < 12; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        // ...and the view is built only now, so its seed sees 3 while the truth is 12.
        var view = txView.Query<TouchArch>().ToView();

        using (var refresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(refresh);
        }

        Assert.That(view.Count, Is.EqualTo(12),
            "the nine entities committed between the view transaction's snapshot and ToView() are in neither the seed nor the buffer; without the "
            + "first-refresh resync they were lost permanently and every later refresh reported 'nothing changed'");

        view.Dispose();
    }

    /// <summary>A view seeded from a transaction that then rolls back must not keep the phantom ids.</summary>
    /// <remarks>
    /// The seed runs <c>Execute()</c> on the creating transaction, which folds in that transaction's UNCOMMITTED spawns. If it rolls back, no
    /// commit ever publishes a matching deletion, so nothing on the channel can ever remove them — the pull path healed this on its next refresh
    /// because it re-executed; the channel does not. Regression for that.
    /// </remarks>
    [Test]
    public void ViewSeededFromARolledBackTransaction_DropsThePhantoms()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 2; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        EcsView<TouchArch> view;
        using (var doomed = dbe.CreateQuickTransaction())
        {
            for (var i = 2; i < 5; i++)
            {
                var v = new TouchPos { X = i };
                doomed.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }
            view = doomed.Query<TouchArch>().ToView();
            Assert.That(view.Count, Is.EqualTo(5), "PREMISE: the seed sees the creating transaction's own pending spawns");
            // no Commit — the transaction rolls back on dispose
        }

        using (var refresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(refresh);
        }

        Assert.That(view.Count, Is.EqualTo(2),
            "the three never-committed ids do not resolve in EntityMap and must not survive in the view; nothing on the channel can remove them, so "
            + "only a resync can");

        view.Dispose();
    }

    /// <summary>Refreshing against a transaction holding its own uncommitted spawns and destroys must reflect them.</summary>
    /// <remarks>
    /// The channel carries committed entries only, and uncommitted work moves no structural epoch — so the gate would short-circuit and the view
    /// would contradict the very transaction that refreshed it. <c>RefreshPull</c> folded the overlay in (pending spawns included, pending
    /// destroys excluded) and the membership path must keep doing so by falling back to it. No fixture covered this before, which is how the
    /// regression landed silently.
    /// </remarks>
    [Test]
    public void MembershipRefresh_AgainstATransactionWithItsOwnPendingWork_SeesTheOverlay()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        var ids = new System.Collections.Generic.List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 4; i++)
            {
                var v = new TouchPos { X = i };
                ids.Add(tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }
        Assert.That(view.Count, Is.EqualTo(4), "PREMISE");

        using var work = dbe.CreateQuickTransaction();
        var fresh = new TouchPos { X = 100 };
        work.Spawn<TouchArch>(TouchArch.Pos.Set(in fresh));
        work.Destroy(ids[0]);

        view.Refresh(work);

        Assert.Multiple(() =>
        {
            Assert.That(view.Count, Is.EqualTo(4), "one added, one removed, against a starting four");
            Assert.That(view.Contains(ids[0]), Is.False, "an entity this transaction destroyed must not still be in the view it just refreshed");
        });

        view.Dispose();
    }

    /// <summary>MEMB-01: the structural epoch must be released only after the entries it accounts for.</summary>
    /// <remarks>
    /// <para>
    /// <b>Deterministic, not a stress loop</b>, for the reason <c>ActiveClusterListPublicationTests</c> gives about the active-cluster pair: racing
    /// for a window this narrow does not reproduce. The interleaving is CONSTRUCTED instead — <c>QueryPathProbe.MembershipPrePublishBumpHook</c>
    /// fires on the commit thread at the one instant the rule is about, and the assertions state the rule directly: the entries are already in the
    /// subscriber's buffer, and no epoch has moved yet.
    /// </para>
    /// <para>
    /// <b>Why the randomised differential cannot do this job.</b> It is sequential — commit, fence, refresh — so it never has a reader between the
    /// bump and the appends, and moving <c>Bump()</c> above the appends leaves it green. Verified by mutation, twice. A rule marked
    /// <c>[fatal][silent]</c> whose verifier passes against its own mutant is worse than an unverified one, because the coverage ratchet then holds
    /// the count.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("MEMB-01")]
    public void EpochIsReleasedOnlyAfterTheEntriesItAccountsFor()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }

        var meta = ArchetypeRegistry.GetMetadata<TouchArch>();
        var registry = dbe._archetypeStates[meta.ArchetypeId].MembershipViews;
        var epochBefore = registry.StructuralEpoch;

        long epochAtHook = -1;
        long bufferedAtHook = -1;
        QueryPathProbe.MembershipPrePublishBumpHook = () =>
        {
            epochAtHook = registry.StructuralEpoch;
            bufferedAtHook = view.DeltaBuffer.Count;
        };
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            var v = new TouchPos { X = 7 };
            tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            tx.Commit();
        }
        finally
        {
            QueryPathProbe.MembershipPrePublishBumpHook = null;
        }

        Assert.Multiple(() =>
        {
            Assert.That(bufferedAtHook, Is.EqualTo(1),
                "PREMISE: the hook must fire after the commit's entries are in the subscriber's buffer, or it is not observing the ordering at all");
            Assert.That(epochAtHook, Is.EqualTo(epochBefore),
                "the epoch must NOT have moved while entries sit unaccounted-for in the buffer. Released first, a reader here sees the moved epoch, drains "
                + "an empty buffer, records it as consumed, and never sees these entities — silent and permanent, which is MEMB-01's on_violation");
            Assert.That(registry.StructuralEpoch, Is.GreaterThan(epochBefore), "and it must have moved by the time the commit returns");
        });

        dbe.WriteTickFence(1);
        using (var refresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(refresh);
        }
        Assert.That(view.Count, Is.EqualTo(1), "and the entity actually arrives");

        view.Dispose();
    }

    /// <summary>Shows MEMB-01's verifier can fail: the reversed publication order is detected.</summary>
    /// <remarks>
    /// The mutant is applied to the OBSERVATION, not to the engine — the hook fires at the one instant the rule constrains, and this case asserts
    /// that a bump seen there is what the verifier rejects. It exists because the previous verifier for this rule was green against a real source
    /// mutation (Bump moved above the appends), which is the failure mode <c>audit-rule-coverage.py</c>'s VERIFIER_WITHOUT_MUTANT check is for.
    /// </remarks>
    [Test]
    [RuleMutant("MEMB-01")]
    public void Memb01Verifier_RejectsAnEpochMovedBeforeItsEntries()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }

        var meta = ArchetypeRegistry.GetMetadata<TouchArch>();
        var registry = dbe._archetypeStates[meta.ArchetypeId].MembershipViews;
        var epochBefore = registry.StructuralEpoch;

        long epochAtHook = -1;
        QueryPathProbe.MembershipPrePublishBumpHook = () =>
        {
            // The mutation: move the release ahead of the entries it accounts for.
            registry.Bump();
            epochAtHook = registry.StructuralEpoch;
        };
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            var v = new TouchPos { X = 7 };
            tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            tx.Commit();
        }
        finally
        {
            QueryPathProbe.MembershipPrePublishBumpHook = null;
        }

        Assert.That(epochAtHook, Is.Not.EqualTo(epochBefore),
            "the verifier's assertion is `epochAtHook == epochBefore`; with the release moved ahead of the entries that is false, so the verifier goes red — "
            + "which is what a fatal rule needs from its coverage claim");

        view.Dispose();
    }

    /// <summary>A refresh against a transaction that then rolls back must not leave its uncommitted work in the view.</summary>
    /// <remarks>
    /// <para>
    /// The sibling of <c>ViewSeededFromARolledBackTransaction_DropsThePhantoms</c>, and it was missed because that one only covers the SEED, where
    /// <c>_needsResync</c> is already set. Refreshing against a transaction holding pending work also re-queries — deliberately, so the view reflects
    /// that transaction's overlay — but uncommitted work moves no structural epoch, so the resync's own epoch comparison finds nothing changed and
    /// would clear <c>_needsResync</c>, marking the view anchored over state that exists only in one transaction's staging.
    /// </para>
    /// <para>
    /// On rollback nothing publishes a compensating entry and no epoch ever moves, so every later refresh takes the gate and the phantoms are
    /// permanent. Reproduced at gateHits=1, requeries=0, count=5 against a truth of 2 before the fix.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("MEMB-01")]
    public void MembershipRefresh_AgainstATransactionThatRollsBack_DropsItsUncommittedWork()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        var ids = new System.Collections.Generic.List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 2; i++)
            {
                var v = new TouchPos { X = i };
                ids.Add(tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }
        Assert.That(view.Count, Is.EqualTo(2), "PREMISE: anchored on the committed two");

        using (var doomed = dbe.CreateQuickTransaction())
        {
            for (var i = 2; i < 5; i++)
            {
                var v = new TouchPos { X = i };
                doomed.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }
            doomed.Destroy(ids[0]);

            view.Refresh(doomed);
            Assert.That(view.Count, Is.EqualTo(4), "PREMISE: the refresh reflects that transaction's own overlay — 2 + 3 spawned - 1 destroyed");
            // no Commit — rolls back on dispose
        }

        QueryPathProbe.Reset();
        using (var after = dbe.CreateQuickTransaction())
        {
            view.Refresh(after);
        }

        Assert.Multiple(() =>
        {
            Assert.That(view.Count, Is.EqualTo(2), "the rolled-back transaction's spawns must not survive, and its destroy must not stick");
            Assert.That(view.Contains(ids[0]), Is.True, "the entity it pending-destroyed is still live and must come back");
            Assert.That(QueryPathProbe.MembershipGateHits, Is.Zero,
                "and the view must NOT have considered itself anchored after a resync over uncommitted state — gating there is what made the phantoms "
                + "permanent, since no epoch will ever move to reopen it");
        });

        view.Dispose();
    }



    /// <summary>MEMB-04: a view disposed mid-publish is written through LIVE memory, and its buffer is freed only once no publisher can hold it.</summary>
    /// <remarks>
    /// <para>
    /// The #864 window, finally deterministic. A publisher reads <c>reg.View.IsDisposed</c> and then writes 24 bytes through the buffer's raw
    /// pointers; the check is a filter, never a guarantee. Until the free was deferred this rule was <c>verified: NOT COVERED</c> — the exclusion was
    /// a shared/exclusive latch, so disposing from inside the publish pass was a self-deadlocking upgrade on the publishing thread and the rule
    /// forbade it outright. There was no way to be in the window.
    /// </para>
    /// <para>
    /// With the free deferred there is no latch, so the verifier is single-threaded: dispose from the hook, on the commit thread, and assert the
    /// block is still mapped when the append lands. Then assert the arithmetic — retired while a transaction is still pinned, freed once it is not.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("MEMB-04")]
    public void ViewDisposedMidPublish_IsWrittenThroughLiveMemory_AndFreedOnlyAfterTheEpochPasses()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        // The creating transaction is closed immediately and deliberately: it pins an epoch, and a pin below the retire stamp is exactly what
        // defers reclamation. Leaving it open at method scope would make the "freed once nothing is pinned" half of this test unobservable — which
        // is itself a demonstration that the watermark is doing its job.
        EcsView<TouchArch> view;
        using (var txView = dbe.CreateQuickTransaction())
        {
            view = txView.Query<TouchArch>().ToView();
        }
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }

        QueryPathProbe.Reset();
        var reclaimer = dbe.ViewBufferReclaimer;
        reclaimer.Drain();
        var freedBefore = reclaimer.FreedTotal;

        var liveAtAppend = false;
        var hookRan = false;
        QueryPathProbe.PrePublishAppendHook = () =>
        {
            if (hookRan)
            {
                return;
            }
            hookRan = true;
            view.Dispose();                                  // deregister + retire, inline, no wait
            liveAtAppend = view.DeltaBuffer.BlockIsLive;      // the append below must land in mapped memory
        };

        using (var tx = dbe.CreateQuickTransaction())
        {
            try
            {
                var v = new TouchPos { X = 1 };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
                tx.Commit();
            }
            finally
            {
                // [ThreadStatic] on a reused NUnit worker: a throw that skipped this would leak a closure capturing a DISPOSED view into every later
                // test on this thread, where it would re-dispose it and read a buffer the reclaimer may already have freed.
                QueryPathProbe.PrePublishAppendHook = null;
            }

            Assert.Multiple(() =>
            {
                Assert.That(hookRan, Is.True, "PREMISE: the hook must actually fire inside the publish pass, or this asserts nothing");
                Assert.That(liveAtAppend, Is.True,
                    "the buffer must still be MAPPED when a publisher already past its IsDisposed check writes through it — freeing at Dispose is what "
                    + "made that write silent heap corruption");
                Assert.That(reclaimer.PendingCount, Is.GreaterThan(0),
                    "and it must be RETIRED, not freed, while this transaction still pins an epoch below the retire stamp");
            });
        }

        // The transaction is gone, so nothing can still hold the registration. Now it may be freed.
        reclaimer.Drain();
        Assert.Multiple(() =>
        {
            Assert.That(reclaimer.PendingCount, Is.Zero, "once no thread is pinned below the retire stamp the block is reclaimable");
            Assert.That(reclaimer.FreedTotal, Is.EqualTo(freedBefore + 1), "and is actually freed — deferral must not become a leak");
            Assert.That(view.DeltaBuffer.BlockIsLive, Is.False, "the block is genuinely gone afterwards");
        });
    }

    /// <summary>Shows MEMB-04's verifier can fail: a disposal that frees instead of retiring is detected from inside the publish window.</summary>
    /// <remarks>
    /// The mutation is applied where the rule actually binds — the disposal path a publisher races — rather than by calling
    /// <c>ViewDeltaRingBuffer.Dispose</c> and asserting it disposed something. That earlier shape was a tautology: <c>Dispose</c> nulls the block, so
    /// the assertion could not fail no matter what <c>ViewBase.Dispose</c> did, which is precisely what a mutant must never be.
    /// </remarks>
    [Test]
    [RuleMutant("MEMB-04")]
    public void Memb04Verifier_RejectsAFreeAtDisposalTime()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        QueryPathProbe.Reset();

        EcsView<TouchArch> view;
        using (var txView = dbe.CreateQuickTransaction())
        {
            view = txView.Query<TouchArch>().ToView();
        }
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }

        var liveAtAppend = false;
        var hookRan = false;
        QueryPathProbe.PrePublishAppendHook = () =>
        {
            if (hookRan)
            {
                return;
            }
            hookRan = true;

            // THE MUTATION: the pre-#864 behaviour — free the block at disposal time instead of retiring it. Everything else is the real path.
            view.DeltaBuffer.Dispose();

            liveAtAppend = view.DeltaBuffer.BlockIsLive;
        };

        Exception faulted = null;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            var v = new TouchPos { X = 1 };
            tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            tx.Commit();
        }
        catch (Exception e)
        {
            faulted = e;
        }
        finally
        {
            QueryPathProbe.PrePublishAppendHook = null;
        }

        Assert.Multiple(() =>
        {
            Assert.That(hookRan, Is.True, "PREMISE: the mutation must be applied from inside the publish window the rule is about");
            Assert.That(liveAtAppend, Is.False,
                "the verifier's assertion is that the block is STILL MAPPED at this point; freeing at disposal makes it false, so the verifier goes red. "
                + "If this ever reads true the mutation is not reaching the path the verifier observes and the coverage claim is empty");
            Assert.That(faulted, Is.Not.Null,
                "and the mutation produces the fault the rule exists to prevent: the publisher writes through the buffer it just had freed. Here that "
                + "surfaces as a managed throw because the pointers were also nulled; in production, freeing WITHOUT nulling is the silent-corruption "
                + "case, which is why the rule forbids both");
        });

        view.Dispose();
    }

    /// <summary>Disposal must never wait, whatever else is in flight.</summary>
    /// <remarks>
    /// The latch this replaced took a full <c>DefaultCommitTimeout</c> — 30 seconds — PER ARCHETYPE on the exclusive acquire, and then freed the
    /// buffer unlatched anyway when it timed out: a liveness hazard bolted onto the memory-safety hazard it was meant to remove. Deferral has no
    /// lock to acquire, so there is nothing to wait for.
    /// </remarks>
    [Test]
    [VerifiesRule("MEMB-04")]
    public void DisposingManyViews_NeverBlocks()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        // A long-lived reader, pinned the whole time. It does NOT reproduce the deleted latch's contention — that latch was held by a commit's
        // publish pass, not by an epoch pin — so this test is about the property that survives: disposal acquires nothing, so there is nothing it
        // can wait on, whatever else is in flight. The retention it causes IS asserted below, which is the part that would regress silently.
        using var pinned = dbe.CreateQuickTransaction();

        var views = new System.Collections.Generic.List<EcsView<TouchArch>>();
        using (var txView = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 50; i++)
            {
                views.Add(txView.Query<TouchArch>().ToView());
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var v in views)
        {
            v.Dispose();
        }
        sw.Stop();

        var reclaimer = dbe.ViewBufferReclaimer;
        Assert.Multiple(() =>
        {
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)),
                $"50 disposals took {sw.ElapsedMilliseconds} ms — disposal must not wait on a publisher, and must certainly not wait out a commit timeout "
                + "per archetype only to then do the unsafe thing");
            Assert.That(reclaimer.PendingCount, Is.EqualTo(50),
                "and every one is RETAINED while that reader is still pinned below their stamps — the cost of never waiting, which must be visible");
        });

        // Releasing the pin makes them reclaimable. Without this the deferral is a leak, not a deferral.
        pinned.Dispose();
        reclaimer.Drain();
        Assert.That(reclaimer.PendingCount, Is.Zero, "once nothing is pinned below the stamps every block is freed");
    }

    /// <summary>The same mechanism covers the FIELD channel, which never had a latch at all (#864).</summary>
    /// <remarks>
    /// Asserted rather than assumed: "one mechanism for both channels" was the constraint, and the field channel's publisher — the indexed-field
    /// update path in <c>ReconcileClusterIndexAndViews</c>, which fires on every mutation of an indexed field rather than only on spawn and destroy —
    /// is the busiest publisher in the engine. Before the reclaimer this channel had no exclusion of any kind.
    /// </remarks>
    [Test]
    [VerifiesRule("MEMB-04")]
    public void FieldChannelView_DisposedMidPublish_IsAlsoWrittenThroughLiveMemory()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new CompD { A = 1f, B = 10, C = 1.0 };
            id = tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        EcsView<CompDArch> view;
        using (var txView = dbe.CreateQuickTransaction())
        {
            view = txView.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).ToView();
        }
        Assert.That(view.Count, Is.EqualTo(1), "PREMISE: the incremental view is populated");
        QueryPathProbe.Reset();

        var liveAtAppend = true;
        var hookRan = false;
        QueryPathProbe.PrePublishAppendHook = () =>
        {
            if (hookRan)
            {
                return;
            }
            hookRan = true;
            view.Dispose();
            liveAtAppend = view.DeltaBuffer.BlockIsLive;
        };

        try
        {
            using var tx = dbe.CreateQuickTransaction();
            ref var w = ref tx.OpenMut(id).Write(CompDArch.D);
            w.B = 42;
            tx.Commit();
        }
        finally
        {
            QueryPathProbe.PrePublishAppendHook = null;
        }

        Assert.Multiple(() =>
        {
            Assert.That(hookRan, Is.True, "PREMISE: the field-channel publisher must reach the hook, or this asserts nothing");
            Assert.That(liveAtAppend, Is.True,
                "a WhereField view disposed mid-publish must also be written through live memory — this channel never had the latch, so before the "
                + "reclaimer it was the wholly unguarded one");
        });
    }

    /// <summary>Differential: the channel and the re-query must produce the same membership over a randomised spawn/destroy workload.</summary>
    /// <remarks>
    /// The channel is an incremental reconstruction of something the re-query computes from scratch, so the assertion that really matters is that the two never
    /// disagree. Two views built from the identical query, one refreshed normally and one forced onto the re-query, over a workload whose spawn/destroy
    /// interleaving no hand-written case would think to produce. A single-sided test — "the view has the count I expect" — cannot catch a delta applied twice,
    /// or one dropped in a state the fixture author did not imagine.
    /// </remarks>
    [Test]
    [VerifiesRule("MEMB-01")]
    public void MembershipRefresh_AgreesWithTheReQuery_UnderRandomisedChurn()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using var txView = dbe.CreateQuickTransaction();
        var channel = txView.Query<TouchArch>().ToView();
        var oracle = txView.Query<TouchArch>().ToView();

        var rng = new Random(20260823);
        var live = new System.Collections.Generic.List<EntityId>();
        var fence = 1L;

        for (var tick = 0; tick < 40; tick++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                var spawns = rng.Next(0, 8);
                for (var i = 0; i < spawns; i++)
                {
                    var v = new TouchPos { X = tick * 100 + i };
                    live.Add(tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v)));
                }

                var destroys = live.Count == 0 ? 0 : rng.Next(0, Math.Min(5, live.Count) + 1);
                for (var i = 0; i < destroys; i++)
                {
                    var victim = rng.Next(live.Count);
                    tx.Destroy(live[victim]);
                    live.RemoveAt(victim);
                }
                tx.Commit();
            }
            dbe.WriteTickFence(fence++);

            using var refresh = dbe.CreateQuickTransaction();
            channel.Refresh(refresh);

            QueryPathProbe.ForceViewRequery = true;
            try
            {
                oracle.Refresh(refresh);
            }
            finally
            {
                // [ThreadStatic] on a reused NUnit worker: a throw that skipped this would leave every later membership test on this thread
                // silently taking the re-query branch, failing in a cascade that does not reproduce in isolation.
                QueryPathProbe.ForceViewRequery = false;
            }

            Assert.That(channel.Count, Is.EqualTo(oracle.Count), $"tick {tick}: channel and re-query disagree on size");
            foreach (var id in live)
            {
                Assert.That(channel.Contains(id), Is.True, $"tick {tick}: entity {id.RawValue} is live but absent from the channel-fed view");
            }
            Assert.That(channel.Count, Is.EqualTo(live.Count), $"tick {tick}: the channel holds entities that are not live");

            channel.ClearDelta();
            oracle.ClearDelta();
        }

        channel.Dispose();
        oracle.Dispose();
    }

    /// <summary>A burst larger than the ring buffer overflows it, and the view still lands on exact membership.</summary>
    /// <remarks>
    /// Overflow is not an error path here, it is the designed degradation: the fallback is the whole-archetype re-query, which is precisely the behaviour the
    /// channel replaces, so a mass spawn costs what it always cost and never yields a wrong set. The assertion that matters is the membership, not the mode.
    /// </remarks>
    [Test]
    public void MembershipRefresh_BurstBeyondTheRingBuffer_FallsBackAndStaysExact()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();

        // Anchor FIRST. Without this the assertions below are vacuous: every view's first refresh resynchronises by design, so ViewRequeries would
        // be 1 whether or not the burst overflowed anything, and the test would pin the seed path while claiming to pin overflow recovery.
        using (var warm = dbe.CreateQuickTransaction())
        {
            view.Refresh(warm);
        }

        // ViewDeltaRingBuffer.DefaultCapacity is 4096; spawn past it without refreshing in between.
        const int burst = 6_000;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < burst; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        QueryPathProbe.Reset();
        using (var refresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(refresh);
        }

        Assert.Multiple(() =>
        {
            Assert.That(view.Count, Is.EqualTo(burst), "membership must be exact after an overflow, not approximate");
            Assert.That(QueryPathProbe.ViewRequeries, Is.EqualTo(1), "the burst took the re-query fallback");
            Assert.That(view.HasOverflow, Is.False, "and the sticky flag is cleared by the resync, not left latched");
        });

        // And the view returns to the channel rather than staying on the slow path forever — but not on the very next refresh. A resync that
        // OBSERVED an overflow cannot settle: the dropped entries may belong to a commit whose TSN exceeds the resyncing reader's snapshot, so the
        // re-query cannot have covered them and the epochs it read cannot be trusted. It stays in resync for one more round, which a later snapshot
        // then covers. That is a deliberate extra O(N), not a latch.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var v = new TouchPos { X = -1 };
            tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var settle = dbe.CreateQuickTransaction())
        {
            view.Refresh(settle);
        }
        Assert.That(view.Count, Is.EqualTo(burst + 1), "the view keeps tracking after an overflow");

        // Now quiet: no overflow seen on the last resync, so the channel resumes.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var v = new TouchPos { X = -2 };
            tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            tx.Commit();
        }
        dbe.WriteTickFence(3);

        QueryPathProbe.Reset();
        using (var refresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(refresh);
        }

        Assert.Multiple(() =>
        {
            Assert.That(view.Count, Is.EqualTo(burst + 2), "membership stays exact");
            Assert.That(QueryPathProbe.ViewRequeries, Is.Zero,
                "and the view is back on the channel — the overflow flag is sticky and clearing it is the whole point; asserting only Count let the "
                + "comment above be false while the test stayed green");
            Assert.That(QueryPathProbe.MembershipDrains, Is.EqualTo(1), "via the channel, not another rescan");
        });

        view.Dispose();
    }

    /// <summary>
    /// A <c>.Where(lambda)</c> view must NOT be put on the membership channel or the epoch gate, because its membership changes on component writes that
    /// emit no structural event at all.
    /// </summary>
    /// <remarks>
    /// This is the boundary the whole design turns on. <c>ViewBase.IsPullMode</c> is true for three query shapes and only one of them is membership; gating
    /// the other two on the structural epoch would make them silently stale, which is strictly worse than the honest O(N) they have today. Asserting the
    /// re-query still runs is what stops a future "IsPullMode is close enough" simplification.
    /// </remarks>
    [Test]
    [VerifiesRule("MEMB-03")]
    public void WhereLambdaView_IsNotMembershipEligible_AndStillReQueries()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().Where<TouchPos>(p => p.X < 5).ToView();
        Assert.That(view.Count, Is.EqualTo(5), "PREMISE: the lambda filtered the set");

        QueryPathProbe.Reset();
        using (var refresh = dbe.CreateQuickTransaction())
        {
            view.Refresh(refresh);
        }

        Assert.Multiple(() =>
        {
            Assert.That(QueryPathProbe.MembershipGateHits, Is.Zero, "a lambda view must never take the epoch gate — an entity whose X changed produces no "
                + "spawn and no destroy, so the epoch would say 'nothing happened' while membership had in fact changed");
            Assert.That(QueryPathProbe.MembershipDrains, Is.Zero, "nor drain the membership channel");
            Assert.That(QueryPathProbe.ViewRequeries, Is.EqualTo(1), "it keeps the honest re-query");
        });

        view.Dispose();
    }
}
