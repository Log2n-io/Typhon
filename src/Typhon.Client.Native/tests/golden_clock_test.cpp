// clock-trace and interpolation: the cross-language pins of the clock, the round-trip estimator (05 § 1) and 03 § 6's interpolation of an
// angle and a quat3 (#1398). The .NET SDK generated both; this core replays them and must land on the same IEEE bits at every step.

#include <cstdint>
#include <string>

#include "clock/clock.hpp"
#include "golden_support.hpp"
#include "net/reconnect.hpp"
#include "test_framework.hpp"
#include "wire/math.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

std::int64_t IntOf(const Value& object, const char* name) { return static_cast<std::int64_t>(object.Find(name)->AsNumber()); }

double BitsOf(const Value& object, const char* name) { return FromBits(object.Find(name)->AsString()); }

}  // namespace

TEST(GoldenClockTrace_ReplaysEveryEventToTheSameClockAndRoundTrip)
{
    const Value vector = GoldenJson("clock-trace");
    ClockOptions options;
    options.tickPeriodMs = BitsOf(*vector.Find("options"), "tickPeriodMs");
    Clock clock(options);
    PingScheduler ping(4);
    int updates = 0;
    int index = 0;
    for (const Value& e : vector.Find("events")->Items())
    {
        const std::string op = e.Find("op")->AsString();
        const std::string at = "event " + std::to_string(index++) + " (" + op + ")";
        if (op == "frame")
        {
            clock.OnFrame(IntOf(e, "tick"), BitsOf(e, "recvMs"));
        }
        else if (op == "period")
        {
            clock.OnPeriodChange(IntOf(e, "tick"), BitsOf(e, "periodMs"));
        }
        else if (op == "reset")
        {
            clock.Reset();
        }
        else if (op == "update")
        {
            clock.Update(BitsOf(e, "nowMs"));
            updates++;
            const Value& expect = *e.Find("expect");
            CHECK_MSG(clock.RenderTick() == IntOf(expect, "renderTick"), at + " renderTick");
            CHECK_MSG(Bits(clock.RenderFrac()) == expect.Find("renderFrac")->AsString(), at + " renderFrac");
            CHECK_MSG(Bits(clock.OffsetMs()) == expect.Find("offsetMs")->AsString(), at + " offsetMs");
            CHECK_MSG(Bits(clock.RenderDelayMs()) == expect.Find("renderDelayMs")->AsString(), at + " renderDelayMs");
            CHECK_MSG(Bits(clock.TickPeriodMs()) == expect.Find("tickPeriodMs")->AsString(), at + " tickPeriodMs");
            CHECK_MSG(clock.LatestTick() == IntOf(expect, "latestTick"), at + " latestTick");
        }
        else if (op == "phase")
        {
            const double phase = clock.SubTickPhase(BitsOf(e, "atMs"), BitsOf(e, "oneWayDelayMs"));
            CHECK_MSG(Bits(phase) == e.Find("value")->AsString(), at + " phase");
        }
        else if (op == "pong")
        {
            PongMessage pong;
            pong.clientMs = static_cast<std::uint32_t>(IntOf(e, "clientMs"));
            ping.OnPong(pong, BitsOf(e, "recvMs"));
            const Value& expect = *e.Find("expect");
            CHECK_MSG(Bits(ping.RttMs()) == expect.Find("rttMs")->AsString(), at + " rttMs");
            CHECK_MSG(Bits(ping.LastRttMs()) == expect.Find("lastRttMs")->AsString(), at + " lastRttMs");
        }
        else
        {
            CHECK_MSG(false, at + ": an op this replay does not know");
        }
    }

    CHECK(updates > 100);
}

TEST(GoldenInterpolation_EveryCaseLandsOnTheSameBits)
{
    const Value vector = GoldenJson("interpolation");
    int angles = 0;
    for (const Value& c : vector.Find("angle")->Items())
    {
        const double value =
            math::InterpolateAngle(BitsOf(c, "from"), BitsOf(c, "to"), BitsOf(c, "t"), static_cast<int>(c.Find("bits")->AsNumber()));
        CHECK_MSG(Bits(value) == c.Find("value")->AsString(), "angle case " + std::to_string(angles));
        angles++;
    }

    int quats = 0;
    for (const Value& c : vector.Find("quat3")->Items())
    {
        double from[4];
        double to[4];
        for (int k = 0; k < 4; k++)
        {
            from[k] = FromBits(c.Find("from")->Items()[k].AsString());
            to[k] = FromBits(c.Find("to")->Items()[k].AsString());
        }

        double out[4];
        math::InterpolateQuat3(from, to, BitsOf(c, "t"), out);
        for (int k = 0; k < 4; k++)
        {
            CHECK_MSG(Bits(out[k]) == c.Find("value")->Items()[k].AsString(), "quat3 case " + std::to_string(quats));
        }

        // Aliased: written over `from`.
        math::InterpolateQuat3(from, to, BitsOf(c, "t"), from);
        for (int k = 0; k < 4; k++)
        {
            CHECK_MSG(Bits(from[k]) == c.Find("value")->Items()[k].AsString(), "quat3 case " + std::to_string(quats) + " aliased");
        }

        quats++;
    }

    CHECK(angles > 0);
    CHECK(quats > 0);
}
