using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// T6 (BKP-06, BKP-09, AC-C2, AC-C3): a point is incremental only on a trusted map and an unbroken chain. A clean close carries the lineage to the next
/// session; a crash, an offline repair, a copied bundle that fell behind its store, a wiped store or a new timeline each make the next point full.
/// </summary>
class BackupAdmissionTests : BackupTestBase<BackupAdmissionTests>
{
    private const string Marker = "BKP-06 admission";

    /// <summary>
    /// AC-C2: after a clean close and reopen the next point is incremental, and its page set is exactly the pages written between the two freezes
    /// (recorded by a write probe across the close and the open) plus the meta pair.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-06")]
    public void CleanClose_TheNextSessionsFirstPoint_IsIncremental_WithExactlyThePagesWritten()
    {
        var store = new FakeBackupStore();
        var written = new ConcurrentDictionary<int, byte>();
        void Record(int first, int count)
        {
            for (var p = first; p < first + count; p++)
            {
                written.TryAdd(p, 0);
            }
        }

        BackupPointInfo first;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var ids = Spawn(dbe, 200);
            dbe.BackupCoordinator.Probe = (phase, _) =>
            {
                if (phase == CapturePhase.AfterFreeze)
                {
                    dbe.MMF.DataFileWriteProbe = Record;
                }
            };
            first = Capture(dbe, store);
            Update(dbe, ids.Take(40), 1);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            // The data file first, so the probe sees the open's own writes too (C21).
            scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>().DataFileWriteProbe = Record;
            var dbe = Open(scope);
            Assert.That(dbe.LastOpenBackupStateResult, Is.EqualTo(BackupStateReadResult.Loaded));
            var ids = Spawn(dbe, 30, seed: 1000);
            dbe.BackupCoordinator.Options.CheckpointBeforeFreeze = false;
            dbe.BackupCoordinator.Probe = (phase, _) =>
            {
                if (phase == CapturePhase.AfterFreeze)
                {
                    dbe.MMF.DataFileWriteProbe = null;
                }
            };

            var second = Capture(dbe, store);
            var point = store.Points.Last();
            var expected = written.Keys.Append(0).Append(1).Where(p => p < point.ImagePages).Distinct().OrderBy(p => p).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(second.Kind, Is.EqualTo(BackupKind.Incremental), $"{Marker}: a clean close keeps the lineage");
                Assert.That(point.Plan.ParentId, Is.EqualTo(first.BackupId));
                Assert.That(point.Pages.Keys.OrderBy(p => p).ToArray(), Is.EqualTo(expected),
                    "the incremental holds exactly the pages written between the freezes, plus the meta pair");
            });
            _ = ids;
        }
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-06")]
    public void AHardCrash_MakesTheNextPointFull()
    {
        var store = new FakeBackupStore();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, store);
            Spawn(dbe, 20);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Spawn(dbe, 20, seed: 50);
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: the open recovered");
            AssertKind(Capture(dbe, store), BackupKind.Full, "after a crash");
        }
    }

    /// <summary>BKP-09: an offline repair writes the data file behind any engine's back, so it deletes the backup-state first.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-09")]
    public void RepairApply_DeletesTheBackupState_AndTheNextPointIsFull()
    {
        var store = new FakeBackupStore();
        string bundle;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, store);
            bundle = dbe.MMF.BundleDirectory;
        }

        var path = BackupStateFile.PathIn(bundle);
        Assert.That(File.Exists(path), Is.True, "premise: the clean close left a backup-state");

        IntegrityReport Scan()
        {
            using var source = new OfflineBundlePageSource(bundle);
            return IntegrityScanner.Scan(source, new IntegrityOptions { Depth = ScanDepth.Standard });
        }

        var badSlot = 1 - Scan().Identity.MetaSlot;
        DamageMetaSlot(bundle, badSlot);
        var plan = DatabaseRepair.Plan(Scan());
        Assert.That(plan.Steps, Is.Not.Empty, "premise: the repair has a step to apply");
        var outcome = DatabaseRepair.Apply(bundle, plan, backupFirst: false);
        Assert.That(outcome.Succeeded, Is.True);
        AssertNoBackupStateAfterAnOfflineWrite(path);

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            AssertKind(Capture(dbe, store), BackupKind.Full, "after an offline repair");
        }
    }

    /// <summary>
    /// BKP-09: the backup-state cannot be deleted (held open, no sharing). The repair refuses before its first write, or (where an open file can be
    /// unlinked) deletes it; it never writes behind a file vouched for by the clean close's nonce.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-09")]
    public void RepairApply_ThatCannotDeleteTheBackupState_RefusesBeforeWriting()
    {
        string bundle, dataPath;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, new FakeBackupStore());
            bundle = dbe.MMF.BundleDirectory;
            dataPath = dbe.MMF.DataFilePath;
        }

        var path = BackupStateFile.PathIn(bundle);
        IntegrityReport Scan()
        {
            using var source = new OfflineBundlePageSource(bundle);
            return IntegrityScanner.Scan(source, new IntegrityOptions { Depth = ScanDepth.Standard });
        }

        DamageMetaSlot(bundle, 1 - Scan().Identity.MetaSlot);
        var plan = DatabaseRepair.Plan(Scan());
        Assert.That(plan.Steps, Is.Not.Empty, "premise: the repair has a step to apply");
        var before = File.ReadAllBytes(dataPath);

        InvalidOperationException refused = null;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                DatabaseRepair.Apply(bundle, plan, backupFirst: false);
            }
            catch (InvalidOperationException e)
            {
                refused = e;
            }
        }

        if (refused != null)
        {
            Assert.That(File.ReadAllBytes(dataPath), Is.EqualTo(before), "a refused repair wrote the data file");
        }
        else
        {
            Assert.That(OperatingSystem.IsWindows(), Is.False, "Windows cannot delete a file held without sharing: the repair must have refused");
            AssertNoBackupStateAfterAnOfflineWrite(path);
        }
    }

    /// <summary>The verifier must see an offline write that left the backup-state in place.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-09")]
    public void Mutant_AnOfflineWriteThatKeepsTheFile_IsReported()
    {
        string bundle;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, new FakeBackupStore());
            bundle = dbe.MMF.BundleDirectory;
        }

        DamageMetaSlot(bundle, 1);   // the violation: a raw offline write that does not delete the file
        RuleMutants.AssertDetects("BKP-09", "BKP-09", () => AssertNoBackupStateAfterAnOfflineWrite(BackupStateFile.PathIn(bundle)));
    }

    private static void AssertNoBackupStateAfterAnOfflineWrite(string path)
        => Assert.That(File.Exists(path), Is.False, "BKP-09: an offline writer must delete the backup-state before writing");

    /// <summary>A copy of the bundle whose source went on to take a later point: the copy's last point is no longer its chain's head.</summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-06")]
    public void ACopiedBundle_BehindItsChainsHead_TakesAFullPoint()
    {
        var store = new FakeBackupStore();
        string bundle;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Capture(dbe, store);
            bundle = dbe.MMF.BundleDirectory;
        }

        var copy = bundle + ".copy";
        CopyDirectory(bundle, copy);
        try
        {
            using (var scope = ServiceProvider.CreateScope())
            {
                var dbe = Open(scope);
                Spawn(dbe, 10);
                AssertKind(Capture(dbe, store), BackupKind.Incremental, "the source keeps its chain");
            }

            // The copy takes the source's place.
            Directory.Delete(bundle, true);
            Directory.Move(copy, bundle);
            using (var scope = ServiceProvider.CreateScope())
            {
                var dbe = Open(scope);
                Assert.That(dbe.LastOpenBackupStateResult, Is.EqualTo(BackupStateReadResult.Loaded), "premise: the copy's own state is valid");
                AssertKind(Capture(dbe, store), BackupKind.Full, "a copy whose last point is not its chain's head");
            }
        }
        finally
        {
            if (Directory.Exists(copy))
            {
                Directory.Delete(copy, true);
            }
        }
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-06")]
    public void AWipedStore_OrANewTimeline_TakesAFullPoint()
    {
        var store = new FakeBackupStore();
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Capture(dbe, store);
        AssertKind(Capture(dbe, store), BackupKind.Incremental, "premise: a trusted session on an intact chain");

        store.Wipe();
        AssertKind(Capture(dbe, store), BackupKind.Full, "into a wiped store");
        AssertKind(Capture(dbe, store), BackupKind.Incremental, "premise: the new chain");

        dbe.AdoptTimeline(Guid.NewGuid());
        AssertKind(Capture(dbe, store), BackupKind.Full, "on a new timeline");
    }

    /// <summary>The decision itself, against a store whose answers the test controls.</summary>
    [Test]
    [VerifiesRule("BKP-06")]
    public void Decide_IsIncrementalOnlyOnATrustedMapAndAnIntactChain()
    {
        AssertDecisions(BackupAdmission.Decide);
    }

    /// <summary>A decision that ignores the map's trust must be caught by the matrix above.</summary>
    [Test]
    [RuleMutant("BKP-06")]
    public void Mutant_ADecisionIgnoringTrust_IsReported()
    {
        RuleMutants.AssertDetects("BKP-06", Marker, () => AssertDecisions((BackupKind requested, BackupLineage lineage, IBackupStoreSession session,
            Guid timeline, out BackupChainHead parent) =>
        {
            var trusted = lineage.Trusted;
            lineage.Trusted = true;
            try
            {
                return BackupAdmission.Decide(requested, lineage, session, timeline, out parent);
            }
            finally
            {
                lineage.Trusted = trusted;
            }
        }));
    }

    private delegate BackupKind DecideFn(BackupKind requested, BackupLineage lineage, IBackupStoreSession session, Guid timeline, out BackupChainHead parent);

    private static void AssertDecisions(DecideFn decide)
    {
        var timeline = Guid.NewGuid();
        var store = new FakeBackupStore();
        using var session = store.OpenSession(Guid.NewGuid(), CancellationToken.None);
        var sink = (CapturingSink)session.BeginPoint(new BackupPointPlan(store.DatabaseId, timeline, BackupKind.Full, Guid.Empty, 0, 0, "", false));
        sink.SetImage(2, 0, 0);
        sink.AddPage(0, new byte[PagedMMF.PageSize], false);
        sink.AddPage(1, new byte[PagedMMF.PageSize], false);
        var head = sink.Complete(10, 0);

        BackupLineage Lineage(bool trusted = true, Guid? storeId = null, uint last = 0) => new()
        {
            Trusted = trusted,
            StoreId = storeId ?? store.StoreId,
            ChainId = head.ChainId,
            LastBackupId = last == 0 ? head.BackupId : last,
            LastPointLsn = 10,
        };

        Assert.Multiple(() =>
        {
            Assert.That(decide(BackupKind.Incremental, Lineage(), session, timeline, out var parent), Is.EqualTo(BackupKind.Incremental));
            Assert.That(parent.BackupId, Is.EqualTo(head.BackupId));
            Assert.That(decide(BackupKind.Full, Lineage(), session, timeline, out _), Is.EqualTo(BackupKind.Full), $"{Marker}: a full point was asked for");
            Assert.That(decide(BackupKind.Incremental, Lineage(trusted: false), session, timeline, out _), Is.EqualTo(BackupKind.Full),
                $"{Marker}: an untrusted map");
            Assert.That(decide(BackupKind.Incremental, Lineage(storeId: Guid.NewGuid()), session, timeline, out _), Is.EqualTo(BackupKind.Full),
                $"{Marker}: another store");
            Assert.That(decide(BackupKind.Incremental, Lineage(last: head.BackupId + 5), session, timeline, out _), Is.EqualTo(BackupKind.Full),
                $"{Marker}: the chain's head is another point");
            Assert.That(decide(BackupKind.Incremental, Lineage(), session, Guid.NewGuid(), out _), Is.EqualTo(BackupKind.Full),
                $"{Marker}: another timeline");
        });
    }

    private static void AssertKind(BackupPointInfo point, BackupKind expected, string situation)
        => Assert.That(point.Kind, Is.EqualTo(expected), $"{Marker}: {situation} the point must be {expected}");

    private static void DamageMetaSlot(string bundle, int slot)
    {
        var dataPath = Path.Combine(bundle, IntegrityConstants.DataFileName);
        using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var page = new byte[PagedMMF.PageSize];
        fs.Seek(slot * (long)PagedMMF.PageSize, SeekOrigin.Begin);
        fs.ReadExactly(page);
        page.AsSpan(300, 128).Fill(0xAB);
        fs.Seek(slot * (long)PagedMMF.PageSize, SeekOrigin.Begin);
        fs.Write(page);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            if (Path.GetFileName(file) != "db.lock")
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }
        }

        foreach (var dir in Directory.GetDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
