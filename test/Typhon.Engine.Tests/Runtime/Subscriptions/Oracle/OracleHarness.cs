using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The differential oracle: a runtime, a seeded workload, and one decoded client replica per session, compared against the engine's own state whenever the
/// world is quiet.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it actually proves.</b> Everything between an ECS write and a client field: the push set, the blocks step, the projection's quantization, the
/// push index and log, the geometric known-set, the record sort, the frame encoder, the wire, <c>Typhon.Client</c>'s decoder and its store. The only code
/// the two sides share is the codec math, and that is golden-tested against fixed vectors from both languages.
/// </para>
/// <para>
/// <b>Why it compares at quiet points rather than every tick.</b> A session is entitled to be behind: K frames are outstanding at most, enters are budgeted,
/// far updates are deferred, and a skipped session's next frame carries the union of what it missed (SUB-03). "The client equals the server right now" is
/// therefore false by design at almost every tick, and an oracle asserting it would be asserting the absence of a feature. What is NOT negotiable is that the
/// client converges once the world stops moving and every pending frame is delivered — so the run alternates churn with a quiet window, and compares in the
/// quiet.
/// </para>
/// <para>
/// <b>The skip rate is the point of the parameterization.</b> At 0 % every frame is delivered as soon as it is produced and the union path is never taken.
/// At 90 % the session's two slots are nearly always full, the producer skips, and what finally arrives has to carry every enter, every leave and every
/// group change since that session's last frame — including a netId released and re-leased in between, which is the case D1's quarantine window exists
/// for.
/// </para>
/// <para>
/// <b>Divergence names the entity.</b> A failure prints the archetype, the netId, the tick, the field and both values, because a differential test that
/// reports only "the worlds differ" costs a day to turn back into a defect.
/// </para>
/// </remarks>
internal sealed unsafe class OracleHarness : IDisposable
{
    /// <summary>How many ticks of stillness precede a comparison: enough for a skipped session's slots to drain and its union frame to be produced.</summary>
    public const int QuietTicks = 8;

    /// <summary>The profile every oracle session is bound to.</summary>
    public const string Profile = "oracle-world";

    /// <summary>
    /// How far a moving entity's predicted position may sit from its true one: the declared 5 cm motion tolerance, plus the position quantum and a margin.
    /// </summary>
    /// <remarks>
    /// The tolerance is the projection's licence not to send: a drift smaller than it produces no segment, on purpose, so the client's prediction is allowed
    /// to be exactly that stale. Asserting anything tighter would be asserting that the motion model does not exist.
    /// </remarks>
    public const double MotionToleranceM = 0.05 + (2.0 * ProjectionTestSchema.PositionStepM) + 0.005;

    /// <summary>How far a static entity's position may sit from its true one: its enter record is absolute, so only the quantum is in play.</summary>
    public const double StaticToleranceM = (2.0 * ProjectionTestSchema.PositionStepM) + 0.001;

    /// <summary>How far a health fraction may sit from its true ratio: one step of its 8-bit codec.</summary>
    public const double FractionTolerance = 1.0 / 255.0;

    /// <summary>
    /// The push profile's radius: from the origin it reaches every corner of the test world, so a push session placed there is entitled to the whole of
    /// it — the same set the <c>World</c> profile watches, which is what lets one truth walk serve both.
    /// </summary>
    public const double PushRadiusM = ProjectionTestSchema.WorldExtentM * 1.5;

    private readonly FrameHarness _harness;
    private readonly SessionId[] _sessions;
    private readonly int[] _skipPercent;
    private readonly Random[] _delivery;
    private readonly int _creatureIndex;
    private readonly int _rockIndex;

    // The geometric mode: sessions with a disc smaller than the world, walking and now and then teleporting. Their truth is the disc.
    private readonly double _radius;
    private readonly bool _walk;
    private readonly Vector3D[] _viewpoints;
    private readonly Random _walker;

    private long _tick;

