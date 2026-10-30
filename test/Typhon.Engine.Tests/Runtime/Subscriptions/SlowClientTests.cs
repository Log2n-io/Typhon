using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// AC-16 (#1040, 07 § 8): a client too slow for its frames is closed with 1013 within five seconds, and a client 200 ms away at god rates is never degraded.
/// </summary>
/// <remarks>
/// <para>
/// <b>The link is simulated, the server is not.</b> The harness's assembler, skip policy and send states are the runtime's; what stands in for a socket is a
/// per-tick byte allowance (the slow link) or a one-tick delivery delay each way (the far one). That is what makes the criterion a test rather than a
/// throttled-network run: the two numbers it is about — bytes a second and a round trip — are exactly the two the simulation controls, and nothing else on the
/// box can move them.
/// </para>
/// <para>
/// <b>God rates:</b> a world observer over 400 creatures, every one of which moves every tick — about 56 KB/s. The harness ticks at 10 Hz, so 5 s is 50
/// ticks and 10 KB/s is 1 000 bytes a tick.
/// </para>
/// <para>
/// <b>What "too slow" means, measured (2026-10-19).</b> AC-16 as 07 words it — "a 10 KB/s client closed with 1013 within 5 s" — does not hold at that rate,
/// and should not: skip-not-queue serves a slow client every frame it can finish, each one current (SUB-03), and closes only a client that cannot finish
/// one within <see cref="SubscriptionsOptions.CloseStalledAfter"/> (SUB-15). A 10 KB/s client of a 56 KB/s stream is served about one frame in six — its
/// longest skip run is 6 ticks against a close bound of 30 — and is never closed. A client whose single frame takes longer than the bound to drain is
/// closed, and within five seconds. Both are pinned here; the criterion is restated in 08.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class SlowClientTests : TestBase<SlowClientTests>
{
    private const string Profile = "god-world";
    private const int Creatures = 400;
    private const int TickMs = 100;

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    private FrameHarness Create(int creatures = Creatures)
    {
        var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, TestContext.CurrentContext.Test.Name,
            new SubscriptionsOptions
        {
            MaxSessions = 4,
            StatePoolBudgetBytes = 32L * 1024 * 1024,
            FramePoolBudgetBytes = 32L * 1024 * 1024,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0),
        });
        harness.RunFence = true;

        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < creatures; i++)
        {
            var bounds = At(i % 64 * 40f, i / 64 * 40f);
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(new ProjAi { Template = (byte)i }));
        }

        tx.Commit();
        return harness;
    }

    private static ProjBounds At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    /// <summary>Every creature takes a step in a direction of its own, changing every tick: a velocity a segment never predicts.</summary>
    private static void MoveEveryCreature(FrameHarness harness, long tick)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                var b = cluster.GetReadOnly(ProjCreature.Bounds, slot).Bounds;
                var angle = (tick * 0.7) + (slot * 1.3);
                cluster.WriteSpatial(ProjCreature.Bounds, slot, At(b.MinX + (float)Math.Cos(angle), b.MinY + (float)Math.Sin(angle)));
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// A client of a 10 KB/s link facing frames it cannot drain within the close bound — 4 000 creatures moving, so one frame is more than the 30 KB that
    /// three seconds carry: the in-flight bound skips it, the skip run degrades it, then closes it with 1013 — within five seconds of the last frame it
    /// finished. Its fill, a frame of enters at a time, is progress, and is not what the bound times.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-15")]
    public void AClientThatCannotDrainAFrameWithinTheCloseBoundIsClosedWith1013WithinFiveSeconds() =>
        AssertTheStalledClientIsClosed(stallClockStopsWhenGated: false);

    /// <summary>
    /// The same client with the stall clock stopped on the ticks the degrade gate refuses, as before #1040: its degraded run advances one tick in two, then
    /// one in four, and it is closed late. The test above must say so.
    /// </summary>
    [Test]
    [RuleMutant("SUB-15")]
    public void AStallClockStoppedByTheDegradeGateIsDetected() =>
        RuleMutants.AssertDetects("SUB-15", StalledLateMarker, () => AssertTheStalledClientIsClosed(stallClockStopsWhenGated: true));

    private const string StalledLateMarker = "closed within five seconds of the last frame it finished";

    /// <summary>
    /// The ticks a stalled metronome runs back to back advance a skip run no faster than time passes (SUB-15): sixty ticks 1 ms apart — twice the close
    /// bound in ticks, 60 ms of time — add at most one to a stalled client's run, where counted as ticks they would degrade it twice and close it.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-15")]
    public void ACatchUpAdvancesTheSkipRunNoFasterThanTime() => AssertCatchUpCountsTime(creditIgnored: false);

    /// <summary>Every catch-up tick counted, as before the policy clock: the test above must say so.</summary>
    [Test]
    [RuleMutant("SUB-15")]
    public void ASkipRunThatCountsCatchUpTicksIsDetected() =>
        RuleMutants.AssertDetects("SUB-15", CatchUpMarker, () => AssertCatchUpCountsTime(creditIgnored: true));

    private const string CatchUpMarker = "the catch-up's ticks advanced the skip run faster than time";

    private void AssertCatchUpCountsTime(bool creditIgnored)
    {
        using var harness = Create();
        harness.Assembler.SkipCreditIgnoredForTest = creditIgnored;
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));

        // A client that pings and never takes a frame: two frames in flight, then every tick is skipped. Five ticks on schedule start its run.
        long tick = 0;
        void Step()
        {
            tick++;
            MoveEveryCreature(harness, tick);
            harness.RunTick(tick);
            harness.Assembler.SendStateOf(session.Slot).NotePing(harness.Assembler.PolicyClockUs);
        }

        for (var i = 0; i < 5; i++)
        {
            Step();
        }

        var before = harness.Assembler.SendStateOf(session.Slot).SkipRun;
        var catchUp = 2 * harness.Assembler.CloseBoundTicks;
        harness.TickSpacingUs = 1_000;
        for (var i = 0; i < catchUp; i++)
        {
            Step();
        }

        var after = harness.Assembler.SendStateOf(session.Slot).SkipRun;
        Assert.Multiple(() =>
        {
            Assert.That(before, Is.GreaterThan(0), "the stalled client's run had started: the skip path is live in this fixture");
            Assert.That(after - before, Is.LessThanOrEqualTo(1), CatchUpMarker);
            Assert.That(harness.Assembler.SessionsDegraded, Is.Zero, "degraded by ticks that took 60 ms between them");
            Assert.That(harness.Sessions.Row(session).PendingClose, Is.Zero, "closed by ticks that took 60 ms between them");
        });
    }

    private void AssertTheStalledClientIsClosed(bool stallClockStopsWhenGated)
    {
        using var harness = Create(creatures: 4_000);
        harness.Assembler.StallClockStopsWhenGatedForTest = stallClockStopsWhenGated;
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var link = new SlowLink(harness, session, 10_000 * TickMs / 1000);

        var closedAt = -1L;
        for (long tick = 1; tick <= 100 && closedAt < 0; tick++)
        {
            MoveEveryCreature(harness, tick);
            harness.RunTick(tick);
            link.Carry(tick);
            if (harness.Sessions.Row(session).PendingClose != 0)
            {
                closedAt = tick;
            }
        }

        var code = (ushort)(harness.Sessions.Row(session).PendingClose & 0xFFFF);
        Assert.Multiple(() =>
        {
            Assert.That(link.LargestFrame, Is.GreaterThan(30_000), "a frame bigger than three seconds of the link");
            Assert.That(closedAt - link.LastFinishedTick, Is.InRange(1L, 5000L / TickMs), StalledLateMarker);
            Assert.That(harness.Assembler.LongestSkipRun, Is.EqualTo(harness.Assembler.CloseBoundTicks), "closed by the bound, at the bound");
            Assert.That(code, Is.EqualTo(CloseCodes.TryAgainLater), "1013: the SDK reconnects, with backoff");
            Assert.That(harness.Assembler.SessionsClosedLagging, Is.EqualTo(1), "closed by the skip rule");
            Assert.That(harness.Assembler.SessionsClosedSilent, Is.Zero, "not for silence: it pinged throughout");
            Assert.That(harness.Assembler.SessionsDegraded, Is.GreaterThan(0), "degraded before it was closed");
        });
    }

    /// <summary>
    /// A client of a 10 KB/s link facing the 56 KB/s god stream, whose frames it CAN drain: served every frame it finishes, each current, never closed — and
    /// once the world stops moving, the next frame it finishes holds the world as it is.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-15")]
    [VerifiesRule("SUB-03")]
    public void AClientDrainingTenKilobytesASecondIsServedAtTheRateItDrains()
    {
        using var harness = Create();
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var link = new SlowLink(harness, session, 10_000 * TickMs / 1000);

        long tick;
        for (tick = 1; tick <= 100; tick++)
        {
            MoveEveryCreature(harness, tick);
            harness.RunTick(tick);
            link.Carry(tick);
        }

        var delivered = link.Delivered;
        var skipped = harness.Assembler.FramesSkipped;

        // The world stops: the next frames are small, and the link catches up with the world as it is.
        for (var quiet = 0; quiet < 40; quiet++, tick++)
        {
            harness.RunTick(tick);
            link.Carry(tick);
        }

        var replica = harness.Replica(session);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Sessions.Row(session).PendingClose, Is.Zero, "a client that finishes its frames is not closed");
            Assert.That(delivered, Is.InRange(10, 60), "served at the rate it drains: fewer frames than ticks, more than none");
            Assert.That(skipped, Is.GreaterThan(0), "the ticks it could not take were skipped, not queued");
            Assert.That(harness.Assembler.LongestSkipRun, Is.LessThan(harness.Assembler.CloseBoundTicks));
            Assert.That(replica.Store.Anomalies, Is.Zero);
        });

        // Caught up: nothing left in flight, and the last frame it finished was produced after the world stopped (a still world produces none).
        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.SendStateOf(session.Slot).IsDrained, Is.True, "the link drained");
            Assert.That(replica.Store.Tick, Is.GreaterThan(100), "the client holds a frame of the still world");
            Assert.That(replica.NetIds(harness.PlanIndex(nameof(ProjCreature))), Has.Length.EqualTo(Creatures), "every creature held");
        });
    }

    /// <summary>
    /// The application is told every step of the degrade ladder: a session that stops draining is reported <see cref="SessionEventKind.Degraded"/> at
    /// level 1, then 2; once it drains again and has served <see cref="SkipPolicy.RecoveryFrames"/> clean frames at a level, it is reported back at 1, then 0.
    /// One event per step, never one per tick spent at a level.
    /// </summary>
    /// <remarks>
    /// The clean-frame count is STAGED rather than served — 200 frames a step, at one frame in four on level 2, is 1 200 ticks of a moving world for a
    /// precondition the sweep reads as one number. What the case claims is that the sweep's decision reaches the application, not how long recovery takes.
    /// </remarks>
    [Test]
    public void EveryStepOfTheDegradeLadderIsToldToTheApplication()
    {
        using var harness = Create(creatures: 32);
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var levels = new List<int>();
        var tick = 0L;

        void Step(Action<long> carry)
        {
            tick++;
            MoveEveryCreature(harness, tick);
            harness.RunTick(tick);
            carry?.Invoke(tick);
            var batch = harness.Sessions.Events.AsSpan();
            for (var i = 0; i < batch.Length; i++)
            {
                if (batch[i].Kind == SessionEventKind.Degraded && batch[i].Session == session)
                {
                    levels.Add(batch[i].DegradeLevel);
                }
            }
        }

        // Stalled: nothing is carried, the in-flight slots fill, the skip run climbs past the degrade bound twice. Stopped short of the close bound.
        while ((harness.Assembler.StateOf(session)?.DegradeLevel ?? 0) < SkipPolicy.MaxDegradeLevel && tick < harness.Assembler.CloseBoundTicks)
        {
            Step(null);
        }

        Step(null);
        var stalled = levels.ToArray();

        // Draining again, with all the bandwidth it needs; each level's clean-frame count staged to the threshold the sweep reads.
        var link = new SlowLink(harness, session, 64 * 1024 * 1024);

        // First until it is served again — its skip run back to zero. Staging recovery before that would let the sweep step it up while the run still
        // stands past the degrade bound, and the next sweep step it straight back down: a ladder that oscillates, which is the test's doing, not the engine's.
        for (var guard = 0; guard < 64 && harness.Assembler.SendStateOf(session.Slot).SkipRun > 0; guard++)
        {
            Step(link.Carry);
        }

        for (var guard = 0; guard < 64 && harness.Assembler.StateOf(session).DegradeLevel > 0; guard++)
        {
            harness.Assembler.StateOf(session).FramesSinceDegrade = SkipPolicy.RecoveryFrames;
            Step(link.Carry);
        }

        for (var i = 0; i < 4; i++)
        {
            Step(link.Carry);
        }

        Assert.Multiple(() =>
        {
            Assert.That(stalled, Is.EqualTo(new[] { 1, 2 }), "one event per step down, carrying the level reached");
            var close = harness.Sessions.Row(session).PendingClose & 0xFFFF;
            var run = harness.Assembler.SendStateOf(session.Slot).SkipRun;
            Assert.That(levels, Is.EqualTo(new[] { 1, 2, 1, 0 }),
                $"and one per step back, ending at 0 — full rate; levels {string.Join(",", levels)}, close {close}, run {run}");
            Assert.That(harness.Sessions.Row(session).PendingClose, Is.Zero, "degraded, never closed");
        });
    }

    /// <summary>
    /// A socket whose send buffer is full: it carries a fixed number of bytes a tick, one frame at a time, completed once its last byte is through, the next
    /// claimed only then. The client applies what arrives and pings every 200 ms with what it has applied.
    /// </summary>
    private sealed class SlowLink(FrameHarness harness, SessionId session, int bytesPerTick)
    {
        private FrameView _inFlight;
        private int _remaining;

        public int Delivered { get; private set; }

        public int LargestFrame { get; private set; }

        public long LastFinishedTick { get; private set; }

        public void Carry(long tick)
        {
            ref var send = ref harness.Assembler.SendStateOf(session.Slot);
            var replica = harness.Replica(session);
            var budget = bytesPerTick;
            while (budget > 0)
            {
                if (_remaining == 0)
                {
                    if (!send.TryClaimFrame(harness.Assembler.Gate.CommittedTick, out _inFlight))
                    {
                        break;
                    }

                    _remaining = _inFlight.Length;
                    LargestFrame = Math.Max(LargestFrame, _inFlight.Length);
                }

                var carried = Math.Min(budget, _remaining);
                _remaining -= carried;
                budget -= carried;
                if (_remaining == 0)
                {
                    replica.Apply(new ReadOnlySpan<byte>(_inFlight.Bytes, _inFlight.Length));
                    send.CompleteSend(_inFlight.Sequence);
                    Delivered++;
                    LastFinishedTick = tick;
                }
            }

            if (tick % 2 == 0)
            {
                send.NotePing(harness.Assembler.PolicyClockUs);
                send.ReportAppliedTick(replica.Store.Tick);
            }
        }
    }

    /// <summary>
    /// A client 100 ms away each way with all the bandwidth it needs: every frame leaves at once and arrives a tick later; its pings, at about 3 Hz, carry
    /// what it has applied and also arrive a tick later. Sixty seconds of god-rate traffic: never skipped, never degraded, never closed.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-15")]
    public void AClientTwoHundredMillisecondsAwayAtGodRatesIsNeverDegraded()
    {
        using var harness = Create();
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        ref var send = ref harness.Assembler.SendStateOf(session.Slot);
        var replica = harness.Replica(session);

        var inTransit = new Queue<(long Arrives, byte[] Frame)>();
        var pingsInTransit = new Queue<(long Arrives, long Applied)>();
        long bytes = 0;
        for (long tick = 1; tick <= 60_000 / TickMs; tick++)
        {
            MoveEveryCreature(harness, tick);
            harness.RunTick(tick);

            // The kernel takes every frame at once: the bandwidth is not the constraint here, the distance is.
            while (send.TryClaimFrame(harness.Assembler.Gate.CommittedTick, out var frame))
            {
                inTransit.Enqueue((tick + 1, new ReadOnlySpan<byte>(frame.Bytes, frame.Length).ToArray()));
                bytes += frame.Length;
                send.CompleteSend(frame.Sequence);
            }

            while (inTransit.Count > 0 && inTransit.Peek().Arrives <= tick)
            {
                replica.Apply(inTransit.Dequeue().Frame);
            }

            if (tick % 3 == 0)
            {
                pingsInTransit.Enqueue((tick + 1, replica.Store.Tick));
            }

            while (pingsInTransit.Count > 0 && pingsInTransit.Peek().Arrives <= tick)
            {
                var ping = pingsInTransit.Dequeue();
                send.NotePing(harness.Assembler.PolicyClockUs);
                send.ReportAppliedTick(ping.Applied);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(bytes / 60, Is.GreaterThan(10_000), "god rates: more than the slow client's 10 KB/s");
            Assert.That(harness.Assembler.FramesSkipped, Is.Zero, "never skipped for lag");
            Assert.That(harness.Assembler.SessionsDegraded, Is.Zero, "never degraded");
            Assert.That(harness.Sessions.Row(session).PendingClose, Is.Zero, "never closed");
            Assert.That(replica.Store.Anomalies, Is.Zero);
        });
    }
}
