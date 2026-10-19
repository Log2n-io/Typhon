using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// T1a (AC-C1, BKP-02, BKP-04): points taken while the engine runs, at random instants and staged inside checkpoint cycles, each restored the way a
/// restore will (<see cref="TestPointMaterializer"/>: chain image + tail), opened through the crash path, and compared with the workload's committed
/// state at the point's LSN: the highest commit or fence at or below <c>P</c>. Versioned spawns, updates and destroys under Immediate commits, and
/// SingleVersion values made durable by tick fences.
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

    /// <summary>The nightly breadth: 200 points per seed.</summary>
    [Test]
    [CancelAfter(300_000)]
    [Explicit("Breadth sweep of the round trip, 200 points per seed; the gate runs 20")]
    [Category("Nightly")]
    public void EveryPoint_RestoresToTheCommittedStateAtItsPointLsn_Nightly()
        => AssertRoundTrip(RunAndRestore(TestSeed.For("bkp-roundtrip-nightly"), points: 200, skipFirstTailSegment: false));

    private sealed record Snapshot(long Lsn, Dictionary<EntityId, int> Versioned, Dictionary<EntityId, int> SingleVersion);

    private sealed record Outcome(List<string> Failures, int Points, int FrozenInsideACycle);

    private Outcome RunAndRestore(int seed, int points, bool skipFirstTailSegment)
    {
        var rng = new Random(seed);
        var store = new FakeBackupStore();
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
                var capture = StartCapture(dbe, store, point % 7 == 0 ? BackupKind.Full : BackupKind.Incremental);
                try
                {
                    // Commits during the capture, except while a cycle is held: it may hold a lock a commit needs.
                    while (!staged && !capture.IsCompleted)
                    {
                        Step(singleVersionToo: false);
                    }

                    Assert.That(capture.Wait(Budget), Is.True, "the capture did not finish");
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

        // Each point, restored and opened through the crash path.
        var failures = new List<string>();
        foreach (var point in store.Points.ToList())
        {
            TestPointMaterializer.Materialize(store, point, dataPath, walDir, skipFirstTailSegment);
            using var scope = ServiceProvider.CreateScope();
            var dbe = Open(scope);
            var expected = history.Last(h => h.Lsn <= point.PointLsn);
            var where = $"point {point.BackupId} ({point.Plan.Kind}, C={point.CheckpointLsn}, P={point.PointLsn})";
            if (dbe.LastWalV2RecoveryResult.MaxLsn > point.PointLsn)
            {
                failures.Add($"{where}: recovery applied LSN {dbe.LastWalV2RecoveryResult.MaxLsn}, past P");
            }

            using var t = dbe.CreateQuickTransaction();
            Compare(where, "versioned", expected.Versioned, Read(t, t.Query<CompAArch>().Execute(), id => t.Open(id).Read(CompAArch.A).A), failures);
            Compare(where, "single-version", expected.SingleVersion,
                Read(t, t.Query<SvTestArchetype>().Execute(), id => t.Open(id).Read(SvTestArchetype.SvComp).Value), failures);
        }

        return new Outcome(failures, store.Points.Count, insideACycle);
    }

    private static Dictionary<EntityId, int> Read(Transaction t, IEnumerable<EntityId> ids, Func<EntityId, int> value)
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
