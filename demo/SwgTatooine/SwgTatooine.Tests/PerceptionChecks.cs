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

    /// <summary>
    /// CL-13: a bot does not hunt a creature far above its level. Snakes of level 45 stand 15 m from the bot: its eyes see them, with their level read off
    /// the wire, and a CL-1 brain does not make them a hunt where a CL-45 brain does.
    /// </summary>
    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void ABot_LeavesACreatureFarAboveItsLevel()
    {
        using var sim = new TatooineSim(Worlds.Served(Dir));
        sim.Initialize();
        Harness = new SessionHarness(sim);
        var link = Connect();

        var port = sim.Index.Shuttleports[0];
        Worlds.Park(sim, link.Session, port.X + 40f, port.Z, out var parked);
        var lair = EntityId.Null;
        var config = sim.Config;
        sim.WriteInTick(tx =>
        {
            var rng = new Rng(3);
            lair = WorldBuilder.SpawnLair(tx, ref rng, CreatureTemplates.Snake, 45, port.X + 55f, port.Z, site: 0, expireTick: 0, config,
                SwgTatooine.Server.Terrain.TerrainField.Shared(config.ContentScale), 0);
        });
        SessionHarness.Until(() => !lair.IsNull, "the lair to be spawned");
        SessionHarness.UntilNamed(link.Session, lair, "the lair's view to reach the session", parked);
        Harness.Ticks(6);

        var snakes = new System.Collections.Generic.HashSet<uint>();
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    EntityId home = homes[slot].Lair;
                    if (home == lair)
                    {
                        snakes.Add(SessionHarness.NetIdOf(link.Session, cluster.GetEntityId(slot)));
                    }
                }
            }
        }

        var world = Replay(link, new WorldStore(link.Plan), new Perception(), new FacilityMap());
        var low = new BotBrain(BotMap.Cities, 7u) { CombatLevel = 1 };
        var high = new BotBrain(BotMap.Cities, 7u) { CombatLevel = 45 };
        low.Decide(in world, 1);
        high.Decide(in world, 1);
        Assert.Multiple(() =>
        {
            Assert.That(snakes, Is.Not.Empty.And.Not.Contains(0u), "the snakes are not in the session's view");
            Assert.That(world.NearestCreature, Is.AnyOf([.. snakes]), "the bot does not see the level-45 snakes 15 m away: the premise failed");
            Assert.That(world.NearestCreatureLevel, Is.EqualTo(45), "the level read off the wire is not the snakes'");
            Assert.That(high.Goal, Is.EqualTo(Goal.Hunting), "a CL-45 bot does not hunt a level-45 snake 15 m away: the premise failed");
            Assert.That(low.Goal, Is.Not.EqualTo(Goal.Hunting), "a CL-1 bot hunts a level-45 snake");
        });
    }

    /// <summary>
    /// character-level.md § 9: a bot reads its own combat level off the wire, and, given it, hunts what is within it — a CL 12 character's bot takes on a
    /// level 15 creature that a CL 1 bot leaves alone.
    /// </summary>
    /// <remarks>The character is given CL 12 between two logins, so the body that carries it is spawned with it, as any login's is.</remarks>
    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void APerceivedBot_ReadsItsLevel_AndHuntsWithinIt()
    {
        var config = Worlds.Served(Dir);
        config.SpawnSpread = 0;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        var account = EntityId.FromRawValue(0x8301);
        var first = Harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the first body to be spawned");
        Harness.Ticks(2);
        Assert.That(SwgTatooine.Server.Accounts.CharacterStore.TryFind(sim.Dbe, account, out var character, out _), Is.True, "no character");
        sim.WriteInTick(tx => tx.OpenMut(character).Set(CharacterArch.Progress, new CharacterProgress { Level = 12, Xp = Progression.XpAt(12) }));
        var destroyed = TatooineReplication.AvatarsDestroyed;
        first.Close(CloseCodes.Normal, "logging out");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed > destroyed, "the first body to be destroyed");

        var link = Harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 2, "the second body to be spawned");
        Harness.Ticks(2);
        var body = TatooineReplication.ControlledBy(sim.Dbe, link.Session);
        PlayerPlacement at = default;
        sim.WriteInTick(tx => at = tx.Open(body).Read(Player.Bounds));

        var lair = EntityId.Null;
        sim.WriteInTick(tx =>
        {
            var rng = new Rng(11);
            lair = WorldBuilder.SpawnLair(tx, ref rng, CreatureTemplates.Frog, 15, at.X + 15f, at.Z, site: 0, expireTick: 0, config,
                TerrainField.Shared(config.ContentScale), 0);
        });
        SessionHarness.UntilNamed(link.Session, lair, "the lair to reach the bot's view");
        Harness.Ticks(6);

        // As BotSwarm does: the level read off the wire becomes the brain's combat level, and the brain bounds the hunt with it.
        var world = Replay(link, new WorldStore(link.Plan), new Perception(), new FacilityMap());
        var unlevelled = new BotBrain(BotMap.Cities, 7u) { CombatLevel = 1 };
        var levelled = new BotBrain(BotMap.Cities, 7u) { CombatLevel = world.Level };
        unlevelled.Decide(in world, 1);
        levelled.Decide(in world, 1);
        Assert.Multiple(() =>
        {
            Assert.That(world.Level, Is.EqualTo(12), "the bot did not read its own level off the wire");
            Assert.That(world.NearestCreatureLevel, Is.EqualTo(15), "the premise: the level 15 frogs are the nearest creature");
            Assert.That(unlevelled.Goal, Is.Not.EqualTo(Goal.Hunting), "the premise: a CL 1 bot leaves a level 15 creature alone");
            Assert.That(levelled.Goal, Is.EqualTo(Goal.Hunting), "a CL 12 bot left a level 15 creature, within its reach, alone");
        });
    }

    /// <summary>A possessed player session, settled enough that the binding is visible to a transaction.</summary>
    /// <returns>Its link.</returns>
    /// <remarks>
    /// The two ticks after the counter are not padding: the possession counter moves INSIDE the tick that claims the
    /// body and the binding lands with that tick's commit, so a transaction opened the instant the counter moves finds
    /// no player and the case fails on its own premise.
    /// </remarks>
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
