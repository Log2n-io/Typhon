using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>
/// CLI3D-11: the per-realm population the realm inventory is built on — that it is exact, that it still answers for a
/// realm the policy put to sleep, and that it costs a measured run nothing.
/// </summary>
/// <remarks>
/// <b>The dormant case is the one this whole file exists for.</b> "An unobserved realm costs zero" is the load-bearing
/// claim of the realms design and a dormant realm is dispatched to no system, so a census built out of system passes
/// would report every sleeping realm's population as whatever it held when it last ran — and the sleeping realms are the
/// ones the panel is about. Walking the clusters through the transaction is what makes them visible, so a case that
/// pins it is what stops somebody moving this back into a <c>QuerySystem</c> for looking tidier.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class RealmCensusChecks
{
    private string _dir;

    [SetUp]
    public void SetUp() => _dir = Worlds.NewDirectory();

    [TearDown]
    public void TearDown() => Worlds.Delete(_dir);

    private SimConfig Config()
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;

        // 40 Hz for the same reason SpectateChecks uses it: every wait in here is wall-clock, and these cases step tens
        // of ticks. Nothing under test depends on the period beyond the cadence arithmetic, which is asserted directly.
        config.TickRateHz = 40;
        config.WorkerCount = 1;
        config.Interiors = true;
        config.Shuttles = false;
        return config;
    }

    /// <summary>What a realm's spatial index answers for, counted the long way round: a different path from the census's.</summary>
    private static int CountIn<T>(DatabaseEngine dbe, ushort realm)
        where T : Archetype<T>, new()
    {
        var seen = new HashSet<EntityId>();
        Span<ClusterSpatialQueryResult> buffer = new ClusterSpatialQueryResult[128];
        var box = Worlds.Everywhere;

        // The query rents a chunk accessor, and one created outside an epoch scope is a Debug.Fail: a system body runs
        // under the scheduler's scope, and a caller walking the index from its own thread has to open one. A read-only
        // quick transaction is the supported way to get one from outside the engine — RealmChecks' own helper is called
        // inside one for exactly this reason.
        using var tx = dbe.CreateQuickTransaction();
        var e = dbe.ClusterSpatialQuery<T>(new RealmId(realm)).AABB(in box);
        try
        {
            int got;
            while ((got = e.Fill(buffer)) > 0)
            {
                for (var i = 0; i < got; i++)
                {
                    seen.Add(buffer[i].Entity);
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        return seen.Count;
    }

    /// <summary>Waits for the census to publish a snapshot taken after <paramref name="after"/>, and returns it.</summary>
    private static RealmCensusSnapshot AwaitCensus(TatooineSim sim, SessionHarness harness, long after)
    {
        RealmCensusSnapshot snapshot = null;
        SessionHarness.Until(
            () =>
            {
                harness.Ticks(1);
                snapshot = sim.RealmPopulation.Current;
                return snapshot.Tick > after;
            },
            "a census taken after the world settled");
        return snapshot;
    }

    /// <summary>
    /// AC-1: what the census says a realm holds is what that realm's own spatial index answers for.
    /// </summary>
    /// <remarks>
    /// Checked against the spatial query rather than against a second cluster walk, so the two sides do not share the
    /// mechanism under test: an occupancy bit the census misreads would have to be missed by the index in the same way.
    /// </remarks>
    [Test]
    public void TheCensusCountsWhatEachRealmActuallyHolds()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        var harness = new SessionHarness(sim);
        harness.Ticks(5);

        var census = AwaitCensus(sim, harness, sim.Runtime.CurrentTickNumber);
        Assert.That(sim.InteriorsPerPlanet, Is.GreaterThan(0), "the world has no interiors, so this case proves nothing about realms");

        // Planet 0 and the first few interiors: enough to cover a realm with everything in it and realms with only NPCs.
        var realms = new List<ushort> { 0 };
        for (ushort interior = 1; interior <= 3 && interior <= sim.InteriorsPerPlanet; interior++)
        {
            realms.Add(interior);
        }

        Assert.Multiple(() =>
        {
            foreach (var realm in realms)
            {
                var row = census.Of(realm);

                // Players first, because they are the column a viewer actually watches — the one that changes when a
                // bot walks through a door — and the one every other case here reads.
                Assert.That(row.Players, Is.EqualTo(CountIn<Player>(sim.Dbe, realm)), $"realm {realm}: players");
                Assert.That(row.Npcs, Is.EqualTo(CountIn<CityNpc>(sim.Dbe, realm)), $"realm {realm}: NPCs");
                Assert.That(row.Creatures, Is.EqualTo(CountIn<Creature>(sim.Dbe, realm)), $"realm {realm}: creatures");
                Assert.That(row.Structures,
                    Is.EqualTo(CountIn<WorldObject>(sim.Dbe, realm) + CountIn<CreatureLair>(sim.Dbe, realm)),
                    $"realm {realm}: structures");
            }
        });
    }

    /// <summary>
    /// AC-2: a realm the policy has put to sleep still reports the population it holds.
    /// </summary>
    /// <remarks>
    /// The case the design turns on. A dormant realm is dispatched to no system — that IS dormancy — so anything counted
    /// from a system pass reports a sleeping realm's population as whatever it held when it last ran, which for an
    /// interior nobody has entered is zero forever. The transaction's cluster enumerator is not realm-filtered, and that
    /// is the whole reason the census is shaped the way it is.
    /// </remarks>
    [Test]
    public void ARealmThePolicyPutToSleepIsStillCounted()
    {
        var config = Config();

        // Short enough that an untouched interior is asleep within a few ticks of the run starting.
        config.InteriorSleepS = 0.1f;
        config.InteriorNpcs = Math.Max(1, config.InteriorNpcs);
        using var sim = new TatooineSim(config);
        sim.Initialize();
        var harness = new SessionHarness(sim);

        // An interior nobody has been in: no session observes it and no pin holds it, so the policy has nothing to keep
        // it awake. Which one hardly matters, so take the last, the least likely to be somebody's first destination.
        var interior = (ushort)sim.InteriorsPerPlanet;
        var npcs = CountIn<CityNpc>(sim.Dbe, interior);
        Assert.That(npcs, Is.GreaterThan(0), "the interior chosen has nobody in it, so a count of 0 would prove nothing");

        SessionHarness.Until(
            () =>
            {
                harness.Ticks(1);
                return sim.Dbe.Realms.StateOf(new RealmId(interior)) == RealmRunState.Dormant;
            },
            "the untouched interior to go dormant");

        var census = AwaitCensus(sim, harness, sim.Runtime.CurrentTickNumber);
        Assert.Multiple(() =>
        {
            Assert.That(sim.Dbe.Realms.StateOf(new RealmId(interior)), Is.EqualTo(RealmRunState.Dormant), "the interior woke up under the census");
            Assert.That(census.Of(interior).Npcs, Is.EqualTo(npcs), "a dormant realm's population was not counted");
        });
    }

    /// <summary>
    /// AC-3: the census runs on its cadence and not on every tick.
    /// </summary>
    /// <remarks>
    /// The assertion is on the published tick numbers rather than on a call counter, because the cadence is the whole
    /// cost story: at 40 Hz and 1 Hz this walk happens forty times less often than it could, and a gate that silently
    /// stopped gating would be a forty-fold regression that every other case here would still pass.
    /// </remarks>
    [Test]
    public void TheCensusRunsOnItsCadenceAndNotEveryTick()
    {
        var config = Config();

        // 40 Hz / 8 = one walk every 5 ticks. A shorter period than the obvious 1 Hz on purpose: every tick here is a
        // real wall-clock wait, and four periods prove the gate exactly as well as four do at ten ticks each.
        config.RealmCensusHz = 8f;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        var harness = new SessionHarness(sim);

        var ticks = new List<long>();
        var deadline = sim.Runtime.CurrentTickNumber + 22;
        while (sim.Runtime.CurrentTickNumber < deadline)
        {
            harness.Ticks(1);
            var tick = sim.RealmPopulation.Current.Tick;
            if (tick >= 0 && (ticks.Count == 0 || ticks[^1] != tick))
            {
                ticks.Add(tick);
            }
        }

        Assert.That(ticks, Is.Not.Empty, "no census was taken at all");
        Assert.Multiple(() =>
        {
            foreach (var tick in ticks)
            {
                Assert.That(tick % 5, Is.Zero, $"a census was taken at tick {tick}, which is not on the 5-tick cadence");
            }

            // Twenty-two ticks at one every five is four or five walks. More than that is the gate not gating.
            Assert.That(ticks, Has.Count.LessThanOrEqualTo(6), "the census ran more often than its cadence allows");
        });
    }

    /// <summary>AC-3: a census Hz of zero puts no census system in the DAG, so nothing is ever walked.</summary>
    /// <remarks>
    /// The escape hatch for a run that wants the serving path without the walk — and the thing that makes
    /// <see cref="SimConfig.RealmCensusHz"/> a real switch rather than a documented intention.
    /// </remarks>
    [Test]
    public void ACensusHzOfZeroTakesNoCensusAtAll()
    {
        var config = Config();
        config.RealmCensusHz = 0f;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        var harness = new SessionHarness(sim);
        harness.Ticks(30);

        Assert.That(sim.RealmPopulation.Current.Tick, Is.EqualTo(-1), "a census was taken with --realm-census-hz 0");
        Assert.That(sim.RealmPopulation.Current.Count, Is.Zero);
    }

    /// <summary>
    /// AC-4: a player walking into an interior shows up in that realm's count, and is gone from it when they leave.
    /// </summary>
    /// <remarks>
    /// Staged rather than waited for. A bot uses a door only on an activity re-decision that rolls under the interior
    /// share, so waiting for the world to produce a crossing makes a case that passes in minutes or never; the teleport
    /// here is the same one the portal path commits, and what is under test is the census following it.
    /// </remarks>
    [Test]
    public void APlayerCrossingIntoARealmIsCountedThere()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        var harness = new SessionHarness(sim);
        harness.Ticks(5);

        var interior = (ushort)sim.InteriorsPerPlanet;
        var player = sim.Index.Players[0];
        var before = AwaitCensus(sim, harness, sim.Runtime.CurrentTickNumber).Of(interior).Players;

        using (var tx = sim.Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            tx.Teleport(player, Player.Bounds, new RealmId(interior), Worlds.At(32.5f, 6.5f));
            Assert.That(tx.Commit(), Is.True);
        }

        var inside = AwaitCensus(sim, harness, sim.Runtime.CurrentTickNumber);
        Assert.That(inside.Of(interior).Players, Is.EqualTo(before + 1), "the crossing was not counted in the interior");

        using (var tx = sim.Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            tx.Teleport(player, Player.Bounds, RealmId.Default, Worlds.At(0f, 0f));
            Assert.That(tx.Commit(), Is.True);
        }

        var outside = AwaitCensus(sim, harness, sim.Runtime.CurrentTickNumber);
        Assert.That(outside.Of(interior).Players, Is.EqualTo(before), "the player left the interior and the count did not");
    }

    /// <summary>
    /// Looking a realm up finds the right row, and answers zero for one the census found nothing in.
    /// </summary>
    /// <remarks>
    /// <b>Against a populated snapshot, because the empty one cannot exercise the lookup at all.</b> The rows are
    /// ascending and the scan stops early on that assumption, so a realm between two rows must read zero rather than
    /// picking up its neighbour's population — and every realm a document mentions goes through here.
    /// </remarks>
    [Test]
    public void ARealmIsLookedUpByItsOwnIdAndNotItsNeighbours()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        var harness = new SessionHarness(sim);
        harness.Ticks(5);

        var census = AwaitCensus(sim, harness, sim.Runtime.CurrentTickNumber);
        Assert.That(census.Count, Is.GreaterThan(1), "this world produced fewer than two populated realms, so the scan is untested");

        // Every row answers for itself, and for nothing else.
        var byId = new Dictionary<ushort, RealmCensusRow>();
        foreach (var row in census.Rows)
        {
            byId[row.Realm] = row;
        }

        Assert.Multiple(() =>
        {
            foreach (var (realm, row) in byId)
            {
                Assert.That(census.Of(realm), Is.EqualTo(row), $"realm {realm} looked up as something else");
            }

            // A realm the census found nothing in, between two it did: the early break must not hand back a neighbour.
            var highest = 0;
            foreach (var row in census.Rows)
            {
                highest = Math.Max(highest, row.Realm);
            }

            for (var realm = 0; realm <= highest; realm++)
            {
                if (!byId.ContainsKey((ushort)realm))
                {
                    Assert.That(census.Of((ushort)realm).Total, Is.Zero, $"realm {realm} held nothing and did not read as zero");
                }
            }
        });
    }

    /// <summary>A realm the census found nothing in reads as zero rather than as missing.</summary>
    /// <remarks>
    /// The rows hold only the realms that held something — a two-planet shipping map has more than twelve hundred
    /// interiors and most of them are empty — so the lookup has to answer for the others. A caller formatting a document
    /// realm by realm must not have to know which ones were omitted.
    /// </remarks>
    [Test]
    public void ARealmThatHeldNothingReadsAsZero()
    {
        var snapshot = RealmCensusSnapshot.Empty;
        var row = snapshot.Of(4242);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Tick, Is.EqualTo(-1));
            Assert.That(snapshot.Count, Is.Zero);
            Assert.That(row.Realm, Is.EqualTo(4242));
            Assert.That(row.Total, Is.Zero);
        });
    }
}
