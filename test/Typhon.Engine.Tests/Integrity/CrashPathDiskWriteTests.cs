using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Typhon.Engine;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// OQ-6a — measured: opening a damaged database rewrites the evidence a scan would have used.
/// </summary>
/// <remarks>
/// <para>
/// <c>05 §9.6</c> asked whether a crash-path open can put re-derived state on disk before <c>RB-04</c> refuses, and
/// said the answer decides *"whether offline-first is a preference or a hard requirement"*. The reasoning on the code
/// suggested it could not: <c>RebuildEntityMapOnCrash</c> only dirties page-cache pages, and
/// <c>ResolveSuspectPrimaryPages</c> throws before <c>SealRecovery</c>. Nobody had traced eviction, so this measures.
/// </para>
/// <para>
/// <b>Result: offline-first is a hard requirement.</b> Against a database with a torn live cluster page — one an
/// offline scan calls <c>DataLoss</c> — a crash-path open rewrites roughly two dozen pages, seventeen of them
/// <c>EntityMap</c>, plus <c>Occupancy</c>, <c>Component</c>, <c>Revision</c> and a <c>Cluster</c> page. Those derived
/// structures are exactly what a cross-structure check reads as independent corroboration. One open replaces them with
/// values re-derived from the damaged database, so the corroboration is gone: the scan afterwards can only confirm
/// that the rebuild was self-consistent.
/// </para>
/// <para>
/// <b>A second thing fell out of it, and it is not a bug.</b> The open <i>succeeded</i>. The engine served a database
/// the scanner calls <c>DataLoss</c>, because <c>RB-04</c>'s detection is read-triggered — a page enters the suspect
/// set when a load CRCs it, and recovery only loads what it needs. A torn page nothing happened to touch is not
/// checked. That is the documented detection boundary rather than a defect, and this test is the first thing to
/// demonstrate it end to end. It is also the plainest possible argument for an exhaustive offline sweep: the engine
/// finds damage where it steps, and a scanner finds it everywhere.
/// </para>
/// <para>
/// The assertions below pin the measurement in both directions, so that an engine change which stops writing on this
/// path — or which starts refusing the open — surfaces here as a failing test rather than as a design document that
/// has quietly gone out of date.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class CrashPathDiskWriteTests : IntegrityFixtureBase
{
    /// <summary>A page that changed during the open, with the segment kind that owns it.</summary>
    private readonly record struct ChangedPage(int PageIndex, string OwnerKind);

    [Test]
    [CancelAfter(60_000)]
    public void CrashPathOpen_RewritesDerivedStructures_SoAScanMustComeFirst()
    {
        RunProbe(minimumCache: false);
    }

    /// <summary>
    /// The same probe with the page cache at its floor, so eviction is forced rather than hoped for.
    /// </summary>
    /// <remarks>
    /// This is the variant the open question actually named. A comfortable cache never evicts on a database this small,
    /// so a result there alone would show only that nothing was flushed <i>voluntarily</i> — a much weaker claim.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void CrashPathOpen_RewritesDerivedStructures_UnderEvictionPressure()
    {
        RunProbe(minimumCache: true);
    }

    private void RunProbe(bool minimumCache)
    {
        // 1. A real database, plus a map from physical page to owning segment kind, taken while an engine can be asked.
        var segmentPages = BuildAndMapSegments(out var clusterPages);
        Assert.That(clusterPages, Is.Not.Empty, "precondition: the fixture must produce a cluster segment to tear");

        // 2. Tear a live cluster page, then snapshot. The damage is ours; anything that moves later is the engine's.
        var torn = clusterPages.Last();
        DamageKit.FlipByteInPage(BundlePath, torn, IntegrityVerdict.DataLoss);
        var afterDamage = File.ReadAllBytes(DamageKit.DataPath(BundlePath));

        var damagedScan = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        TestContext.Out.WriteLine($"cluster pages [{string.Join(", ", clusterPages)}]; tore {torn}");
        TestContext.Out.WriteLine($"offline scan: {damagedScan.Verdict} "
            + $"[{string.Join(", ", damagedScan.Findings.Select(f => f.Code + "@" + f.Locus))}]");

        Assert.That(damagedScan.Verdict, Is.EqualTo(IntegrityVerdict.DataLoss),
            "precondition: the tear must land on a live cluster page, or this probe is aimed at the wrong page");
        Assert.That(damagedScan.Identity.CleanShutdown, Is.False,
            "precondition: the database must be unclean, or the open takes the clean path and not the one in question");

        // 3. Open it, and record what that costs.
        var opened = TryOpen(minimumCache, out var failure);
        TestContext.Out.WriteLine(opened
            ? "crash-path open SUCCEEDED over a torn live cluster page"
            : $"crash-path open failed with {failure?.GetType().Name}: {failure?.Message}");

        var after = File.ReadAllBytes(DamageKit.DataPath(BundlePath));
        var changed = DiffPages(afterDamage, after, segmentPages);
        foreach (var c in changed)
        {
            TestContext.Out.WriteLine($"  page {c.PageIndex} ({c.OwnerKind}) rewritten");
        }

        // 4. The answer to OQ-6a, asserted so it cannot rot.
        Assert.That(changed, Is.Not.Empty,
            "OQ-6a: if an open over a damaged database wrote nothing, offline-first would be a preference. "
            + "It is not — see the fixture summary.");

        var entityMapPages = changed.Where(c => c.OwnerKind == nameof(StorageSegmentKind.EntityMap)).ToArray();
        Assert.That(entityMapPages, Is.Not.Empty,
            "the EntityMap is the derived structure a cross-structure check leans on hardest; if the open rewrites it, "
            + "a scan run afterwards is reading the rebuild rather than the evidence");

        TestContext.Out.WriteLine(
            $"OQ-6a ANSWER: an open rewrote {changed.Count} page(s), {entityMapPages.Length} of them EntityMap. "
            + "Offline-first is a HARD REQUIREMENT: scan before opening, or lose the independent evidence.");
    }

    /// <summary>
    /// The engine's detection is read-triggered; an untouched torn page is not checked at open.
    /// </summary>
    /// <remarks>
    /// Split out from the probe because it is a separate claim about the engine rather than about the scanner, and
    /// because it is the sharpest one-line case for an exhaustive offline sweep: the offline scan calls this database
    /// <c>DataLoss</c> and the engine opens it without complaint. Consistent with <c>RB-04</c>'s documented boundary
    /// (detection is via CRC mismatch on load, and recovery loads only what it needs) — demonstrated here rather than
    /// argued.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void ATornLiveClusterPageDoesNotByItselfStopAnOpen()
    {
        BuildAndMapSegments(out var clusterPages);
        DamageKit.FlipByteInPage(BundlePath, clusterPages.Last(), IntegrityVerdict.DataLoss);

        var scan = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(scan.Verdict, Is.EqualTo(IntegrityVerdict.DataLoss), "the scanner sees the damage");

        var opened = TryOpen(minimumCache: false, out _);

        Assert.That(opened, Is.True,
            "recorded, not endorsed: the engine opens a database the scanner calls DataLoss, because a page nothing "
            + "reads during recovery is never CRC-checked. If this ever starts failing, RB-04's reach has changed and "
            + "the offline sweep's rationale should be revisited.");
    }

    /// <summary>Builds the database, records segment ownership per page, leaves a WAL delta, then crashes the engine.</summary>
    private Dictionary<int, string> BuildAndMapSegments(out List<int> clusterPages)
    {
        var map = new Dictionary<int, string>();
        var clusters = new List<int>();

        using (var scope = Provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 128; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            // Checkpoint so the cluster pages exist on disk to be torn, then commit again so recovery has a WAL delta
            // to replay. Without the delta the crash path has no work, and the open under test would not be the one
            // OQ-6a is about.
            dbe.ForceCheckpoint();
            for (var i = 0; i < 64; i++)
            {
                using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
                using var tx = uow.CreateTransaction();
                var comp = new CompA(1000 + i, i, i);
                tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                tx.Commit();
                uow.Flush();
            }

            foreach (var seg in dbe.EnumerateStorageSegments())
            {
                foreach (var page in seg.Pages.Span)
                {
                    map[page] = seg.Kind.ToString();
                    if (seg.Kind == StorageSegmentKind.Cluster)
                    {
                        clusters.Add(page);
                    }
                }
            }

            // Leaves the clean-shutdown flag clear, so the next open takes the crash path rather than the clean one.
            dbe.SimulateHardCrash();
        }

        CloseEngine();
        clusterPages = clusters;
        return map;
    }

    /// <summary>Attempts an open; returns whether it succeeded and, if not, what it threw.</summary>
    private bool TryOpen(bool minimumCache, out Exception failure)
    {
        failure = null;
        ServiceProvider provider = null;
        try
        {
            provider = minimumCache ? ReopenProviderWithMinimumCache() : ReopenProvider();
            using var scope = provider.CreateScope();
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
        finally
        {
            try
            {
                provider?.Dispose();
            }
            catch
            {
                // A failed open can leave disposal unhappy; that is not what this test measures.
            }
        }
    }

    /// <summary>Page indices whose bytes differ, annotated with the segment kind that owned them.</summary>
    private static List<ChangedPage> DiffPages(byte[] before, byte[] after, Dictionary<int, string> segmentPages)
    {
        var changed = new List<ChangedPage>();
        var pages = Math.Min(before.Length, after.Length) / IntegrityConstants.PageSize;

        for (var p = 0; p < pages; p++)
        {
            var offset = p * IntegrityConstants.PageSize;
            if (!before.AsSpan(offset, IntegrityConstants.PageSize).SequenceEqual(after.AsSpan(offset, IntegrityConstants.PageSize)))
            {
                changed.Add(new ChangedPage(p, segmentPages.GetValueOrDefault(p, "unattributed")));
            }
        }

        return changed;
    }
}
