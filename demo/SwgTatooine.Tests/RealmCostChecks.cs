using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>
/// SWG-08 / PRV-05 — the counted zero: at SWG scale, a realm nobody is in costs replication <b>exactly</b> nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is RLM-B's headline claim at workload scale, and until now it was only ever made at unit scale.</b> The engine has
/// <c>RealmSessionTests</c> and <c>RealmPolicyTests</c> proving that an unobserved realm is dispatched to no system and served to no session, on two or three
/// realms with a handful of entities each. What an SWG galaxy asks is different in kind: it registers a realm per enterable building — about 600 per planet —
/// and at any moment a thousand of them hold nobody while holding furniture, NPCs and, sometimes, players. "Free" has to mean free at that number or it means
/// nothing.
/// </para>
/// <para>
/// <b>Counted, not timed.</b> Every assertion here is an equality against zero or a strict inequality against it. A timing would answer "is it cheap", which
/// is a different and weaker question that a fast machine can answer yes to while the work is really being done.
/// </para>
/// <para>
/// <b>Both halves, always.</b> Each case asserts the zero AND a non-zero beside it, because a suite that only ever asserted zeros would pass against a server
/// that replicated nothing at all — which is the failure the assertions would be least likely to notice and most likely to be reassured by.
/// </para>
/// <para>
/// One world, built once: it is a two-planet galaxy with every building a realm, ticked with real sessions attached, which takes a few seconds to stand up and
/// is read by every case here.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class RealmCostChecks
{
    private string _dir;
    private TatooineSim _sim;
    private SessionHarness _harness;
    private RuntimeStatsSnapshot _stats;

    /// <summary>The interiors' realm ids, which are <c>Planets + planet·N + portal</c> — read from the sim rather than recomputed.</summary>
    private IEnumerable<RealmStat> Interiors =>
        _stats.Realms.Where(r => r.Realm >= _sim.Config.Planets && r.Realm < _sim.Config.Planets * (1 + _sim.InteriorsPerPlanet));

    [OneTimeSetUp]
    public void BuildGalaxy()
    {
        _dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();

        var config = Worlds.Small(_dir);
        config.Unpaced = false;
        config.TickRateHz = 40;
        config.WorkerCount = 2;
        config.Planets = 2;
        config.Interiors = true;
        config.Space = true;

        // The two rate knobs this fixture reads back: planet 1 and space are unobserved, so they run at their divisor, and planet 0 — where the sessions are —
        // runs at 1. Distinct values so a row reporting the wrong realm's divisor cannot pass.
        config.PlanetDivisor = 4;
        config.SpaceDivisor = 8;

        // Short enough that an interior nobody is in is dormant well inside the run.
        config.InteriorSleepS = 0.5f;

        // One dungeon, which is this fixture's POPULATED and UNOBSERVED realm. The simulation opens it on its first tick, teleports a party into it and pins it
        // active for as long as the party is inside — so it is a realm that is awake, full of players and mobs, and that no session is in. That combination is
        // the one the whole item is about, and this is the demo's own code producing it rather than a test arranging it. The interval and the stay are pushed
        // past any run of this fixture so it neither closes nor is followed by another while the cases read it.
        config.Dungeons = 1;
        config.DungeonIntervalS = 3600f;
        config.DungeonStayS = 3600f;
        _sim = new TatooineSim(config);
        _sim.Initialize();
        _harness = new SessionHarness(_sim);

        // A player client, which the demo hands a player of planet 0, and a god camera, which this fixture sends into one interior.
        var player = _harness.Connect(TatooineReplication.PlayerKind);
        _god = _harness.Connect(TatooineReplication.GodKind);
        SessionHarness.Until(() => player.Received(MessageTypes.Welcome) && _god.Received(MessageTypes.Welcome), "both WELCOMEs");
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the player client to be given a player");
        _harness.Ticks(2);

        // <b>The observed interior is staged by a COMMAND, not by moving an entity.</b> A first version teleported players into rooms from the test thread and
        // found them afterwards in neither realm's spatial index: an interior nobody is in is dormant, and the fence does not index into a dormant realm, so
        // the crossing does not complete until something wakes it. The demo never produces that state — its own portal path arrives with a session, and its
        // dungeons pin the realm before filling it — so reproducing it by hand was testing an impossible world. `ViewRealm` is the product's own way to put a
        // session in a realm, it is deterministic, and a session in a realm makes it Active by definition (RLM-03).
        _observed = (ushort)_sim.Config.Planets;
        _god.SendCommand(nameof(ViewRealm), new RecordValues { ["realm"] = FieldValue.Of((uint)_observed) });
        // <b>The realm table, not ReadStats, inside the predicate.</b> A snapshot allocates a row per registered realm — about 1 200 here — plus the archetype
        // list, and a spin predicate evaluates hundreds of times: the wait would allocate more than the thing it is waiting for. <b>A realm with a session in
        // it is Active by definition</b> (RLM-03, 12-realms § 1.6), so the run state is the same fact the row's session count reports and it is one load.
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return _sim.Dbe.Realms.StateOf(new RealmId(_observed)) == RealmRunState.Active;
            },
            "the god camera to arrive in the interior it asked for");

        _dungeon = (ushort)_sim.FirstDungeonRealm;
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return _sim.Dbe.Realms.IsRegistered(new RealmId(_dungeon)) && PartyInTheDungeon() > 0;
            },
            "the simulation to open its dungeon and put a party in it");

        // Past the sleep window several times over, so every interior nobody is in has had every chance to go dormant.
        _harness.Ticks(60);
        _stats = _sim.Runtime.ReadStats();
        Assert.That(_stats.Realms, Is.Not.Empty, "precondition: the stats snapshot reports realms at all");
    }

    /// <summary>
    /// Stops the world, resets the statics a served runtime leaves behind and removes the database.
    /// </summary>
    /// <remarks>
    /// <b>Every line here is load-bearing, and leaving it out broke a fixture three files away.</b> Without the release the keepalive static still points at
    /// this harness; without the dispose this galaxy keeps TICKING through the next fixture and <c>TatooineReplication</c>'s statics — the last runtime's
    /// <c>SubscriptionsCommands</c> above all — go on answering for a runtime nobody is using, so the next fixture's <c>SubjectOf</c> reads a session table
    /// that is not its own; and without the delete a 1 200-realm world is left under %TEMP% on every run. The order matters too: release before dispose, so no
    /// later wait can tick a disposed runtime.
    /// </remarks>
    [OneTimeTearDown]
    public void Dispose()
    {
        _harness?.Release();
        _sim?.Dispose();
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        Worlds.Delete(_dir);
    }

    /// <summary>
    /// How many players the dungeon holds, read through a transaction.
    /// </summary>
    /// <remarks>
    /// The transaction is not for isolation, it is for the epoch: a spatial query's chunk accessor must be created inside one, and a bare call asserts rather
    /// than answering. Cheap — a quick transaction is a handful of stores — and this is a wait predicate, not a hot path.
    /// </remarks>
    private int PartyInTheDungeon()
    {
        using var tx = _sim.Dbe.CreateQuickTransaction();
        return Worlds.PlayersIn(_sim.Dbe, _dungeon, Worlds.Everywhere).Count;
    }

    /// <summary>The god camera's link, kept so the last case can send it somewhere else.</summary>
    private FakeLink _god;

    /// <summary>The interior the god camera is in, so exactly one interior has a session.</summary>
    private ushort _observed;

    /// <summary>The dungeon: a realm the simulation pinned awake and filled with a party, and that no session is in.</summary>
    private ushort _dungeon;

    /// <summary>
    /// <c>UnobservedInterior_ZeroReplicationWork</c> — every interior no session is in has been served nothing at all, while the realm the sessions ARE in has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The interesting half is the interiors that hold ENTITIES and still cost nothing.</b> This world's players walk into buildings on their own, so at the
    /// end of the run a number of interiors hold players, NPCs and furniture and have no session in them — and replication's cost is supposed to follow the
    /// sessions, not the entities. An assertion over empty rooms alone would be true of an engine that charged per entity in an observed realm and simply
    /// had no entities to charge for.
    /// </para>
    /// <para>
    /// <b>And <c>Served</c> is asserted beside <c>Work</c>, because they are different facts.</b> Zero work could mean a realm that was served and found
    /// nothing to send; <c>Served == false</c> means replication holds no state for that realm at all, which is the claim — the cost is structurally absent
    /// rather than measured small.
    /// </para>
    /// </remarks>
    [Test]
    [Order(1)]
    public void UnobservedInterior_ZeroReplicationWork()
    {
        var empty = 0;
        foreach (var interior in Interiors)
        {
            if (interior.Sessions > 0)
            {
                Assert.That(interior.Realm, Is.EqualTo(_observed), "the only interior with a session is the one the god camera was sent into");
                continue;
            }

            empty++;
            Assert.That(interior.Served, Is.False, $"interior {interior.Realm} has no session, so replication must hold no state for it");
            Assert.That(interior.Work, Is.Zero,
                $"interior {interior.Realm} has no session and was served {interior.Work} units of work: enters {interior.Enters}, updates "
                + $"{interior.Updates}, leaves {interior.Leaves}, cells {interior.CellsDelivered}, resets {interior.Resets}, events {interior.Events}");
        }

        var served = _stats.Realms.Where(r => r.Served).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(empty, Is.GreaterThan(100), $"precondition: this is a scale claim and only {empty} interiors were unobserved");

            // <b>The realm that makes this about sessions rather than entities.</b> The dungeon is awake — the simulation pinned it — and holds a party of
            // players and a crowd of mobs, and no session is in it. If replication charged per entity in a live realm this row would be the one to show it.
            var dungeon = _stats.Realms.Single(r => r.Realm == _dungeon);
            var party = PartyInTheDungeon();
            Assert.That(party, Is.GreaterThan(0), "precondition: the dungeon holds its party");
            Assert.That(dungeon.State, Is.Not.EqualTo(RealmRunState.Dormant), "precondition: the dungeon is pinned awake, so this is not a dormancy result");
            Assert.That(dungeon.Sessions, Is.Zero, "precondition: no session is in the dungeon");
            Assert.That(dungeon.Served, Is.False, $"the dungeon is awake with {party} players in it and replication is holding state for it anyway");
            Assert.That(dungeon.Work, Is.Zero, $"the dungeon is awake with {party} players in it and cost {dungeon.Work} to replicate for nobody");

            // The other half, and the one that stops "zero everywhere" being the passing answer: the interior the god camera entered IS served, and was served
            // real work.
            var observed = _stats.Realms.Single(r => r.Realm == _observed);
            Assert.That(observed.Served, Is.True, "the interior the god camera is in is served");
            Assert.That(observed.Sessions, Is.EqualTo(1));
            Assert.That(observed.Work, Is.GreaterThan(0), "and it was served real work: the room, its people and the reset that carried them");
            Assert.That(served.All(r => r.Sessions > 0 || r.Realm == 0), Is.True,
                "replication holds state only for realms sessions are in — planet 0 keeps its own because it is the primary realm");
        });
    }

    /// <summary>
    /// <c>DormantRealm_ZeroMaintenanceOps</c> — most interiors are dormant at the end of the run, and not one of them has cost replication anything.
    /// </summary>
    /// <remarks>
    /// <b>Dormant is a stronger statement than unobserved and is asserted separately for that reason.</b> An unobserved realm is served to no session; a
    /// dormant one is additionally dispatched to no system, so its clusters are not walked, not repaired and not re-clustered. The demo is where that becomes
    /// a claim about a thousand realms rather than about one: <c>RealmPolicyTests.DormantRealm_ZeroClustersDispatched</c> is the same claim on three.
    /// </remarks>
    [Test]
    [Order(2)]
    public void DormantRealm_ZeroReplicationAndNoServedState()
    {
        // One pass, not two: Interiors is a lazily re-evaluated LINQ property over every row in the snapshot.
        var interiors = Interiors.ToArray();
        var dormant = Array.FindAll(interiors, r => r.State == RealmRunState.Dormant);
        var awake = Array.FindAll(interiors, r => r.State != RealmRunState.Dormant);

        Assert.Multiple(() =>
        {
            Assert.That(dormant, Has.Length.GreaterThan(100), "precondition: with --interior-sleep most of a galaxy's buildings are asleep");
            foreach (var realm in dormant)
            {
                Assert.That(realm.Served, Is.False, $"dormant interior {realm.Realm} still has replication state");
                Assert.That(realm.Work, Is.Zero, $"dormant interior {realm.Realm} cost {realm.Work}");
                Assert.That(realm.Sessions, Is.Zero, "a realm with a session in it is Active by definition (RLM-03), so a dormant one cannot hold one");
            }

            // The other half, and it is what stops "all dormant" being the passing answer: the registry agrees that most of this galaxy is asleep, and at
            // least one interior is awake.
            //
            // <b>Read live against a live threshold, not against the frozen snapshot's count.</b> Comparing the registry's Dormant NOW with a length taken in
            // setup was a race dressed as an equality: the world keeps ticking between the two reads, --interior-sleep is half a second, and players walk into
            // buildings on their own — so the two numbers differ by however much churned in between, and a tolerance wide enough to absorb that is wide enough
            // to absorb the bug. What is actually claimed is that a galaxy of empty rooms is overwhelmingly asleep.
            Assert.That(_sim.Dbe.Realms.Counts.Dormant, Is.GreaterThan(100), "a galaxy of empty buildings is not sleeping");
            Assert.That(awake.Select(r => (int)r.Realm), Does.Contain((int)_observed),
                "every interior asleep would mean the sleep policy is not the thing being observed: the one holding a session must be awake");
        });
    }

    /// <summary>
    /// A realm's simulation rate divisor is on the snapshot, and it is the one its configuration named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the reportable half of <c>Divisor_EachClusterOnceInNTicks</c>, and not the counted half.</b> That each of a divided realm's clusters is
    /// dispatched exactly once per N ticks is proved at engine scale by <c>RealmDivisorTests</c>, which can see the dispatch. Proving the same thing at demo
    /// scale needs a per-realm count of dispatched clusters, which is not on any public surface and which this does not add — so what is asserted here is what
    /// can be: the rate each realm is running at, per realm, read from outside, with three different values so a row carrying another realm's number cannot
    /// pass.
    /// </para>
    /// <para>
    /// Worth having on its own: an operator watching a realm board needs to know which realms are being simulated at a fraction of the rate, and before this
    /// the number existed only inside the realm table.
    /// </para>
    /// </remarks>
    [Test]
    [Order(3)]
    public void EachRealmReportsTheRateItIsSimulatedAt()
    {
        var byId = _stats.Realms.ToDictionary(r => r.Realm);
        Assert.Multiple(() =>
        {
            Assert.That(byId[0].Divisor, Is.EqualTo(1), "planet 0 holds the sessions, so it is observed and runs at full rate");
            Assert.That(byId[1].Divisor, Is.EqualTo(_sim.Config.PlanetDivisor), "planet 1 has nobody in it and runs at --planet-divisor");
            Assert.That(byId[(ushort)_sim.SpaceRealm].Divisor, Is.EqualTo(_sim.Config.SpaceDivisor), "space runs at its own divisor");
            Assert.That(_sim.Dbe.Realms.Counts.Divided, Is.GreaterThan(0), "the registry agrees that something is divided");
        });
    }

    /// <summary>
    /// A realm row names the realm it is about: its id, its generation, its kind and whether replication serves it.
    /// </summary>
    /// <remarks>
    /// The identity half, which the counted cases rest on. A row whose <c>Kind</c> or id were wrong would make every assertion above true of the wrong realm,
    /// and at 1 200 realms nobody would see it.
    /// </remarks>
    [Test]
    [Order(4)]
    public void EachRealmRowNamesTheRealmItIsAbout()
    {
        var byId = _stats.Realms.ToDictionary(r => r.Realm);
        var firstInterior = (ushort)_sim.Config.Planets;
        Assert.Multiple(() =>
        {
            Assert.That(_stats.Realms.Select(r => (int)r.Realm), Is.Unique, "one row per realm");
            Assert.That(byId[0].Kind, Is.Empty, "planet 0 is ConfigureSpatialGrid's realm and declares the default kind");
            Assert.That(byId[1].Kind, Is.Empty, "a planet is the default kind too");
            Assert.That(byId[firstInterior].Kind, Is.EqualTo(TatooineReplication.InteriorKind));
            Assert.That(byId[(ushort)_sim.SpaceRealm].Kind, Is.EqualTo(TatooineReplication.SpaceKind));
            Assert.That(_stats.Realms.All(r => r.Generation == 0), Is.True, "every realm here was registered in the session that created the database");
        });
    }

    /// <summary>
    /// An interior the last session LEFT stops being served, and stops costing anything from that moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case that actually exercises the mechanism, and the others do not.</b> A realm is given replication state when a session enters it, so a
    /// realm no session has ever been in never had any to drop — which means every assertion over the galaxy's thousand empty interiors passes whether or not
    /// the engine releases a realm it is finished with. Proved by mutant: neutering the sweep that deactivates an unobserved realm left every other case
    /// in this fixture green. A realm has to be entered and then left for the release to be the thing under test.
    /// </para>
    /// <para>
    /// <b>Asserted as "stops", not "is zero".</b> The room was served while the camera was in it, so its counters are non-zero for ever after — that is what
    /// cumulative means. The claim is that they do not move again, which is checked over enough ticks that a per-tick cost of one would show.
    /// </para>
    /// <para>
    /// <b>Ordered last, and every case in this fixture carries an <c>[Order]</c> because of how NUnit reads it.</b> A test with <c>[Order]</c> runs BEFORE
    /// every test without one — so putting the attribute on this case alone made it run FIRST, which is the opposite of what its comment claimed. It survived
    /// only because the other four read the snapshot frozen in setup. This case moves the god camera, so it must genuinely be last, and the only way to say
    /// that is to number them all.
    /// </para>
    /// </remarks>
    [Test]
    [Order(5)]
    public void AnInteriorTheLastSessionLeavesStopsBeingServedAndStopsCosting()
    {
        var before = _stats.Realms.Single(r => r.Realm == _observed);
        Assert.That(before.Served, Is.True, "precondition: the camera is in the interior and it is served");
        Assert.That(before.Work, Is.GreaterThan(0), "precondition: being served cost something");

        // Back to planet 0, which is the only other realm a god camera needs. The session leaves the interior in the tick the switch is published.
        _god.SendCommand(nameof(ViewRealm), new RecordValues { ["realm"] = FieldValue.Of(0u) });
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return _sim.Dbe.Realms.StateOf(new RealmId(_observed)) != RealmRunState.Active;
            },
            "the god camera to leave the interior");

        // <b>The release is a sweep, not a tick.</b> A realm left with no session is dropped by SweepUnplaced, which runs once every 64 ticks — so this waits
        // for the sweep rather than for a fixed number of ticks, which is the difference between a test that describes the contract and one that pins an
        // interval it does not care about.
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(8);
                return !_sim.Runtime.ReadStats().Realms.Single(r => r.Realm == _observed).Served;
            },
            "the sweep to release the interior nobody is in");

        var released = _sim.Runtime.ReadStats().Realms.Single(r => r.Realm == _observed);
        _harness.Ticks(40);
        var later = _sim.Runtime.ReadStats().Realms.Single(r => r.Realm == _observed);

        Assert.Multiple(() =>
        {
            Assert.That(released.Served, Is.False, "the interior nobody is in any more still has replication state");
            Assert.That(released.Sessions, Is.Zero);
            Assert.That(later.Work, Is.EqualTo(released.Work),
                $"the interior nobody is in cost {later.Work - released.Work} more over 40 ticks: enters +{later.Enters - released.Enters}, updates "
                + $"+{later.Updates - released.Updates}, leaves +{later.Leaves - released.Leaves}, cells +{later.CellsDelivered - released.CellsDelivered}, "
                + $"resets +{later.Resets - released.Resets}, events +{later.Events - released.Events}");

            // The other half: the realm the camera went BACK to is being served in those same ticks, so "nothing moved" is not true of the whole engine.
            var planet = _sim.Runtime.ReadStats().Realms.Single(r => r.Realm == 0);
            Assert.That(planet.Served, Is.True);
            Assert.That(planet.Sessions, Is.GreaterThan(0), "the camera is on planet 0 now");
        });
    }
}
