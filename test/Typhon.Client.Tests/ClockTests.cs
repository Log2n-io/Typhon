using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Client.Tests;

/// <summary>
/// The render clock, case for case against the TypeScript SDK's <c>test/clock.test.ts</c> over the same arrivals: both SDKs must land on the same render tick,
/// or the differential oracle would compare two clients evaluating motion at two different times.
/// </summary>
[TestFixture]
public class ClockTests
{
    private const double Period = 100;

    /// <summary>Server time of a tick in the tests' constant-period world: tick 0 is sent at local time 5 000.</summary>
    private static double SentAt(long tick) => 5000 + (tick * Period);

    /// <summary>Before the first frame there is no tick → time map, so there is nothing to advance.</summary>
    [Test]
    public void NothingHappensBeforeTheFirstFrame()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period });
        clock.Update(1000);

        Assert.Multiple(() =>
        {
            Assert.That(clock.RenderTick, Is.Zero);
            Assert.That(clock.LatestTick, Is.EqualTo(-1));
        });
    }

    /// <summary>With a constant network delay the clock settles exactly one render delay behind the least-delayed frame.</summary>
    [Test]
    public void ItSettlesOneRenderDelayBehindTheLeastDelayedFrame()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period, InitialDelayMs = 200 });
        var next = 0L;
        var worstLead = 0.0;
        for (var now = 5000.0; now < 11_000; now += 1000.0 / 60)
        {
            while (SentAt(next) + 30 <= now)
            {
                clock.OnFrame(next, SentAt(next) + 30);
                next++;
            }

            clock.Update(now);
            // The delay drops from its initial 200 ms to 150 ms once jitter is measurable; ±5 % closes that in about 2.5 s.
            if (now > 8500)
            {
                // Constant 30 ms delay, no jitter: the delay settles at max(150, period + 0) = 150; target = now − 5 030 − 150.
                var targetTicks = (now - 5030 - clock.RenderDelayMs) / Period;
                worstLead = Math.Max(worstLead, Math.Abs(clock.RenderTime - targetTicks));
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(clock.RenderDelayMs, Is.EqualTo(150));
            Assert.That(worstLead * Period, Is.LessThan(1));
        });
    }

    /// <summary>Render time never runs backwards, however the arrivals jitter: a renderer that rewinds shows entities walking backwards.</summary>
    [Test]
    public void RenderTimeNeverGoesBackwardsUnderJitter()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period });
        var next = 0;
        var previous = double.NegativeInfinity;
        // The vitest twin's LCG, in doubles: its product exceeds 2⁵³, so JavaScript's rounding is part of the sequence.
        var seed = 7.0;
        double Random()
        {
            seed = ((seed * 1103515245) + 12345) % 2147483648.0;
            return seed / 2147483648.0;
        }

        var arrivals = new List<double>();
        var last = 0.0;
        for (var t = 0; t < 400; t++)
        {
            last = Math.Max(last, SentAt(t) + 20 + (Random() * 60));
            arrivals.Add(last);
        }

        Assert.Multiple(() =>
        {
            for (var now = 5000.0; now < 5000 + 38_000; now += 1000.0 / 60)
            {
                while (next < arrivals.Count && arrivals[next] <= now)
                {
                    clock.OnFrame(next, arrivals[next]);
                    next++;
                }

                clock.Update(now);
                if (clock.LatestTick >= 0)
                {
                    Assert.That(clock.RenderTime, Is.GreaterThanOrEqualTo(previous));
                    previous = clock.RenderTime;
                }
            }
        });
    }

    /// <summary>The delay follows a nearest-rank p95, so one late frame in twenty does not raise it.</summary>
    [Test]
    public void ASingleOutlierDoesNotSetTheDelay()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period, MinDelayMs = 100, MaxDelayMs = 300 });
        for (var t = 0; t < 20; t++)
        {
            clock.OnFrame(t, SentAt(t) + (t == 10 ? 160 : 10));
        }

        Assert.That(clock.RenderDelayMs, Is.EqualTo(Period));
    }

    /// <summary>Jitter beyond the ceiling is clamped: the delay a store's segment ring is sized for is a hard bound.</summary>
    [Test]
    public void TheDelayIsClampedIntoItsBounds()
    {
        var noisy = new Clock(new ClockOptions { TickPeriodMs = Period, MinDelayMs = 150, MaxDelayMs = 300 });
        for (var t = 0; t < 20; t++)
        {
            noisy.OnFrame(t, SentAt(t) + (t % 2 == 0 ? 900 : 0));
        }

        Assert.That(noisy.RenderDelayMs, Is.EqualTo(300));
    }

    /// <summary>The first frame after a stall looks late; render time holds until the target catches up rather than jumping back.</summary>
    [Test]
    public void ItHoldsInsteadOfJumpingBackAfterAStall()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period, InitialDelayMs = 200 });
        double now;
        for (var t = 0; t < 30; t++)
        {
            now = SentAt(t) + 10;
            clock.OnFrame(t, now);
            clock.Update(now);
        }

        var before = clock.RenderTime;
        // A 3 s stall: frames 30..59 all arrive at once, 20 ms apart, the first one alone in the window.
        now = SentAt(59) + 10;
        clock.OnFrame(30, now);
        clock.Update(now);

        Assert.Multiple(() =>
        {
            Assert.That(clock.RenderTime, Is.GreaterThanOrEqualTo(before));
            for (var t = 31; t < 60; t++)
            {
                now += 20;
                clock.OnFrame(t, now);
                clock.Update(now);
                Assert.That(clock.RenderTime, Is.GreaterThanOrEqualTo(before));
            }
        });
    }

    /// <summary>When frames stop arriving render time runs one extrapolation window past the newest, then holds.</summary>
    [Test]
    public void ItHoldsOneSecondPastTheNewestFrameWhenFramesStop()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period, InitialDelayMs = 200, MaxExtrapolationMs = 1000 });
        var now = 5000.0;
        for (var t = 0; t <= 4; t++)
        {
            now = SentAt(t);
            clock.OnFrame(t, now);
        }

        clock.Update(now);
        for (var k = 1; k <= 200; k++)
        {
            clock.Update(now + (k * 16));
        }

        Assert.Multiple(() =>
        {
            Assert.That(clock.RenderTick, Is.EqualTo(14));
            Assert.That(clock.RenderFrac, Is.Zero);
        });
    }

    /// <summary>A quiet view sends a frame every 500 ms; render time must still advance every rendered frame.</summary>
    [Test]
    public void KeepaliveOnlyTrafficDoesNotStutter()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period });
        var next = 0L;
        var frozenFrames = 0;
        var previous = -1.0;
        for (var now = 5000.0; now < 15_000; now += 1000.0 / 60)
        {
            // Only every fifth tick carries a frame (a header-only keepalive every 500 ms).
            while (SentAt(next) <= now)
            {
                clock.OnFrame(next, SentAt(next));
                next += 5;
            }

            clock.Update(now);
            if (now > 7000 && clock.RenderTime == previous)
            {
                frozenFrames++;
            }

            previous = clock.RenderTime;
        }

        Assert.That(frozenFrames, Is.Zero);
    }

    /// <summary>Time before a <c>PERIOD</c> change keeps the old period and time after it the new one, with no drift across the seam.</summary>
    [Test]
    public void APeriodChangeSplitsTheTimelineWithoutDrift()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period, InitialDelayMs = 200, MinDelayMs = 200, MaxDelayMs = 200 });
        // Ticks 0..49 at 100 ms, then ticks 50.. at 200 ms (time dilation). Server time of tick 50 is 5 000 ms.
        static double ServerAt(long tick) => tick <= 50 ? tick * 100 : 5000 + ((tick - 50) * 200);
        var next = 0L;
        var changed = false;
        var worst = 0.0;
        for (var now = 0.0; now < 20_000; now += 1000.0 / 60)
        {
            while (ServerAt(next) <= now)
            {
                if (next == 50 && !changed)
                {
                    clock.OnPeriodChange(50, 200);
                    changed = true;
                }

                clock.OnFrame(next, ServerAt(next));
                next++;
            }

            clock.Update(now);
            if (now > 1000)
            {
                var renderServerMs = now - 200;
                var expected = renderServerMs <= 5000 ? renderServerMs / 100 : 50 + ((renderServerMs - 5000) / 200);
                worst = Math.Max(worst, Math.Abs(clock.RenderTime - expected));
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(clock.TickPeriodMs, Is.EqualTo(200));
            Assert.That(worst, Is.LessThan(0.02));
        });
    }

    /// <summary>A reconnection forgets every frame, sample and period change; the next frame re-anchors the map.</summary>
    [Test]
    public void ResetForgetsEverything()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period, InitialDelayMs = 200 });
        for (var t = 0; t < 20; t++)
        {
            clock.OnFrame(t, SentAt(t) + (t % 2 == 0 ? 150 : 0));
        }

        clock.OnPeriodChange(20, 50);
        clock.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(clock.LatestTick, Is.EqualTo(-1));
            Assert.That(clock.RenderDelayMs, Is.EqualTo(200));
            Assert.That(clock.TickPeriodMs, Is.EqualTo(Period));
        });

        clock.OnFrame(100, 1000);
        clock.Update(1000);
        Assert.That(clock.RenderTime, Is.EqualTo(98).Within(1e-9));
    }

    /// <summary>The clock runs once per rendered frame: after construction it must not allocate.</summary>
    [Test]
    public void TheClockAllocatesNothingPerFrame()
    {
        var clock = new Clock(new ClockOptions { TickPeriodMs = Period });
        for (var t = 0; t < 40; t++)
        {
            clock.OnFrame(t, SentAt(t) + 12);
            clock.Update(SentAt(t) + 12);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 40; t < 240; t++)
        {
            clock.OnFrame(t, SentAt(t) + 12);
            clock.Update(SentAt(t) + 13);
        }

        clock.OnPeriodChange(240, 200);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