    private OracleHarness(FrameHarness harness, int[] skipPercent, int seed, PushDetection detection, double radius, bool walk)
    {
        _harness = harness;
        _skipPercent = skipPercent;
        _radius = radius;
        _walk = walk;
        _walker = new Random(seed ^ 0x5EED);
        _sessions = harness.OpenSessions(skipPercent.Length, Profile);
        _viewpoints = new Vector3D[_sessions.Length];
        // A session sees nothing until it is placed.
        for (var i = 0; i < _sessions.Length; i++)
        {
            _viewpoints[i] = walk ? RandomViewpoint() : new Vector3D(0d, 0d, 0d);
            Assert.That(harness.Sessions.SetViewpoint(_sessions[i], _viewpoints[i]), Is.True, "a just-opened session can be placed");
        }

        _delivery = new Random[skipPercent.Length];
        for (var i = 0; i < skipPercent.Length; i++)
        {
            // One generator per session, each seeded from the run's seed and the session's index, so one session's delivery pattern does not shift when
            // another's changes — which is what makes a failing case reproducible when it is narrowed to a single session.
            _delivery[i] = new Random(seed + (7919 * (i + 1)));
        }

        _creatureIndex = harness.PlanIndex(nameof(ProjCreature));
        _rockIndex = harness.PlanIndex(nameof(ProjRock));
        Workload = new OracleWorkload(harness, seed) { Replicate = detection == PushDetection.Explicit };
    }

    /// <summary>How many entities the most recent <see cref="AssertConverged"/> REQUIRED a session to hold — in geometric mode, the ones well inside a disc.</summary>
    public long RequiredAtLastPoint { get; private set; }

    /// <summary>How many viewpoint teleports the walk made: each one resets the session.</summary>
    public int ViewpointTeleports { get; private set; }

    private Vector3D RandomViewpoint()
    {
        const double Limit = ProjectionTestSchema.WorldExtentM - 256.0;
        return new Vector3D(((_walker.NextDouble() * 2.0) - 1.0) * Limit, ((_walker.NextDouble() * 2.0) - 1.0) * Limit, 0d);
    }

    /// <summary>Moves every session's viewpoint: mostly a stride that crosses the anchor's slack every few ticks, sometimes a teleport.</summary>
    private void Walk()
    {
        const double Limit = ProjectionTestSchema.WorldExtentM - 256.0;
        for (var i = 0; i < _sessions.Length; i++)
        {
            var roll = _walker.Next(100);
            if (roll < 3)
            {
                _viewpoints[i] = RandomViewpoint();
                ViewpointTeleports++;
            }
            else if (roll < 90)
            {
                var angle = _walker.NextDouble() * Math.PI * 2.0;
                var stride = _radius * 0.01;
                _viewpoints[i] = new Vector3D(
                    Math.Clamp(_viewpoints[i].X + (Math.Cos(angle) * stride), -Limit, Limit),
                    Math.Clamp(_viewpoints[i].Y + (Math.Sin(angle) * stride), -Limit, Limit),
                    0d);
            }

            _harness.Sessions.SetViewpoint(_sessions[i], _viewpoints[i]);
        }
    }

    /// <summary>The seeded churn driving the world.</summary>
    public OracleWorkload Workload { get; }

    /// <summary>How many entities of the replicated archetypes are live in the engine right now, whatever any client has been told.</summary>
    public int LiveEntityCount => _harness.Replication.LiveEntityCount(_creatureIndex) + _harness.Replication.LiveEntityCount(_rockIndex);

    /// <summary>The tick last run.</summary>
    public long Tick => _tick;

    /// <summary>The sessions, in the order their skip rates were given.</summary>
    public SessionId[] Sessions => _sessions;

    /// <summary>The engine under test.</summary>
    public DatabaseEngine Engine => _harness.Engine;

    /// <summary>The push path.</summary>
    public PushReplication Push => _harness.Subscriptions.Push;

    /// <summary>The creatures' resolved visibility slack h, in metres.</summary>
    public double CreatureSlackM => _harness.Subscriptions.Plans[_creatureIndex].VisibilitySlackM;

    /// <summary>The frame harness underneath, for what the oracle does not wrap (the frame digest).</summary>
    public FrameHarness Frames => _harness;

