using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Guards the H1 per-cluster MVCC visibility summary by <b>checking</b> it rather than trusting the sites that maintain it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this fixture exists.</b> <c>ClusterMaxBornTsn</c> / <c>ClusterAnyDied</c> let the SoA scan skip its per-entity EntityMap probe for a whole cluster —
/// the single change that restored the consolidation's 4.3–6.0× headline. Five sites maintain the pair (spawn commit, WAL replay, both reopen rebuilds,
/// spatial cluster migration) and, before this fixture, none verified it: a sixth born-site added later would produce a phantom that every existing test
/// still passes. <see cref="DatabaseEngine.RunStorageIntegrityCheck"/> now recomputes the summary from the EntityMap, and the tests below assert it actually
/// catches both ways the summary can become unsound.
/// </para>
/// <para>
/// <b>Why the corruption is injected directly.</b> The failure mode being guarded is "a future site forgets to fold", which no sequence of public API calls
/// can produce today — by construction, since today's five sites are correct. Writing the unsound state into the arrays reproduces exactly the state such a
/// site would leave behind, and is the only way to prove the check fires rather than merely that it runs.
/// </para>
/// <para>
/// <b>Why the pessimistic direction is asserted too.</b> The summary is a conservative approximation: saying "probe" when probing was unnecessary is legal
/// and merely slower. A check that asserted equality would fail on healthy engines, so <see cref="PessimisticSummary_IsNotReported"/> pins the asymmetry.
/// </para>
/// </remarks>
/// <remarks>
/// <b>NonParallelizable</b> for the same reason <c>ClusterStorageTests</c> is: this fixture registers <c>ClPosition</c> / <c>ClMovement</c> into the global
/// <c>ArchetypeRegistry</c>, whose concurrent-registration race is a known flake source. Run in parallel it measurably raised the failure rate of
/// <c>EcsConcurrencyTests.ParallelSpawn_SameArchetype_AllEntitiesUnique</c>; serialized it costs ~130 ms.
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterVisibilitySummaryIntegrityTests : TestBase<ClusterVisibilitySummaryIntegrityTests>
{
    /// <summary>Spans 2 clusters at 64 slots each, so the audit iterates more than one summary entry.</summary>
    private const int EntityCount = 80;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Spawns <see cref="EntityCount"/> entities across two transactions, so the summary bounds a non-zero and a strictly larger BornTSN.</summary>
    private static EntityId[] Populate(DatabaseEngine dbe)
    {
        var ids = new EntityId[EntityCount];
        using (var first = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount / 2; i++)
            {
                ids[i] = first.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            }

            first.Commit();
        }

        using (var second = dbe.CreateQuickTransaction())
        {
            for (var i = EntityCount / 2; i < EntityCount; i++)
            {
                ids[i] = second.Spawn<ClAnt>(ClAnt.Position.Set(new ClPosition(i, 0)), ClAnt.Movement.Set(new ClMovement(0, 0)));
            }

            second.Commit();
        }

        return ids;
    }

    /// <summary>Every issue of the visibility-summary class in <paramref name="dbe"/>'s current state, with the rest of the audit's findings printed.</summary>
    private static List<StorageIntegrityIssue> VisibilityIssues(DatabaseEngine dbe, out int clustersChecked)
    {
        var report = dbe.RunStorageIntegrityCheck();
        clustersChecked = report.VisibilitySummaryClustersChecked;

        var found = new List<StorageIntegrityIssue>();
        foreach (var issue in report.Issues)
        {
            TestContext.WriteLine($"ISSUE {issue.Kind}: {issue.Detail}");
            if (issue.Kind == StorageIntegrityIssueKind.ClusterVisibilitySummaryUnsound)
            {
                found.Add(issue);
            }
        }

        return found;
    }

    /// <summary>First cluster whose summary a site has established — the one the corruption tests then make unsound.</summary>
    private static int FirstEstablishedCluster(ArchetypeClusterState cs)
    {
        for (var c = 0; c < cs.ClusterMaxBornTsn.Length; c++)
        {
            if (cs.ClusterMaxBornTsn[c] != ArchetypeClusterState.VisibilityUnknown)
            {
                return c;
            }
        }

        Assert.Fail("no cluster has an established BornTSN summary — the spawn path stopped folding into it entirely");
        return -1;
    }

    // ── The audit passes on a healthy engine, and passes on something ──────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void HealthyEngine_SummaryMatchesTheEntityMap()
    {
        using var dbe = SetupEngine();
        var ids = Populate(dbe);

        using (var destroy = dbe.CreateQuickTransaction())
        {
            destroy.Destroy(ids[0]);
            destroy.Destroy(ids[EntityCount - 1]);
            destroy.Commit();
        }

        var issues = VisibilityIssues(dbe, out var clustersChecked);

        Assert.That(issues, Is.Empty, "a live engine's maintained summary must bound every entity its EntityMap places in each cluster");
        Assert.That(clustersChecked, Is.GreaterThanOrEqualTo(2),
            "GENUINENESS: the audit must have recomputed at least the two clusters 80 entities occupy — 0 would mean it looked at nothing and passed vacuously");
    }

    // ── Both unsound directions are caught ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A summary that under-states a cluster's maximum BornTSN is what a born-site that forgot to fold leaves behind: the gate passes for readers whose
    /// snapshot predates the entity, the per-entity probe is skipped, and the scan emits an entity that does not exist yet.
    /// </summary>
    [Test]
    public void UnderstatedMaxBornTsn_IsReported()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        var cs = ClusterStateOf(dbe);
        var cluster = FirstEstablishedCluster(cs);
        var maintained = cs.ClusterMaxBornTsn[cluster];
        Assert.That(maintained, Is.GreaterThan(0), "spawned entities carry a non-zero BornTSN, so there is room to under-state the summary");
        Assert.That(VisibilityIssues(dbe, out _), Is.Empty, "baseline must be clean, or the assertion below proves nothing");

        // Exactly the state a sixth born-site that never called NoteClusterBorn would leave: "this cluster is all-genesis".
        cs.ClusterMaxBornTsn[cluster] = 0;

        var issues = VisibilityIssues(dbe, out _);
        Assert.That(issues, Has.Count.EqualTo(1), "the under-stated cluster must be reported exactly once");
        Assert.That(issues[0].Detail, Does.Contain($"cluster {cluster}").And.Contain("ClusterMaxBornTsn=0"),
            "the issue must localise the cluster and quote both the claimed and the actual bound");
    }

    /// <summary>
    /// An under-recorded death watermark is the tombstone twin: the gate passes for readers whose snapshot sits between the recorded value and the real death,
    /// and the scan misses a tombstone those readers must still see.
    /// </summary>
    /// <remarks>
    /// Stronger than the cleared-flag check it replaced (#722). A boolean could only be wrong by being absent; a maximum can also be wrong by being too small,
    /// which admits exactly the readers in the gap — so the mutation here lowers the watermark rather than clearing a bit.
    /// </remarks>
    [Test]
    public void UnderRecordedDiedWatermark_IsReported()
    {
        using var dbe = SetupEngine();
        var ids = Populate(dbe);

        // Opened BEFORE the destroy and held for the rest of the test. The death watermark exists for exactly one
        // reader — one whose snapshot predates the death and must therefore still be shown the tombstone — so the
        // scenario this test corrupts only exists while such a reader is live. Without the pin the destroyed entity's
        // EntityMap record is reclaimed as soon as the destroying transaction retires (nothing can see it any more),
        // the audit finds no dead entity in the cluster, and a lowered watermark bounds nothing: 0 issues, correctly.
        //
        // This used to pass without the pin only because the reclamation never happened — the ECS cleanup queue had no
        // production drain, so every destroyed entity's record was retained for the life of the engine (#681). The test
        // was reading a leak as if it were the steady state.
        using var snapshotOlderThanTheDeath = dbe.CreateQuickTransaction();

        using (var destroy = dbe.CreateQuickTransaction())
        {
            destroy.Destroy(ids[0]);
            destroy.Commit();
        }

        var cs = ClusterStateOf(dbe);
        var cluster = -1;
        for (var c = 0; c < cs.ClusterMaxDiedTsn.Length && cluster < 0; c++)
        {
            if (cs.ClusterMaxDiedTsn[c] != 0)
            {
                cluster = c;
            }
        }

        Assert.That(cluster, Is.GreaterThanOrEqualTo(0), "the destroy path must have recorded the death in the summary");
        Assert.That(VisibilityIssues(dbe, out _), Is.Empty, "baseline must be clean, or the assertion below proves nothing");

        cs.ClusterMaxDiedTsn[cluster] -= 1;   // one TSN too low: a reader at exactly that snapshot now passes the gate and misses the tombstone

        var issues = VisibilityIssues(dbe, out _);
        Assert.That(issues, Has.Count.EqualTo(1), "the cluster holding the tombstone must be reported exactly once");
        Assert.That(issues[0].Detail, Does.Contain($"cluster {cluster}").And.Contain("ClusterMaxDiedTsn"),
            "the issue must name the death watermark rather than the born bound");
    }

    // ── The fold must precede the store that publishes the slot, not merely accompany it ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The born summary must already bound the incoming entity <b>at the moment the claim returns</b> — because the claim is what publishes the occupancy bit,
    /// and a reader consumes the two together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this catches that the audit above cannot.</b> Every assertion in this fixture so far inspects a settled engine, where the fold has certainly
    /// happened by the time anything looks. The defect this test guards is purely one of ORDER: the spawn path used to publish the occupancy bit inside
    /// <c>ClaimSlot</c> and fold the watermark ~80 lines later at the call site, next to the <c>BornTSN</c> write. Between those two stores a reader that
    /// sampled the occupancy word saw the new entity's bit set while the summary still predated it, so
    /// <see cref="ArchetypeClusterState.IsClusterFullyVisibleAt"/> vouched for a cluster holding an entity born after that reader's snapshot — and
    /// <c>TryCountViaOccupancy</c>, which popcounts the word on the strength of exactly that vouch, over-counted. Both states are momentary and both settle
    /// correct, which is why 5 000 tests and a from-scratch recomputation audit all pass either way.
    /// </para>
    /// <para>
    /// <b>Why it is deterministic.</b> Racing two threads to catch the window would be flaky and would prove nothing when green. Moving the fold inside the
    /// claim makes the ordering observable single-threaded: call the claim and look before doing anything else. Restore the fold to the caller and this test
    /// fails on every run, because on return the summary would still hold its pre-claim value.
    /// </para>
    /// <para>
    /// <b>Why the claim is invoked directly.</b> No public API exposes the instant between the bit and the fold — <c>Commit</c> runs the whole of
    /// <c>FinalizeSpawns</c> before returning. The claimed slot is deliberately left orphaned (bit set, no EntityMap entry), so this test must not run the
    /// integrity check afterwards; the engine is disposed instead.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("CLUSTERVIS-01")]
    public void ClaimingASlot_BoundsTheClusterBeforeItPublishesTheOccupancyBit()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.FreeClusterHead, Is.GreaterThanOrEqualTo(0),
            "PRECONDITION: 80 entities must leave the second cluster partly free, so the claim below takes the existing-cluster CAS path and needs no ChangeSet");

        var settled = cs.ClusterMaxBornTsn[cs.FreeClusterHead];
        Assert.That(settled, Is.Not.EqualTo(ArchetypeClusterState.VisibilityUnknown), "the spawns above must have established this cluster's bound");

        // A TSN far beyond anything the engine has issued, so "the summary moved" cannot be confused with "the summary was already there".
        var incoming = settled + 1000;

        using var epoch = EpochGuard.Enter(dbe.EpochManager);   // a ChunkAccessor may only exist inside an epoch scope
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        int claimed;
        try
        {
            (claimed, _) = cs.ClaimSlot(ref accessor, null, incoming);

            // Nothing between the claim and this read. If the fold happened after the publishing store, the summary is still `settled` here.
            AssertBoundedBeforePublish(cs.ClusterMaxBornTsn[claimed], cs.IsClusterFullyVisibleAt(claimed, incoming - 1),
                cs.IsClusterFullyVisibleAt(claimed, incoming), incoming);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private const string ClusterVis01Marker = "CLUSTERVIS-01";

    /// <summary>
    /// Clause-specific substrings of the three assertions below, so each mutant proves it tripped the clause it targets.
    /// </summary>
    /// <remarks>
    /// Matching on <see cref="ClusterVis01Marker"/> alone would not: all three messages carry it, so a mutant aimed at clause 3 that in fact tripped clause 1
    /// would be indistinguishable from one that worked. That is the same failure <c>RuleMutants.AssertDetects</c> exists to exclude one level up — "it failed"
    /// is not evidence of "it failed for this reason" — and it is cheap to close, since the messages already differ.
    /// </remarks>
    private const string BoundBeforePublishMarker = "BEFORE it publishes the occupancy bit";

    /// <inheritdoc cref="BoundBeforePublishMarker"/>
    private const string GateIsBoundedMarker = "GENUINENESS";

    /// <summary>
    /// The verifier's own assertions, taken as VALUES so the mutant below drives this exact path rather than a copy of it that could drift from it.
    /// </summary>
    /// <remarks>
    /// <b>The third clause requires the caller to fold exactly <paramref name="incoming"/>, and is NOT a general property of CLUSTERVIS-01.</b> A cluster
    /// whose bound was raised by migration can legally sit above anything committed — <see cref="ArchetypeClusterState.NoteClusterBorn"/>'s own remark says
    /// so, and calls the overshoot conservative — and such a cluster fails this clause while the rule is perfectly intact. It is written down because the
    /// failure it produces reads as a regression rather than as a stale assumption, and whoever inherits it would have no way to tell the difference.
    /// <b>Do not relax it to <c>&gt;=</c>:</b> that readmits a gate which is merely switched off, which is the one thing the clause exists to exclude. A
    /// caller that cannot fold exactly should assert against its own expected bound instead of loosening this one.
    /// </remarks>
    private static void AssertBoundedBeforePublish(long summaryAtClaim, bool visibleBeforeIncoming, bool visibleAtIncoming, long incoming)
    {
        Assert.That(summaryAtClaim, Is.GreaterThanOrEqualTo(incoming),
            $"{ClusterVis01Marker}: the claim must fold the incoming BornTSN into the cluster summary BEFORE it publishes the occupancy bit");
        Assert.That(visibleBeforeIncoming, Is.False,
            $"{ClusterVis01Marker}: a reader whose snapshot predates the claimed entity must be denied the whole-cluster shortcut the instant the slot exists");
        Assert.That(visibleAtIncoming, Is.True,
            $"{ClusterVis01Marker}: GENUINENESS — a reader at or past the incoming TSN must still get the shortcut, so the gate is bounded rather than off");
    }

    /// <summary>
    /// The state a caller-side fold leaves behind: the occupancy bit published while the summary still holds what it held before the claim.
    /// </summary>
    /// <remarks>
    /// CLUSTERVIS-01 had a verifier and no mutant, so nothing showed that verifier could fail — which is the gap the rule-coverage audit lists and the
    /// reason the fixture's own remark ("5 300 tests pass with the fold on either side of the publish") is worth distrusting until something demonstrates
    /// the assertion rejects the wrong side.
    /// </remarks>
    [Test]
    [RuleMutant("CLUSTERVIS-01")]
    public void AFoldAfterThePublishingStore_IsRejected()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        var cs = ClusterStateOf(dbe);
        var head = cs.FreeClusterHead;
        Assert.That(head, Is.GreaterThanOrEqualTo(0), "PRECONDITION: the claim below must take the existing-cluster path");

        var settled = cs.ClusterMaxBornTsn[head];
        var incoming = settled + 1_000;

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var (claimed, _) = cs.ClaimSlot(ref accessor, null, incoming);

            // Roll the REAL summary back to its pre-claim value. That is precisely the state a caller-side fold leaves behind — bit published, summary
            // unmoved — so the helper is driven by engine state a violating implementation would actually produce, rather than by fabricated scalars that
            // would only prove NUnit rejects one number against another. The slot is left orphaned, so this test must not run the integrity audit.
            cs.ClusterMaxBornTsn[claimed] = settled;

            // Marker is clause 1's own text, not the shared rule id: this mutant must be shown to trip the ordering clause specifically.
            RuleMutants.AssertDetects("CLUSTERVIS-01", BoundBeforePublishMarker, () =>
                AssertBoundedBeforePublish(cs.ClusterMaxBornTsn[claimed], cs.IsClusterFullyVisibleAt(claimed, incoming - 1),
                    cs.IsClusterFullyVisibleAt(claimed, incoming), incoming));
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// A recorded DEATH denies the whole-cluster shortcut even when the born bound is intact — the clause a rolled-back summary cannot reach.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a second mutant, and why this clause specifically.</b> Clause 2 is derivable from clause 1: if <c>maxBorn &gt;= incoming</c> then it
    /// exceeds <c>incoming - 1</c>, so no state satisfies the first and fails the second — a mutant for it would be unreachable. Clause 3 is different in
    /// kind: it fails in states where both others pass, so it needs its own.</para>
    /// <para><b>Why the DIED watermark rather than an overshooting born bound.</b> An overshoot would also trip clause 3, but
    /// <see cref="ArchetypeClusterState.NoteClusterBorn"/>'s own remark calls overshoot legal and conservative — migration folds a high-water mark that no
    /// spawn moves. A mutant built on it would prove the verifier OVER-STRICT rather than genuine, which is the failure mode a mutant exists to exclude.</para>
    /// </remarks>
    [Test]
    [RuleMutant("CLUSTERVIS-01")]
    public void ARecordedDeathStillDenyingTheShortcut_IsRejected()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        var cs = ClusterStateOf(dbe);
        var head = cs.FreeClusterHead;
        Assert.That(head, Is.GreaterThanOrEqualTo(0), "PRECONDITION: the claim below must take the existing-cluster path");

        var incoming = cs.ClusterMaxBornTsn[head] + 1_000;

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var (claimed, _) = cs.ClaimSlot(ref accessor, null, incoming);
            cs.ClusterMaxDiedTsn[claimed] = long.MaxValue;   // a death nothing can have reached: the gate must deny every reader, including this one

            // Marker is clause 3's own text. Matching the shared rule id would have accepted a failure of clause 1 — which is exactly what this mutant must
            // NOT be, since clause 1 is already covered and a born bound left intact is the whole point of reaching this clause.
            RuleMutants.AssertDetects("CLUSTERVIS-01", GateIsBoundedMarker, () =>
                AssertBoundedBeforePublish(cs.ClusterMaxBornTsn[claimed], cs.IsClusterFullyVisibleAt(claimed, incoming - 1),
                    cs.IsClusterFullyVisibleAt(claimed, incoming), incoming));
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ── A peer emptying the free-cluster head mid-claim (#842, the regression #807 was filed against) ──────────────────────────────────────────────────────

    /// <summary>
    /// A claim that reads <see cref="ArchetypeClusterState.FreeClusterHead"/> must use the value it read, even when a peer empties the head immediately
    /// afterwards — never re-resolve it and claim into cluster <c>-1</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a probe rather than threads.</b> This is #842: both overloads tested the field and then re-read it for the value, and a peer filling
    /// the cluster stores <c>-1</c> between the two. Raced, it reproduced about three times in forty — a rate at which a green run proves nothing, which is
    /// exactly how the bug stayed open through two issues. <see cref="ArchetypeClusterState.ClaimSlotHeadReadProbe"/> performs the peer's store at the one
    /// instant that matters, so the case is deterministic.</para>
    /// <para><b>What failure looks like if it regresses.</b> The second read returns <c>-1</c>, and <c>NoteClusterBorn</c> rejects it by name. Before the
    /// precondition added alongside this test it was an <c>IndexOutOfRangeException</c>, which reads as "the visibility array had not grown" — the theory
    /// #807 was filed on and which cost the better part of a month.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("CLUSTERVIS-01")]
    public void APeerEmptyingTheFreeClusterHead_DoesNotMakeTheClaimResolveItTwice()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.FreeClusterHead, Is.GreaterThanOrEqualTo(0),
            "PRECONDITION: the claim must take the existing-cluster path, or the probe below fires on a path that never reads the head");

        var headBeforeClaim = cs.FreeClusterHead;
        var fired = 0;

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            // Assigned INSIDE the try. This probe mutates engine state, so a throw between the assignment and the finally would leave every later claim in
            // the process emptying its own free-cluster head. The read-only precedent in CleanBranchChangeListTests can afford a looser shape; this cannot.
            ArchetypeClusterState.ClaimSlotHeadReadProbe = state =>
            {
                // Archetype-filtered, because the hook is process-wide. An unfiltered store would empty the head of every other archetype in this process,
                // and counting fires would catch the stray one only after it had already done so.
                if (state.ArchetypeId != cs.ArchetypeId)
                {
                    return;
                }

                fired++;
                state.FreeClusterHead = -1;   // the peer that just filled this cluster
            };

            var (claimed, slot) = cs.ClaimSlot(ref accessor, null, 10_000);

            Assert.Multiple(() =>
            {
                Assert.That(fired, Is.EqualTo(1), "PRECONDITION: the probe must have fired, or this test asserts nothing about the claim path");
                Assert.That(claimed, Is.EqualTo(headBeforeClaim),
                    "PRECONDITION: the claim must have used the head it read. Falling through to a fresh allocation satisfies the assertion below without "
                    + "the single-read value ever being used, which would make this test pass while guarding nothing");
                Assert.That(claimed, Is.GreaterThanOrEqualTo(0),
                    $"{ClusterVis01Marker}: the claim resolved FreeClusterHead a second time and took the peer's -1 — the #842 defect, which reaches "
                    + "NoteClusterBorn as a negative cluster id and corrupts the occupancy word one chunk below chunk 0 when the throw does not stop it");
                Assert.That(slot, Is.GreaterThanOrEqualTo(0), "the claim must still yield a real slot");
            });
        }
        finally
        {
            accessor.Dispose();
            ArchetypeClusterState.ClaimSlotHeadReadProbe = null;
        }
    }

    /// <summary>
    /// <c>NoteClusterBorn</c> names a negative cluster id instead of surfacing it as an out-of-range index from deep inside the fold.
    /// </summary>
    [Test]
    public void ANegativeClusterId_IsRejectedByName()
    {
        // No Populate: this is a pure argument check, and 80 entities across two transactions to obtain an ArchetypeId for a message is dead weight.
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);

        var ex = Assert.Throws<InvalidOperationException>(() => cs.NoteClusterBorn(-1, 10_000));
        Assert.That(ex.Message, Does.Contain("resolved its cluster").And.Contains("#842"),
            "the message must name the caller-side cause; an IndexOutOfRangeException here reads as a grow problem and sent #807 after the wrong theory");

        // int.MaxValue is the same case with a different arithmetic route: `+ 1` overflows to int.MinValue and walks past a `< 0` guard.
        Assert.That(Assert.Throws<InvalidOperationException>(() => cs.NoteClusterBorn(int.MaxValue, 10_000)).Message, Does.Contain("only ever grows"));
        Assert.That(Assert.Throws<InvalidOperationException>(() => cs.NoteClusterDied(-1, 10_000)).Message, Does.Contain("NoteClusterDied"));
    }

    // ── The conservative direction is a legal state, not a finding ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The summary may claim LESS visibility than the data supports — an unestablished bound and a spurious death bit both cost a probe and stay correct.
    /// Reporting either would make the audit fail on healthy engines, which is how an invariant check gets disabled.
    /// </summary>
    [Test]
    public void PessimisticSummary_IsNotReported()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        var cs = ClusterStateOf(dbe);
        var cluster = FirstEstablishedCluster(cs);

        cs.ClusterMaxBornTsn[cluster] = ArchetypeClusterState.VisibilityUnknown;   // "cannot tell" — the gate rejects it outright
        cs.ClusterMaxDiedTsn[cluster] = long.MaxValue;                            // a death that never happened — costs a probe, emits nothing wrong

        Assert.That(VisibilityIssues(dbe, out _), Is.Empty, "an over-pessimistic summary is slower, never wrong, and must not be reported as a defect");
    }
}
