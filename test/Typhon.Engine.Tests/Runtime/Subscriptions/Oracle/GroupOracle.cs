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
/// The group oracle (14 § 3.8 and § 5, the acceptance of steps 4.1 and 4.2): towns, their buildings, citizens and shards — about half the members have no
/// position — under seeded churn — key changes, spawns, destroys, teleports, towns appearing, vanishing and moving — and walking sessions that skip frames,
/// each compared with what it is entitled to hold.
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
    private readonly int _shardPlan;
    private readonly uint _townSet;
    private readonly uint _listSet;

    // Per session, the client side of the explicit set (14 § 4): its next seq and request id, the requests it awaits an answer to, and the lists it holds.
    private readonly ushort[] _seq;
    private readonly ushort[] _requestIds;
    private readonly Dictionary<ushort, (byte Op, int List, ushort Seq)>[] _pending;
    private readonly HashSet<int>[] _subscribed;
    private readonly Random _requests;
    private readonly double[] _tokens;
    private readonly long[] _tokensAt;
    private const int Lists = 6;

    // Visibility tags (14 § 6): whether the members declare a mask, and each session's own.
    private readonly bool _tags;
    private readonly uint[] _masks;

    private readonly List<EntityId> _towns = [];
    private readonly List<EntityId> _buildings = [];
    private readonly List<EntityId> _citizens = [];
    private readonly List<EntityId> _shards = [];
    private int _nextTownKey = 100;
    private int _nextTemplate = 1;
    private long _tick;

    private GroupOracle(FrameHarness harness, int seed, int[] skipPercent, bool tags)
    {
        _harness = harness;
        _tags = tags;
        _skipPercent = skipPercent;
        _random = new Random(seed);
        _sessions = harness.OpenSessions(skipPercent.Length, GroupTestSchema.Profile);
        _viewpoints = new Vector3D[_sessions.Length];
        _delivery = new Random[_sessions.Length];
        _anchorSlack = Math.Min(Radius / 48d, harness.Subscriptions.Push.CellSize / 2d);
        _buildingPlan = harness.PlanIndex(nameof(GroupBuilding));
        _citizenPlan = harness.PlanIndex(nameof(GroupCitizen));
        _shardPlan = harness.PlanIndex(nameof(GroupPositionless));
        _townSet = (uint)harness.CatalogPlan.GroupSetIndex(GroupTestSchema.Set);
        _listSet = (uint)harness.CatalogPlan.GroupSetIndex(GroupTestSchema.ListSet);
        _seq = new ushort[_sessions.Length];
        _requestIds = new ushort[_sessions.Length];
        _pending = new Dictionary<ushort, (byte, int, ushort)>[_sessions.Length];
        _subscribed = new HashSet<int>[_sessions.Length];
        _requests = new Random(seed * 31 + 7);
        _tokens = new double[_sessions.Length];
        _tokensAt = new long[_sessions.Length];
        for (var i = 0; i < _sessions.Length; i++)
        {
            _pending[i] = [];
            _subscribed[i] = [];
        }
        for (var i = 0; i < _sessions.Length; i++)
        {
            _delivery[i] = new Random(seed + (7919 * (i + 1)));
        }

        // Public only, one tag, both: what a player, a faction member and a GM see.
        _masks = new uint[_sessions.Length];
        for (var i = 0; tags && i < _sessions.Length; i++)
        {
            _masks[i] = (i % 3) switch { 0 => 0u, 1 => 1u, _ => 3u };
            Assert.That(harness.Sessions.SetVisibility(_sessions[i], _masks[i]), Is.True);
        }
    }

    /// <summary>Members whose mask the workload changed, each pushed; session masks it changed, each a reset.</summary>
    public int MaskFlips { get; private set; }

    /// <inheritdoc cref="MaskFlips"/>
    public int SessionMaskChanges { get; private set; }

    /// <summary>Members a session was required not to hold because it does not see them, summed over the comparisons.</summary>
    public long HiddenMembers { get; private set; }

    // P19: public, or a bit in common with the session's mask.
    private bool Sees(int session, uint mask) => mask == 0 || (mask & _masks[session]) != 0;

    // Most members public; the rest carry one of two tags.
    private uint RandomMask()
    {
        if (!_tags)
        {
            return 0;
        }

        var roll = _random.Next(10);
        return roll < 6 ? 0u : roll < 8 ? 1u : 2u;
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

    /// <summary>SubscribeRequests sent, and answers checked: each APPLIED against the client's store in the frame it arrives, each ERROR against its key.</summary>
    public int RequestsSent { get; private set; }

    /// <inheritdoc cref="RequestsSent"/>
    public int AnswersChecked { get; private set; }

    /// <summary>Lists a session was required to hold complete at a quiet point, summed.</summary>
    public long RequiredLists { get; private set; }

    /// <summary>The push path.</summary>
    public PushReplication Push => _harness.Subscriptions.Push;

    /// <summary>The frame stage under test.</summary>
    public FrameAssembler Assembler => _harness.Assembler;

    /// <summary>What every failure of this oracle's own assertions starts with: a mutant test's evidence that the oracle, not its scaffolding, rejected.</summary>
    public const string FailureMarker = "group oracle:";

    /// <summary>
    /// The mutant SUB-33's tap clause forbids, made from the test side: a member's key component no longer marks its slot when written, so a key change
    /// reaches the index and never the projection.
    /// </summary>
    public void RemoveTheTapForTest()
    {
        foreach (var plan in new[] { _buildingPlan, _citizenPlan, _shardPlan })
        {
            _harness.Subscriptions.ReplicationStates[plan].ClusterState.ClearGroupKeySlotsForTest();
        }
    }

    /// <summary>
    /// The mutant SUB-34's projection clause forbids, made from the test side: a member's mask is written and replication is not told, so the world's
    /// mask and the one the sessions are judged by part.
    /// </summary>
    public void FlipMasksUntoldForTest() => _untoldFlips = true;

    private bool _untoldFlips;

    public static GroupOracle Create(DatabaseEngine engine, int seed, int[] skipPercent, string name, int enterBudget, bool tags = false, int frameBytes = 0)
    {
        var harness = FrameHarness.Create(engine, s => GroupTestSchema.Declare(s, Radius, Reach, Margin, shards: true, tags: tags), name,
            new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxSessions = 16,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
            MaxGroupsPerSession = 12,
            EnterBudgetPerFrame = enterBudget,
            FrameBytes = frameBytes > 0 ? frameBytes : new SubscriptionsOptions().FrameBytes,
            CheckFrameMeterForTest = frameBytes > 0,
            PushShadow = true,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
        });
        try
        {
            harness.RunFence = true;
            harness.DrainNetIds = true;
            harness.RunIngress = true;
            // #1409: the region index is incremental; every refresh that changes it is checked against a from-scratch build.
            harness.Subscriptions.Push.VerifyRegionIndexForTest = true;
            // #1410: the wanted keys are counted, not rebuilt; every blocks step is checked against a recount.
            harness.Subscriptions.Hub.VerifyWantedForTest = true;
            var oracle = new GroupOracle(harness, seed, skipPercent, tags);
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

                for (var s = 10 + _random.Next(30); s > 0; s--)
                {
                    SpawnShard(tx, zone.Key);
                }
            }

            for (var b = 0; b < 30; b++)
            {
                SpawnBuilding(tx, b % 3 == 0 ? OrphanKey : 0, Anywhere());
                SpawnShard(tx, b % 3 == 0 ? OrphanKey : 0);
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
            GroupBuilding.Key.Set(new GroupKey { Town = key }), GroupBuilding.Tag.Set(new GroupTag { Mask = RandomMask() })));
    }

    private void SpawnShard(Transaction tx, int key)
    {
        var ai = new ProjAi { Template = (byte)(_nextTemplate++ & 0xFF) };
        var list = _random.Next(Lists + 2);   // 0 and Lists + 1: no list, or one nobody may ask for twice as often as any other
        _shards.Add(tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(in ai), GroupPositionless.Key.Set(new GroupKey { Town = key }),
            GroupPositionless.List.Set(new GroupListKey { List = list > Lists ? 0 : list }), GroupPositionless.Tag.Set(new GroupTag { Mask = RandomMask() })));
    }

    private void SpawnCitizen(Transaction tx, int key, (float X, float Y) at)
    {
        var bounds = GroupTestSchema.At(at.X, at.Y);
        var ai = new ProjAi { Template = (byte)(_nextTemplate++ & 0xFF) };
        _citizens.Add(tx.Spawn<GroupCitizen>(GroupCitizen.Bounds.Set(in bounds), GroupCitizen.Ai.Set(in ai),
            GroupCitizen.Key.Set(new GroupKey { Town = key }), GroupCitizen.Tag.Set(new GroupTag { Mask = RandomMask() })));
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
        Ask();
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
        for (var q = 0; q < PushReplication.LogDepth + 2 || Push.AnyGroupFillingForTest() || Owes() || Filling(); q++)
        {
            Assert.That(q, Is.LessThan(200), $"{FailureMarker} after 200 quiet ticks a key is still filling ({Push.AnyGroupFillingForTest()}), a session "
                + $"still owes records ({Owes()}) or a view is incomplete ({Filling()}): a fill or a drain that never completes");
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

    // A frame a few records wide (15) paces the geometric fill as well: quiet means every view complete.
    private bool Filling()
    {
        foreach (var session in _sessions)
        {
            if (_harness.StateOf(session) is { ViewComplete: false })
            {
                return true;
            }
        }

        return false;
    }

    // A frame a few records wide (15) drains what it owes over several frames: quiet means nothing owed as well.
    private bool Owes()
    {
        foreach (var session in _sessions)
        {
            if (_harness.StateOf(session) is { OwedCount: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    private void Churn()
    {
        using var tx = _harness.Engine.CreateQuickTransaction();

        // Visibility (14 § 6): a member's mask changes, pushed as any projected field is; now and then a session's own, which resets its view.
        if (_tags && _random.Next(100) < 25)
        {
            FlipMask(tx);
        }

        if (_tags && _random.Next(100) < 2)
        {
            var i = _random.Next(_sessions.Length);
            _masks[i] = (uint)_random.Next(4);
            Assert.That(_harness.Sessions.SetVisibility(_sessions[i], _masks[i]), Is.True);
            SessionMaskChanges++;
        }

        // The shards' own churn, beside the rest: their key changes reach a cluster with no block, or attach one, or leave one idle.
        var shardRoll = _random.Next(100);
        if (shardRoll < 20)
        {
            ChangeKeys(tx, _shards, GroupPositionless.Key, 1 + _random.Next(4));
        }
        else if (shardRoll < 28 && _towns.Count > 0)
        {
            var zone = tx.Open(_towns[_random.Next(_towns.Count)]).Read(GroupTown.Zone);
            for (var s = 1 + _random.Next(4); s > 0; s--)
            {
                SpawnShard(tx, zone.Key);
            }
        }
        else if (shardRoll < 34)
        {
            Destroy(tx, _shards);
        }
        else if (shardRoll < 44 && _shards.Count > 0)
        {
            tx.OpenMut(_shards[_random.Next(_shards.Count)]).Set(GroupPositionless.List, new GroupListKey { List = _random.Next(Lists + 1) });
            KeyChanges++;
        }

        var roll = _random.Next(100);
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

    // Now and then a session asks for a list or gives one back, within SubscribeRequest's rate (5 a second, a burst of 5) as a client keeps it: a bucket of
    // its own, refilled a little slower than the server's, so no request is refused. One that were would be answered by an ACK — which a session further
    // behind than the ACK history never receives, a refusal then settling through lastSeq alone — and the oracle could not tell it from a lost answer.
    private void Ask()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < _sessions.Length; i++)
        {
            _tokens[i] = _tokensAt[i] == 0 ? 5d : Math.Min(5d, _tokens[i] + (4d * (now - _tokensAt[i]) / System.Diagnostics.Stopwatch.Frequency));
            _tokensAt[i] = now;
            if (_requests.Next(100) >= 3 || _tokens[i] < 1d)
            {
                continue;
            }

            _tokens[i] -= 1d;

            var list = 1 + _requests.Next(Lists);
            var op = _subscribed[i].Contains(list) ? BuiltInCommands.UnsubscribeOp : BuiltInCommands.SubscribeOp;
            var id = ++_requestIds[i];
            var seq = ++_seq[i];
            var values = new RecordValues
            {
                [BuiltInCommands.SubscribeRequestIdField] = FieldValue.Of(id),
                [BuiltInCommands.SubscribeSetField] = FieldValue.Of(_listSet),
                [BuiltInCommands.SubscribeKeyField] = new FieldValue { Integers = [(ulong)list] },
                [BuiltInCommands.SubscribeOpField] = FieldValue.Of(op),
            };

            var buffer = new byte[256];
            var writer = new WireWriter(buffer);
            CommandsMessage.Write(ref writer, (uint)_tick, [(_harness.CatalogPlan.CommandByName(BuiltInCommands.SubscribeRequest), seq, values)],
                _harness.Subscriptions.Realm0Frame);
            _harness.Subscriptions.Ingress.OnCommands(_sessions[i], writer.Written);
            _pending[i][id] = (op, list, seq);
            RequestsSent++;
        }
    }

    // A frame's answers to this session's requests: an APPLIED subscription's list is complete in the store the frame left, an unsubscription's is not; an
    // ERROR is the application's refusal of the denied list; a refusal ACK (the rate) forgets the request.
    private void CheckAnswers(int session, SessionReplica replica)
    {
        foreach (var (seq, _) in replica.Store.Acks)
        {
            foreach (var (id, request) in _pending[session])
            {
                if (request.Seq == seq)
                {
                    _pending[session].Remove(id);
                    break;
                }
            }
        }

        foreach (var (requestId, status, code) in replica.Store.Sources)
        {
            if (!_pending[session].Remove(requestId, out var request))
            {
                Assert.Fail($"{FailureMarker} tick {_tick}, session {session}: an answer to request {requestId}, which is not awaiting one (twice, or never asked)");
                continue;
            }

            AnswersChecked++;
            var complete = replica.Store.IsGroupComplete(_listSet, request.List);
            if (status == SourceStatus.Error)
            {
                Assert.That(request.List == GroupTestSchema.DeniedList && code == GroupTestSchema.DeniedListCode, Is.True,
                    $"{FailureMarker} tick {_tick}, session {session}: request {requestId} for list {request.List} refused with code {code}");
            }
            else if (request.Op == BuiltInCommands.SubscribeOp)
            {
                Assert.That(complete, Is.True,
                    $"{FailureMarker} tick {_tick}, session {session}: request {requestId} for list {request.List} APPLIED and the list is not complete");
                _subscribed[session].Add(request.List);
            }
            else
            {
                Assert.That(complete, Is.False,
                    $"{FailureMarker} tick {_tick}, session {session}: request {requestId} giving list {request.List} back APPLIED and it is still complete");
                _subscribed[session].Remove(request.List);
            }
        }
    }

    private void FlipMask(Transaction tx)
    {
        var mask = RandomMask();
        switch (_random.Next(3))
        {
            case 0 when _buildings.Count > 0:
            {
                var e = tx.OpenMut(_buildings[_random.Next(_buildings.Count)]);
                e.Set(GroupBuilding.Tag, new GroupTag { Mask = mask });
                if (!_untoldFlips)
                {
                    _harness.Subscriptions.Commands.Replicate(in e);
                }

                break;
            }

            case 1 when _citizens.Count > 0:
            {
                var e = tx.OpenMut(_citizens[_random.Next(_citizens.Count)]);
                e.Set(GroupCitizen.Tag, new GroupTag { Mask = mask });
                if (!_untoldFlips)
                {
                    _harness.Subscriptions.Commands.Replicate(in e);
                }

                break;
            }

            case 2 when _shards.Count > 0:
            {
                var e = tx.OpenMut(_shards[_random.Next(_shards.Count)]);
                e.Set(GroupPositionless.Tag, new GroupTag { Mask = mask });
                if (!_untoldFlips)
                {
                    _harness.Subscriptions.Commands.Replicate(in e);
                }

                break;
            }

            default:
                return;
        }

        MaskFlips++;
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
            AssertEntersVisible(session, replica);
            if ((replica.Store.Flags & TickFlags.Reset) != 0 && held > 0)
            {
                ResetsDroppingGroups++;
            }

            foreach (var (kind, set, key) in replica.Store.Groups)
            {
                if (kind == SourceStatus.GroupReleased)
                {
                    Releases++;
                }
                else if (_skipPercent[session] == 0)
                {
                    AssertCompleteNow(session, set, key, replica);
                }

                if (_skipPercent[session] == 0 && set == _townSet)
                {
                    AssertRecordIsLegal(session, kind, key);
                }
            }

            CheckAnswers(session, replica);
        }
    }

    /// <summary>How many entered members were checked, frame by frame, against the mask of the session that received them.</summary>
    public int EntersChecked { get; private set; }

    // SUB-34's "is sent", frame by frame: every enter of a frame built this tick — the world still the one it was built from — is of a member the session
    // sees. The quiet-point comparison alone misses one entered and left between two points.
    private void AssertEntersVisible(int session, SessionReplica replica)
    {
        var store = replica.Store;
        if (!_tags || store.Tick != _tick)
        {
            return;
        }

        using var tx = _harness.Engine.CreateQuickTransaction();
        foreach (var plan in new[] { _buildingPlan, _citizenPlan, _shardPlan })
        {
            var archetype = store.Archetypes[plan];
            for (var i = 0; i < archetype.EnteredCount; i++)
            {
                var netId = archetype.NetIds[archetype.Entered[i]];
                if (!_harness.Subscriptions.Commands.TryResolveAny(_sessions[session], netId, out var entity))
                {
                    continue;
                }

                var reference = tx.Open(entity);
                var mask = plan == _buildingPlan ? reference.Read(GroupBuilding.Tag).Mask
                    : plan == _citizenPlan ? reference.Read(GroupCitizen.Tag).Mask
                    : reference.Read(GroupPositionless.Tag).Mask;
                EntersChecked++;
                if (!Sees(session, mask))
                {
                    Assert.Fail($"{FailureMarker} tick {_tick}, session {session}: netId {netId} entered and the session's mask does not see it");
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
    private void AssertCompleteNow(int session, uint set, long key, SessionReplica replica)
    {
        CompletionsChecked++;
        var missing = new List<string>();
        using var tx = _harness.Engine.CreateQuickTransaction();
        var lists = set == _listSet;
        foreach (var (list, comp, tag, name) in lists
                     ? new[] { (_shards, GroupPositionless.Key, GroupPositionless.Tag, nameof(GroupPositionless)) }
                     : new[]
                     {
                         (_buildings, GroupBuilding.Key, GroupBuilding.Tag, nameof(GroupBuilding)),
                         (_citizens, GroupCitizen.Key, GroupCitizen.Tag, nameof(GroupCitizen)),
                         (_shards, GroupPositionless.Key, GroupPositionless.Tag, nameof(GroupPositionless)),
                     })
        {
            foreach (var entity in list)
            {
                var reference = tx.Open(entity);
                var member = lists ? reference.Read(GroupPositionless.List).List : reference.Read(comp).Town;

                // Complete means every member the session sees (P19): one it does not is never sent.
                if (member != key || !Sees(session, reference.Read(tag).Mask))
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
            $"{FailureMarker} tick {_tick}, session {session}: GROUP_COMPLETE for {(lists ? "list" : "town")} {key} arrived and these members are not in the store");
    }

    // ══ The comparison ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    public void AssertConverged(string because)
    {
        var divergences = new List<string>();
        if (Push.ShadowIllegal != 0)
        {
            divergences.Add($"the push path published {Push.ShadowIllegal} record(s) a client could not legally apply");
        }

        if (_harness.Subscriptions.Hub.WantedMismatches != 0)
        {
            divergences.Add($"the wanted-key counts differed from a recount {_harness.Subscriptions.Hub.WantedMismatches} time(s)");
        }

        if (Push.RegionIndexMismatches != 0)
        {
            divergences.Add($"the incremental region index differed from a from-scratch build {Push.RegionIndexMismatches} time(s)");
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

                // Nothing went over the bound, so no session may have been told it left a town out (#1407).
                for (var set = 0u; set < (uint)_harness.CatalogPlan.GroupSets.Length; set++)
                {
                    var over = _harness.Replica(_sessions[i]).Store.GroupSetOverLimit(set);
                    if (over != 0)
                    {
                        divergences.Add($"session {i} was told it left {over} key(s) of set {set} out, with none over the bound");
                    }
                }
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
            if (!replica.Store.IsGroupComplete(_townSet, key))
            {
                divergences.Add($"session {session}: the observer meets town {key} and the client does not hold it complete");
            }
        }

        foreach (var (set, key) in replica.Store.CompleteGroups)
        {
            if (set == _townSet && !possible.Contains((int)key))
            {
                divergences.Add($"session {session}: the client holds town {key} complete and the observer is beyond its reach and margin (or it is gone)");
            }
            else if (set == _listSet && !_subscribed[session].Contains((int)key))
            {
                divergences.Add($"session {session}: the client holds list {key} complete and no APPLIED subscription of its asked for it");
            }
        }

        // Every list a subscription was applied for is held complete; every request has its answer by now.
        foreach (var list in _subscribed[session])
        {
            RequiredLists++;
            if (!replica.Store.IsGroupComplete(_listSet, list))
            {
                divergences.Add($"session {session}: its subscription to list {list} was APPLIED and the client does not hold it complete");
            }
        }

        if (_pending[session].Count > 0)
        {
            divergences.Add($"session {session}: {_pending[session].Count} request(s) still unanswered at a quiet point");
        }

        if (replica.Store.Anomalies != 0)
        {
            divergences.Add($"session {session}: the decoder recorded {replica.Store.Anomalies} anomalies");
        }

        // At a quiet point no key is filling, so every key the session holds is complete on the client: the members it must hold through a key are exactly
        // those of the keys it holds complete — required ⊆ complete ⊆ possible was checked above — and the hysteresis band no longer licenses anything.
        var complete = new HashSet<int>();
        var completeLists = new HashSet<int>();
        foreach (var (set, key) in replica.Store.CompleteGroups)
        {
            if (set == _townSet)
            {
                complete.Add((int)key);
            }
            else if (set == _listSet)
            {
                completeLists.Add((int)key);
            }
        }

        var visibility = _harness.Subscriptions.Plans[_citizenPlan].VisibilitySlackM;
        var margin = _anchorSlack + visibility + OracleHarness.MotionToleranceM + 0.01;
        CompareMembers(tx, session, replica, _buildingPlan, nameof(GroupBuilding), truth[_buildingPlan], complete, double.NaN, divergences);
        CompareMembers(tx, session, replica, _citizenPlan, nameof(GroupCitizen), truth[_citizenPlan], complete, margin, divergences);
        CompareShards(tx, session, replica, truth[_shardPlan], complete, completeLists, divergences);
    }

    /// <summary>
    /// The shards, from the world's side: a positionless member has an identity only while a held town needs it (14 § 5), so the truth the blocks give is
    /// not every shard. Every shard of a complete town is held, with its own state; no other is.
    /// </summary>
    private void CompareShards(Transaction tx, int session, SessionReplica replica, Dictionary<uint, EntityId> identified, HashSet<int> complete,
        HashSet<int> completeLists, List<string> divergences)
    {
        var held = new HashSet<uint>(replica.NetIds(_shardPlan));
        foreach (var netId in held)
        {
            if (!identified.ContainsKey(netId))
            {
                divergences.Add($"session {session}: shard netId {netId} is in the client's world and holds no identity on the server");
            }
        }

        foreach (var entity in _shards)
        {
            var reference = tx.Open(entity);
            var key = reference.Read(GroupPositionless.Key).Town;
            var list = reference.Read(GroupPositionless.List).List;
            var netId = _harness.NetIdOf(entity);
            var holds = netId != 0 && held.Contains(netId);
            if (!Sees(session, reference.Read(GroupPositionless.Tag).Mask))
            {
                HiddenMembers++;
                if (holds)
                {
                    divergences.Add($"session {session}: shard netId {netId} (entity {entity.RawValue}) is held and the session does not see it");
                }

                continue;
            }

            if (complete.Contains(key) || completeLists.Contains(list))
            {
                RequiredMembers++;
                if (!holds)
                {
                    divergences.Add($"session {session}: shard entity {entity.RawValue} (netId {netId}, town {key}, list {list}) must be held, its town or list is complete, and is not");
                    continue;
                }

                Compared++;
                var template = reference.Read(GroupPositionless.Ai).Template;
                var actual = replica.Value(_shardPlan, netId, "kind");
                if (actual != template)
                {
                    divergences.Add($"session {session}: shard netId {netId} carries template {actual} and the entity has {template}: another entity's state");
                }
            }
            else if (holds)
            {
                divergences.Add($"session {session}: shard netId {netId} (entity {entity.RawValue}, town {key}, list {list}) is held and no town or list of the client's holds it");
            }
        }
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
            var mask = plan == _buildingPlan ? reference.Read(GroupBuilding.Tag).Mask : reference.Read(GroupCitizen.Tag).Mask;
            if (!Sees(session, mask))
            {
                // Neither its town nor the disc licenses an entity the session does not see (P19).
                HiddenMembers++;
                if (held.Contains(netId))
                {
                    divergences.Add($"session {session}: {name} netId {netId} (entity {entity.RawValue}, mask {mask}) is held and the session does not see it");
                }

                continue;
            }

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
        foreach (var plan in new[] { _buildingPlan, _citizenPlan, _shardPlan })
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
