using System;
using SwgTatooine.World.Terrain;
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
            var owner = entity.Read(Player.Session);
            return (place.X, place.Z, move.DestX, move.DestZ, move.VelX, move.VelZ, state.Activity, owner.Target);
        }

        /// <summary>The one-tick split the intent path last left for the move phase (A14b), read outside the tick.</summary>
        public (float X, float Z, long Tick) ReadBias()
        {
            using var tx = Sim.Dbe.CreateQuickTransaction();
            var move = tx.For<Player>().Open(Entity).Read(Player.Move);
            return (move.BiasX, move.BiasZ, move.BiasTick);
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
            Assert.That(TatooineReplication.Intents.Owned, Is.Zero, "a NaN destination reached the tick");
            Assert.That(after.X, Is.EqualTo(before.X).Within(StepM * 0.01f), "the player moved on a command that should never have been decoded");
            Assert.That(float.IsFinite(after.DestX) && float.IsFinite(after.DestZ), Is.True);
        });
    }

    // ── A14b: the tick an intent changes in is split ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A run that starts half way into a tick is paid for half of that tick, not all of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The claim is that both sides integrate the same DURATION, and this is the half of it the server owns.</b> A tick advances a player by one whole
    /// period of whatever intent it held at the boundary, while a client that predicts moves on the key event — so without this the two differ by
    /// <c>v x (startPhase - stopPhase)</c>, uniformly over a full tick of travel in either direction, which is the back-step a player sees on every
    /// release. Measured before the fix at mean 0.164 m over twelve randomised stops at 10 Hz, against 0.167 m predicted for that distribution.
    /// </para>
    /// <para>
    /// Asserted on the bias rather than on a distance, because the bias is state that persists: a position would have to be sampled between two ticks of a
    /// runtime pacing itself on another thread, which is a race, and the existing cases above show what that costs in tolerance.
    /// </para>
    /// </remarks>
    [Test]
    public void ARunThatStartsMidTickIsPaidForTheRestOfThatTick()
    {
        using var world = new Possessed(Config());

        // From rest, and asserted rather than assumed: a pooled player arrives with whatever velocity its previous life left, and the whole point of the
        // figure below is that it is the share of the NEW velocity that the old one (zero) displaces.
        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Stop));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the stop to be applied");
        Assert.That(world.Read().VelX, Is.EqualTo(0f), "precondition: the player is standing still");

        // 128/255 = 0.502: a key pressed a shade past the middle of a tick.
        const byte Phase = 128;
        const float Fraction = Phase / 255f;
        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Run, Phase));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 2, "the run to be applied");

        var after = world.Read();
        var bias = world.ReadBias();
        Assert.Multiple(() =>
        {
            Assert.That(after.VelX, Is.EqualTo(StepM).Within(0.001f), "the velocity is the whole tick's, which is what the bias is subtracted from");
            Assert.That(bias.X, Is.EqualTo(-StepM * Fraction).Within(0.001f), $"the first tick should move {1f - Fraction:F3} of a step, not all of it");
            Assert.That(bias.Z, Is.EqualTo(0f).Within(0.001f));
            Assert.That(bias.Tick, Is.EqualTo(world.Sim.Runtime.CurrentTickNumber).Within(2L), "the bias is owed for the tick that applied the intent");
        });
    }

    /// <summary>
    /// A stop that arrives part way into a tick still pays the run for the part before it — which is the half a player can see.
    /// </summary>
    /// <remarks>
    /// The commonest case in the game and the one the move phase has to go out of its way for: the new velocity is exactly zero, so a move phase that tested
    /// "standing still" before reading the bias would discard the last fraction of every run anyone ever makes.
    /// </remarks>
    [TestCase((byte)0, TestName = "AStopOnTheBoundaryPaysTheRunNothingMore")]
    [TestCase((byte)64, TestName = "AStopAQuarterIntoATickPaysTheRunThatQuarter")]
    [TestCase((byte)255, TestName = "AStopAtTheEndOfATickPaysTheRunTheWholeTick")]
    public void AStopPaysTheRunForTheShareOfTheTickItHeld(byte phase)
    {
        using var world = new Possessed(Config());

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Run));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the run to be applied");
        var running = world.Read();
        Assert.That(running.VelX, Is.EqualTo(StepM).Within(0.001f), "precondition: the player is running along +X");

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Stop, phase));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 2, "the stop to be applied");

        var after = world.Read();
        var bias = world.ReadBias();
        Assert.Multiple(() =>
        {
            Assert.That(after.VelX, Is.EqualTo(0f), "the player is stopped from the next tick on, whatever the phase");
            Assert.That(bias.X, Is.EqualTo(running.VelX * (phase / 255f)).Within(0.001f));
        });
    }

    /// <summary>
    /// A bias is paid ONCE, in metres, on the tick it is stamped for — and a standing player is paid it too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The move-phase half, driven by writing the split directly rather than by sending an intent, because what is under test is the stamp: the bias is
    /// never cleared — clearing it would make <c>PlayerMove</c> a third writer of <c>PlayerMotion</c> and need an edge in the DAG to say which writer wins —
    /// so "applied once" is a property of comparing the stamp, and a stamp that compared wrongly would move a stopped player for ever.
    /// </para>
    /// <para>
    /// The tolerance is a half and a half: it separates not-paid (0) from paid-once from paid-twice, which is the whole claim, and leaves room for terrain
    /// rung (c), which can shorten a step that climbs and never lengthen one.
    /// </para>
    /// </remarks>
    [Test]
    public void ABiasIsPaidOnceInMetresAndEvenToAStandingPlayer()
    {
        using var world = new Possessed(Config());
        const float OwedX = 0.3f;
        const float OwedZ = -0.2f;
        var expected = MathF.Sqrt((OwedX * OwedX) + (OwedZ * OwedZ));

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Stop));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the stop to be applied");
        var before = world.Read();

        // Three ticks of slack, so the stamp is still in the future by the time this commits.
        long owedFor;
        using (var tx = world.Sim.Dbe.CreateQuickTransaction())
        {
            var player = tx.OpenMut(world.Entity);
            var move = player.Read(Player.Move);
            move.VelX = 0f;
            move.VelZ = 0f;
            move.BiasX = OwedX;
            move.BiasZ = OwedZ;
            owedFor = world.Sim.Runtime.CurrentTickNumber + 3;
            move.BiasTick = owedFor;
            player.Set(Player.Move, move);
        }

        world.Harness.Ticks(8);

        var after = world.Read();
        var moved = MathF.Sqrt(((after.X - before.X) * (after.X - before.X)) + ((after.Z - before.Z) * (after.Z - before.Z)));
        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.GreaterThan(expected * 0.5f), "a standing player was not paid its bias at all — the standing-still test ran first");
            Assert.That(moved, Is.LessThan(expected * 1.5f), $"moved {moved:F3} m over eight ticks for a {expected:F3} m bias — the stamp is not being compared");
            Assert.That(after.VelX, Is.EqualTo(0f), "the bias is not a velocity and must not become one");
        });
    }

    /// <summary>A phase of zero is the behaviour the server had before A14b: no bias, the whole tick to the new velocity.</summary>
    /// <remarks>
    /// Worth its own case because every intent in the game sends 0 except the two frames a gesture begins and ends on — a bot, a click, a re-send of a held
    /// key — so this is the path almost all of the traffic takes, and it has to be exactly the old one.
    /// </remarks>
    [Test]
    public void APhaseOfZeroLeavesNoBiasAtAll()
    {
        using var world = new Possessed(Config());
        var stale = world.ReadBias();

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(1f, SpeedClasses.Run));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the run to be applied");

        Assert.That(world.ReadBias(), Is.EqualTo(stale), "a phase of zero wrote a bias, so the commonest intent in the game no longer takes the old path");
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

    /// <summary>
    /// A14: the avatar carries the speeds its own session is entitled to, and they are the figures the command path grants.
    /// </summary>
    /// <remarks>
    /// <b>Two copies of this mapping would be two speeds, and the one the client integrated would be the one that drifts.</b>
    /// The client predicts its own movement by running the server's rule, and it sends a speed CLASS rather than a speed — so
    /// the magnitude has to reach it from here. Asserted against <c>TrySpeedFor</c>'s own output via the commands, not against
    /// a literal, because a literal in this test would be the third copy.
    /// </remarks>
    [Test]
    public void AnAvatarCarriesTheSpeedsItsSessionIsEntitledTo()
    {
        using var world = new Possessed(Config());

        // Every gait the client can select, in the order its shortcut does: Shift+1..4 after the walk.
        var classes = new[] { SpeedClasses.Walk, SpeedClasses.Run, SpeedClasses.Burst, SpeedClasses.Speeder, SpeedClasses.SpeederFast };

        float[] replicated;
        using (var tx = world.Sim.Dbe.CreateQuickTransaction())
        {
            var move = tx.For<Player>().Open(world.Entity).Read(Player.Move);
            replicated = [move.WalkMps, move.RunMps, move.BurstMps, move.SpeederMps, move.SpeederFastMps];
        }

        // What the command path actually grants, observed rather than asserted against a constant: one intent per gait, each
        // read back from the velocity the server set.
        var granted = new float[classes.Length];
        for (var i = 0; i < classes.Length; i++)
        {
            var applied = i + 1;
            world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, classes[i]));
            SessionHarness.Until(() => TatooineReplication.Intents.Applied >= applied, $"intent {applied} to be applied");
            world.Harness.Ticks(1);
            granted[i] = MathF.Abs(world.Read().VelX);
        }

        Assert.Multiple(() =>
        {
            for (var i = 0; i < classes.Length; i++)
            {
                Assert.That(replicated[i], Is.GreaterThan(0f), $"class {classes[i]} has no replicated speed, so the client has nothing to predict with");

                // Steer sets metres per TICK, so the stat is the per-tick step times the rate.
                Assert.That(granted[i] * TickRateHz, Is.EqualTo(replicated[i]).Within(0.01f),
                    $"the stat for class {classes[i]} is not what an intent at that class is granted");

                // Strictly faster as the ladder rises, which is the only promise the four shortcuts make to the person
                // pressing them. A pair that collapsed — a copy-paste in SpeedFor, or two classes mapped to one constant —
                // would leave Shift+3 and Shift+4 indistinguishable and nothing else would notice.
                if (i > 0)
                {
                    Assert.That(replicated[i], Is.GreaterThan(replicated[i - 1]), $"class {classes[i]} is not faster than {classes[i - 1]}");
                }
            }
        });
    }

    /// <summary>
    /// The teleport threshold is above the fastest gait, with the margin the engine's test needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the invariant that broke once and cost a session of debugging in the browser.</b> The engine compares
    /// <c>step²</c> against <c>(TeleportMaxSpeedMps × tickPeriod)²</c> over QUANTIZED positions with no margin, so an entity at exactly the declared maximum
    /// tips over the boundary on about half its ticks — and each tip is reported as a teleport, which forbids interpolation. The symptom is not an error: a
    /// player stands still for most of a second and then jumps. It was found by watching a mounted player stutter, not by a test.
    /// </para>
    /// <para>
    /// <b>Asserted here because the two numbers live apart and only arithmetic keeps them together.</b> The threshold is a replication constant and the gaits
    /// are game data; adding a gait is an edit to the second that silently invalidates the first. <c>MaxSpeedMps</c> is computed from the ladder's top for
    /// that reason, and this fails if anyone replaces it with a figure again.
    /// </para>
    /// </remarks>
    [Test]
    public void TheTeleportThresholdClearsTheFastestGait()
    {
        var fastest = 0f;
        for (byte speedClass = 0; speedClass < SpeedClasses.Count; speedClass++)
        {
            fastest = MathF.Max(fastest, TatooineReplication.SpeedFor(speedClass));
        }

        Assert.Multiple(() =>
        {
            Assert.That(fastest, Is.EqualTo(TatooineData.PlayerSpeederFastSpeedMps),
                "the fastest granted class is not the one the threshold is computed from");
            Assert.That(TatooineReplication.MaxSpeedMps, Is.GreaterThan(fastest),
                "an entity at the declared maximum reads as a teleport on about half its ticks");
            Assert.That(TatooineReplication.MaxSpeedMps, Is.EqualTo(fastest * 1.05).Within(0.001),
                "the margin is 5 %: more wastes a velocity width on every moving entity, less is the stutter");
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
    /// <b>Refused rather than clamped</b> because a clamp lets a client ask for whatever it likes on every tick and never learn it is not getting it, and the
    /// refusal count is the only number that says whether anything is trying. The ceiling is the ladder's top (<c>SpeedClasses.Count</c>) and not the run
    /// speed any more — the four gait shortcuts grant up to 24 m/s — so what this case sends is a class far past the end of it, which is what a client
    /// inventing a speed would look like.
    /// </para>
    /// <para>
    /// <b>The counter is asserted, and it took a fix to the wire declaration to make that possible.</b> The class bound was in the command's
    /// <c>Precheck</c>, which runs on the transport thread — so every over-range class died before the tick and the system's refusal counter could never move,
    /// while the code claimed "the refusal is counted". The pre-check now checks only finiteness, which is what the engine's contract for one says it is for
    /// ("rejects the impossible, not the disallowed"), and the entitlement is refused where the entitlement lives.
    /// </para>
    /// </remarks>
    [Test]
    public void ASpeedClassTheServerDoesNotGrantIsRefusedAndCounted()
    {
        using var world = new Possessed(Config());
        var before = world.Read();

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, 200));
        SessionHarness.Until(() => TatooineReplication.Intents.RefusedSpeed >= 1, "the speed class to be refused by the tick");
        world.Harness.Ticks(3);

        var after = world.Read();
        var travelled = MathF.Abs(after.X - before.X) + MathF.Abs(after.Z - before.Z);
        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.Intents.RefusedSpeed, Is.EqualTo(1), "the refusal is the only number that says whether anything is trying");
            Assert.That(TatooineReplication.Intents.Applied, Is.Zero, "a refused intent was counted as applied");
            Assert.That(travelled, Is.LessThan(StepM), "a refused speed class moved the player");
            Assert.That(after.VelX, Is.Zero, "a refused intent set a velocity");
        });
    }

    /// <summary>
    /// A stopped player is <c>Idle</c>, not <c>Travelling</c> — and the activity is on the wire, so every watching client sees the difference.
    /// </summary>
    /// <remarks>
    /// <c>SteerTo</c> set <c>Travelling</c> unconditionally, so a client that pressed stop was still broadcast as running. A stop is the commonest intent there
    /// is, and this is the only visible consequence in the whole intent path.
    /// </remarks>
    [Test]
    public void AStoppedPlayerIsIdleOnTheWire()
    {
        using var world = new Possessed(Config());

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Run));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 1, "the move to be applied");
        world.Harness.Ticks(2);
        Assert.That(world.Read().Activity, Is.EqualTo(PlayerActivity.Travelling), "the player is not moving, so stopping it proves nothing");

        world.Link.SendCommand(nameof(MoveDir), ClientSays.MoveDir(0f, SpeedClasses.Stop));
        SessionHarness.Until(() => TatooineReplication.Intents.Applied >= 2, "the stop to be applied");
        world.Harness.Ticks(2);

        Assert.That(world.Read().Activity, Is.EqualTo(PlayerActivity.Idle), "a stopped player is still broadcast as running");
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
        SessionHarness.Until(() => TatooineReplication.Intents.Owned >= 2, "the clear to reach a tick");
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
    /// An avatar whose client leaves is DESTROYED, rather than left standing in the world for ever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This case was inverted by A5, and the old assertion is worth recording.</b> It used to require that the player went back
    /// to the simulation as <c>ControllerKind.InProcess</c>, because a session was handed a body out of the pooled crowd and had to
    /// give it back. An avatar is not borrowed — it is spawned for the session and belongs to nothing else — so going back to the
    /// simulation would leave an ownerless body standing where its client left it, which is the leak the old assertion was guarding
    /// against in the only form it could then take.
    /// </para>
    /// <para>
    /// The failure ruled out is the same one and is still quiet and permanent: a body nobody drives and nobody destroys stays in
    /// every awareness query and every spatial walk for the life of the process. Over a day of connections the world fills with them.
    /// This is the half of acceptance criterion A-9 that one login and one logout can show.
    /// </para>
    /// </remarks>
    [Test]
    public void AnAvatarWhoseClientLeavesIsDestroyed()
    {
        var config = Config();
        using var sim = new TatooineSim(config);
        sim.Initialize();

        var harness = _harness = new SessionHarness(sim);
        var link = harness.Connect(TatooineReplication.PlayerKind);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar to be spawned");
        var entity = TatooineReplication.ControlledBy(sim.Dbe, link.Session);
        Assert.That(entity, Is.Not.EqualTo(EntityId.Null), "the session must have been given a body to start with");

        link.Close(CloseCodes.Normal, "leaving");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= 1, "the avatar to be destroyed by a tick");
        harness.Ticks(2);

        using var tx = sim.Dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(tx.For<Player>().TryOpen(entity, out _), Is.False, "the avatar outlived its session");
            Assert.That(TatooineReplication.ControlledBy(sim.Dbe, link.Session), Is.EqualTo(EntityId.Null),
                "and nothing is marked as controlled by a session that has gone");
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

    // =======================================================================
    // A11 - a target has to be ALIVE, not merely resolvable
    // =======================================================================

    /// <summary>
    /// A dead target is refused, a live one is accepted, and something with no vitals is not mistaken for dead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why "resolvable" was not enough.</b> A netId stays resolvable for as long as the session's view holds the entity, so a client
    /// that clicks the instant something dies — or replays an old click — names a corpse. <c>SetTarget</c> accepted it, and the combat
    /// system then carried a target it could never resolve a shot against. The refusal belongs at the intent, where it is counted and
    /// the client is told, rather than dropped per shot downstream.
    /// </para>
    /// <para>
    /// <b>The predicate is tested directly, and the one <c>&amp;&amp;</c> that calls it is not.</b> Reaching the wiring needs a netId for
    /// a specific dead entity, and there is no server-side entity-to-netId lookup — so a case would have to brute-force
    /// <c>TryResolve</c> over the view's dense id range from inside a tick. That is more machinery than the branch is worth; the branch
    /// is one condition visible in review, and this pins the thing that could actually be wrong.
    /// </para>
    /// <para>
    /// <b>The entities are set up before replication starts</b>, which is why a plain transaction and an explicit fence are safe here:
    /// there is no tick running, so this is the same window <c>WorldBuilder</c> itself builds the world in. Writing a spatial
    /// archetype while a server ticks would be the one thing <c>EW-02</c> forbids outright.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void OnlyALiveTargetIsAccepted()
    {
        var config = Config();
        using var sim = new TatooineSim(config);
        sim.Initialize();

        // ConfigureIntents caches the routing ids IsAliveTarget compares against; a serving run does it, a bare Initialize does not.
        TatooineReplication.ConfigureIntents(sim.Dbe, config.WorldEdgeM, config.TickRateHz);

        EntityId dead, alive, building;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            var bounds = default(PlayerPlacement);
            bounds.SetAt(0f, 0f, 0f, 1f);

            var corpse = new PlayerVitals { Health = 0, MaxHealth = 1400, AttackDamage = 95 };
            var living = new PlayerVitals { Health = 1400, MaxHealth = 1400, AttackDamage = 95 };

            dead = tx.Spawn<Player>(Player.Bounds.Set(in bounds), Player.Vitals.Set(in corpse), Player.Realm.Set(new PlayerRealm(0)));
            alive = tx.Spawn<Player>(Player.Bounds.Set(in bounds), Player.Vitals.Set(in living), Player.Realm.Set(new PlayerRealm(0)));
            tx.Commit();
        }

        sim.Dbe.WriteTickFence(1);

        // A building, which has no vitals at all: the world already has them, so one is borrowed rather than spawned.
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            building = EntityId.Null;
            foreach (var cluster in tx.For<WorldObject>().GetClusterEnumerator())
            {
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    building = cluster.GetEntityId(System.Numerics.BitOperations.TrailingZeroCount(bits));
                    break;
                }

                if (building != EntityId.Null)
                {
                    break;
                }
            }
        }

        Assert.That(building, Is.Not.EqualTo(EntityId.Null), "the world has no WorldObject, so the no-vitals case would be vacuous");

        using var read = sim.Dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.IsAliveTarget(read, alive), Is.True, "a living player must be targetable");
            Assert.That(TatooineReplication.IsAliveTarget(read, dead), Is.False, "a player at zero health was accepted as a target");

            // NOT false: a building is not dead, it is unshootable — which ClassifyTarget describes and the combat system refuses.
            // Answering false here would conflate "you cannot attack a cantina" with "that thing is dead", and a client reacts to
            // those differently.
            Assert.That(TatooineReplication.IsAliveTarget(read, building), Is.True, "a building must not be reported as DEAD");

            // An entity that never existed: gone is gone, whatever archetype its id claims.
            Assert.That(TatooineReplication.IsAliveTarget(read, EntityId.FromRawValue(alive.RawValue | 0xFFFF_0000_0000_0000UL)), Is.False);
        });
    }

    /// <summary>
    /// A <c>SetTarget</c> at an entity the client CAN see but which is dead is refused — the wiring, end to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case that was uncovered, and the reason it is coverable now is a new engine accessor.</b> The refusal needs a
    /// netId for one specific dead entity, and the identity is held per entity in its replication block while the engine's only public
    /// direction was netId to entity. <c>SubscriptionsCommands.TryNetIdOf</c> is the inverse, session-scoped so it cannot hand a client
    /// an identity it was never shown; <c>TatooineReplication.NetIdOf</c> is the demo's line of it.
    /// </para>
    /// <para>
    /// <b>Both halves, because either alone is weak.</b> A live player at the same place must be accepted and the dead one refused, from
    /// the same client, in the same run — so the case cannot pass against a server that refuses every target, which is what an
    /// <c>IsAliveTarget</c> returning a constant false would be.
    /// </para>
    /// <para>
    /// The two targets are spawned before replication starts, which is the window <c>WorldBuilder</c> itself builds the world in: no
    /// tick is running, so a plain transaction and an explicit fence are safe. Writing a spatial archetype while a server ticks is the
    /// one thing <c>EW-02</c> forbids outright.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(90_000)]
    public void ASetTargetAtADeadEntityTheClientCanSeeIsRefused()
    {
        var config = Config();
        using var sim = new TatooineSim(config);
        sim.Initialize();

        // Where the avatar will be: ConfigureAvatars puts a first login at the capital, so the two targets go there to be inside its sphere.
        var capital = sim.Map.Cities[0];
        var ground = TerrainField.Shared(config.ContentScale).GroundAt(capital.X, capital.Z);

        EntityId dead, alive;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            var place = default(PlayerPlacement);
            place.SetAt(capital.X + 3f, capital.Z + 3f, ground, 1f);

            var corpse = new PlayerVitals { Health = 0, MaxHealth = 1400, AttackDamage = 95 };
            dead = tx.Spawn<Player>(Player.Bounds.Set(in place), Player.Vitals.Set(in corpse), Player.Realm.Set(new PlayerRealm(0)));

            place.SetAt(capital.X - 3f, capital.Z - 3f, ground, 1f);
            var living = new PlayerVitals { Health = 1400, MaxHealth = 1400, AttackDamage = 95 };
            alive = tx.Spawn<Player>(Player.Bounds.Set(in place), Player.Vitals.Set(in living), Player.Realm.Set(new PlayerRealm(0)));
            tx.Commit();
        }

        sim.Dbe.WriteTickFence(1);

        var harness = _harness = new SessionHarness(sim);
        var link = harness.Connect(TatooineReplication.PlayerKind);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar to be spawned");
        harness.Ticks(4);

        // Both must be in the client's view before either can be named, or the refusal below would be "never shown" rather than "dead".
        SessionHarness.Until(
            () => TatooineReplication.NetIdOf(link.Session, dead) != 0 && TatooineReplication.NetIdOf(link.Session, alive) != 0,
            "both targets to reach the client's view");

        var deadNetId = TatooineReplication.NetIdOf(link.Session, dead);
        var aliveNetId = TatooineReplication.NetIdOf(link.Session, alive);
        Assert.That(deadNetId, Is.Not.EqualTo(aliveNetId), "the two targets must be distinct identities");

        // The LIVE one first, so a server refusing everything is caught by the accept rather than passing on the refuse.
        link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(aliveNetId));
        SessionHarness.Until(() => TatooineReplication.Intents.TargetsSet >= 1, "the live target to be accepted");
        harness.Ticks(2);

        var afterLive = TargetOf(sim, link.Session);

        var refusedBefore = TatooineReplication.Intents.TargetsRefused;
        link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(deadNetId));
        SessionHarness.Until(() => TatooineReplication.Intents.TargetsRefused > refusedBefore, "the dead target to be refused");
        harness.Ticks(2);

        Assert.Multiple(() =>
        {
            Assert.That(afterLive, Is.EqualTo(alive), "a live target the client was shown must be accepted");
            Assert.That(TargetOf(sim, link.Session), Is.EqualTo(EntityId.Null), "a dead target was accepted, or did not clear the previous one");
            Assert.That(TatooineReplication.Intents.TargetsSet, Is.EqualTo(1), "the dead target must not have been counted as set");
        });
    }

    /// <summary>The entity a session's avatar is currently targeting.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="session">The session.</param>
    /// <returns>The target, or <c>EntityId.Null</c>.</returns>
    private static EntityId TargetOf(TatooineSim sim, SessionId session)
    {
        var avatar = TatooineReplication.ControlledBy(sim.Dbe, session);
        if (avatar == EntityId.Null)
        {
            return EntityId.Null;
        }

        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.For<Player>().Open(avatar).Read(Player.Session).Target;
    }
}