    /// <summary>The frame assembler, for the one test that has to break a rule on the production object to prove the oracle can see it.</summary>
    public FrameAssembler Assembler => _harness.Assembler;

    /// <summary>How many frames the producer skipped because a session's slots were full.</summary>
    public long FramesSkipped => _harness.Assembler.FramesSkipped;

    /// <summary>How many frames were produced across every session.</summary>
    public long FramesProduced => _harness.Assembler.FramesProduced;

    /// <summary>
    /// How many entity-to-entity comparisons the oracle has actually made.
    /// </summary>
    /// <remarks>
    /// The one number that distinguishes "the worlds agree" from "the comparison looked at nothing". An oracle whose server-truth walk silently returned
    /// an empty set — a directory lookup that stopped matching, a plan index resolved to the wrong archetype — is green on every run and proves nothing,
    /// and that failure has no other symptom. Every test here asserts it.
    /// </remarks>
    public long Compared { get; private set; }

    /// <summary>How many entity comparisons the most recent <see cref="AssertConverged"/> made, across every session.</summary>
    /// <remarks>
    /// The cumulative count only ever proves the walk was not empty. This one is checkable against the engine's own live entity count, which is what turns
    /// "it compared something" into "it compared everything".
    /// </remarks>
    public long ComparedAtLastPoint { get; private set; }

    /// <summary>
    /// Builds an oracle over a fresh engine.
    /// </summary>
    /// <param name="engine">The engine, with its archetypes initialized.</param>
    /// <param name="seed">The run's seed: it drives both the workload and each session's delivery pattern.</param>
    /// <param name="skipPercent">One entry per session: the percentage of ticks on which that session's frames are left undrained.</param>
    /// <param name="name">A name for the resource registry.</param>
    /// <returns>The oracle.</returns>
    /// <param name="detection">
    /// Who signals a change. Both archetypes are served to sessions placed where the profile's disc covers the world; in
    /// <see cref="PushDetection.Explicit"/> the workload pushes every slot it writes.
    /// </param>
    /// <param name="walkRadius">
    /// When positive, the disc's radius, and the sessions WALK — each is placed at random, strides every tick and now and then teleports — and each is
    /// compared against its own disc rather than the whole world.
    /// </param>
    /// <param name="worldObserver">Serve the profile through a <c>World</c> observer instead of a covering disc.</param>
    /// <param name="every">The profile's tick divisor.</param>
    /// <param name="bigWorld">Seed the walking oracle's population even when the sessions do not walk.</param>
    /// <param name="deterministicProjection">
    /// Build the push index serially, in the frame prologue — the collapsed shape's path — instead of sorted by the projection and merged by its stage.
    /// </param>
    /// <param name="replicationCellM">The replication cell side; zero for <c>ProjectionTestSchema.ReplicationCellFor</c> of the profile's radius.</param>
    /// <param name="forceDeep">Serve the flat world with the deep implementation, which must agree with the flat one on it (10 § 3.5).</param>
    /// <param name="visibilitySlackM">The creatures' visibility slack h (09 § 2); <see cref="double.NaN"/> for the rule, R / 48.</param>
    public static OracleHarness Create(DatabaseEngine engine, int seed, int[] skipPercent, string name, PushDetection detection = PushDetection.Explicit,
        double walkRadius = 0, bool worldObserver = false, int every = 1, bool bigWorld = false, bool deterministicProjection = false,
        double replicationCellM = 0, bool forceDeep = false, double visibilitySlackM = double.NaN)
    {
        ArgumentNullException.ThrowIfNull(skipPercent);

        var walk = walkRadius > 0;
        var radius = walk ? walkRadius : PushRadiusM;
        var harness = FrameHarness.Create(engine, subs => Declare(subs, detection, radius, worldObserver && !walk, every), name,
            Options(detection == PushDetection.Automatic, deterministicProjection,
                replicationCellM > 0 ? replicationCellM : ProjectionTestSchema.ReplicationCellFor(worldObserver && !walk ? 0 : radius), forceDeep,
                visibilitySlackM));
        try
        {
            harness.SerialIndex = deterministicProjection;
            var oracle = new OracleHarness(harness, skipPercent, seed, detection, radius, walk);

            // A walking disc covers a few percent of the world, so the world is denser for it to hold anything worth comparing.
            oracle.Workload.Seed(creatures: walk || bigWorld ? 400 : 24, rocks: walk || bigWorld ? 120 : 8);

            // The first tick pushes every live entity.
            oracle._tick = 1;
            engine.WriteTickFence(1);
            harness.RunTick(1);

            return oracle;
        }
        catch
        {
            harness.Dispose();
            throw;
        }
    }

