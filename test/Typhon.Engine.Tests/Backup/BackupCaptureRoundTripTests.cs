using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// T1a (AC-C1, AC-R1, AC-R3, BKP-02, BKP-04, BKP-05): points taken while the engine runs, at random instants and staged inside checkpoint cycles, each
/// restored and opened through the crash path, and compared with the workload's committed state at the point's LSN: the highest commit or fence at or
/// below <c>P</c>. Versioned spawns, updates and destroys under Immediate commits, and SingleVersion values made durable by tick fences. Two restores
/// of one workload: the capture's own stand-in (<see cref="TestPointMaterializer"/> over a <see cref="FakeBackupStore"/>), and the real thing — packs
/// in a store, <see cref="RestoreStager"/>, the restored bundle opened as itself, its timeline adopted.
/// </summary>
[Category("Seeded")]
[Seeded]
class BackupCaptureRoundTripTests : BackupTestBase<BackupCaptureRoundTripTests>
{
    private const string Marker = "BKP-02/04 round trip";

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = 40;   // checkpoints keep running under the captures
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-02")]
    [VerifiesRule("BKP-04")]
    public void EveryPoint_RestoresToTheCommittedStateAtItsPointLsn()
        => AssertRoundTrip(RunAndRestore(TestSeed.For("bkp-roundtrip"), points: 20, skipFirstTailSegment: false));

    /// <summary>A tail restored from its second segment on loses records some image needs: the round trip must see it.</summary>
    [Test]
    [CancelAfter(30_000)]
    [RuleMutant("BKP-04")]
    public void Mutant_ATailStartingOneSegmentLate_IsReported()
    {
        var outcome = RunAndRestore(TestSeed.For("bkp-roundtrip"), points: 20, skipFirstTailSegment: true);
        RuleMutants.AssertDetects("BKP-04", Marker, () => AssertRoundTrip(outcome));
    }

    /// <summary>
    /// The same workload into a real store: each point restored from its packs (byte-identical to the image and tail capture handed the store), opened
    /// as a restored bundle through the crash path to the committed state at its P, closed cleanly and found Sound by a deep scan; the first backup of
    /// a restored database is full, on a new chain, on the restore's timeline.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-05")]
    public void EveryPoint_RestoredFromItsPacks_OpensToTheCommittedStateAtItsPointLsn()
        => AssertRoundTrip(RunAndRestoreFromPacks(TestSeed.For("bkp-roundtrip-packs"), points: 20, dropRestoredWal: false));

