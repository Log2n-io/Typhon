using System;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Typhon.Client.Tests;

/// <summary>
/// The cross-language pin of the clock (#1398, 05-sdks § 1): one committed arrival trace — frames, period changes, a stall, a reset, render updates, phase
/// queries and <c>PONG</c>s — and what each SDK's clock and round-trip estimator must report after every step, as IEEE bits.
/// </summary>
/// <remarks>
/// <para>
/// The three clocks are ports of one another, arithmetic for arithmetic, and until this vector nothing bound them: a change to one SDK's percentile, its
/// rate correction or its piece walk failed no other SDK's tests. The trace is generated here, from the .NET clock; the TypeScript and native SDKs replay
/// it and must land on the same bits. Inputs are bits too, so no SDK parses a decimal.
/// </para>
/// <para>
/// What it walks through: a steady 20 Hz stream with arrival jitter, rendered at 60 Hz; a 1.5 s stall, with render time running into the extrapolation
/// limit, then a burst of late frames; an overload dilation to 75 ms ticks and back (<c>PERIOD</c>); a reset and a re-anchor; phase queries with and
/// without a one-way delay; and <c>PONG</c>s, one of whose stamps wraps at 2³² ms.
/// </para>
/// </remarks>
[TestFixture]
public class ClockTraceGoldenTests
{
    private const string VectorName = "clock-trace";

    private const string Description =
        "The clock and round-trip estimator pinned across the SDKs (#1398): `options` builds the clock, then `events` are applied in order — `frame` "
        + "(onFrame), `period` (onPeriodChange), `reset`, `update` (update(nowMs), then the clock's state must equal `expect`), `phase` (subTickPhase(atMs, "
        + "oneWayDelayMs) must equal `value`) and `pong` (the PingScheduler's onPong with clientMs and recvMs, after which rttMs and lastRttMs must equal "
        + "`expect`). Every double is its IEEE bits as 16 hex digits; ticks are plain integers. Generated from the .NET Clock and RoundTripEstimator.";

    private const double TickPeriodMs = 50;

