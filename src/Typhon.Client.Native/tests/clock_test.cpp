// The render clock — the TypeScript SDK's clock.test.ts, case for case.

#include <algorithm>
#include <cmath>
#include <limits>

#include "clock/clock.hpp"
#include "test_framework.hpp"

using namespace typhon::client;

namespace {

constexpr double Period = 100;

// Server time of a tick in the tests' constant-period world: tick 0 is sent at local time 5 000.
double SentAt(std::int64_t tick) { return 5000 + static_cast<double>(tick) * Period; }

double RenderTime(const Clock& clock) { return static_cast<double>(clock.RenderTick()) + clock.RenderFrac(); }

ClockOptions Options(double initialDelay = 200, double minDelay = 150, double maxDelay = 300)
{
    ClockOptions o;
    o.tickPeriodMs = Period;
    o.initialDelayMs = initialDelay;
    o.minDelayMs = minDelay;
    o.maxDelayMs = maxDelay;
    return o;
}

}  // namespace

TEST(Clock_DoesNothingBeforeTheFirstFrame)
{
    Clock clock(Options());
    clock.Update(1000);
    CHECK_EQ(clock.RenderTick(), 0);
    CHECK_EQ(clock.LatestTick(), -1);
}

TEST(Clock_SettlesOneRenderDelayBehindTheLeastDelayedFrame)
{
    Clock clock(Options());
    std::int64_t next = 0;
    double worstLead = 0;
    for (double now = 5000; now < 11000; now += 1000.0 / 60)
    {
        while (SentAt(next) + 30 <= now)
        {
            clock.OnFrame(next, SentAt(next) + 30);
            next++;
        }

        clock.Update(now);
        if (now > 8500)
        {
            const double targetTicks = (now - 5030 - clock.RenderDelayMs()) / Period;
            worstLead = std::max(worstLead, std::abs(RenderTime(clock) - targetTicks));
        }
    }

    CHECK_EQ(clock.RenderDelayMs(), 150.0);
    CHECK(worstLead * Period < 1);
}

TEST(Clock_NeverGoesBackwardsUnderJitter)
{
    Clock clock(Options());
    std::uint64_t seed = 7;
    const auto random = [&]
    {
        seed = (seed * 1103515245 + 12345) % 2147483648;
        return static_cast<double>(seed) / 2147483648;
    };

    std::vector<double> arrivals;
    double last = 0;
    for (int t = 0; t < 400; t++)
    {
        last = std::max(last, SentAt(t) + 20 + random() * 60);
        arrivals.push_back(last);
    }

    std::size_t next = 0;
    double previous = -std::numeric_limits<double>::infinity();
    for (double now = 5000; now < 43000; now += 1000.0 / 60)
    {
        while (next < arrivals.size() && arrivals[next] <= now)
        {
            clock.OnFrame(static_cast<std::int64_t>(next), arrivals[next]);
            next++;
        }

        clock.Update(now);
        if (clock.LatestTick() >= 0)
        {
            CHECK(RenderTime(clock) >= previous);
            previous = RenderTime(clock);
        }
    }
}

TEST(Clock_TakesTheNearestRankP95)
{
    Clock clock(Options(200, 100, 300));
    for (int t = 0; t < 20; t++)
    {
        clock.OnFrame(t, SentAt(t) + (t == 10 ? 160 : 10));
    }

    CHECK_EQ(clock.RenderDelayMs(), Period);
}

TEST(Clock_ClampsTheDelayIntoItsBounds)
{
    Clock clock(Options());
    for (int t = 0; t < 20; t++)
    {
        clock.OnFrame(t, SentAt(t) + (t % 2 == 0 ? 900 : 0));
    }

    CHECK_EQ(clock.RenderDelayMs(), 300.0);
}

TEST(Clock_HoldsInsteadOfJumpingBackAfterAStall)
{
    Clock clock(Options());
    double now = 0;
    for (int t = 0; t < 30; t++)
    {
        now = SentAt(t) + 10;
        clock.OnFrame(t, now);
        clock.Update(now);
    }

    const double before = RenderTime(clock);
    now = SentAt(59) + 10;
    clock.OnFrame(30, now);
    clock.Update(now);
    CHECK(RenderTime(clock) >= before);
    for (int t = 31; t < 60; t++)
    {
        now += 20;
        clock.OnFrame(t, now);
        clock.Update(now);
        CHECK(RenderTime(clock) >= before);
    }
}

TEST(Clock_HoldsOneSecondPastTheNewestFrame)
{
    Clock clock(Options());
    double now = 5000;
    for (int t = 0; t <= 4; t++)
    {
        now = SentAt(t);
        clock.OnFrame(t, now);
    }

    clock.Update(now);
    for (int k = 1; k <= 200; k++)
    {
        clock.Update(now + k * 16);
    }

    CHECK_EQ(clock.RenderTick(), 14);
    CHECK_EQ(clock.RenderFrac(), 0.0);
}

TEST(Clock_DoesNotStutterOnKeepaliveOnlyTraffic)
{
    Clock clock(Options());
    std::int64_t next = 0;
    int frozen = 0;
    double previous = -1;
    for (double now = 5000; now < 15000; now += 1000.0 / 60)
    {
        while (SentAt(next) <= now)
        {
            clock.OnFrame(next, SentAt(next));
            next += 5;
        }

        clock.Update(now);
        if (now > 7000 && RenderTime(clock) == previous)
        {
            frozen++;
        }

        previous = RenderTime(clock);
    }

    CHECK_EQ(frozen, 0);
}

TEST(Clock_KeepsTheOldPeriodBeforeAChangeAndTheNewOneAfter)
{
    Clock clock(Options(200, 200, 200));
    const auto serverAt = [](std::int64_t tick) { return tick <= 50 ? tick * 100.0 : 5000 + (tick - 50) * 200.0; };
    std::int64_t next = 0;
    bool changed = false;
    double worst = 0;
    for (double now = 0; now < 20000; now += 1000.0 / 60)
    {
        while (serverAt(next) <= now)
        {
            if (next == 50 && !changed)
            {
                clock.OnPeriodChange(50, 200);
                changed = true;
            }

            clock.OnFrame(next, serverAt(next));
            next++;
        }

        clock.Update(now);
        if (now > 1000)
        {
            const double renderServerMs = now - 200;
            const double expected = renderServerMs <= 5000 ? renderServerMs / 100 : 50 + (renderServerMs - 5000) / 200;
            worst = std::max(worst, std::abs(RenderTime(clock) - expected));
        }
    }

    CHECK_EQ(clock.TickPeriodMs(), 200.0);
    CHECK(worst < 0.02);
}

TEST(Clock_ForgetsEverythingOnReset)
{
    Clock clock(Options());
    for (int t = 0; t < 20; t++)
    {
        clock.OnFrame(t, SentAt(t) + (t % 2 == 0 ? 150 : 0));
    }

    clock.OnPeriodChange(20, 50);
    clock.Reset();
    CHECK_EQ(clock.LatestTick(), -1);
    CHECK_EQ(clock.RenderDelayMs(), 200.0);
    CHECK_EQ(clock.TickPeriodMs(), Period);
    clock.OnFrame(100, 1000);
    clock.Update(1000);
    CHECK(std::abs(RenderTime(clock) - 98) < 1e-9);
}