    /// <summary>A restore that left out <c>wal/</c>: the image opens without the records it needs. The round trip must see it.</summary>
    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("BKP-05")]
    public void Mutant_ARestoreWithoutItsWal_IsReported()
    {
        var outcome = RunAndRestoreFromPacks(TestSeed.For("bkp-roundtrip-packs"), points: 8, dropRestoredWal: true);
        RuleMutants.AssertDetects("BKP-05", Marker, () => AssertRoundTrip(outcome));
    }

    /// <summary>The nightly breadth: 200 points per seed.</summary>
    [Test]
    [CancelAfter(300_000)]
    [Explicit("Breadth sweep of the round trip, 200 points per seed; the gate runs 20")]
    [Category("Nightly")]
    public void EveryPoint_RestoresToTheCommittedStateAtItsPointLsn_Nightly()
        => AssertRoundTrip(RunAndRestore(TestSeed.For("bkp-roundtrip-nightly"), points: 200, skipFirstTailSegment: false));

    private sealed record Snapshot(long Lsn, Dictionary<EntityId, int> Versioned, Dictionary<EntityId, int> SingleVersion);

    private sealed record Outcome(List<string> Failures, int Points, int FrozenInsideACycle);

    private sealed record Workload(List<Snapshot> History, List<BackupPointInfo> Points, int FrozenInsideACycle, string DataPath, string WalDirectory);

    private Outcome RunAndRestore(int seed, int points, bool skipFirstTailSegment)
    {
        var store = new FakeBackupStore();
        var run = Run(seed, points, store);
        var history = run.History;

        // Each point, restored and opened through the crash path.
        var failures = new List<string>();
        foreach (var point in store.Points.ToList())
        {
            TestPointMaterializer.Materialize(store, point, run.DataPath, run.WalDirectory, skipFirstTailSegment);
            using var scope = ServiceProvider.CreateScope();
            var dbe = Open(scope);
            var where = $"point {point.BackupId} ({point.Plan.Kind}, C={point.CheckpointLsn}, P={point.PointLsn})";
            CheckState(dbe, history.Last(h => h.Lsn <= point.PointLsn), point.PointLsn, where, failures);
        }

        return new Outcome(failures, store.Points.Count, run.FrozenInsideACycle);
    }

    private Outcome RunAndRestoreFromPacks(int seed, int points, bool dropRestoredWal)
    {
        var root = Path.Combine(TestStorage.ForFixture(nameof(BackupCaptureRoundTripTests)), Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(root, "store");
        var recording = new RecordingStore(new FileSystemBackupStore(storePath));
        var run = Run(seed, points, recording);
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < run.Points.Count; i++)
            {
                var point = run.Points[i];
                var where = $"point {point.BackupId} ({point.Kind}, C={point.CheckpointLsn}, P={point.PointLsn})";
                var restoreDir = Path.Combine(root, "r" + point.BackupId);
                var outcome = RestoreStager.Run(storePath, PointSelector.ById(point.BackupId), restoreDir);

                // BKP-02 through the store: the restored data file is the image capture handed the store, the restored wal/ its tail (C3).
                CheckBytes(recording, point.BackupId, outcome.Target, where, failures);
                if (dropRestoredWal)
                {
                    Directory.Delete(Path.Combine(outcome.Target, "wal"), true);
                    Directory.CreateDirectory(Path.Combine(outcome.Target, "wal"));
                }

                using (var provider = ProviderFor(restoreDir))
                {
                    using (var scope = provider.CreateScope())
                    {
                        var dbe = Open(scope);
                        if (!dbe.LastOpenRanCrashRecovery)
                        {
                            failures.Add($"{where}: the restored image did not open through the crash path");
                        }

                        if (dbe.TimelineId != outcome.NewTimelineId)
                        {
                            failures.Add($"{where}: the restored database is on timeline {dbe.TimelineId}, not the restore's {outcome.NewTimelineId}");
                        }

                        CheckState(dbe, run.History.Last(h => h.Lsn <= point.PointLsn), point.PointLsn, where, failures);

                        // AC-R3, once: the restored database's first backup is full, on a new chain, on the restore's timeline.
                        if (i == run.Points.Count - 1 && !dropRestoredWal)
                        {
                            var first = dbe.BackupCoordinator.CaptureAsync(new FileSystemBackupStore(storePath), BackupKind.Incremental,
                                CancellationToken.None);
                            Assert.That(first.Wait(Budget), Is.True, "the restored database's first backup did not finish");
                            var view = StoreView.Open(storePath, trustCatalog: false);
                            var pack = view.Catalog.Packs.Single(p => p.BackupId == first.Result.BackupId);
                            if (first.Result.Kind != BackupKind.Full || run.Points.Any(p => p.ChainId == first.Result.ChainId)
                                || pack.TimelineId != outcome.NewTimelineId)
                            {
                                failures.Add($"{where}: the restored database's first backup is {first.Result.Kind} on chain {first.Result.ChainId}, "
                                             + $"timeline {pack.TimelineId}: expected a full point on a new chain, timeline {outcome.NewTimelineId}");
                            }
                        }
                    }

                    // AC-R1(e): closed cleanly, the restored database is Sound to a deep scan.
                    if (!dropRestoredWal)
                    {
                        using var source = new OfflineBundlePageSource(outcome.Target);
                        var report = IntegrityScanner.Scan(source, IntegrityOptions.Deep);
                        // The one tolerated divergence is the engine's own crash-recovery defect, reproduced with no backup at all
                        // (AHardCrashUnderTheSameWorkload_IsSoundAfterRecovery): a destroyed entity's key left in its freed cluster slot.
                        var unexplained = report.Findings.Where(f => !IsKnownRecoveryDivergence(f)).ToList();
                        if (report.Verdict != IntegrityVerdict.Sound && unexplained.Count > 0)
                        {
                            failures.Add($"{where}: a deep scan after a clean close reports {report.Verdict} (only {KnownRecoveryIssue}'s CHK-CLU-01 is "
                                         + $"tolerated): {string.Join("; ", unexplained.Take(3))}");
                        }
                    }
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }

        return new Outcome(failures, run.Points.Count, run.FrozenInsideACycle);
    }

    /// <summary>The issue tracking the crash-recovery divergence the deep scan tolerates.</summary>
    private const string KnownRecoveryIssue = "#1450";

    /// <summary>
    /// CHK-CLU-01, Lossless, on a <c>CompAArch</c> cluster slot: a free slot still holding a key after crash recovery and a clean close
    /// (<see cref="KnownRecoveryIssue"/>). Anything else a deep scan finds is a failure.
    /// </summary>
    private static bool IsKnownRecoveryDivergence(IntegrityFinding f) =>
        f.Code == "CHK-CLU-01" && f.Severity == IntegritySeverity.Divergence && f.Repair == Repairability.Lossless
        && f.Summary.Contains("'CompAArch'", StringComparison.Ordinal) && f.Summary.Contains("free cluster slot", StringComparison.Ordinal);

    /// <summary>The restored bundle against what capture handed the store: the chain's newest page images, zeros elsewhere; each segment's prefix.</summary>
    private static void CheckBytes(RecordingStore recording, uint id, string bundle, string where, List<string> failures)
    {
        var point = recording.Points[id];
        var data = File.ReadAllBytes(Path.Combine(bundle, "data"));
        if (data.Length != (long)point.ImagePages * PagedMMF.PageSize)
        {
            failures.Add($"{where}: the restored data file is {data.Length} bytes, the image {point.ImagePages} pages");
            return;
        }

        var zeros = new byte[PagedMMF.PageSize];
        for (var p = 0; p < point.ImagePages; p++)
        {
            byte[] expected = null;
            for (var c = point; c != null && expected == null; c = c.ParentId == 0 ? null : recording.Points[c.ParentId])
            {
                c.Pages.TryGetValue(p, out expected);
            }

            if (!data.AsSpan(p * PagedMMF.PageSize, PagedMMF.PageSize).SequenceEqual(expected ?? zeros))
            {
                failures.Add($"{where}: restored page {p} is not the image's");
                return;
            }
        }

        foreach (var (segmentId, declared, prefix) in point.Tail)
        {
            var wal = File.ReadAllBytes(Path.Combine(bundle, "wal", $"{segmentId:D16}.wal"));
            if (wal.Length != declared || !wal.AsSpan(0, prefix.Length).SequenceEqual(prefix) || wal.AsSpan(prefix.Length).IndexOfAnyExcept((byte)0) >= 0)
            {
                failures.Add($"{where}: restored segment {segmentId} is not its copied prefix then zeros to {declared} bytes");
            }
        }
    }

    private void CheckState(DatabaseEngine dbe, Snapshot expected, long pointLsn, string where, List<string> failures)
    {
        if (dbe.LastWalV2RecoveryResult.MaxLsn > pointLsn)
        {
            failures.Add($"{where}: recovery applied LSN {dbe.LastWalV2RecoveryResult.MaxLsn}, past P");
        }

        using var t = dbe.CreateQuickTransaction();
        Compare(where, "versioned", expected.Versioned, Read(t.Query<CompAArch>().Execute(), id => t.Open(id).Read(CompAArch.A).A), failures);
        Compare(where, "single-version", expected.SingleVersion,
            Read(t.Query<SvTestArchetype>().Execute(), id => t.Open(id).Read(SvTestArchetype.SvComp).Value), failures);
    }

    private Workload Run(int seed, int points, IBackupPointStore store)
    {
        var rng = new Random(seed);
        var captured = new List<BackupPointInfo>();
        var history = new List<Snapshot>();
        var versioned = new Dictionary<EntityId, int>();
        var singleVersion = new Dictionary<EntityId, int>();
        var insideACycle = 0;
        string dataPath, walDir;

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var coordinator = dbe.BackupCoordinator;
            coordinator.Options.CbwStallTimeout = TimeSpan.FromSeconds(5);   // correctness under test, not the stall budget
            dataPath = dbe.MMF.DataFilePath;
            walDir = dbe.WalDirectory;
            var tick = 0L;
            var nextValue = 1;

            void Record() => history.Add(new Snapshot(dbe.WalManager.LastPublishedLsn, new(versioned), new(singleVersion)));

            // SingleVersion steps only between captures: an update is durable at its fence, so a freeze between the two would see a state the oracle
            // cannot name (the value in memory, maybe on disk, not yet in the log). That is SingleVersion's contract, not a backup defect.
            void Step(bool singleVersionToo)
            {
                switch (rng.Next(singleVersionToo ? 4 : 3))
                {
                    case 0:
                    case 1:
                        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
                        {
                            for (var i = rng.Next(1, 4); i > 0; i--)
                            {
                                var v = nextValue++;
                                versioned[t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(v)))] = v;
                            }

                            foreach (var id in versioned.Keys.OrderBy(_ => rng.Next()).Take(rng.Next(0, 5)).ToList())
                            {
                                var v = nextValue++;
                                var e = t.OpenMut(id);
                                e.Set(CompAArch.A, new CompA(v));
                                versioned[id] = v;
                            }

                            t.Commit();
                        }

                        break;
                    case 2:
                        if (versioned.Count > 5)
                        {
                            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                            var victim = versioned.Keys.ElementAt(rng.Next(versioned.Count));
                            t.Destroy(victim);
                            t.Commit();
                            versioned.Remove(victim);
                        }

                        break;
                    default:
                        // SingleVersion: a value is durable at the fence that follows it, so the update and its fence form one step.
                        using (var t = dbe.CreateQuickTransaction())
                        {
                            var id = singleVersion.Keys.ElementAt(rng.Next(singleVersion.Count));
                            var v = nextValue++;
                            var e = t.OpenMut(id);
                            e.Set(SvTestArchetype.SvComp, new CompSmSingleVersion(v));
                            singleVersion[id] = v;
                            t.Commit();
                        }

                        dbe.WriteTickFence(++tick);
                        break;
                }

                Record();
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 8; i++)
                {
                    var v = nextValue++;
                    singleVersion[t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(new CompSmSingleVersion(v)))] = v;
                }

                t.Commit();
            }

            dbe.WriteTickFence(++tick);

            // A SingleVersion spawn emits no record (CM-07) and sets no dirty bit (DIRTY-01): its values are durable at the first checkpoint that
            // writes them, not at the fence. A point frozen before that checkpoint restores them as a crash would, as zeros; the oracle names the
            // state after it.
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
            Record();

            for (var point = 0; point < points; point++)
            {
                for (var s = rng.Next(1, 6); s > 0; s--)
                {
                    Step(singleVersionToo: true);
                }

                // Every other point is frozen inside a checkpoint cycle: the cycle is held once a batch's pages are written and before its flush2,
                // so the image holds pages reflecting records that are published and not durable (C1).
                using var hit = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var armed = 0;
                Task<bool> cycle = null;
                if (point % 2 == 1)
                {
                    armed = 1;
                    dbe.CheckpointManager.BeforeSettleProbe = () =>
                    {
                        if (Interlocked.Exchange(ref armed, 0) == 1)
                        {
                            hit.Set();
                            release.Wait();
                        }
                    };
                    // Dirty pages for the forced cycle to write, under a Deferred commit: published, not yet durable when the wave writes them.
                    Update(dbe, versioned.Keys.Take(20), -1, DurabilityMode.Deferred);
                    foreach (var id in versioned.Keys.Take(20).ToList())
                    {
                        versioned[id] = -1;
                    }

                    Record();
                    cycle = Task.Run(() => dbe.CheckpointManager.ForceCheckpointAndWait(Budget));
                }

                var staged = cycle != null && hit.Wait(Budget);
                coordinator.Options.CheckpointBeforeFreeze = !staged && rng.Next(2) == 0;
                var capture = coordinator.CaptureAsync(store, point % 7 == 0 ? BackupKind.Full : BackupKind.Incremental, CancellationToken.None);
                try
                {
                    // Commits during the capture, except while a cycle is held: it may hold a lock a commit needs.
                    while (!staged && !capture.IsCompleted)
                    {
                        Step(singleVersionToo: false);
                    }

                    Assert.That(capture.Wait(Budget), Is.True, "the capture did not finish");
                    captured.Add(capture.Result);
                }
                finally
                {
                    Interlocked.Exchange(ref armed, 0);
                    release.Set();
                    cycle?.Wait(Budget);
                    dbe.CheckpointManager.BeforeSettleProbe = null;
                }

                if (staged)
                {
                    insideACycle++;
                }
            }
        }

        return new Workload(history, captured, insideACycle, dataPath, walDir);
    }

    /// <summary>
    /// Classification, crash only: the round trip's Versioned workload (spawns, updates, destroys, Immediate) under its 40 ms checkpoints, a hard
    /// crash at a seeded instant, the crash open, a clean close, a deep scan. No backup code runs. About one seed in five leaves a destroyed
    /// entity's key in its freed cluster slot (CHK-CLU-01): the engine's recovery defect #1450, which the round trip over packs therefore tolerates.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(10)]
    [TestCase(11)]
    [TestCase(12)]
    [TestCase(13)]
    [TestCase(14)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(17)]
    [TestCase(18)]
    [TestCase(19)]
    [TestCase(20)]
    [TestCase(21)]
    [TestCase(22)]
    [TestCase(23)]
    [TestCase(24)]
    [TestCase(25)]
    [TestCase(26)]
    [TestCase(27)]
    [TestCase(28)]
    [TestCase(29)]
    [TestCase(30)]
    [TestCase(31)]
    [TestCase(32)]
    [TestCase(33)]
    [TestCase(34)]
    [TestCase(35)]
    [TestCase(36)]
    [TestCase(37)]
    [TestCase(38)]
    [TestCase(39)]
    [TestCase(40)]
    [CancelAfter(60_000)]
    [Category("Quarantine")]   // #1450: crash recovery leaves a destroyed entity's key in its freed cluster slot (CHK-CLU-01); no backup involved
    public void AHardCrashUnderTheSameWorkload_IsSoundAfterRecovery(int seed)
    {
        var rng = new Random(seed);
        var versioned = new List<EntityId>();
        string walDir;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            walDir = dbe.WalDirectory;
            var steps = rng.Next(800, 3000);
            for (var s = 0; s < steps; s++)
            {
                using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                switch (rng.Next(3))
                {
                    case 0:
                        for (var i = rng.Next(1, 4); i > 0; i--)
                        {
                            versioned.Add(t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(s))));
                        }

                        break;
                    case 1:
                        foreach (var id in versioned.OrderBy(_ => rng.Next()).Take(rng.Next(0, 5)).ToList())
                        {
                            t.OpenMut(id).Set(CompAArch.A, new CompA(-s));
                        }

                        break;
                    default:
                        if (versioned.Count > 5)
                        {
                            var victim = versioned[rng.Next(versioned.Count)];
                            t.Destroy(victim);
                            versioned.Remove(victim);
                        }

                        break;
                }

                t.Commit();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.True);
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Query<CompAArch>().Execute().Count, Is.EqualTo(versioned.Count));
        }

        using var source = new OfflineBundlePageSource(Path.Combine(Path.GetDirectoryName(walDir), CurrentDatabaseName + ".typhon"));
        var report = IntegrityScanner.Scan(source, IntegrityOptions.Deep);
        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound), string.Join("; ", report.Findings));
    }

    /// <summary>A store that also keeps what capture handed it: each point's image pages, its tail and its parent.</summary>
    private sealed class RecordingStore(IBackupPointStore inner) : IBackupPointStore
    {
        public Dictionary<uint, RecordedPoint> Points { get; } = [];

        public IBackupStoreSession OpenSession(Guid databaseId, CancellationToken ct) => new Session(this, inner.OpenSession(databaseId, ct));

        private sealed class Session(RecordingStore owner, IBackupStoreSession session) : IBackupStoreSession
        {
            public Guid StoreId => session.StoreId;

            public bool TryGetChainHead(Guid chainId, out BackupChainHead head) => session.TryGetChainHead(chainId, out head);

            public IBackupPointSink BeginPoint(in BackupPointPlan plan) => new RecordingSink(owner, session.BeginPoint(plan), plan.ParentId);

            public void Dispose() => session.Dispose();
        }
    }

    private sealed class RecordedPoint
    {
        public uint ParentId;
        public int ImagePages;
        public readonly Dictionary<int, byte[]> Pages = [];
        public readonly List<(long SegmentId, uint Declared, byte[] Prefix)> Tail = [];
    }

    private sealed class RecordingSink(RecordingStore owner, IBackupPointSink inner, uint parentId) : IBackupPointSink
    {
        private readonly RecordedPoint _point = new() { ParentId = parentId };
        private MemoryStream _segment;
        private (long Id, uint Declared) _current;

        public uint BackupId => inner.BackupId;

        public Guid ChainId => inner.ChainId;

        public void SetImage(int imagePages, long checkpointLsn, long freezeUtcTicks)
        {
            _point.ImagePages = imagePages;
            inner.SetImage(imagePages, checkpointLsn, freezeUtcTicks);
        }

        public void AddPage(int pageId, ReadOnlySpan<byte> image, bool suspect)
        {
            _point.Pages[pageId] = image.ToArray();
            inner.AddPage(pageId, image, suspect);
        }

        public void BeginTailSegment(long segmentId, long firstLsn, uint declaredSize)
        {
            _segment = new MemoryStream();
            _current = (segmentId, declaredSize);
            inner.BeginTailSegment(segmentId, firstLsn, declaredSize);
        }

        public void AppendTail(ReadOnlySpan<byte> bytes)
        {
            _segment.Write(bytes);
            inner.AppendTail(bytes);
        }

        public void EndTailSegment()
        {
            _point.Tail.Add((_current.Id, _current.Declared, _segment.ToArray()));
            inner.EndTailSegment();
        }

        public BackupPointInfo Complete(long pointLsn, long tailEndUtcTicks)
        {
            var info = inner.Complete(pointLsn, tailEndUtcTicks);
            owner.Points[info.BackupId] = _point;
            return info;
        }

        public void Abort() => inner.Abort();
    }

    private static Dictionary<EntityId, int> Read(IEnumerable<EntityId> ids, Func<EntityId, int> value)
    {
        var result = new Dictionary<EntityId, int>();
        foreach (var id in ids)
        {
            result[id] = value(id);
        }

        return result;
    }

    private static void Compare(string where, string what, Dictionary<EntityId, int> expected, Dictionary<EntityId, int> actual, List<string> failures)
    {
        foreach (var (id, v) in expected)
        {
            if (!actual.TryGetValue(id, out var got))
            {
                failures.Add($"{where}: {what} {id} lost");
            }
            else if (got != v)
            {
                failures.Add($"{where}: {what} {id} = {got}, expected {v}");
            }
        }

        foreach (var id in actual.Keys.Where(id => !expected.ContainsKey(id)))
        {
            failures.Add($"{where}: {what} {id} present, not in the committed state at P");
        }
    }

    private static void AssertRoundTrip(Outcome outcome)
    {
        Assert.That(outcome.Points, Is.GreaterThan(0), "premise: points were taken");
        Assert.That(outcome.Failures, Is.Empty, $"{Marker}: {outcome.Failures.Count} mismatch(es):\n  " + string.Join("\n  ", outcome.Failures.Take(40)));
        Assert.That(outcome.FrozenInsideACycle, Is.GreaterThanOrEqualTo(outcome.Points / 4), "AC-C1.1: at least a quarter of the points froze inside a cycle");
    }
}
