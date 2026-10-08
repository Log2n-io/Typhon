using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// The <c>MAP</c> family: the directory walk survives a damaged pointer and reports it, and stays quiet when clean.
/// </summary>
/// <remarks>
/// <c>MAP-04</c> is the check the catalogue calls "a hard requirement on the traversal code, not merely a finding" —
/// so the load-bearing assertion in the damage case is that the scan <b>completes</b>, not merely that it reports.
/// </remarks>
[TestFixture]
internal sealed class EntityMapChecksTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void AHealthyDatabaseDrawsNoEntityMapFinding()
    {
        BuildHealthyDatabase();

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code.StartsWith("CHK-MAP-", StringComparison.Ordinal)), Is.Empty,
            "the entity-map family fired on an undamaged database:\n" + IntegrityReportText.Render(report));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnOverflowPointerOutsideTheSegmentIsReportedAndNotFollowed()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.RedirectEntityMapOverflowPointer(BundlePath);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.Single(f => f.Code == "CHK-MAP-04");
        Assert.That(finding.RuleId, Is.EqualTo("RB-01"));
        Assert.That(finding.Repair, Is.EqualTo(Repairability.Lossless),
            "an EntityMap is derived from the cluster, so rebuilding it costs nothing");
        Assert.That(finding.Detail, Does.Contain("NOT followed"),
            "the finding must state that the damaged pointer was not dereferenced — that is MAP-04's actual requirement");
    }

    /// <summary>
    /// A map large enough to have split and to chain draws no finding either: the overflow checks — link ranges, owner tags, every chunk past the
    /// buckets reached exactly once, the count — only run on a map that has overflow chunks, which the 64-entity fixture never does.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void AHealthyMapThatHasSplitAndChained_DrawsNoEntityMapFinding()
    {
        BuildHealthyDatabase(6_000, perTransaction: 500);
        var shape = DamageKit.EntityMapShape(BundlePath);
        Assert.That(shape.BucketCount, Is.GreaterThan(DatabaseEngine.EntityMapInitialBuckets), "premise: the map split");
        Assert.That(shape.ChainedBuckets, Is.GreaterThan(0), "premise: some bucket chains to an overflow chunk");

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code.StartsWith("CHK-MAP-", StringComparison.Ordinal)), Is.Empty,
            "the entity-map family fired on an undamaged database:\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// A file an unclean close left is not judged: a checkpoint may have captured its map mid-split, and the open rebuilds the map anyway. The same
    /// leaked chunk a clean file reports draws no finding here — the walk still runs, and says in a caveat what it set aside.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnUncleanClose_SetsTheMapsStructureAside()
    {
        BuildHealthyDatabase(uncleanClose: true);
        DamageKit.BreakEntityMap(BundlePath, DamageKit.EntityMapBreak.LeakedChunk);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Findings.Where(f => f.Code is EntityMapChecks.PointersResolve or EntityMapChecks.StructureHolds), Is.Empty,
            "a crash's file is not judged:\n" + IntegrityReportText.Render(report));
        Assert.That(report.Limits.Caveats, Has.Some.Contains("structure not judged"), "the walk says what it set aside");
    }

    /// <summary>
    /// Each structural fault EMAP-01 rules out is reported, as exactly the finding it is: a header the engine refuses, a link to the meta, before the
    /// chunks or into another bucket's primary — whose latch word reads as the right owner tag, so only the range check sees it — a leaked chunk, a
    /// miscount, a free bucket chunk.
    /// </summary>
    [TestCase(DamageKit.EntityMapBreak.MetaFormat)]
    [TestCase(DamageKit.EntityMapBreak.MetaN0)]
    [TestCase(DamageKit.EntityMapBreak.LinkToMeta)]
    [TestCase(DamageKit.EntityMapBreak.LinkBeforeTheChunks)]
    [TestCase(DamageKit.EntityMapBreak.LinkIntoTheBuckets)]
    [TestCase(DamageKit.EntityMapBreak.LeakedChunk)]
    [TestCase(DamageKit.EntityMapBreak.EntryCount)]
    [TestCase(DamageKit.EntityMapBreak.FreeBucket)]
    [TestCase(DamageKit.EntityMapBreak.Cycle)]
    [TestCase(DamageKit.EntityMapBreak.OverfullBucket)]
    [CancelAfter(30_000)]
    public void AStructuralFaultIsReported(DamageKit.EntityMapBreak how)
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakEntityMap(BundlePath, how);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);
        Assert.That(report.Findings.Where(f => f.Code.StartsWith("CHK-MAP-", StringComparison.Ordinal)).All(f => f.Repair == Repairability.Lossless),
            Is.True, "an EntityMap is derived from the cluster, so rebuilding it costs nothing");

        // Two shapes share MAP-05 with a finding they also cause (an over-full bucket miscounts, a cycle is no leak): their own must be there.
        var own = how switch
        {
            DamageKit.EntityMapBreak.OverfullBucket => "more entries than a chunk holds",
            DamageKit.EntityMapBreak.Cycle => "reaches a chunk twice",
            _ => null
        };
        if (own != null)
        {
            Assert.That(report.Findings.Select(f => f.Summary), Has.Some.Contains(own), IntegrityReportText.Render(report));
        }
    }

    /// <summary>An overflow chunk naming another bucket as owner — the move a split relies on would defer for good.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void AMisownedOverflowChunkIsReported()
    {
        BuildHealthyDatabase(6_000, perTransaction: 500);
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.BreakEntityMap(BundlePath, DamageKit.EntityMapBreak.MisownedOverflow);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        DamageKit.AssertDetectedExactly(DamageKit.Scan(BundlePath, ScanDepth.Deep), damage);
    }

    /// <summary>
    /// A repair's recovery open (<see cref="DatabaseEngineOptions.ForceCrashRecoveryAtOpen"/>) rebuilds a map whose meta the engine cannot use, the
    /// state the scan calls lossless: the open used to tolerate an unusable meta only after an unclean close, so this one refused, and the repair the
    /// plan promised could not run. Every entity is reachable through the rebuilt map.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ARecoveryOpen_RebuildsAMapWhoseMetaItCannotUse()
    {
        BuildHealthyDatabase();
        DamageKit.BreakEntityMap(BundlePath, DamageKit.EntityMapBreak.MetaFormat);

        using (var provider = ReopenProviderForcingRecovery())
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            Assert.DoesNotThrow(() => dbe.InitializeArchetypes(), "the recovery open must rebuild the map, not refuse its meta");
            dbe.ForceCheckpoint();
        }

        var after = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(after.Findings.Where(f => f.Code.StartsWith("CHK-MAP-", StringComparison.Ordinal)), Is.Empty,
            "every entity reachable through the rebuilt map, and the map sound:\n" + IntegrityReportText.Render(after));
    }

    /// <summary>
    /// A session that only despawns still persists the map's entry count. The checkpoint flushed it only on a cycle where the next entity key or a
    /// segment root changed — a despawn changes neither — so the count on disk stayed at the spawn total after a clean close.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ADespawnOnlySession_PersistsTheEntryCount()
    {
        var ids = new List<EntityId>();
        BuildHealthyDatabase(spawned: ids);

        using (var provider = ReopenProvider())
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
            using (var tx = uow.CreateTransaction())
            {
                for (var i = 0; i < 10; i++)
                {
                    tx.Destroy(ids[i]);
                }

                tx.Commit();
            }

            uow.Flush();
        }

        Assert.That(DamageKit.EntityMapShape(BundlePath).EntryCount, Is.EqualTo(ids.Count - 10), "the meta's count after the despawns");
        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(report.Findings.Where(f => f.Code == EntityMapChecks.StructureHolds), Is.Empty,
            "the meta's count must be the chains' after the despawns:\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// The walk recovers <b>every</b> entry of a healthy map — the property MAP-02 is worthless without.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion whose absence cost two rounds of debugging. An earlier walk returned a strict subset of a
    /// healthy map and there was no test that could tell: <c>MAP-01</c> passes trivially on a subset (everything found
    /// is real), <c>MAP-03</c> passes (fewer entries cannot collide), <c>MAP-04</c> passes (no pointer was bad). Only
    /// counting against a known population sees it, and the count has to come from outside the walk.
    /// </para>
    /// <para>
    /// The cause was not the bucket layout it was blamed on. The cursor handed back a span over one reused page buffer,
    /// and the nested bucket reads rewrote the directory the outer loop was still iterating — so 27 of 256 buckets were
    /// visited and four keys of sixty-four came back.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void EveryLiveEntityIsFoundInTheMap()
    {
        const int Entities = 64;
        BuildHealthyDatabase(Entities);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(report.Limits.ChecksSkipped.Any(s => s.Contains("CHK-MAP-01", StringComparison.Ordinal)), Is.False,
            "MAP-01/02 must actually run:\n" + IntegrityReportText.Render(report));

        // MAP-02 fires when a live cluster entity has no map entry. On a healthy database of known population, its
        // silence IS the statement that all 64 were found — a walk that lost even one would report it.
        Assert.That(report.Findings.Where(f => f.Code is "CHK-MAP-01" or "CHK-MAP-02"), Is.Empty,
            $"all {Entities} entities must be reachable through the map:\n" + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// An entity missing from the cluster is reported as an orphaned map entry.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnEntryForAnEntityTheClusterLostIsReported()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        // Zeroing a live slot's key removes that identity from the cluster while leaving the map's entry for it — the
        // exact shape MAP-01 exists to name.
        var damage = DamageKit.BreakClusterSlot(BundlePath, DamageKit.ClusterBreak.ClearLiveKey);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.First(f => f.Code == "CHK-MAP-01");
        Assert.That(finding.Repair, Is.EqualTo(Repairability.Lossless),
            "the map is derived from the cluster, so rebuilding it loses nothing");
    }

    /// <summary>
    /// A live entity absent from the map is reported — the reverse direction, which forward-only checking cannot see.
    /// </summary>
    /// <remarks>
    /// Both directions are required and shipping only <c>MAP-01</c> is the classic mistake
    /// (<c>03 §7</c>): a map missing half its entries satisfies forward-only checking completely, and that is precisely
    /// what a rebuild over pre-apply state produces — <c>RB-02</c>'s failure mode.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ALiveEntityAbsentFromTheMapIsReported()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        // Raising a live key past the watermark gives the cluster an identity the map has never heard of, and strands
        // the map's entry for the old one: the disagreement appears in both directions at once.
        var damage = DamageKit.BreakClusterSlot(BundlePath, DamageKit.ClusterBreak.KeyAboveWatermark);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var finding = report.Findings.First(f => f.Code == "CHK-MAP-02");
        Assert.That(finding.RuleId, Is.EqualTo("RB-02"));
        Assert.That(finding.Detail, Does.Contain("unfindable"),
            "the finding must say what the operator loses: the entity is present but not reachable by id");
    }
}