    [Test]
    public void TheClockTraceMatchesTheCommittedVector()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = TickPeriodMs });
        var roundTrip = new RoundTripEstimator();
        var events = new JsonArray();
        var random = new Random(1398);
        var frameSpacing = TickPeriodMs;
        long tick = 1000;
        var nextFrameMs = 10_000d;
        var nextPongMs = 10_100d;
        var now = 9_990d;

        void Frame(long t, double recvMs)
        {
            clock.OnFrame(t, recvMs);
            events.Add(new JsonObject { ["op"] = "frame", ["tick"] = t, ["recvMs"] = GoldenFiles.Bits(recvMs) });
        }

        void Update(double nowMs)
        {
            clock.Update(nowMs);
            events.Add(new JsonObject
            {
                ["op"] = "update",
                ["nowMs"] = GoldenFiles.Bits(nowMs),
                ["expect"] = new JsonObject
                {
                    ["renderTick"] = clock.RenderTick,
                    ["renderFrac"] = GoldenFiles.Bits(clock.RenderFrac),
                    ["offsetMs"] = GoldenFiles.Bits(clock.OffsetMs),
                    ["renderDelayMs"] = GoldenFiles.Bits(clock.RenderDelayMs),
                    ["tickPeriodMs"] = GoldenFiles.Bits(clock.TickPeriodMs),
                    ["latestTick"] = clock.LatestTick,
                },
            });
        }

        void Phase(double atMs, double oneWayDelayMs) =>
            events.Add(new JsonObject
            {
                ["op"] = "phase",
                ["atMs"] = GoldenFiles.Bits(atMs),
                ["oneWayDelayMs"] = GoldenFiles.Bits(oneWayDelayMs),
                ["value"] = GoldenFiles.Bits(clock.SubTickPhase(atMs, oneWayDelayMs)),
            });

        void Pong(double sentMs, double recvMs)
        {
            var clientMs = RoundTripEstimator.Stamp(sentMs);
            roundTrip.OnPong(clientMs, recvMs);
            events.Add(new JsonObject
            {
                ["op"] = "pong",
                ["clientMs"] = clientMs,
                ["recvMs"] = GoldenFiles.Bits(recvMs),
                ["expect"] = new JsonObject { ["rttMs"] = GoldenFiles.Bits(roundTrip.RttMs), ["lastRttMs"] = GoldenFiles.Bits(roundTrip.LastRttMs) },
            });
        }

        // Runs the stream until `untilMs`, rendering at 60 Hz: frames sent every `frameSpacing`, received 20 ms later plus up to 30 of jitter and never
        // before the frame ahead of them (one TCP stream), unless `stalled`; PONGs every 250 ms with 20–60 ms round trips; a phase query every 40th
        // render. An arrival is applied at the first render at or after it, as a client's loop would.
        var renders = 0;
        var lastRecv = 0d;
        var pendingFrame = double.NaN;
        var pendingPong = double.NaN;
        void Run(double untilMs, bool stalled = false)
        {
            while (now < untilMs)
            {
                now += 1000d / 60d;
                while (!stalled)
                {
                    if (double.IsNaN(pendingFrame))
                    {
                        pendingFrame = Math.Max(lastRecv, nextFrameMs + 20 + (random.NextDouble() * 30));
                    }

                    if (pendingFrame > now)
                    {
                        break;
                    }

                    Frame(tick++, pendingFrame);
                    lastRecv = pendingFrame;
                    nextFrameMs += frameSpacing;
                    pendingFrame = double.NaN;
                }

                while (true)
                {
                    if (double.IsNaN(pendingPong))
                    {
                        pendingPong = nextPongMs + 20 + (random.NextDouble() * 40);
                    }

                    if (pendingPong > now)
                    {
                        break;
                    }

                    Pong(nextPongMs, pendingPong);
                    nextPongMs += 250;
                    pendingPong = double.NaN;
                }

                Update(now);
                if (++renders % 40 == 0)
                {
                    Phase(now, 0);
                    Phase(now, roundTrip.RttMs / 2);
                }
            }
        }

        // Before any frame: an update does nothing, and the phase is NaN.
        Update(now);
        Phase(now, 0);

        // Loopback round trips of 0 ms are measurements: the first seeds the smoothed value at 0, and the next is blended in at 1/8 rather than taken
        // whole — the seed is the first sample, not "the smoothed value is still zero".
        Pong(now - 3, now - 3);
        Pong(now - 2, now - 2);
        Pong(now - 2, now);

        Run(12_000);

        // A stall of 1.5 s: render time runs into the extrapolation limit and holds there. Then the frames that queued arrive in a burst.
        var stallEnd = now + 1500;
        Run(stallEnd, stalled: true);
        for (var i = 0; i < 30; i++)
        {
            lastRecv = now + (i * 0.5);
            Frame(tick++, lastRecv);
        }

        pendingFrame = double.NaN;
        nextFrameMs = now;
        Run(now + 1500);

        // Overload: ticks stretched to 75 ms, then back to 50 — PERIOD in frame N+1 describes [N, N+1], applied as onPeriodChange(N, ms).
        clock.OnPeriodChange(tick - 1, 75);
        events.Add(new JsonObject { ["op"] = "period", ["tick"] = tick - 1, ["periodMs"] = GoldenFiles.Bits(75) });
        frameSpacing = 75;
        Run(now + 2000);
        clock.OnPeriodChange(tick - 1, 50);
        events.Add(new JsonObject { ["op"] = "period", ["tick"] = tick - 1, ["periodMs"] = GoldenFiles.Bits(50) });
        frameSpacing = 50;
        Run(now + 1500);

        // A reconnection: everything forgotten; the next frame re-anchors, at a tick far from the last, and the next update snaps.
        clock.Reset();
        events.Add(new JsonObject { ["op"] = "reset" });
        tick = 500_000;
        pendingFrame = double.NaN;
        nextFrameMs = now + 30;
        Run(now + 1500);

        // A PONG whose stamp wraps at 2³² ms: the round trip is still the difference.
        Pong(4294967295.5, 4294967295.5 + 31.25);

        // A stamp from the future — a clock that stepped back: the unsigned difference wraps, identically in every SDK.
        Pong(now + 5, now);

        var expectation = new JsonObject
        {
            ["description"] = Description,
            ["options"] = new JsonObject { ["tickPeriodMs"] = GoldenFiles.Bits(TickPeriodMs) },
            ["events"] = events,
        };

        GoldenFiles.Assert(VectorName, [], expectation);
    }
}
