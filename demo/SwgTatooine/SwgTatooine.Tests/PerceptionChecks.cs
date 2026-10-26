using NUnit.Framework;
using Typhon.Client;
using Typhon.Protocol;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Server;
using SwgTatooine.Server.Terrain;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The scan that turns a bot's replicated store into the struct its brain decides from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one part of the brain that cannot be tested by writing a world down, so it is tested against the real
/// wire.</b> Everything above <c>Perception</c> is a pure function of a <see cref="Perceived"/> and is covered by
/// <see cref="BotBrainChecks"/>; this is the half that reads a <c>WorldStore</c> filled by the server's own frames, and
/// it had no test at all. Both of the bugs that made the first fleet useless lived here: a SELF gate that was true for
/// one frame in fifty, and a field index resolved against one archetype and read out of another.
/// </para>
/// <para>
/// <b>A real session, a real catalog, real frames.</b> The player is possessed and parked, the frames the server sends
/// it are fed through the same <c>FrameApplier</c> a browser uses, and the assertions are on what a bot standing there
/// would conclude. A hand-built store would have agreed with whatever the scan did.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class PerceptionChecks : ServedChecks
{
    /// <summary>Standing at a port, a bot sees its own body, learns the port, and knows it can board.</summary>
    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void StandingAtAPortItLearnsThePortAndKnowsItCanBoard()
    {
        using var sim = new TatooineSim(Worlds.Served(Dir));
        sim.Initialize();
        Harness = new SessionHarness(sim);
        var link = Connect();

        const int From = 0;
        var port = sim.Index.Shuttleports[From];
        Worlds.Park(sim, link.Session, port.X, port.Z);
        Harness.Ticks(6);

        var store = new WorldStore(link.Plan);
        var eyes = new Perception();
        var facilities = new FacilityMap();
        var world = Replay(link, store, eyes, facilities);

        Assert.Multiple(() =>
        {
            Assert.That(world.HasBody, Is.True, "a possessed session did not see its own body, which is what a per-frame SELF gate looks like");
            Assert.That(world.X, Is.EqualTo(port.X).Within(2f), "the body is not where the server put it, east");
            Assert.That(world.Z, Is.EqualTo(port.Z).Within(2f), "the body is not where the server put it, north");
            Assert.That(world.Realm, Is.Zero, "a body on the planet was read as being in an interior");

            // region = city + 1, so city 0's port is region 1. A bot that decoded this wrong would ask to fly to the
            // wrong city, or to the one it is standing in — which the server refuses.
            Assert.That(world.PortRegion, Is.EqualTo(From + 1), "standing on the port, the bot did not notice it could board");
            Assert.That(facilities.TryFind(From + 1, Routing.ShuttleportKind, out var learned), Is.True, "the port was not learned");
            Assert.That(learned.X, Is.EqualTo(port.X).Within(2f), "the learned port is in the wrong place, east");
            Assert.That(learned.Z, Is.EqualTo(port.Z).Within(2f), "the learned port is in the wrong place, north");
        });
    }

    /// <summary>Walking out of boarding range drops the prompt and keeps the memory.</summary>
    /// <remarks>
    /// The two halves are a pair and the second is the point of the table: a bot that forgot a port when it walked away
    /// could never plan a flight, because planning one needs the port at the FAR end of a journey it is not standing in.
    /// </remarks>
    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void WalkingOutOfRangeForgetsTheProximityAndNotThePort()
    {
        using var sim = new TatooineSim(Worlds.Served(Dir));
        sim.Initialize();
        Harness = new SessionHarness(sim);
        var link = Connect();

        var port = sim.Index.Shuttleports[0];
        Worlds.Park(sim, link.Session, port.X, port.Z);
        Harness.Ticks(6);

        var store = new WorldStore(link.Plan);
        var eyes = new Perception();
        var facilities = new FacilityMap();
        Assert.That(Replay(link, store, eyes, facilities).PortRegion, Is.EqualTo(1), "the premise failed: the bot was not at the port to begin with");

        // Well outside ShuttleBoardM, still inside the sphere that delivers the port.
        Worlds.Park(sim, link.Session, port.X + 60f, port.Z);
        Harness.Ticks(6);
        var away = Replay(link, store, eyes, facilities);

        Assert.Multiple(() =>
        {
            Assert.That(away.HasBody, Is.True, "the body was lost by moving it");
            Assert.That(away.PortRegion, Is.Zero, "60 m from the port, the bot still thought it could board");
            Assert.That(facilities.TryFind(1, Routing.ShuttleportKind, out _), Is.True, "walking away from a port made the bot forget it");
        });
    }

    /// <summary>A possessed player session, settled enough that the binding is visible to a transaction.</summary>
    /// <returns>Its link.</returns>
    /// <remarks>
    /// The two ticks after the counter are not padding: the possession counter moves INSIDE the tick that claims the
    /// body and the binding lands with that tick's commit, so a transaction opened the instant the counter moves finds
    /// no player and the case fails on its own premise.
    /// </remarks>
    /// <summary>
    /// character-level.md § 9: a bot reads its own combat level off the wire, and the nearest creature it would fight skips any six or more levels above
    /// it — a level 7 frog is red to a CL 1 bot, a level 6 one is not, however much nearer the red one stands.
    /// </summary>
    /// <remarks>
    /// A creature's <c>level</c> is on the wire once, on enter, and it is set at spawn — content-levels' spawn code, not this branch's — so the frogs' levels
    /// are written into the bot's store, in the column the wire fills, after the real frames have been applied. The store, the catalog and the scan are real.
    /// </remarks>
    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void APerceivedBot_ReadsItsLevel_AndSkipsCreaturesSixLevelsAbove()
    {
        var config = Worlds.Served(Dir);
        config.SpawnSpread = 0;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        var link = Connect();
        var body = TatooineReplication.ControlledBy(sim.Dbe, link.Session);
        PlayerPlacement at = default;
        sim.WriteInTick(tx => at = tx.Open(body).Read(Player.Bounds));

        var terrain = TerrainField.Shared(config.ContentScale);
        var red = EntityId.Null;
        var plain = EntityId.Null;
        sim.WriteInTick((tx, _) =>
        {
            var rng = new Rng(11);
            red = WorldBuilder.SpawnLair(tx, ref rng, CreatureTemplates.Frog, at.X + 15f, at.Z, 1, expireTick: 0, config, terrain, 0);
            plain = WorldBuilder.SpawnLair(tx, ref rng, CreatureTemplates.Frog, at.X - 45f, at.Z, 1, expireTick: 0, config, terrain, 0);
        });

        var redFrogs = new System.Collections.Generic.List<EntityId>();
        var plainFrogs = new System.Collections.Generic.List<EntityId>();
        sim.WriteInTick((tx, _) =>
        {
            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    var home = (EntityId)homes[slot].Lair;
                    if (home == red)
                    {
                        redFrogs.Add(cluster.GetEntityId(slot));
                    }
                    else if (home == plain)
                    {
                        plainFrogs.Add(cluster.GetEntityId(slot));
                    }
                }
            }
        });

        Harness.Ticks(6);
        foreach (var frog in redFrogs)
        {
            SessionHarness.UntilNamed(link.Session, frog, "the red frogs to reach the bot's view");
        }

        var redNetIds = redFrogs.ConvertAll(f => SessionHarness.NetIdOf(link.Session, f));
        var plainNetIds = plainFrogs.ConvertAll(f => SessionHarness.NetIdOf(link.Session, f));
        var store = new WorldStore(link.Plan);
        var eyes = new Perception();
        var facilities = new FacilityMap();
        var before = Replay(link, store, eyes, facilities);

        // The levels content-levels' spawn puts on the wire: 7 on the near lair's frogs, 6 on the far one's.
        var creatures = System.Array.FindIndex(store.Archetypes, a => a.Plan.Name == "Creature");
        var column = System.Array.FindIndex(store.Archetypes[creatures].Plan.Fields, f => f.Name == "level");
        Assert.That(column, Is.GreaterThanOrEqualTo(0), "the creature catalog carries no level");
        var set = store.Archetypes[creatures];
        for (var i = 0; i < set.LiveCount; i++)
        {
            var slot = set.Live[i];
            var netId = set.NetIds[slot];
            set.Numbers[column][slot] = redNetIds.Contains(netId) ? 7 : plainNetIds.Contains(netId) ? 6 : 0;
        }

        var world = eyes.Read(store, facilities);
        Assert.Multiple(() =>
        {
            Assert.That(world.Level, Is.EqualTo(1), "the bot did not read its own level");
            Assert.That(redNetIds, Is.Not.Empty, "the premise: the red lair has frogs");
            Assert.That(redNetIds, Has.None.Zero, "the premise: the red frogs are in the bot's view");
            Assert.That(redNetIds, Does.Contain(before.NearestCreature), "the premise: unlevelled, a red frog is the nearest");
            Assert.That(world.NearestCreature, Is.Not.Zero, "the level 6 frogs, in view, were skipped too");
            Assert.That(redNetIds, Does.Not.Contain(world.NearestCreature), "the bot picked a creature six levels above it");
            Assert.That(Perception.IsRed(7, 1), Is.True);
            Assert.That(Perception.IsRed(6, 1), Is.False);
            Assert.That(Perception.IsRed(50, 0), Is.False, "an unknown level is never red");
        });
    }

    private FakeLink Connect()
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the possession to be claimed by a tick");
        Harness.Ticks(2);
        return link;
    }

    /// <summary>Feeds every frame this session has been sent through the applier, then reads the store.</summary>
    /// <param name="link">The session's link.</param>
    /// <param name="store">The store to fill.</param>
    /// <param name="eyes">The scan under test.</param>
    /// <param name="facilities">The map it teaches.</param>
    /// <returns>What a bot standing there would decide from.</returns>
    /// <remarks>
    /// Every TICK message, from the first: a store is a fold over frames and the SELF block arrives in one of the early
    /// ones, so replaying only the newest would be reading a store the client never actually holds.
    /// </remarks>
    private static Perceived Replay(FakeLink link, WorldStore store, Perception eyes, FacilityMap facilities)
    {
        var applier = new FrameApplier(store);
        var sent = link.Sent;
        var world = default(Perceived);
        for (var i = 0; i < sent.Length; i++)
        {
            if (sent[i].Length > 0 && sent[i][0] == MessageTypes.Tick)
            {
                applier.Apply(sent[i]);
                world = eyes.Read(store, facilities);
            }
        }

        return world;
    }
}
