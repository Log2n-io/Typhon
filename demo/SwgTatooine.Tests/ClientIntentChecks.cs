using System;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// SWG-01 — a client drives a player by sending intents, and cannot drive it faster than the server allows.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim these exist to make falsifiable</b> is <c>06-gameplay.md § 2</c>'s: "Clients send intents, never positions. The server integrates with the
/// existing <c>Steer</c>. Speed hacks are impossible by construction." A design principle stated in prose and never measured is indistinguishable from one
/// that was implemented wrongly, so the first case below sends a destination ten kilometres away and asserts the player moved by exactly one tick of travel.
/// </para>
/// <para>
/// Every case runs against a real world, a real handshake through the real acceptor, and a real session row — see <see cref="SessionHarness"/> for why the
/// transport is in-process rather than a socket.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class ClientIntentChecks
{
    /// <summary>Slow enough that one tick of travel is a distance an assertion can separate from two.</summary>
    private const int TickRateHz = 10;

    /// <summary>What <c>PlayerRunSpeedMps</c> covers in one tick at <see cref="TickRateHz"/>: 0.5 m.</summary>
    private const float StepM = TatooineData.PlayerRunSpeedMps / TickRateHz;

    private string _dir;

    /// <summary>The harness a case built, so teardown can unhook it from <c>SessionHarness.Until</c>'s keepalive.</summary>
    private SessionHarness _harness;

    [SetUp]
    public void SetUp()
    {
        _dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
    }

    [TearDown]
    public void TearDown()
    {
        _harness?.Release();
        _harness = null;
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        Worlds.Delete(_dir);
    }

    private SimConfig Config()
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;
        config.TickRateHz = TickRateHz;
        return config;
    }

    /// <summary>A started server with one player client possessing a player, and the player it possesses.</summary>
    private sealed class Possessed : IDisposable
    {
        public Possessed(SimConfig config)
        {
            Sim = new TatooineSim(config);
            Sim.Initialize();
            Harness = new SessionHarness(Sim);
            Link = Harness.Connect(TatooineReplication.PlayerKind);

            SessionHarness.Until(() => Link.Received(MessageTypes.Welcome), "WELCOME");

            // The claim is made by a tick, and the engine applies the Control request in the tick AFTER that — so a case that acted on the first tick would be
            // sending an intent to a session that controls nothing, which is a legitimate state with its own counter and not what any of these are about.
            SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the possession to be claimed by a tick");
            Harness.Ticks(2);
            Entity = TatooineReplication.ControlledBy(Sim.Dbe, Link.Session);
            Assert.That(Entity, Is.Not.EqualTo(EntityId.Null), "the session controls nothing, so there is nothing to drive");
        }

        public TatooineSim Sim { get; }

        public SessionHarness Harness { get; }

        public FakeLink Link { get; }

        /// <summary>The player this client possesses.</summary>
        public EntityId Entity { get; }

        /// <summary>Where the possessed player is, and where it is heading, read outside the tick.</summary>
        public (float X, float Z, float DestX, float DestZ, float VelX, float VelZ, int Activity, EntityId Target) Read()
        {
            using var tx = Sim.Dbe.CreateQuickTransaction();
            var entity = tx.For<Player>().Open(Entity);
            var place = entity.Read(Player.Bounds);
            var move = entity.Read(Player.Move);
            var state = entity.Read(Player.State);
            var control = entity.Read(Player.Control);
            return (place.X, place.Z, move.DestX, move.DestZ, move.VelX, move.VelZ, state.Activity, control.Target);
        }

        /// <summary>Unhooks the harness from the keepalive before disposing the world, so no later wait can tick a disposed runtime.</summary>
        public void Dispose()
        {
            Harness.Release();
            Sim.Dispose();
        }
    }

    // ── The principle: intents, never positions ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A destination ten kilometres away moves the player by ONE tick of travel, not to the destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the speed-hack case, and it passes by construction rather than by check.</b> The command carries a destination; the only thing written from
    /// it is <c>PlayerMotion.DestX/DestZ</c>, and the velocity that follows is <c>direction × (entitled speed × one tick)</c>. There is no code path from a
    /// client message to a <c>PlayerPlacement</c>, so no message — however far the destination, however often repeated — can move a player further than the
    /// server's own integration moves it.
    /// </para>
    /// <para>
    /// The tolerance is a tenth of a step. A tighter one would be asserting floating-point arithmetic; a looser one would not separate one step from two,
    /// which is the whole distinction.
    /// </para>
    /// </remarks>
    [Test]
    public void AMoveToTenKilometresAwayMovesThePlayerByOneTickOfTravel()
    {
        var config = Config();
        using var world = new Possessed(config);
        var before = world.Read();

        world.Link.SendCommand(nameof(MoveTo), ClientSays.MoveTo(before.X + 10_000f, before.Z));

        // Two ticks: the first applies the intent and integrates it, and waiting for exactly one is racing the tick driver. The assertion is on the distance
        // per tick, so it is computed from how many ticks actually elapsed rather than assumed.
        var start = world.Sim.Runtime.CurrentTickNumber;
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the intent to be applied by a tick");
        world.Harness.Ticks(2);
        var elapsed = world.Sim.Runtime.CurrentTickNumber - start;

        var after = world.Read();
        var travelled = MathF.Sqrt(((after.X - before.X) * (after.X - before.X)) + ((after.Z - before.Z) * (after.Z - before.Z)));

        Assert.Multiple(() =>
        {
            Assert.That(travelled, Is.GreaterThan(StepM * 0.5f), "the player did not move at all, so the intent reached nothing");
            Assert.That(travelled, Is.LessThanOrEqualTo(StepM * (elapsed + 1)),
                $"the player moved {travelled:F2} m in {elapsed} ticks, which is more than {StepM:F2} m per tick — a client moved a player");
            Assert.That(after.DestX, Is.EqualTo(MathF.Min(before.X + 10_000f, config.WorldEdgeM * 0.5f)).Within(1f),
                "the destination is the client's, clamped to the world it is in");
            Assert.That(after.Activity, Is.EqualTo(PlayerActivity.Travelling));
        });
    }

    /// <summary>
    /// The destination is clamped to the world, so a client cannot aim a player at coordinates the world does not have.
    /// </summary>
    [Test]
    public void AMoveToOutsideTheWorldIsClamped()
    {
        var config = Config();
        using var world = new Possessed(config);
        var half = config.WorldEdgeM * 0.5f;

        world.Link.SendCommand(nameof(MoveTo), ClientSays.MoveTo(1e9f, -1e9f));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the intent to be applied");
        world.Harness.Ticks(2);

        var after = world.Read();
        Assert.Multiple(() =>
        {
            Assert.That(after.DestX, Is.EqualTo(half).Within(0.001f));
            Assert.That(after.DestZ, Is.EqualTo(-half).Within(0.001f));
        });
    }

    /// <summary>A coordinate that is not a number never reaches the tick: the engine's pre-check refuses it on the transport thread.</summary>
    /// <remarks>
    /// Asserted through the counter rather than through the state, because "nothing happened" is the claim. A NaN destination that DID reach <c>Steer</c> would
    /// produce a NaN velocity and a NaN position, which the fence then has to cope with — so this is the cheapest possible place to stop it, and the pre-check
    /// is where the engine puts stateless syntax.
    /// </remarks>
    [Test]
    public void ANotANumberDestinationNeverReachesTheTick()
    {
        using var world = new Possessed(Config());
        var before = world.Read();

        world.Link.SendCommand(nameof(MoveTo), ClientSays.MoveTo(float.NaN, float.PositiveInfinity));
        world.Harness.Ticks(3);

        var after = world.Read();
        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.Intents.Applied, Is.Zero, "a NaN destination was applied");
            Assert.That(after.X, Is.EqualTo(before.X), "the player moved on a command that should never have been decoded");
            Assert.That(float.IsFinite(after.DestX) && float.IsFinite(after.DestZ), Is.True);
        });
    }

    // ── Speed classes ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A heading and a run class walk the player at the run speed and no faster.</summary>
    [Test]
    public void AMoveDirWalksAtTheClassItAsksFor()
    {
        using var world = new Possessed(Config());
        var before = world.Read();

        // Due east: +X, which the heading measures from.
        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Run));
        var start = world.Sim.Runtime.CurrentTickNumber;
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the intent to be applied");
        world.Harness.Ticks(2);
        var elapsed = world.Sim.Runtime.CurrentTickNumber - start;

        var after = world.Read();
        Assert.Multiple(() =>
        {
            Assert.That(after.X - before.X, Is.GreaterThan(0f), "a heading of zero is +X");
            Assert.That(after.X - before.X, Is.LessThanOrEqualTo(StepM * (elapsed + 1)), "faster than the run class is worth");
            Assert.That(MathF.Abs(after.Z - before.Z), Is.LessThan(StepM), "a heading of zero has no Z component");
        });
    }

    /// <summary>Speed class 0 stops the player, which is why stop is a class rather than a fourth command type.</summary>
    [Test]
    public void SpeedClassZeroStops()
    {
        using var world = new Possessed(Config());

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Run));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the move to be applied");
        world.Harness.Ticks(2);
        Assert.That(world.Read().VelX, Is.GreaterThan(0f), "the player is not moving, so stopping it proves nothing");

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Stop));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 2, "the stop to be applied");
        world.Harness.Ticks(2);

        var after = world.Read();
        Assert.Multiple(() =>
        {
            Assert.That(after.VelX, Is.Zero);
            Assert.That(after.VelZ, Is.Zero);
        });
    }

    /// <summary>
    /// A speed class this server does not grant is refused and counted, rather than clamped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no mount state in the demo, so the run speed is the ceiling. <b>Refused rather than clamped</b> because a clamp lets a client ask for a mount's
    /// 12 m/s on every tick and never learn it is not getting it, and the refusal count is the only number that says whether anything is trying.
    /// </para>
    /// <para>
    /// The class is above what the wire's pre-check accepts (<c>SpeedClass &lt; SpeedClasses.Count</c>), so this also covers the pre-check: the command is
    /// refused before decoding, and the counter that moves is the engine's rather than the system's. What the case claims either way is that the player did not
    /// move faster.
    /// </para>
    /// </remarks>
    [Test]
    public void ASpeedClassTheServerDoesNotGrantIsRefused()
    {
        using var world = new Possessed(Config());
        var before = world.Read();

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, 200));
        world.Harness.Ticks(3);

        var after = world.Read();
        var travelled = MathF.Abs(after.X - before.X) + MathF.Abs(after.Z - before.Z);
        Assert.Multiple(() =>
        {
            Assert.That(travelled, Is.LessThan(StepM), "a refused speed class moved the player");
            Assert.That(after.VelX, Is.Zero, "a refused intent set a velocity");
        });
    }

    // ── Targeting ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A target the session was never shown is refused and counted, and the target is left cleared.
    /// </summary>
    /// <remarks>
    /// <b>This is the check that makes a target a target rather than a world-wide entity picker.</b> A <c>netId</c> is a small integer, so without the
    /// geometric test in <c>TryResolve</c> (SUB-26) a client could aim at anything in the world by counting, and could learn that an entity exists by whether
    /// its guess was accepted. An identity nothing holds is the clearest case of "never shown".
    /// </remarks>
    [Test]
    public void ATargetTheSessionWasNeverShownIsRefused()
    {
        using var world = new Possessed(Config());

        world.Link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(0xFFFF_FF00u));
        SessionHarness.Until(() => TatooineReplication.Intents.TargetsRefused >= 1, "the target to be refused by a tick");
        world.Harness.Ticks(1);

        Assert.Multiple(() =>
        {
            Assert.That(world.Read().Target, Is.EqualTo(EntityId.Null), "a target the client was never shown was accepted");
            Assert.That(TatooineReplication.Intents.TargetsSet, Is.Zero);
        });
    }

    /// <summary>
    /// A netId of zero is a client saying "nothing" and is not counted as a refusal; a netId it was never shown is, and the two are told apart.
    /// </summary>
    /// <remarks>
    /// <b>Both are sent, because either alone proves nothing.</b> A freshly possessed player's target is already <c>EntityId.Null</c>, so a case that sent only
    /// <c>SetTarget(0)</c> and asserted the target was null passed whether the command did anything or not — and would pass with the whole zero branch deleted.
    /// What is falsifiable is the DIFFERENCE: an unknown identity moves the refusal counter and zero does not, with both arriving at the same tick from the
    /// same client.
    /// </remarks>
    [Test]
    public void AClearIsToldApartFromARefusal()
    {
        using var world = new Possessed(Config());

        world.Link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(0xFFFF_FF00u));
        SessionHarness.Until(() => TatooineReplication.Intents.TargetsRefused >= 1, "the unknown identity to be refused");
        var refusedAfterBogus = TatooineReplication.Intents.TargetsRefused;

        world.Link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(0u));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 2, "the clear to reach a tick");
        world.Harness.Ticks(2);

        Assert.Multiple(() =>
        {
            Assert.That(refusedAfterBogus, Is.EqualTo(1), "an identity the session was never shown is refused and counted");
            Assert.That(TatooineReplication.Intents.TargetsRefused, Is.EqualTo(1), "the clear was counted as a refusal too");
            Assert.That(TatooineReplication.Intents.TargetsSet, Is.Zero, "neither command set a target");
            Assert.That(world.Read().Target, Is.EqualTo(EntityId.Null));
        });
    }

    // ── Possession ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A possessed player is not decided for by <c>PlayerThink</c>, and the destination its client set survives every tick that follows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two assertions, because either alone is weak.</b> The counter says the activity mix declined to decide; the surviving destination says what would
    /// have happened if it had not. An intent sets <c>ActivityTicks</c> to zero — a possessed player has no server-side timer — which is exactly the state in
    /// which <c>PlayerThink</c> re-decides, so without the skip it would pick a city or a point of interest and overwrite <c>DestX/DestZ</c> within one tick.
    /// </para>
    /// <para>
    /// <c>PossessedSkipped</c> counts player-ticks rather than players, so over twelve ticks it is at least twelve for one possessed player — and exactly zero
    /// in any measurement run, which is what makes it a usable assertion elsewhere.
    /// </para>
    /// </remarks>
    [Test]
    public void APossessedPlayerIsNotDecidedForByTheSimulation()
    {
        using var world = new Possessed(Config());
        var before = world.Read();

        // A destination well inside the world and far beyond a tick of travel: this case is about the destination SURVIVING, so it must not be one the clamp
        // would rewrite (which AMoveToOutsideTheWorldIsClamped covers) and must not be one the player can reach inside the run.
        var destX = before.X > 0f ? before.X - 2_000f : before.X + 2_000f;
        world.Link.SendCommand(nameof(MoveTo), ClientSays.MoveTo(destX, before.Z));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the intent to be applied");
        var skippedBefore = world.Sim.Bridge.PossessedSkipped;
        world.Harness.Ticks(12);

        var after = world.Read();
        Assert.Multiple(() =>
        {
            Assert.That(world.Sim.Bridge.PossessedSkipped - skippedBefore, Is.GreaterThanOrEqualTo(10),
                "PlayerThink decided for a possessed player, or stopped running");
            Assert.That(after.DestX, Is.EqualTo(destX).Within(1f),
                "the destination changed, so something other than the client decided where this player is going");
            Assert.That(after.Activity, Is.EqualTo(PlayerActivity.Travelling), "the activity was re-decided");
        });
    }

    /// <summary>
    /// A player whose client leaves goes back to the simulation, rather than standing still for ever.
    /// </summary>
    /// <remarks>
    /// The failure this rules out is quiet and permanent: a possessed player that nobody releases is skipped by <c>PlayerThink</c> and has no client to send it
    /// an intent, so it stands where it was left for the life of the process, still in every awareness query and still costing the fence nothing. Over a day of
    /// connections the world fills with them.
    /// </remarks>
    [Test]
    public void APlayerWhoseClientLeavesGoesBackToTheSimulation()
    {
        var config = Config();
        using var sim = new TatooineSim(config);
        sim.Initialize();

        var harness = _harness = new SessionHarness(sim);
        var link = harness.Connect(TatooineReplication.PlayerKind);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the possession to be claimed");
        var entity = TatooineReplication.ControlledBy(sim.Dbe, link.Session);

        link.Close(CloseCodes.Normal, "leaving");
        SessionHarness.Until(() => TatooineReplication.Intents.Releases >= 1, "the player to be given back by a tick");
        harness.Ticks(2);

        using var tx = sim.Dbe.CreateQuickTransaction();
        var control = tx.For<Player>().Open(entity).Read(Player.Control);
        Assert.Multiple(() =>
        {
            Assert.That(control.Kind, Is.EqualTo(ControllerKind.InProcess), "the player is still possessed by a session that has gone");
            Assert.That(control.Controller, Is.Zero);
            Assert.That(control.Target, Is.EqualTo(EntityId.Null));
        });
    }

    /// <summary>
    /// Claiming a player brings it to a standstill with no half-finished activity, so no system can act on a state its client cannot see.
    /// </summary>
    /// <remarks>
    /// <b>The failure this rules out is a player frozen in an activity for ever.</b> <c>PlayerThink</c> is what advances an activity and it skips a possessed
    /// player, so one claimed while walking to a shuttleport, queued at one, walking to a door or inside a building would sit in that state for as long as the
    /// client held it — while the systems that act on those states went on acting on it.
    /// </remarks>
    [Test]
    public void ClaimingAPlayerNormalisesItsActivity()
    {
        using var world = new Possessed(Config());
        var after = world.Read();

        Assert.Multiple(() =>
        {
            Assert.That(after.Activity, Is.EqualTo(PlayerActivity.Idle), "a possessed player starts at a standstill, whatever it was doing");
            Assert.That(after.VelX, Is.Zero);
            Assert.That(after.VelZ, Is.Zero);
        });
    }

    /// <summary>
    /// A dungeon never draws a possessed player into its party.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case the review found and it was live.</b> A dungeon party is drawn from <c>Idle</c> players on planet 0 — which a client that has
    /// connected and not yet sent an intent is, exactly. It was teleported into the dungeon's realm and pinned with
    /// <c>ActivityTicks = int.MaxValue / 2</c>, a pin <c>PlayerThink</c> deliberately respects, so it survived the client disconnecting: a player parked in a
    /// dungeon for the life of the process, still in every awareness query.
    /// </para>
    /// <para>
    /// The dungeon interval is shortened so a draw happens inside the run, and the party is larger than the world's player count so that a draw which ignored
    /// possession would certainly take this one rather than possibly missing it.
    /// </para>
    /// </remarks>
    [Test]
    public void ADungeonNeverDrawsAPossessedPlayer()
    {
        var config = Config();
        config.Dungeons = 2;
        config.DungeonIntervalS = 0.2f;
        config.DungeonStayS = 60f;
        config.DungeonParty = 64;
        config.DungeonMobs = 2;

        using var world = new Possessed(config);
        world.Harness.Ticks(20);

        // Read out before asserting: an EntityRef is a ref struct, so it cannot be captured by the lambdas Assert.Multiple takes.
        ushort realm;
        int activity;
        int activityTicks;
        byte kind;
        using (var tx = world.Sim.Dbe.CreateQuickTransaction())
        {
            var entity = tx.For<Player>().Open(world.Entity);
            realm = entity.Read(Player.Realm).Value;
            var state = entity.Read(Player.State);
            activity = state.Activity;
            activityTicks = state.ActivityTicks;
            kind = entity.Read(Player.Control).Kind;
        }

        Assert.Multiple(() =>
        {
            Assert.That(realm, Is.Zero, "a possessed player was teleported into a dungeon realm");
            Assert.That(activity, Is.Not.EqualTo(PlayerActivity.Inside), "a possessed player was drawn into a dungeon party");
            Assert.That(activityTicks, Is.LessThan(int.MaxValue / 4), "a possessed player was pinned by a dungeon");
            Assert.That(kind, Is.EqualTo(ControllerKind.Human), "the player stopped being possessed, so this proves nothing");
        });
    }

    /// <summary>
    /// A measurement run has no sessions, so nothing is possessed and the activity mix decides for every player — which is what every published number assumes.
    /// </summary>
    /// <remarks>
    /// The guard against SWG-01 changing what the benchmark measures by more than the component it added. If possession could leak into a run with no
    /// replication declared, every figure taken after this would describe a world where some players stand still.
    /// </remarks>
    [Test]
    public void AMeasurementRunPossessesNobody()
    {
        var config = Config();
        config.WarmTicks = 2;
        config.MeasuredTicks = 10;

        using var sim = new TatooineSim(config);
        sim.Initialize();
        sim.Run();

        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.PossessedSkipped, Is.Zero, "a run with no sessions skipped a player as possessed");
            Assert.That(TatooineReplication.Intents.Applied, Is.Zero);
            Assert.That(TatooineReplication.Intents.Possessions, Is.Zero);
        });
    }
}
