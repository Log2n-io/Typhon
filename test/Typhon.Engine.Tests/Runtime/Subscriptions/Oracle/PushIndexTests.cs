using NUnit.Framework;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The sparse push index and the occupancy map (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 2): they follow events and occupied
/// cells, never the grid, and the occupancy counts exactly what a cell delivery would find.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushIndexTests : TestBase<PushIndexTests>
{
    /// <summary>Under churn, skips and walking sessions, the maintained occupancy equals a recount from the blocks every 50 ticks.</summary>
    [Test]
    [VerifiesRule("SUB-24")]
    public void TheOccupancyEqualsARecountUnderChurn()
    {
        // Delivery skips never touch the occupancy — only the index's finish and the recount write it — so one delivery pattern is enough.
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 2401, [0, 30], nameof(PushIndexTests),
            walkRadius: 1500);

        for (var i = 1; i <= 300; i++)
        {
            oracle.Step();
            if (i % 50 == 0)
            {
                Assert.That(oracle.Push.VerifyOccupancy(), Is.Zero, $"the occupancy disagrees with a recount after {i} churned ticks");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(oracle.Push.Occupancy.Underflows, Is.Zero, "an entity left a cell it was never counted in");
            Assert.That(oracle.Push.Occupancy.Count, Is.GreaterThan(0), "nothing was counted, so the comparison proved nothing");
            Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(10), "the workload destroyed too little to exercise leaves");
            Assert.That(oracle.Workload.Teleports, Is.GreaterThan(10), "the workload moved nothing across cells");
        });
    }

    /// <summary>Ticks the track skipped: their cell changes were never indexed, so the next tick recounts, and the map is right again.</summary>
    [Test]
    [VerifiesRule("SUB-24")]
    public void ASkippedTickIsFollowedByARecount()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 2402, [0], nameof(PushIndexTests), walkRadius: 1500);

        for (var i = 0; i < 20; i++)
        {
            oracle.Step();
        }

        // Skipped ticks until the workload has destroyed something across the gap, so the recount has a leave nobody indexed to repair.
        var destroyed = oracle.Workload.Destroyed;
        for (var i = 0; i < 5 || (oracle.Workload.Destroyed == destroyed && i < 60); i++)
        {
            oracle.StepWithoutTrack();
        }

        Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(destroyed), "nothing was destroyed across the gap, so the recount had nothing to repair");
        oracle.Step();
        Assert.Multiple(() =>
        {
            Assert.That(oracle.Push.OccupancyRecounts, Is.EqualTo(1));
            Assert.That(oracle.Push.VerifyOccupancy(), Is.Zero);
        });

        // A wrong count shows only once entities move off it, so the map is checked again after the world has moved.
        for (var i = 0; i < 30; i++)
        {
            oracle.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(oracle.Push.VerifyOccupancy(), Is.Zero);
            Assert.That(oracle.Push.Occupancy.Underflows, Is.Zero);
        });
    }

    /// <summary>A tick whose index was never finished (a stage fault) is followed by a recount at the next finish, and the map is right again.</summary>
    [Test]
    [VerifiesRule("SUB-24")]
    public void AnUnindexedTickIsFollowedByARecount([Values] bool serialIndex)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 2404, [0], nameof(PushIndexTests), walkRadius: 1500,
            deterministicProjection: serialIndex);
        for (var i = 0; i < 20; i++)
        {
            oracle.Step();
        }

        oracle.StepWithoutIndex();
        oracle.Step();
        Assert.That(oracle.Push.OccupancyRecounts, Is.EqualTo(1));
        for (var i = 0; i < 30; i++)
        {
            oracle.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(oracle.Push.VerifyOccupancy(), Is.Zero);
            Assert.That(oracle.Push.Occupancy.Underflows, Is.Zero);
        });
    }

    /// <summary>
    /// The serial path with sessions open but none bound to a profile: every tick is still indexed — no recount is ever needed — and the occupancy follows
    /// the spawns.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-24")]
    public void ATickWithNoBoundSessionIsStillIndexed()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("world", p => p.World().Of<ProjCreature>());
        }, nameof(ATickWithNoBoundSessionIsStillIndexed));
        harness.SerialIndex = true;
        harness.OpenSessions(2, null);

        for (var tick = 1; tick <= 10; tick++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < 10; i++)
                {
                    var x = -6000f + (tick * 500) + (i * 40);
                    tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = x, MaxX = x, MaxY = x } }));
                }

                Assert.That(tx.Commit(), Is.True);
            }

            harness.RunTick(tick);
        }

        var push = harness.Subscriptions.Push;
        Assert.Multiple(() =>
        {
            Assert.That(push.OccupancyRecounts, Is.Zero, "a tick went unindexed, so its changes had to be recounted");
            Assert.That(push.Occupancy.Count, Is.GreaterThan(0));
            Assert.That(push.VerifyOccupancy(), Is.Zero);
        });
    }

    /// <summary>SUB-24's mutant: a secondary that no longer takes its mover out of the cell it left is caught by the recount comparison.</summary>
    [Test]
    [RuleMutant("SUB-24")]
    public void AnOccupancyThatKeepsMoversInTheCellTheyLeftIsCaught()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 2405, [0], nameof(PushIndexTests), walkRadius: 1500);
        oracle.Push.OccupancyMutantForTest = true;
        for (var i = 0; i < 60; i++)
        {
            oracle.Step();
        }

        Assert.That(oracle.Push.VerifyOccupancy(), Is.GreaterThan(0));
    }

    /// <summary>SUB-25's mutant: a mover's secondary filed under the cell it entered is caught by the index's shape check.</summary>
    [Test]
    [RuleMutant("SUB-25")]
    public void AnIndexThatMisfilesSecondariesIsCaught()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 2406, [0], nameof(PushIndexTests), walkRadius: 1500);
        oracle.Push.IndexMutantForTest = true;
        var caught = false;
        for (var i = 0; i < 60 && !caught; i++)
        {
            oracle.Step();
            caught = oracle.Push.IndexShapeForTest() == (-1, -1);
        }

        Assert.That(caught, Is.True);
    }

    /// <summary>
    /// An empty world on a fine grid (1 025 × 1 025 cells at 16 m) with placed, walking sessions: the index holds no cell and the occupancy none, and every
    /// cell a disc reaches is skipped without a cluster query.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-25")]
    public void AnEmptyWorldIndexesNothingWhateverTheGrid()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("near", p => p.Sphere(48).Of<ProjCreature>());
        }, nameof(AnEmptyWorldIndexesNothingWhateverTheGrid), replicationCellM: 16);

        var sessions = harness.OpenSessions(8, "near");
        for (var tick = 1; tick <= 30; tick++)
        {
            for (var i = 0; i < sessions.Length; i++)
            {
                harness.Sessions.SetViewpoint(sessions[i], new Vector3D((i * 700) - 2800 + (tick * 5), (i * 300) - 1200, 0));
            }

            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }

            Assert.That(harness.Subscriptions.Push.IndexCells, Is.Zero, $"tick {tick}: the index holds cells with no event");
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Subscriptions.Push.Occupancy.Count, Is.Zero);
            Assert.That(harness.Subscriptions.Push.CellsDelivered, Is.GreaterThan(0), "no cell was delivered, so the test proved nothing");
            // Every delivered cell is skipped, and the sweeps' cells on top of them.
            Assert.That(harness.Subscriptions.Push.EmptyCellsSkipped, Is.GreaterThanOrEqualTo(harness.Subscriptions.Push.CellsDelivered),
                "every cell is empty");
        });
    }

    /// <summary>
    /// Four worker lists and a bootstrap of 1 500 entities: the merge runs as concurrent key-range chunks, the index is well formed on every tick, and a
    /// World client ends up holding every entity.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-25")]
    [Property("CacheSize", 64 * 1024 * 1024)]
    public void ConcurrentMergeChunksBuildTheSameIndexShape([Values] bool oneCell)
    {
        // All in one cell, every splitter falls on it and all chunks but one are empty.
        const int Creatures = 1500;
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Creatures; i++)
            {
                var x = oneCell ? 100f : -8000f + ((i * 83) % 16000);
                var y = oneCell ? 100f : -8000f + ((i * 137) % 16000);
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f }));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("world", p => p.World().Of<ProjCreature>());
        }, nameof(ConcurrentMergeChunksBuildTheSameIndexShape));
        harness.ProjectionWorkers = 4;

        var session = harness.OpenSessions(1, "world")[0];
        var push = harness.Subscriptions.Push;
        for (var tick = 1; tick <= 20; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(session);
            var (cells, entries) = push.IndexShapeForTest();
            Assert.That((push.IndexCells, push.IndexEntries), Is.EqualTo((cells, entries)),
                $"tick {tick}: the index is malformed or disagrees with the events");
        }

        Assert.Multiple(() =>
        {
            Assert.That(push.MaxMergeChunks, Is.GreaterThan(1), "the merge never split, so nothing ran concurrently");
            Assert.That(push.Occupancy.Count, oneCell ? Is.EqualTo(1) : Is.GreaterThan(1));
            Assert.That(harness.Replica(session).NetIds(harness.PlanIndex(nameof(ProjCreature))).Length, Is.EqualTo(Creatures));
            Assert.That(push.VerifyOccupancy(), Is.Zero);
        });
    }

    /// <summary>
    /// A flat world's event is 48 bytes and a deep one's 64 (10 § 3.5): each is copied into the index and read by every session reaching its cell. The
    /// widest keys round-trip through both.
    /// </summary>
    [Test]
    public void TheFlatEventIsFortyEightBytesAndTheDeepOneSixtyFour()
    {
        const int Max = (1 << 21) - 1;
        Assert.That(System.Runtime.CompilerServices.Unsafe.SizeOf<PushEvent>(), Is.EqualTo(48));
        Assert.That(System.Runtime.CompilerServices.Unsafe.SizeOf<PushEvent3>(), Is.EqualTo(64));
        var flat = new PushEvent { NewKey = PushEvent.Key(Max, Max - 1, 0) };
        Assert.That((PushEvent.KeyX(flat.NewKey), PushEvent.KeyY(flat.NewKey)), Is.EqualTo((Max, Max - 1)), "the flat key in its six bytes");
        var deep = new PushEvent3 { NewKey = PushEvent3.Key(Max, Max - 1, Max - 2) };
        Assert.That((PushEvent3.KeyX(deep.NewKey), PushEvent3.KeyY(deep.NewKey), PushEvent3.KeyZ(deep.NewKey)), Is.EqualTo((Max, Max - 1, Max - 2)));
    }

    /// <summary>
    /// The deep key (10 § 3.3): tiles of 4³ cells major, so every cell of a tile sorts between the tile's first key and the next tile's, tiles sort
    /// z-major, and the key and its compressed sort form round-trip every coordinate.
    /// </summary>
    [Test]
    public void TheDeepKeyIsTileMajorAndRoundTrips()
    {
        var random = new System.Random(3303);
        for (var i = 0; i < 20_000; i++)
        {
            var (cx, cy, cz) = (random.Next(1 << 21), random.Next(1 << 21), random.Next(1 << 21));
            var key = PushEvent3.Key(cx, cy, cz);
            Assert.That((PushEvent3.KeyX(key), PushEvent3.KeyY(key), PushEvent3.KeyZ(key)), Is.EqualTo((cx, cy, cz)));
            var unit = PushEvent3.Unit(key);
            Assert.That(unit, Is.EqualTo(PushEvent3.UnitOf(cx >> 2, cy >> 2, cz >> 2)));
            Assert.That(key, Is.GreaterThanOrEqualTo(PushEvent3.UnitFirstKey(unit)).And.LessThan(PushEvent3.UnitFirstKey(unit + 1)));
        }

        // Within a grid of 100 × 60 × 40 cells: the compressed form keeps the order and inverts exactly.
        const int BitsX = 5, BitsY = 4;
        var keys = new System.Collections.Generic.List<ulong>();
        for (var i = 0; i < 5_000; i++)
        {
            keys.Add(PushEvent3.Key(random.Next(100), random.Next(60), random.Next(40)));
        }

        keys.Sort();
        for (var i = 0; i < keys.Count; i++)
        {
            var compressed = PushEvent3.Compress(keys[i], BitsX, BitsY);
            Assert.That(PushEvent3.Expand(compressed, BitsX, BitsY), Is.EqualTo(keys[i]));
            if (i > 0)
            {
                Assert.That(compressed, Is.GreaterThanOrEqualTo(PushEvent3.Compress(keys[i - 1], BitsX, BitsY)), "compression keeps the key order");
            }
        }

        Assert.That(PushEvent3.Key(3, 3, 3), Is.LessThan(PushEvent3.Key(4, 0, 0)), "a tile's cells precede the next tile's");
        Assert.That(PushEvent3.Key(0, 0, 4), Is.GreaterThan(PushEvent3.Key(1000, 1000, 3)), "tiles are z-major");
    }

    /// <summary>
    /// 10 § 10's empty-world test, timed: 64 walking sessions over 16 km with no entity, at c = 16 m (1 025 × 1 025 cells) and c = 256 m (65 × 65), run
    /// interleaved. The track's cost must not follow the grid: the median tick at 16 m within 5 % of the one at 256 m. The radius is 3c in both, so each
    /// session's window is 11 × 11 cells either way — a window's cost follows (R / c)² by design, and only the grid may differ between the two.
    /// </summary>
    [Test]
    [Explicit("Performance measurement")]
    public void AnEmptyWorldCostsTheSameWhateverTheCellSide()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var medians = new System.Collections.Generic.Dictionary<double, System.Collections.Generic.List<double>> { [16] = [], [256] = [] };
        for (var round = 0; round < 3; round++)
        {
            foreach (var cell in new[] { 16d, 256d })
            {
                medians[cell].Add(MedianTickMs(dbe, cell, $"EmptyWorld{round}_{cell}"));
            }
        }

        var fine = Median(medians[16]);
        var coarse = Median(medians[256]);
        TestContext.Out.WriteLine($"median tick: c = 16 m {fine:F4} ms, c = 256 m {coarse:F4} ms, ratio {fine / coarse:F3}");
        Assert.That(fine / coarse, Is.InRange(0.95, 1.05));

        static double Median(System.Collections.Generic.List<double> v)
        {
            v.Sort();
            return v[v.Count / 2];
        }
    }

    private static double MedianTickMs(DatabaseEngine dbe, double cellM, string name)
    {
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("near", p => p.Sphere(3 * cellM).Of<ProjCreature>());
        }, name, replicationCellM: cellM);

        var sessions = harness.OpenSessions(64, "near");
        var random = new System.Random(64);
        var at = new Vector3D[sessions.Length];
        for (var i = 0; i < at.Length; i++)
        {
            at[i] = new Vector3D((random.NextDouble() * 6000) - 3000, (random.NextDouble() * 6000) - 3000, 0);
        }

        var samples = new System.Collections.Generic.List<double>();
        for (var tick = 1; tick <= 400; tick++)
        {
            for (var i = 0; i < sessions.Length; i++)
            {
                // An eighth of a cell a tick, so both runs are the same geometry at two scales: the anchor moves every tick, a cell is crossed every
                // eight, never a teleport. Only the grid differs. The walk turns back inside the world so no session ends up clamped at an edge.
                var step = (tick / 50) % 2 == 0 ? cellM / 8 : -cellM / 8;
                at[i] = new Vector3D(at[i].X + step, at[i].Y + (step / 2), 0);
                harness.Sessions.SetViewpoint(sessions[i], at[i]);
            }

            var from = System.Diagnostics.Stopwatch.GetTimestamp();
            harness.RunTick(tick);
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(from).TotalMilliseconds;
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }

            if (tick > 100)
            {
                samples.Add(ms);
            }
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>
    /// The index holds exactly the cells this tick's events name — their primaries, and the cells movers left — and every event once per cell.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-25")]
    public void TheIndexHoldsExactlyTheCellsTheEventsName()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 2403, [0], nameof(PushIndexTests), walkRadius: 1500);
        var push = oracle.Push;
        for (var i = 0; i < 60; i++)
        {
            oracle.Step();
            var (cells, entries) = push.IndexShapeForTest();
            Assert.That(push.IndexCells, Is.EqualTo(cells), $"tick {i}: occupied cells");
            Assert.That(push.IndexEntries, Is.EqualTo(entries), $"tick {i}: entries");
        }
    }

    /// <summary>
    /// A <c>World</c> fill over a grid of 2 048 × 2 048 cells holding a few hundred entities (10 § 12, 1.5.3): it visits the occupied cells only, so it
    /// completes in one frame and no cell delivery ever meets an empty cell — where the dense walk needed a thousand frames of 4 096 cells.
    /// </summary>
    [Test]
    public void AWorldFillVisitsOnlyOccupiedCells()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1531, [0, 60], nameof(PushIndexTests),
            worldObserver: true, bigWorld: true, replicationCellM: 8);
        oracle.Frames.DigestFrames = true;
        for (var i = 0; i < 100; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged("a World fill over a sparse fine grid");
        Assert.Multiple(() =>
        {
            Assert.That(oracle.Frames.FramesToComplete(oracle.Sessions[0]), Is.EqualTo(1), "frames to VIEW_COMPLETE");
            Assert.That(oracle.Push.EmptyCellsSkipped, Is.Zero, "a World delivery met an empty cell");
            Assert.That(oracle.Push.WorldOrderMissing, Is.Zero, "a fill found no order taken for it");
            Assert.That(oracle.Push.Occupancy.Count, Is.GreaterThan(100), "the fill had little to walk");
        });
    }
}
