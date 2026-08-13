using NUnit.Framework;
using SimpleSpaceBattle;

namespace SimpleSpaceBattle.Tests;

/// <summary>
/// Tests that pin the load-bearing claims of DESIGN.md. These are deliberately about the *invariants that make the
/// design work* — cluster geometry, determinism, the pull/push equivalence — not about incidental behaviour.
/// </summary>
[TestFixture]
public sealed class SimulationTests
{
    private static string NewLocation(string name)
    {
        // The engine validates that the database's PARENT directory exists before opening, so create it here
        // rather than letting the first test fail with an options-validation error.
        string root = Path.Combine(Path.GetTempPath(), "ssb-tests");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{name}-{Guid.NewGuid():N}");
    }

    private static void Cleanup(string location)
    {
        try
        {
            if (Directory.Exists(location))
            {
                Directory.Delete(location, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort — a leftover test database must not fail the run.
        }
    }

    /// <summary>
    /// AC1 / DESIGN §4.3 — the cluster geometry the whole performance argument rests on. This is a canary: adding a
    /// component or widening a field silently changes N and entities-per-page, and this test is what says so.
    /// </summary>
    [Test]
    public void ClusterGeometry_MatchesDesign()
    {
        string location = NewLocation("geometry");
        try
        {
            var config = SimulationConfig.Default with { ShipCount = 5_000 };
            using BattleHost host = BattleHost.Create(config, location, workerCount: 4);

            Assert.That(host.World.ClusterSize, Is.EqualTo(46),
                "Cluster size N changed — §4.3 computes N=46 from perEntitySize=56. Recompute the table if a component changed.");
        }
        finally
        {
            Cleanup(location);
        }
    }

    /// <summary>
    /// AC8 / DESIGN §9 — identical outcome regardless of worker count. This is the test that proves the "zero
    /// cross-entity writes" rule actually holds: any torn read, any order-dependent accumulation, or any
    /// scan-order tiebreak would show up here as a divergence.
    /// </summary>
    [Test]
    public void Determinism_IsIndependentOfWorkerCount()
    {
        const int ticks = 60;

        // Diagnostic first: with nobody dying, no destroy or cluster-migration happens, so any divergence here is
        // pure simulation logic rather than storage-layout ordering.
        Assert.That(RunToTick(ticks, workerCount: 4, noDeaths: true),
            Is.EqualTo(RunToTick(ticks, workerCount: 1, noDeaths: true)),
            "diverged with no deaths at all — the divergence is in the simulation, not in destroy ordering");

        (int alive, ulong health, ulong targets) baseline = RunToTick(ticks, workerCount: 1);
        (int alive, ulong health, ulong targets) four = RunToTick(ticks, workerCount: 4);
        (int alive, ulong health, ulong targets) many = RunToTick(ticks, workerCount: 16);

        Assert.Multiple(() =>
        {
            Assert.That(four, Is.EqualTo(baseline), "4 workers diverged from 1 worker.");
            Assert.That(many, Is.EqualTo(baseline), "16 workers diverged from 1 worker.");
        });
    }

    /// <summary>DESIGN §2 — the simulation actually does something: ships acquire targets, fire, and die.</summary>
    [Test]
    public void Simulation_ConvergesTowardTermination()
    {
        string location = NewLocation("converge");
        try
        {
            // Dense small world so attrition is visible quickly — the default 50k/1000x1000x200 needs ~150 ticks
            // before the first deaths, which is too slow for a unit test.
            var config = SimulationConfig.Default with
            {
                ShipCount = 4_000,
                WorldX = 200f,
                WorldY = 200f,
                WorldZ = 60f,
                CellSize = 25f,
                MaximumHealth = 100,
                MaximumCompletedTicks = 120,
            };

            using BattleHost host = BattleHost.Create(config, location, workerCount: 4);
            host.Start();
            host.WaitForTerminal(WaitToken(TimeSpan.FromSeconds(30)));
            host.Stop();

            BattleWorld world = host.World;

            Assert.Multiple(() =>
            {
                Assert.That(world.TotalAcquisitions, Is.GreaterThan(0), "no ship ever acquired a target");
                Assert.That(world.TotalShots, Is.GreaterThan(0), "no shot was ever fired");
                Assert.That(world.TotalHits, Is.GreaterThan(0), "no shot ever connected");
                Assert.That(world.TotalDeaths, Is.GreaterThan(0), "no ship ever died");
                Assert.That(world.AliveCount, Is.LessThan(config.ShipCount), "fleet never shrank");
                Assert.That(world.AliveCount, Is.EqualTo(config.ShipCount - world.TotalDeaths),
                    "alive count and death count disagree — the Reaper lost or double-counted a destroy");
            });
        }
        finally
        {
            Cleanup(location);
        }
    }

    /// <summary>
    /// DESIGN §6.3 — the firing cadence is a pure function of (id, tick), evaluable by anyone for anyone. That is
    /// what lets the defender replay the attacker's decision, so it is worth pinning rather than assuming.
    /// </summary>
    [Test]
    public void FiringCadence_IsPureAndEvenlyDistributed()
    {
        const int interval = 8;
        const int mask = interval - 1;

        // Same inputs, same answer — the property the pull formulation depends on.
        Assert.That(CombatRules.Fires(12345L, 100UL, mask), Is.EqualTo(CombatRules.Fires(12345L, 100UL, mask)));

        // Each ship fires exactly once per interval, and the fleet does not pulse in unison.
        var firingShipsPerTick = new int[interval];
        const int ships = 2_000;
        for (int s = 0; s < ships; s++)
        {
            long id = ((long)(s + 1) << 16) | 3100;
            int firedInWindow = 0;
            for (ulong t = 0; t < interval; t++)
            {
                if (CombatRules.Fires(id, t, mask))
                {
                    firedInWindow++;
                    firingShipsPerTick[t]++;
                }
            }

            Assert.That(firedInWindow, Is.EqualTo(1), $"ship {s} fired {firedInWindow} times in one {interval}-tick window");
        }

        int min = firingShipsPerTick.Min();
        int max = firingShipsPerTick.Max();
        Assert.That(max - min, Is.LessThan(ships / 10),
            $"firing is clustered across the interval ({min}..{max} of {ships}) — the phase offset is not spreading load");
    }

    /// <summary>
    /// DESIGN §6.3 — accuracy is symmetric in the pair and falls with range. Symmetry is what makes attacker and
    /// defender agree; the falloff is what makes closing worthwhile.
    /// </summary>
    [Test]
    public void Accuracy_IsSymmetricAndFallsWithRange()
    {
        const float rangeSq = 30f * 30f;
        long a = (1L << 16) | 3100;
        long b = (2L << 16) | 3100;

        Assert.That(
            CombatRules.Hits(a, b, 7UL, 100f, rangeSq),
            Is.EqualTo(CombatRules.Hits(b, a, 7UL, 100f, rangeSq)),
            "accuracy roll is not symmetric in (shooter, target) — pull and push would disagree");

        int pointBlank = 0;
        int maxRange = 0;
        for (ulong t = 0; t < 4_000; t++)
        {
            long shooter = ((long)(t + 3) << 16) | 3100;
            if (CombatRules.Hits(shooter, b, t, 0f, rangeSq)) pointBlank++;
            if (CombatRules.Hits(shooter, b, t, rangeSq, rangeSq)) maxRange++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(pointBlank, Is.EqualTo(4_000), "point-blank shots should always connect");
            Assert.That(maxRange, Is.InRange(1_700, 2_300), $"max-range hit rate should be ~50 %, got {maxRange / 40.0:F1} %");
        });
    }

    /// <summary>
    /// Diagnostic for the residual worker-count divergence: does <c>ClusterSpatialQuery.AABB</c> ever yield the same
    /// entity twice? A duplicate would be counted as two attackers by <see cref="ResolutionSystem"/>, doubling that
    /// shot's damage — and since it would depend on cluster/cell layout, which shifts as entities migrate, it would
    /// make the result worker-count dependent while leaving damage arithmetic itself perfectly order-independent.
    /// </summary>
    [Test]
    public void ClusterSpatialQuery_DoesNotYieldDuplicateEntities()
    {
        string location = NewLocation("dupes");
        try
        {
            var config = SimulationConfig.Default with
            {
                ShipCount = 6_000,
                WorldX = 250f,
                WorldY = 250f,
                WorldZ = 60f,
                CellSize = 25f,
                MaximumHealth = uint.MaxValue,
                MaximumCompletedTicks = 40,
            };

            using BattleHost host = BattleHost.Create(config, location, workerCount: 4);
            host.Start();
            host.WaitForTerminal(WaitToken(TimeSpan.FromSeconds(30)));
            host.Stop();

            int duplicates = host.World.CountDuplicateQueryHits(config.AcquisitionRange, out int clustersProbed, out int worstCluster);

            Assert.That(duplicates, Is.Zero,
                $"ClusterSpatialQuery returned the same entity more than once: {duplicates} duplicate hits across "
                + $"{clustersProbed} cluster queries (worst single cluster: {worstCluster}). Every duplicate is a "
                + "double-counted attacker in ResolutionSystem.");
        }
        finally
        {
            Cleanup(location);
        }
    }

    /// <summary>
    /// Enforces the one invariant in this demo that is a <b>load-bearing negative</b>: <see cref="MovementSystem"/>
    /// writes <c>Hull</c> through <c>GetSpan</c> (with TYPHON009 suppressed, because <c>WriteSpatial</c> rejects
    /// <c>AABB3F</c>) and is only correct while <c>SpatialBarrierOnly</c> stays <b>false</b>, which is what makes the
    /// tick fence rescan every active cluster.
    ///
    /// <para>Calling <c>SetSpatialBarrierOnly&lt;Ship&gt;()</c> would make every position write invisible to spatial
    /// maintenance and freeze the index. Nothing throws. The simulation keeps running and keeps producing deaths —
    /// against stale neighbours — so every other test in this fixture still passes. Only this one fails.</para>
    ///
    /// <para>Ships cruise at 50 u/s over cells of 25 u, so 40 ticks moves each one ~80 units: several cell crossings,
    /// and therefore several migrations that a frozen index would not follow.</para>
    /// </summary>
    [Test]
    public void SpatialIndexTracksMovement_GuardsAgainstSpatialBarrierOnly()
    {
        string location = NewLocation("indextracks");
        try
        {
            var config = SimulationConfig.Default with
            {
                ShipCount = 2_000,
                WorldX = 250f,
                WorldY = 250f,
                WorldZ = 60f,
                CellSize = 25f,
                MaximumHealth = uint.MaxValue,   // nobody dies: isolate migration from destroy
                MaximumCompletedTicks = 40,
            };

            using BattleHost host = BattleHost.Create(config, location, workerCount: 4);
            host.Start();
            host.WaitForTerminal(WaitToken(TimeSpan.FromSeconds(30)));
            host.Stop();

            // Probe radius must exceed the migration hysteresis band (MigrationHysteresisRatio 0.05 x cellSize 25
            // = 1.25 units). Inside that band an entity is deliberately left registered to the cell it has just
            // left, so a query tighter than the band legitimately misses it — that is engine policy, not a fault.
            int missing = host.World.CountShipsMissingFromIndex(probeRadius: 4f);

            Assert.That(missing, Is.Zero,
                $"{missing} of {config.ShipCount} ships cannot be found by a spatial query at their own position. "
                + "The spatial index has stopped following Hull writes — the usual cause is SetSpatialBarrierOnly<Ship>() "
                + "having been enabled, which silently invalidates MovementSystem's GetSpan write path.");
        }
        finally
        {
            Cleanup(location);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static (int Alive, ulong HealthChecksum, ulong TargetChecksum) RunToTick(int ticks, int workerCount, bool noDeaths = false)
    {
        string location = NewLocation($"determinism-w{workerCount}");
        try
        {
            var config = SimulationConfig.Default with
            {
                ShipCount = 3_000,
                WorldX = 250f,
                WorldY = 250f,
                WorldZ = 60f,
                CellSize = 25f,
                MaximumHealth = noDeaths ? uint.MaxValue : 200,
                MaximumCompletedTicks = (ulong)ticks,
            };

            using BattleHost host = BattleHost.Create(config, location, workerCount);
            host.Start();
            host.WaitForTerminal(WaitToken(TimeSpan.FromSeconds(30)));
            host.Stop();

            return host.World.Checksum();
        }
        finally
        {
            Cleanup(location);
        }
    }

    private static CancellationToken WaitToken(TimeSpan timeout) => new CancellationTokenSource(timeout).Token;
}
