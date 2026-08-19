using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

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
            Assert.That(cs.ClusterMaxBornTsn[claimed], Is.GreaterThanOrEqualTo(incoming),
                "the claim must fold the incoming BornTSN into the cluster summary BEFORE it publishes the occupancy bit");
            Assert.That(cs.IsClusterFullyVisibleAt(claimed, incoming - 1), Is.False,
                "a reader whose snapshot predates the claimed entity must be denied the whole-cluster shortcut the instant the slot exists");
            Assert.That(cs.IsClusterFullyVisibleAt(claimed, incoming), Is.True,
                "GENUINENESS: a reader at or past the incoming TSN must still get the shortcut — the gate must be bounded, not merely switched off");
        }
        finally
        {
            accessor.Dispose();
        }
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