    /// <summary>Runs one tick: churn, the whole track, then each session's delivery according to its skip rate.</summary>
    public void Step()
    {
        Workload.Step();
        if (_walk)
        {
            Walk();
        }

        _tick++;

        // The ECS fence, which RunTick does not run: a spatial write MARKS an entity, and the cluster change happens here. Without it the workload's
        // teleports moved coordinates and nothing ever migrated, so the oracle covered no cluster change at all while claiming to — the bug was invisible
        // because both sides agreed about a world in which nothing had moved between clusters.
        Engine.WriteTickFence(_tick);

        _harness.RunTick(_tick);

        for (var i = 0; i < _sessions.Length; i++)
        {
            if (_delivery[i].Next(100) >= _skipPercent[i])
            {
                _harness.Deliver(_sessions[i]);
            }
        }
    }

    /// <summary>
    /// Churn and the ECS fence with no track: a tick the gate skipped, as it does while no session is connected or when the tick aborts. The fence still
    /// drains the tick's structure words, so the track must recover what it never saw.
    /// </summary>
    public void StepWithoutTrack()
    {
        Workload.Step();
        _tick++;
        Engine.WriteTickFence(_tick);
        _harness.SkipTick(_tick);
    }

    /// <summary>Churn and a tick whose track stopped after the index merge: its index is never finished and nothing is delivered.</summary>
    public void StepWithoutIndex()
    {
        Workload.Step();
        _tick++;
        Engine.WriteTickFence(_tick);
        _harness.RunTickWithoutIndex(_tick);
    }

    /// <summary>
    /// One tick with no write at all, the sessions still walking and every frame delivered: what moves is only the viewers. An entity whose last change a
    /// session was never sent is reached by the session's disc, not by an event.
    /// </summary>
    /// <param name="strideOfRadius">How far every session steps this tick, as a fraction of the radius, in a random direction; never a teleport.</param>
    public void StepWalkingOnly(double strideOfRadius)
    {
        Assert.That(_walk, Is.True, "only a walking oracle's sessions can walk");
        Assert.That(_radius * strideOfRadius, Is.LessThan(Push.CellSize), "a step of a cell or more is a teleport, which resets instead of walking");
        const double Limit = ProjectionTestSchema.WorldExtentM - 256.0;
        for (var i = 0; i < _sessions.Length; i++)
        {
            var angle = _walker.NextDouble() * Math.PI * 2.0;
            var stride = _radius * strideOfRadius;
            _viewpoints[i] = new Vector3D(
                Math.Clamp(_viewpoints[i].X + (Math.Cos(angle) * stride), -Limit, Limit),
                Math.Clamp(_viewpoints[i].Y + (Math.Sin(angle) * stride), -Limit, Limit),
                0d);
            _harness.Sessions.SetViewpoint(_sessions[i], _viewpoints[i]);
        }

        _tick++;
        Engine.WriteTickFence(_tick);
        _harness.RunTick(_tick);
        foreach (var session in _sessions)
        {
            _harness.Deliver(session);
        }
    }

