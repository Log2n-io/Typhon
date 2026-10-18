using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The group oracle (14 § 3.8, step 4.1's acceptance): towns, their buildings and citizens under seeded churn — key changes, spawns, destroys, teleports,
/// towns appearing, vanishing and moving — and walking sessions that skip frames, each compared with what it is entitled to hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim (P2, P8, P9).</b> At a quiet point a session holds a member exactly when geometry holds it or its key is held, and holds a key exactly when
/// its observer meets the town: well inside the reach it must, well beyond reach plus margin it must not, between it may (the anchor trails the viewpoint,
/// and hysteresis keeps a key). Every key it must hold is complete on the client.
/// </para>
/// <para>
/// <b>Complete means complete, every frame.</b> The session that skips nothing receives each frame the tick it is produced, so the world it describes is the
/// world the oracle can read: whenever a <c>GROUP_COMPLETE</c> arrives, every current member of that town must already be in its store.
/// </para>
/// <para>
/// <b>Key changes are never pushed by the workload.</b> They go through <c>EntityRefMut.Set</c> with no <c>Replicate</c>: the tap (the key component's
/// structure mark) is what must carry them, and a forgotten tap shows as a member held under its old town.
/// </para>
/// </remarks>
internal sealed unsafe class GroupOracle : IDisposable
{
    public const double Radius = 150d;
    public const double Reach = 100d;
    public const double Margin = 30d;

    /// <summary>A key no town ever has: its members are never held through it.</summary>
    private const int OrphanKey = 999;

    private const float Spread = 1500f;

    /// <summary>Gather workers: sessions are gathered in parallel, so per-worker scratch and per-session state are exercised as the runtime runs them.</summary>
    private const int Workers = 4;

    private readonly FrameHarness _harness;
    private readonly SessionId[] _sessions;
    private readonly int[] _skipPercent;
    private readonly Random[] _delivery;
    private readonly Random _random;
    private readonly Vector3D[] _viewpoints;
    private readonly double _anchorSlack;
    private readonly int _buildingPlan;
    private readonly int _citizenPlan;

    private readonly List<EntityId> _towns = [];
    private readonly List<EntityId> _buildings = [];
    private readonly List<EntityId> _citizens = [];
    private int _nextTownKey = 100;
    private int _nextTemplate = 1;
    private long _tick;

    private GroupOracle(FrameHarness harness, int seed, int[] skipPercent)
    {
        _harness = harness;
        _skipPercent = skipPercent;
        _random = new Random(seed);
        _sessions = harness.OpenSessions(skipPercent.Length, GroupTestSchema.Profile);
        _viewpoints = new Vector3D[_sessions.Length];
        _delivery = new Random[_sessions.Length];
        _anchorSlack = Math.Min(Radius / 48d, harness.Subscriptions.Push.CellSize / 2d);
        _buildingPlan = harness.PlanIndex(nameof(GroupBuilding));
        _citizenPlan = harness.PlanIndex(nameof(GroupCitizen));
        for (var i = 0; i < _sessions.Length; i++)
        {
            _delivery[i] = new Random(seed + (7919 * (i + 1)));
        }
    }

    /// <summary>Entity comparisons made, summed: an oracle that compared nothing proves nothing.</summary>
    public long Compared { get; private set; }

    /// <summary>Members a session was required to hold, summed over the comparisons.</summary>
    public long RequiredMembers { get; private set; }

    /// <summary>Keys a session was required to hold complete, summed over the comparisons.</summary>
    public long RequiredKeys { get; private set; }

    /// <summary><c>GROUP_COMPLETE</c> records the zero-skip session received, each checked against the world.</summary>
    public long CompletionsChecked { get; private set; }

    /// <summary><c>GROUP_RELEASED</c> records received, across the sessions.</summary>
    public long Releases { get; private set; }

    /// <summary>Frames that were a <c>RESET</c> while the session held at least one complete group.</summary>
    public long ResetsDroppingGroups { get; private set; }

    /// <summary>Key changes the workload wrote.</summary>
    public int KeyChanges { get; private set; }

    /// <summary>Viewpoint teleports, each a reset of its session.</summary>
    public int Teleports { get; private set; }

    /// <summary>The push path.</summary>
    public PushReplication Push => _harness.Subscriptions.Push;

    /// <summary>What every failure of this oracle's own assertions starts with: a mutant test's evidence that the oracle, not its scaffolding, rejected.</summary>
    public const string FailureMarker = "group oracle:";

    /// <summary>
    /// The mutant SUB-33's tap clause forbids, made from the test side: a member's key component no longer marks its slot when written, so a key change
    /// reaches the index and never the projection.
    /// </summary>
    public void RemoveTheTapForTest()
    {
        foreach (var plan in new[] { _buildingPlan, _citizenPlan })
        {
            _harness.Subscriptions.ReplicationStates[plan].ClusterState.ClearGroupKeySlotsForTest();
        }
    }

    public static GroupOracle Create(DatabaseEngine engine, int seed, int[] skipPercent, string name, int enterBudget)
    {
        var harness = FrameHarness.Create(engine, s => GroupTestSchema.Declare(s, Radius, Reach, Margin), name, new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxSessions = 16,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
            MaxGroupsPerSession = 12,
            EnterBudgetPerFrame = enterBudget,
            PushShadow = true,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
        });
        try
        {
            harness.RunFence = true;
            harness.DrainNetIds = true;
            var oracle = new GroupOracle(harness, seed, skipPercent);
            oracle.Seed();
            for (var i = 0; i < oracle._sessions.Length; i++)
            {
                oracle._viewpoints[i] = oracle.NearTown();
                Assert.That(harness.Sessions.SetViewpoint(oracle._sessions[i], oracle._viewpoints[i]), Is.True);
            }

            return oracle;
        }
        catch
        {
            harness.Dispose();
            throw;
        }
    }

    public void Dispose() => _harness.Dispose();

    // ══ The world ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private void Seed()
    {
        for (var t = 0; t < 8; t++)
        {
            SpawnTown();
        }

        using (var tx = _harness.Engine.CreateQuickTransaction())
        {
            foreach (var town in _towns)
            {
                var zone = tx.Open(town).Read(GroupTown.Zone);
                for (var b = 8 + _random.Next(25); b > 0; b--)
                {
                    SpawnBuilding(tx, zone.Key, In(zone.Extent));
                }
            }

            for (var b = 0; b < 30; b++)
            {
                SpawnBuilding(tx, b % 3 == 0 ? OrphanKey : 0, Anywhere());
            }

            for (var c = 0; c < 150; c++)
            {
                SpawnCitizen(tx, RandomKey(), NearTownPoint());
            }

            tx.Commit();
        }
    }

    private void SpawnTown()
    {
        using var tx = _harness.Engine.CreateQuickTransaction();
        var half = 40f + ((float)_random.NextDouble() * 80f);
        var zone = new GroupZone { Key = _nextTownKey++, Extent = GroupTestSchema.Square(Coordinate(), Coordinate(), half) };
        _towns.Add(tx.Spawn<GroupTown>(GroupTown.Zone.Set(in zone)));
        tx.Commit();
    }

    private void SpawnBuilding(Transaction tx, int key, (float X, float Y) at)
    {
        var bounds = GroupTestSchema.At(at.X, at.Y);
        var ai = new ProjAi { Template = (byte)(_nextTemplate++ & 0xFF) };
        _buildings.Add(tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(in bounds), GroupBuilding.Ai.Set(in ai),
            GroupBuilding.Key.Set(new GroupKey { Town = key })));
    }

    private void SpawnCitizen(Transaction tx, int key, (float X, float Y) at)
    {
        var bounds = GroupTestSchema.At(at.X, at.Y);
        var ai = new ProjAi { Template = (byte)(_nextTemplate++ & 0xFF) };
        _citizens.Add(tx.Spawn<GroupCitizen>(GroupCitizen.Bounds.Set(in bounds), GroupCitizen.Ai.Set(in ai),
            GroupCitizen.Key.Set(new GroupKey { Town = key })));
    }

    private float Coordinate() => ((float)_random.NextDouble() * 2f - 1f) * Spread;

    private (float X, float Y) Anywhere() => (Coordinate(), Coordinate());

    private (float X, float Y) In(AABB2F extent) =>
        (extent.MinX + ((float)_random.NextDouble() * (extent.MaxX - extent.MinX)), extent.MinY + ((float)_random.NextDouble() * (extent.MaxY - extent.MinY)));

    private (float X, float Y) NearTownPoint()
    {
        using var tx = _harness.Engine.CreateQuickTransaction();
        var e = tx.Open(_towns[_random.Next(_towns.Count)]).Read(GroupTown.Zone).Extent;
        return (Math.Clamp(((e.MinX + e.MaxX) * 0.5f) + (((float)_random.NextDouble() - 0.5f) * 400f), -Spread, Spread),
            Math.Clamp(((e.MinY + e.MaxY) * 0.5f) + (((float)_random.NextDouble() - 0.5f) * 400f), -Spread, Spread));
    }

    private Vector3D NearTown()
    {
        var (x, y) = NearTownPoint();
        return new Vector3D(x, y, 0d);
    }

    /// <summary>A town's key, 0, or the orphan key — what a member is moved to.</summary>
    private int RandomKey()
    {
        var roll = _random.Next(10);
        if (roll < 3 || _towns.Count == 0)
        {
            return roll == 0 ? OrphanKey : 0;
        }

        using var tx = _harness.Engine.CreateQuickTransaction();
        return tx.Open(_towns[_random.Next(_towns.Count)]).Read(GroupTown.Zone).Key;
    }

    /// <summary>One tick: churn, the sessions' walk, the fence and the track, and each session's delivery by its skip rate.</summary>
    public void Step()
    {
        Churn();
        Walk();
        _tick++;
        _harness.RunTick(_tick, Workers);
        for (var i = 0; i < _sessions.Length; i++)
        {
            if (_delivery[i].Next(100) >= _skipPercent[i])
            {
                Deliver(i);
            }
        }
    }

    /// <summary>No writes and no walk, every frame delivered, until every skipped session has caught up and no key is still filling.</summary>
    public void Quiesce()
    {
        for (var q = 0; q < PushReplication.LogDepth + 2 || Push.AnyGroupFillingForTest(); q++)
        {
            Assert.That(q, Is.LessThan(200), $"{FailureMarker} a key is still filling after 200 quiet ticks: a fill that never completes");
            _tick++;
            _harness.RunTick(_tick, Workers);
            for (var i = 0; i < _sessions.Length; i++)
            {
                Deliver(i);
            }
        }

        foreach (var session in _sessions)
        {
            Assert.That(_harness.HasFrame(session), Is.False, $"session {session.Value} still has a frame waiting: raise the quiet window");
        }
    }

    private void Churn()
    {
        var roll = _random.Next(100);
        using var tx = _harness.Engine.CreateQuickTransaction();
        if (roll < 15)
        {
            ChangeKeys(tx, _buildings, GroupBuilding.Key, 1 + _random.Next(3));
        }
        else if (roll < 25)
        {
            ChangeKeys(tx, _citizens, GroupCitizen.Key, 1 + _random.Next(2));
        }
        else if (roll < 33 && _towns.Count > 0)
        {
            var zone = tx.Open(_towns[_random.Next(_towns.Count)]).Read(GroupTown.Zone);
            for (var b = 1 + _random.Next(3); b > 0; b--)
            {
                SpawnBuilding(tx, zone.Key, In(zone.Extent));
            }
        }
        else if (roll < 40)
        {
            Destroy(tx, _buildings);
        }
        else if (roll < 48)
        {
            SpawnCitizen(tx, RandomKey(), NearTownPoint());
        }
        else if (roll < 54)
        {
            Destroy(tx, _citizens);
        }
        else if (roll < 80)
        {
            tx.Commit();
            TeleportCitizens(1 + _random.Next(3));
            return;
        }
        else if (roll < 84 && _towns.Count < 12)
        {
            tx.Commit();
            SpawnTown();
            return;
        }
        else if (roll < 87 && _towns.Count > 4)
        {
            // Its members keep their key and lose their region: released, and held again by nothing.
            Destroy(tx, _towns);
        }
        else if (roll < 91 && _towns.Count > 0)
        {
            var town = _towns[_random.Next(_towns.Count)];
            var zone = tx.Open(town).Read(GroupTown.Zone);
            var dx = ((float)_random.NextDouble() - 0.5f) * 120f;
            var dy = ((float)_random.NextDouble() - 0.5f) * 120f;
            zone.Extent = new AABB2F { MinX = zone.Extent.MinX + dx, MinY = zone.Extent.MinY + dy, MaxX = zone.Extent.MaxX + dx, MaxY = zone.Extent.MaxY + dy };
            tx.OpenMut(town).Set(GroupTown.Zone, zone);
        }

        tx.Commit();
    }

    private void ChangeKeys(Transaction tx, List<EntityId> from, Comp<GroupKey> key, int count)
    {
        for (var i = 0; i < count && from.Count > 0; i++)
        {
            tx.OpenMut(from[_random.Next(from.Count)]).Set(key, new GroupKey { Town = RandomKey() });
            KeyChanges++;
        }
    }

    private void Destroy(Transaction tx, List<EntityId> from)
    {
        if (from.Count == 0)
        {
            return;
        }

        var index = _random.Next(from.Count);
        tx.Destroy(from[index]);
        from.RemoveAt(index);
    }

    private void TeleportCitizens(int count)
    {
        var targets = new HashSet<int>();
        for (var i = 0; i < count && _citizens.Count > 0; i++)
        {
            targets.Add(_random.Next(_citizens.Count));
        }

        var seen = 0;
        using var tx = _harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<GroupCitizen>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (targets.Contains(seen++))
                    {
                        var (x, y) = NearTownPoint();
                        cluster.WriteSpatial(GroupCitizen.Bounds, slot, GroupTestSchema.At(x, y));
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private void Walk()
    {
        for (var i = 0; i < _sessions.Length; i++)
        {
            var roll = _random.Next(100);
            if (roll < 4)
            {
                _viewpoints[i] = NearTown();
                Teleports++;
            }
            else if (roll < 90)
            {
                var angle = _random.NextDouble() * Math.PI * 2.0;
                var stride = Radius * 0.04;
                _viewpoints[i] = new Vector3D(Math.Clamp(_viewpoints[i].X + (Math.Cos(angle) * stride), -Spread, Spread),
                    Math.Clamp(_viewpoints[i].Y + (Math.Sin(angle) * stride), -Spread, Spread), 0d);
            }

            _harness.Sessions.SetViewpoint(_sessions[i], _viewpoints[i]);
        }
    }

    // ══ Delivery, frame by frame ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private void Deliver(int session)
    {
        var id = _sessions[session];
        var replica = _harness.Replica(id);
        ref var send = ref _harness.Assembler.SendStateOf(id.Slot);
        while (send.TryClaimFrame(_harness.Assembler.Gate.CommittedTick, out var frame))
        {
            var held = replica.Store.CompleteGroupCount;
            replica.Apply(new ReadOnlySpan<byte>(frame.Bytes, frame.Length));
            send.CompleteSend(frame.Sequence);
            if ((replica.Store.Flags & TickFlags.Reset) != 0 && held > 0)
            {
                ResetsDroppingGroups++;
            }

            foreach (var (kind, _, key) in replica.Store.Groups)
            {
                if (kind == SourceStatus.GroupReleased)
                {
                    Releases++;
                }
                else if (_skipPercent[session] == 0)
                {
                    AssertCompleteNow(session, key, replica);
                }

                if (_skipPercent[session] == 0)
                {
                    AssertRecordIsLegal(session, kind, key);
                }
            }
        }
    }

    /// <summary>
    /// A group record of this tick is one the observer's geometry allows (P6): a town completes only within reach plus margin of the anchor — it was met
    /// within reach and may have filled while the session walked into the band — and is released only beyond reach plus margin, or gone.
    /// </summary>
    private void AssertRecordIsLegal(int session, byte kind, long key)
    {
        double? distance = null;
        using (var tx = _harness.Engine.CreateQuickTransaction())
        {
            foreach (var town in _towns)
            {
                var zone = tx.Open(town).Read(GroupTown.Zone);
                if (zone.Key == key)
                {
                    distance = DistanceTo(zone.Extent, _viewpoints[session]);
                }
            }
        }

        if (kind == SourceStatus.GroupComplete)
        {
            Assert.That(distance, Is.Not.Null.And.LessThanOrEqualTo(Reach + Margin + _anchorSlack + 0.01),
                $"{FailureMarker} tick {_tick}, session {session}: town {key} completed and the observer is not within its reach and margin (or it is gone)");
        }
        else
        {
            Assert.That(distance == null || distance > Reach + Margin - _anchorSlack - 0.01, Is.True,
                $"{FailureMarker} tick {_tick}, session {session}: town {key} released at {distance:F2} m, inside its reach and margin");
        }
    }

    private static double DistanceTo(AABB2F extent, Vector3D v)
    {
        var dx = Math.Max(0d, Math.Max(extent.MinX - v.X, v.X - extent.MaxX));
        var dy = Math.Max(0d, Math.Max(extent.MinY - v.Y, v.Y - extent.MaxY));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>A <c>GROUP_COMPLETE</c> arrived in a frame of this tick: every current member of the key is in the store.</summary>
    private void AssertCompleteNow(int session, long key, SessionReplica replica)
    {
        CompletionsChecked++;
        var missing = new List<string>();
        using var tx = _harness.Engine.CreateQuickTransaction();
        foreach (var (list, comp, name) in new[] { (_buildings, GroupBuilding.Key, nameof(GroupBuilding)), (_citizens, GroupCitizen.Key, nameof(GroupCitizen)) })
        {
            foreach (var entity in list)
            {
                if (tx.Open(entity).Read(comp).Town != key)
                {
                    continue;
                }

                var netId = _harness.NetIdOf(entity);
                if (netId == 0 || !replica.Store.TryLocate(netId, out _, out _))
                {
                    missing.Add($"{name} entity {entity.RawValue} (netId {netId})");
                }
            }
        }

        Assert.That(missing, Is.Empty,
            $"{FailureMarker} tick {_tick}, session {session}: GROUP_COMPLETE for town {key} arrived and these members are not in the store");
    }

    // ══ The comparison ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    public void AssertConverged(string because)
    {
        var divergences = new List<string>();
        if (Push.ShadowIllegal != 0)
        {
            divergences.Add($"the push path published {Push.ShadowIllegal} record(s) a client could not legally apply");
        }

        if (Push.GroupsOverLimit != 0)
        {
            divergences.Add($"{Push.GroupsOverLimit} key(s) went over MaxGroupsPerSession: the fixture's world is too dense for its truth");
        }

        var truth = ServerTruth(divergences);
        using (var tx = _harness.Engine.CreateQuickTransaction())
        {
            var towns = new List<(int Key, AABB2F Extent)>();
            foreach (var town in _towns)
            {
                var zone = tx.Open(town).Read(GroupTown.Zone);
                towns.Add((zone.Key, zone.Extent));
            }

            for (var i = 0; i < _sessions.Length; i++)
            {
                Compare(tx, i, towns, truth, divergences);
            }
        }

        if (divergences.Count == 0)
        {
            return;
        }

        var report = new StringBuilder().Append(FailureMarker).Append(' ').Append(because).Append(" — at tick ").Append(_tick).Append(':').AppendLine();
        foreach (var d in divergences)
        {
            report.Append("  ").AppendLine(d);
        }

        Assert.Fail(report.ToString());
    }

    private void Compare(Transaction tx, int session, List<(int Key, AABB2F Extent)> towns, Dictionary<int, Dictionary<uint, EntityId>> truth,
        List<string> divergences)
    {
        var replica = _harness.Replica(_sessions[session]);
        var v = _viewpoints[session];
        var required = new HashSet<int>();
        var possible = new HashSet<int>();
        foreach (var (key, extent) in towns)
        {
            var dx = Math.Max(0d, Math.Max(extent.MinX - v.X, v.X - extent.MaxX));
            var dy = Math.Max(0d, Math.Max(extent.MinY - v.Y, v.Y - extent.MaxY));
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance <= Reach - _anchorSlack - 0.01)
            {
                required.Add(key);
            }

            if (distance <= Reach + Margin + _anchorSlack + 0.01)
            {
                possible.Add(key);
            }
        }

        foreach (var key in required)
        {
            RequiredKeys++;
            if (!replica.Store.IsGroupComplete(0, key))
            {
                divergences.Add($"session {session}: the observer meets town {key} and the client does not hold it complete");
            }
        }

        foreach (var (set, key) in replica.Store.CompleteGroups)
        {
            if (set != 0 || !possible.Contains((int)key))
            {
                divergences.Add($"session {session}: the client holds town {key} complete and the observer is beyond its reach and margin (or it is gone)");
            }
        }

        if (replica.Store.Anomalies != 0)
        {
            divergences.Add($"session {session}: the decoder recorded {replica.Store.Anomalies} anomalies");
        }

        // At a quiet point no key is filling, so every key the session holds is complete on the client: the members it must hold through a key are exactly
        // those of the keys it holds complete — required ⊆ complete ⊆ possible was checked above — and the hysteresis band no longer licenses anything.
        var complete = new HashSet<int>();
        foreach (var (set, key) in replica.Store.CompleteGroups)
        {
            if (set == 0)
            {
                complete.Add((int)key);
            }
        }

        var visibility = _harness.Subscriptions.Plans[_citizenPlan].VisibilitySlackM;
        var margin = _anchorSlack + visibility + OracleHarness.MotionToleranceM + 0.01;
        CompareMembers(tx, session, replica, _buildingPlan, nameof(GroupBuilding), truth[_buildingPlan], complete, double.NaN, divergences);
        CompareMembers(tx, session, replica, _citizenPlan, nameof(GroupCitizen), truth[_citizenPlan], complete, margin, divergences);
    }

    /// <param name="discMargin">The geometric band's half-width for a member the Sphere observes; NaN for one it does not.</param>
    private void CompareMembers(Transaction tx, int session, SessionReplica replica, int plan, string name, Dictionary<uint, EntityId> expected,
        HashSet<int> complete, double discMargin, List<string> divergences)
    {
        var held = new HashSet<uint>(replica.NetIds(plan));
        foreach (var netId in held)
        {
            if (!expected.ContainsKey(netId))
            {
                divergences.Add($"session {session}: {name} netId {netId} is in the client's world and not in the server's");
            }
        }

        var v = _viewpoints[session];
        foreach (var (netId, entity) in expected)
        {
            var reference = tx.Open(entity);
            var key = plan == _buildingPlan ? reference.Read(GroupBuilding.Key).Town : reference.Read(GroupCitizen.Key).Town;
            var mustByKey = complete.Contains(key);
            var mayByKey = mustByKey;
            var mustByDisc = false;
            var mayByDisc = false;
            if (!double.IsNaN(discMargin))
            {
                var b = reference.Read(GroupCitizen.Bounds).Bounds;
                var dx = ((b.MinX + b.MaxX) * 0.5) - v.X;
                var dy = ((b.MinY + b.MaxY) * 0.5) - v.Y;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                // The radius the session's known-set was committed at, as the house oracle reads it: a budget loop may have shrunk it (09 § 10).
                var committed = Push.RadiusOf(_sessions[session]);
                var radius = committed > 0 ? Math.Min(committed, Radius) : Radius;
                mustByDisc = distance <= radius - discMargin;
                mayByDisc = distance <= radius + discMargin;
            }

            var holds = held.Contains(netId);
            if ((mustByKey || mustByDisc) && !holds)
            {
                RequiredMembers++;
                divergences.Add($"session {session}: {name} netId {netId} (entity {entity.RawValue}, town {key}) must be held "
                    + $"({(mustByKey ? "its town is met" : "inside the disc")}) and is not");
                continue;
            }

            if (mustByKey || mustByDisc)
            {
                RequiredMembers++;
            }

            if (holds && !mayByKey && !mayByDisc)
            {
                divergences.Add($"session {session}: {name} netId {netId} (entity {entity.RawValue}, town {key}) is held and neither its town nor the disc "
                    + "reaches it");
                continue;
            }

            if (holds)
            {
                Compared++;
                var template = plan == _buildingPlan ? reference.Read(GroupBuilding.Ai).Template : reference.Read(GroupCitizen.Ai).Template;
                var actual = replica.Value(plan, netId, plan == _buildingPlan ? "kind" : "template");
                if (actual != template)
                {
                    divergences.Add($"session {session}: {name} netId {netId} carries template {actual} and the entity has {template}: another entity's state");
                }
            }
        }
    }

    private Dictionary<int, Dictionary<uint, EntityId>> ServerTruth(List<string> divergences)
    {
        var truth = new Dictionary<int, Dictionary<uint, EntityId>>();
        foreach (var plan in new[] { _buildingPlan, _citizenPlan })
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

                var live = occupancy;
                while (live != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(live);
                    live &= live - 1;
                    var hot = (ReplicationHotEntry*)((byte*)block + layout.HotOffset + (slot * layout.HotStride));
                    if (hot->NetId == NetIdAllocator.NoNetId)
                    {
                        divergences.Add($"plan {plan} chunk {chunkId} slot {slot} is live and holds no netId");
                        continue;
                    }

                    if (!byNetId.TryAdd(hot->NetId, hot->Entity))
                    {
                        divergences.Add($"plan {plan}: netId {hot->NetId} is leased to two live slots");
                    }
                }
            }

            truth[plan] = byNetId;
        }

        return truth;
    }
}
