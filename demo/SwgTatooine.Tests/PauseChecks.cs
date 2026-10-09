using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A client can stop and start the simulation (<see cref="TatooineReplication.SetPaused"/>), and everything else keeps running.
/// </summary>
/// <remarks>
/// <para>
/// <b>The half that matters is what does NOT stop.</b> Pausing by gating the simulation's systems is only safe because
/// the session system, replication and the engine's own stages keep ticking — gate the wrong one and the world stops for
/// good, because the command that resumes it is drained by a system that is no longer running. That is a deadlock a
/// reviewer cannot see by reading sixteen early returns, so it is asserted.
/// </para>
/// <para>
/// The other half is a net: a system added later and not gated shows up as an entity that moved while the world was
/// stopped. The net is over <b>creatures and players</b> — not over every moving archetype, which is what this said
/// before. <see cref="Positions"/> enumerates those two and nothing else, so a dropped gate on the townsfolk mover, the
/// shuttles, the portals or the space realm passes it. Widening it means an enumerator per archetype; until then the
/// claim matches the net rather than the intention.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class PauseChecks
{
    private string _dir;
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
        config.TickRateHz = 10;
        config.WorkerCount = 1;
        return config;
    }

    /// <summary>Every creature's and player's position, by entity, so two snapshots can be compared.</summary>
    /// <remarks>Creatures and players only — see the fixture's remarks for what that does not cover.</remarks>
    private static Dictionary<EntityId, (float X, float Z)> Positions(TatooineSim sim)
    {
        var rows = new Dictionary<EntityId, (float, float)>();
        foreach (var row in Causality.Creatures(sim))
        {
            rows[row.Id] = (row.X, row.Z);
        }

        using var tx = sim.Dbe.CreateQuickTransaction();
        foreach (var id in sim.Index.Players)
        {
            var place = tx.Open(id).Read(Player.Bounds);
            rows[id] = (place.X, place.Z);
        }

        return rows;
    }

    private static int Moved(Dictionary<EntityId, (float X, float Z)> before, Dictionary<EntityId, (float X, float Z)> after)
    {
        var moved = 0;
        foreach (var (id, a) in before)
        {
            if (after.TryGetValue(id, out var b) && MathF.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Z - a.Z) * (b.Z - a.Z))) > 0.01f)
            {
                moved++;
            }
        }

        return moved;
    }

    [Test]
    public void APausedWorldStopsMoving_AndAResumedOneMovesAgain()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var link = _harness.Connect(TatooineReplication.GodKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");
        _harness.Ticks(10);

        // Running: something must move, or the rest of this proves nothing.
        var a = Positions(sim);
        _harness.Ticks(10);
        var b = Positions(sim);
        Assert.That(Moved(a, b), Is.GreaterThan(0), "nothing moved in a running world, so this cannot tell a pause from a calm");

        link.SendCommand("SetPaused", new RecordValues { ["paused"] = FieldValue.Of(1) });
        SessionHarness.Until(() => TatooineReplication.SimulationPaused, "the pause to be applied by a tick");

        // A tick to let whatever was already in flight land, then the window the world must sit still through.
        _harness.Ticks(2);
        var c = Positions(sim);
        _harness.Ticks(20);
        var d = Positions(sim);
        Assert.That(Moved(c, d), Is.Zero, "an entity moved while the simulation was paused: a system is not gated");

        link.SendCommand("SetPaused", new RecordValues { ["paused"] = FieldValue.Of(0) });
        SessionHarness.Until(() => !TatooineReplication.SimulationPaused, "the resume to be applied by a tick");

        // THE deadlock check: the resume arrived at all. A gated session system would never have drained it, and the
        // wait above would have timed out rather than reaching here.
        _harness.Ticks(20);
        var e = Positions(sim);
        Assert.That(Moved(d, e), Is.GreaterThan(0), "the world did not restart after a resume");
    }

    [Test]
    public void APausedServerKeepsTickingAndKeepsTalkingToItsClients()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var link = _harness.Connect(TatooineReplication.GodKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");
        _harness.Ticks(10);

        link.SendCommand("SetPaused", new RecordValues { ["paused"] = FieldValue.Of(1) });
        SessionHarness.Until(() => TatooineReplication.SimulationPaused, "the pause to be applied by a tick");

        var tickBefore = sim.Runtime.CurrentTickNumber;
        var framesBefore = link.Sent.Length;
        _harness.Ticks(20);

        Assert.Multiple(() =>
        {
            // The runtime must keep ticking, or nothing could ever drain the resume.
            Assert.That(sim.Runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(tickBefore + 20), "the runtime stopped ticking while paused");

            // And the session must keep hearing from the server, or it would be closed for silence and the operator
            // would have to restart the process to get their world back.
            Assert.That(link.Sent.Length, Is.GreaterThan(framesBefore), "a paused server sent its client nothing");
            Assert.That(link.Closed, Is.Null, "the session was closed while the world was paused");
        });
    }

    [Test]
    public void APlayerSessionMayNotStopEveryoneElsesWorld()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var link = _harness.Connect(TatooineReplication.PlayerKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");
        _harness.Ticks(10);

        link.SendCommand("SetPaused", new RecordValues { ["paused"] = FieldValue.Of(1) });
        _harness.Ticks(20);

        // Roles(SessionRole.Spectator): the engine refuses it for a player before the system ever sees it. Narrow, and
        // still nowhere near narrow enough for a real server — see SetPaused's own remarks.
        Assert.That(TatooineReplication.SimulationPaused, Is.False, "a possessed player stopped the world for every other client");
    }
}