    /// <summary>
    /// Stops writing, runs the world forward, and delivers everything to every session.
    /// </summary>
    /// <remarks>
    /// Delivery happens on every quiet tick, not only at the end: a session holding two full slots produces nothing until one frees, so a single drain
    /// followed by a single tick would leave the union frame unproduced and the comparison would fail against a client that was simply not finished.
    /// </remarks>
    public void Quiesce()
    {
        for (var q = 0; q < QuietTicks; q++)
        {
            _tick++;
            Engine.WriteTickFence(_tick);
            _harness.RunTick(_tick);
            foreach (var session in _sessions)
            {
                _harness.Deliver(session);
            }
        }

        // The window has to have been long enough, and that is checkable rather than assumed. A fixed count of quiet ticks is a guess about how long a
        // session that skipped most of the run needs to drain its outstanding frames and receive its union frame; if the guess is ever short, the comparison
        // that follows fails for a reason that has nothing to do with the engine being wrong. Asserting nothing is left owed turns that silent
        // timing-dependence into a loud one.
        foreach (var session in _sessions)
        {
            Assert.That(_harness.HasFrame(session), Is.False,
                $"session {session.Value} still has a frame waiting after {QuietTicks} quiet ticks, so the comparison below would be against a client the "
                + "engine has not finished talking to — raise QuietTicks rather than trusting the result");
        }
    }

    /// <summary>
    /// Asserts that every span write the workload made still reads back from the engine. Independent of replication: a failure here is the engine losing a
    /// committed write, which every replication mode that re-encodes the world each tick would silently propagate.
    /// </summary>
    /// <param name="because">What the caller is proving.</param>
    public void AssertWritesSurvived(string because)
    {
        var lost = new List<string>();
        using var tx = Engine.CreateQuickTransaction();
        foreach (var (raw, written) in Workload.LastWritten)
        {
            var ai = tx.Open(EntityId.FromRaw(raw)).Read(ProjCreature.Ai);
            if ((int)ai.Mode != written.Mode || ai.Level != written.Level)
            {
                lost.Add($"entity {raw}: wrote mode {written.Mode} level {written.Level}, reads mode {(int)ai.Mode} level {ai.Level}");
            }
        }

        Assert.That(lost, Is.Empty, $"{because}: the engine lost {lost.Count} committed span write(s) of {Workload.LastWritten.Count}");
    }

    /// <summary>Asserts that every session's replica is the engine's world, field for field.</summary>
    /// <param name="because">What the caller is proving, printed with any divergence.</param>
    public void AssertConverged(string because)
    {
        var divergences = new List<string>();

        if (Push.Shadow && Push.ShadowIllegal != 0)
        {
            divergences.Add($"the push path published {Push.ShadowIllegal} record(s) the client could not legally apply ({because})");
        }

        var truth = ServerTruth(divergences);
        var before = Compared;
        RequiredAtLastPoint = 0;

        // One transaction for the whole comparison. Opening one per entity was measurably the dominant cost of a run: a gate case compares tens of entities
        // across several sessions at five points, and a quick transaction is not free.
        using (var tx = Engine.CreateQuickTransaction())
        {
            for (var i = 0; i < _sessions.Length; i++)
            {
                Compare(tx, _harness.Replica(_sessions[i]), i, truth, divergences);
            }
        }

        ComparedAtLastPoint = Compared - before;
        if (divergences.Count == 0)
        {
            return;
        }

        var report = new StringBuilder();
        report.Append(because).Append(" — the client's world is not the server's at tick ").Append(_tick).Append(':').AppendLine();
        foreach (var divergence in divergences)
        {
            report.Append("  ").AppendLine(divergence);
        }

        Assert.Fail(report.ToString());
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// The identities the engine has published, and the entity behind each.
    /// </summary>
    /// <returns>Per plan index, netId to entity.</returns>
    /// <remarks>
    /// Read from the replication blocks rather than from the workload's own bookkeeping, because the netId is the engine's to assign and a shadow copy of
    /// the mapping would be the very thing under test. Every live entity of a replicated archetype is described, so a slot counts when it is occupied (the
    /// cluster's own occupancy word) and carries an identity.
    /// </remarks>
    private Dictionary<int, Dictionary<uint, EntityId>> ServerTruth(List<string> divergences)
    {
        var truth = new Dictionary<int, Dictionary<uint, EntityId>>();
        foreach (var plan in new[] { _creatureIndex, _rockIndex })
        {
            var byNetId = new Dictionary<uint, EntityId>();
            var state = _harness.Subscriptions.ReplicationStates[plan];
            var layout = state.Layout;
            foreach (var (chunkId, occupancy) in _harness.Replication.LiveClusters(plan))
            {
                if (!state.Directory.TryGetBlock(chunkId, out var block))
                {
                    continue;
                }

                // Every live entity is described, so the truth is occupancy alone; the watched mask names only this tick's pushes.
                var live = occupancy;
                while (live != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(live);
                    live &= live - 1;
                    var hot = (ReplicationHotEntry*)((byte*)block + layout.HotOffset + (slot * layout.HotStride));
                    if (hot->NetId == NetIdAllocator.NoNetId)
                    {
                        // A live slot with no identity is not something to skip quietly: it is an entity the engine replicates and cannot name, so no
                        // client can ever be told about it. Silently leaving it out of truth would make that state a passing run.
                        divergences.Add($"plan {plan} chunk {chunkId} slot {slot} (entity {hot->Entity.RawValue}) is live but holds no netId");
                        continue;
                    }

                    // TryAdd, never an indexer assignment. Two live slots carrying the SAME netId is precisely the collision D1's quarantine window exists
                    // to prevent, and an indexer would overwrite the first, shrink the truth set by one, and leave the run green with no other symptom —
                    // the oracle would be blind to the exact defect it was built for.
                    if (!byNetId.TryAdd(hot->NetId, hot->Entity))
                    {
                        divergences.Add($"plan {plan}: netId {hot->NetId} is leased to two live slots at once — entities "
                            + $"{byNetId[hot->NetId].RawValue} and {hot->Entity.RawValue} (SUB-06)");
                    }
                }
            }

            truth[plan] = byNetId;
        }

        return truth;
    }

    private void Compare(Transaction tx, SessionReplica replica, int session, Dictionary<int, Dictionary<uint, EntityId>> truth, List<string> divergences)
    {
        CompareArchetype(tx, replica, session, _creatureIndex, nameof(ProjCreature), truth[_creatureIndex], divergences);
        CompareArchetype(tx, replica, session, _rockIndex, nameof(ProjRock), truth[_rockIndex], divergences);

        if (replica.Store.Anomalies != 0)
        {
            divergences.Add($"session {session}: the decoder recorded {replica.Store.Anomalies} anomalies, so it was handed a frame it could not apply");
        }
    }

    private void CompareArchetype(Transaction tx, SessionReplica replica, int session, int plan, string name, Dictionary<uint, EntityId> expected,
        List<string> divergences)
    {
        if (_walk)
        {
            CompareDisc(tx, replica, session, plan, name, expected, divergences);
            return;
        }

        var held = replica.NetIds(plan);
        // The SET is what the comparison below needs; the LENGTH is asserted first, because collapsing duplicates here would hide a replica holding one
        // identity twice — which is precisely the shape of a missed leave followed by a re-enter, the defect family this oracle exists for.
        var seen = new HashSet<uint>(held);
        if (seen.Count != held.Length)
        {
            divergences.Add($"session {session}: {name} client holds {held.Length} identities but only {seen.Count} distinct ones");
        }

        foreach (var netId in held)
        {
            if (!expected.ContainsKey(netId))
            {
                divergences.Add($"session {session}: {name} netId {netId} is in the client's world and not in the server's");
            }
        }

        foreach (var (netId, entity) in expected)
        {
            if (!seen.Contains(netId))
            {
                divergences.Add($"session {session}: {name} netId {netId} (entity {entity.RawValue}) is in the server's world and not in the client's");
                continue;
            }

            Compared++;
            CompareFields(tx, replica, session, plan, name, netId, entity, divergences);
        }
    }

    /// <summary>
    /// The geometric comparison: an entity well inside the session's disc must be held, one well outside it must not be, and one in the band between may
    /// be either — the anchor trails the viewpoint by up to its slack, and a client's position may trail the entity's by the motion tolerance.
    /// </summary>
    private void CompareDisc(Transaction tx, SessionReplica replica, int session, int plan, string name, Dictionary<uint, EntityId> expected,
        List<string> divergences)
    {
        var held = new HashSet<uint>(replica.NetIds(plan));
        var slack = _radius / 3.0 / 16.0;

        // v̂ trails a mover's true position by up to h (09 § 2): held within R − h, dropped past R + h, either between.
        var visibility = plan == _creatureIndex ? _harness.Subscriptions.Plans[plan].VisibilitySlackM : 0d;
        var margin = slack + visibility + MotionToleranceM + 0.01;
        var inner = _radius - margin;
        var outer = _radius + margin;
        var viewpoint = _viewpoints[session];
        foreach (var netId in held)
        {
            if (!expected.ContainsKey(netId))
            {
                divergences.Add($"session {session}: {name} netId {netId} is in the client's world and not in the server's");
            }
        }

        foreach (var (netId, entity) in expected)
        {
            var reference = tx.Open(entity);
            var bounds = plan == _creatureIndex ? reference.Read(ProjCreature.Bounds) : reference.Read(ProjRock.Bounds);
            var dx = ((bounds.Bounds.MinX + bounds.Bounds.MaxX) * 0.5) - viewpoint.X;
            var dy = ((bounds.Bounds.MinY + bounds.Bounds.MaxY) * 0.5) - viewpoint.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            var holds = held.Contains(netId);
            if (distance <= inner)
            {
                RequiredAtLastPoint++;
                if (!holds)
                {
                    divergences.Add($"session {session}: {name} netId {netId} (entity {entity.RawValue}) is {distance:F2} m from the viewpoint, inside the "
                        + $"{_radius} m disc, and the client does not hold it");
                    continue;
                }
            }
            else if (distance > outer && holds)
            {
                divergences.Add($"session {session}: {name} netId {netId} is {distance:F2} m from the viewpoint, outside the {_radius} m disc, and the client "
                    + "still holds it");
                continue;
            }

            if (holds)
            {
                Compared++;
                CompareFields(tx, replica, session, plan, name, netId, entity, divergences);
            }
        }
    }

    private void CompareFields(Transaction tx, SessionReplica replica, int session, int plan, string name, uint netId, EntityId entity,
        List<string> divergences)
    {
        var reference = tx.Open(entity);
        var moving = plan == _creatureIndex;
        var bounds = moving ? reference.Read(ProjCreature.Bounds) : reference.Read(ProjRock.Bounds);
        var ai = moving ? reference.Read(ProjCreature.Ai) : reference.Read(ProjRock.Ai);

        ComparePosition(replica, session, plan, name, netId, bounds, moving, divergences);

        if (!moving)
        {
            // A static archetype sends one field, once, on enter. That is the whole of its state.
            Check(replica, session, plan, name, netId, "kind", ai.Template, 0.0, divergences);
            return;
        }

        var vitals = reference.Read(ProjCreature.Vitals);

        // The OnEnter field IS comparable, and it is the most valuable field here. The workload writes it once at spawn and never again, so the entity's
        // current value is exactly what its last ENTER carried. That makes it the one field that catches a netId re-leased to a different entity while the
        // client kept the old one's state: position and vitals may coincidentally agree, a template assigned from a monotonic counter will not.
        Check(replica, session, plan, name, netId, "template", ai.Template, 0.0, divergences);
        Check(replica, session, plan, name, netId, "mode", (byte)ai.Mode, 0.0, divergences);
        Check(replica, session, plan, name, netId, "alerted", ai.Alerted != 0 ? 1.0 : 0.0, 0.0, divergences);
        Check(replica, session, plan, name, netId, "level", ai.Level, 0.0, divergences);
        Check(replica, session, plan, name, netId, "hp", (double)vitals.Health / vitals.MaxHealth, FractionTolerance, divergences);
    }

    /// <summary>
    /// Compares the client's position for an entity against the engine's, evaluating the motion model where there is one.
    /// </summary>
    /// <remarks>
    /// A moving archetype's client position is the head segment's anchor plus its per-tick velocity carried to the current tick — that is what a renderer
    /// evaluates, and comparing the raw anchor instead would fail on every entity that is simply still moving. A static archetype has no segment at all: its
    /// position arrived absolute in its enter record.
    /// </remarks>
    private void ComparePosition(SessionReplica replica, int session, int plan, string name, uint netId, ProjBounds bounds, bool moving,
        List<string> divergences)
    {
        double[] actual;
        if (moving)
        {
            var store = replica.Store;
            if (!store.TryLocate(netId, out _, out var slot))
            {
                // Recorded, not thrown: this walk accumulates every divergence so one failure reports the whole picture, and an assertion here would stop
                // at the first entity and hide the rest.
                divergences.Add($"session {session}: {name} netId {netId} vanished from the replica between listing it and reading its position");
                return;
            }

            var archetype = store.Archetypes[plan];
            var anchor = archetype.HeadPosition(slot);
            var velocity = archetype.HeadVelocity(slot);
            var elapsed = (double)((uint)_tick - archetype.HeadT0(slot));
            actual = [anchor[0] + (velocity[0] * elapsed), anchor[1] + (velocity[1] * elapsed)];
        }
        else
        {
            actual = replica.Position(plan, netId);
        }

        var tolerance = moving ? MotionToleranceM : StaticToleranceM;
        double[] truth = [(bounds.Bounds.MinX + bounds.Bounds.MaxX) * 0.5, (bounds.Bounds.MinY + bounds.Bounds.MaxY) * 0.5];
        for (var axis = 0; axis < truth.Length; axis++)
        {
            if (Math.Abs(actual[axis] - truth[axis]) > tolerance)
            {
                divergences.Add($"session {session}: {name} netId {netId} axis {axis} is {actual[axis]:F4} on the client and {truth[axis]:F4} on the "
                    + $"server (tolerance {tolerance:F4})");
            }
        }
    }

    private static void Check(SessionReplica replica, int session, int plan, string name, uint netId, string field, double expected, double tolerance,
        List<string> divergences)
    {
        var actual = replica.Value(plan, netId, field);
        if (actual == null)
        {
            divergences.Add($"session {session}: {name} netId {netId} has no '{field}' on the client");
            return;
        }

        if (Math.Abs(actual.Value - expected) > tolerance)
        {
            divergences.Add($"session {session}: {name} netId {netId} field '{field}' is {actual.Value} on the client and {expected} on the server");
        }
    }

    /// <summary>
    /// Pools wide enough that nothing the oracle observes is a budget talking.
    /// </summary>
    /// <remarks>
    /// The two pool budgets are raised because a pool that runs out makes the producer SKIP, and a skip is indistinguishable from the skips this fixture
    /// induces on purpose — the run would still be correct but it would no longer be measuring what it says it measures. The enter budget is left at the
    /// engine's default: deferring enters across ticks is real behaviour that the quiet window is there to absorb, and raising it would hide it.
    /// </remarks>
    private static SubscriptionsOptions Options(bool automatic, bool deterministicProjection, double replicationCellM, bool forceDeep,
        double visibilitySlackM) => new()
    {
        ReplicationCellM = replicationCellM,
        VisibilitySlackMForTest = visibilitySlackM,
        ForceDeepReplicationForTest = forceDeep,
        AllowAutomaticPushDetection = automatic,

        // The push path's legality check: a record the client could not apply — an enter of a held entity, an update or a leave of an unheld one — is a
        // divergence the comparison below may never see, since the client forgives some of them.
        PushShadow = true,
        DeterministicProjection = deterministicProjection,
        MaxSessions = 64,
        StatePoolBudgetBytes = 64L * 1024 * 1024,
        FramePoolBudgetBytes = 64L * 1024 * 1024,
    };

    /// <summary>The projections and the profile the oracle runs against: a disc, or the whole world.</summary>
    private static void Declare(SubscriptionsRegistry subs, PushDetection detection, double radius, bool world, int every)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclareRock(subs);
        if (world)
        {
            subs.Profile(Profile, p => p.Detection(detection).Every(every).World().Of<ProjCreature>().Of<ProjRock>());
        }
        else
        {
            subs.Profile(Profile, p => p.Detection(detection).Every(every).Sphere(radius).Of<ProjCreature>().Of<ProjRock>());
        }
    }

}
