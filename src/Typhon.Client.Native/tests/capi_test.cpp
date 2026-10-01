// The C ABI end to end over the in-memory transport: a session opens, the kitchen-sink stream applies, and the replica, the events, the
// motion and the commands are read and written through the C surface only.

#include <algorithm>
#include <cstring>

#include "capi/capi_internal.hpp"
#include "fake_transport.hpp"
#include "golden_support.hpp"
#include "test_framework.hpp"
#include "typhon/client.h"
#include "wire/commands.hpp"

using namespace typhon::test;

namespace {

struct Observed {
    std::vector<std::string> events;
    std::vector<double> pingFrom;
    std::vector<std::uint32_t> frames;
    int welcomes = 0;
    int resets = 0;
    int realms = 0;
    std::uint32_t realmId = 0;
};

struct CapiHarness {
    std::vector<std::shared_ptr<Link>> links;
    double now = 0;
    Observed seen;
    typhon_client* client = nullptr;

    CapiHarness()
    {
        typhon_client_config config{};
        config.struct_size = sizeof config;
        config.kind = "player";
        config.user = &seen;
        config.on_welcome = [](void* user, std::uint32_t, std::int32_t) { static_cast<Observed*>(user)->welcomes++; };
        config.on_reset = [](void* user) { static_cast<Observed*>(user)->resets++; };
        config.on_frame = [](void* user, std::uint32_t tick) { static_cast<Observed*>(user)->frames.push_back(tick); };
        config.on_realm_changed = [](void* user, const typhon_realm*, const typhon_realm* current)
        {
            auto* o = static_cast<Observed*>(user);
            o->realms++;
            o->realmId = current != nullptr ? current->realm_id : 0xFFFF;
        };
        config.on_event = [](void* user, const typhon_event* event)
        {
            auto* o = static_cast<Observed*>(user);
            o->events.emplace_back(typhon_event_type_name(event));
            std::uint32_t field = 0;
            if (std::strcmp(typhon_event_type_name(event), "Ping") == 0 && typhon_event_field_index(event, "from", &field) == TYPHON_OK)
            {
                const double* values = nullptr;
                std::size_t count = 0;
                if (typhon_event_numbers(event, field, &values, &count) == TYPHON_OK && count == 1)
                {
                    o->pingFrom.push_back(values[0]);
                }
            }
        };
        const auto status = typhon::client::capi::CreateWithTransport(
            &config,
            [this]
            {
                links.push_back(std::make_shared<Link>());
                return std::make_unique<FakeTransport>(links.back());
            },
            [this] { return now; }, &client);
        CHECK_EQ(static_cast<int>(status), static_cast<int>(TYPHON_OK));
    }

    ~CapiHarness() { typhon_client_destroy(client); }

    void Pump()
    {
        for (int i = 0; i < 256 && !links.back()->incoming.empty(); i++)
        {
            CHECK_EQ(static_cast<int>(typhon_client_poll(client, 0, nullptr)), static_cast<int>(TYPHON_OK));
        }
    }

    // Opens a session and applies the first `frames` messages of the kitchen-sink stream (the catalog the WELCOME carries). Its last
    // frame is a RESET that empties the archetypes, so the default stops before it.
    void OpenAndStream(std::size_t frames = 3)
    {
        CHECK_EQ(static_cast<int>(typhon_client_start(client)), static_cast<int>(TYPHON_OK));
        links.back()->Open();
        links.back()->Deliver(Welcome());
        const auto stream = GoldenBin("stream-kitchen-sink");
        for (std::size_t at = 0; at + 4 <= stream.size() && frames > 0; frames--)
        {
            std::uint32_t length;
            std::memcpy(&length, stream.data() + at, 4);
            links.back()->Deliver(
                Bytes(stream.begin() + static_cast<std::ptrdiff_t>(at + 4), stream.begin() + static_cast<std::ptrdiff_t>(at + 4 + length)));
            at += 4 + length;
        }

        Pump();
    }
};

}  // namespace

TEST(CApi_ReadsTheReplicaTheStreamLeft)
{
    CapiHarness h;
    h.OpenAndStream();
    CHECK_EQ(static_cast<int>(typhon_client_get_status(h.client)), static_cast<int>(TYPHON_CLIENT_OPEN));
    CHECK_EQ(h.seen.welcomes, 1);
    CHECK_EQ(h.seen.frames.size(), 3u);
    CHECK_EQ(typhon_client_tick(h.client), static_cast<std::int64_t>(h.seen.frames.back()));
    CHECK_EQ(typhon_client_anomalies(h.client), 0u);

    // The expectation the golden vector carries after the third frame: two drones with their text and bytes, read through the C surface.
    const Value vector = GoldenJson("stream-kitchen-sink");
    const Value* drones = vector.Find("snapshots")->Items()[2].Find("archetypes")->Find("Drone")->Find("entities");
    CHECK_EQ(drones->Items().size(), 2u);
    std::uint32_t drone = 0;
    CHECK_EQ(static_cast<int>(typhon_archetype_index(h.client, "Drone", &drone)), static_cast<int>(TYPHON_OK));
    typhon_archetype_view view{};
    CHECK_EQ(static_cast<int>(typhon_archetype_view_get(h.client, drone, &view)), static_cast<int>(TYPHON_OK));
    CHECK_EQ(static_cast<std::size_t>(view.live_count), drones->Items().size());
    CHECK_EQ(view.dims, 3);
    CHECK_EQ(view.motion_stride, 6);

    std::uint32_t label = 0;
    std::uint32_t serial = 0;
    std::uint32_t battery = 0;
    CHECK_EQ(static_cast<int>(typhon_field_index(h.client, drone, "label", &label)), static_cast<int>(TYPHON_OK));
    CHECK_EQ(static_cast<int>(typhon_field_index(h.client, drone, "serial", &serial)), static_cast<int>(TYPHON_OK));
    CHECK_EQ(static_cast<int>(typhon_field_index(h.client, drone, "battery", &battery)), static_cast<int>(TYPHON_OK));
    for (const Value& expected : drones->Items())
    {
        const auto netId = static_cast<std::uint32_t>(expected.Find("netId")->AsNumber());
        const std::uint32_t location = typhon_locate(h.client, netId);
        CHECK(location != TYPHON_NOT_FOUND);
        CHECK_EQ(location >> 24, drone);
        const std::uint32_t slot = location & 0xFFFFFF;
        CHECK_EQ(view.net_ids[slot], netId);

        const char* text = nullptr;
        std::size_t length = 0;
        CHECK_EQ(static_cast<int>(typhon_field_text(h.client, drone, label, slot, &text, &length)), static_cast<int>(TYPHON_OK));
        CHECK_EQ(Hex({reinterpret_cast<const std::uint8_t*>(text), length}), expected.Find("fields")->Find("label")->AsString());
        const std::uint8_t* bytes = nullptr;
        CHECK_EQ(static_cast<int>(typhon_field_bytes(h.client, drone, serial, slot, &bytes, &length)), static_cast<int>(TYPHON_OK));
        CHECK_EQ(Hex({bytes, length}), expected.Find("fields")->Find("serial")->AsString());

        typhon_column column{};
        CHECK_EQ(static_cast<int>(typhon_field_column(h.client, drone, battery, &column)), static_cast<int>(TYPHON_OK));
        CHECK(column.data != nullptr);
        CHECK_EQ(column.components, 1);
        CHECK_EQ(static_cast<int>(column.kind), static_cast<int>(TYPHON_FIELD_F64));
        const double value = static_cast<const double*>(column.data)[slot];
        CHECK_EQ(Bits(value), expected.Find("fields")->Find("battery")->Items()[0].AsString());
    }

    // Misuse is a status, never a crash.
    const char* text = nullptr;
    std::size_t length = 0;
    CHECK_EQ(static_cast<int>(typhon_field_text(h.client, drone, battery, 0, &text, &length)), static_cast<int>(TYPHON_ERROR_INVALID_ARGUMENT));
    CHECK_EQ(static_cast<int>(typhon_field_text(h.client, 200, label, 0, &text, &length)), static_cast<int>(TYPHON_ERROR_INVALID_ARGUMENT));
    CHECK(std::strlen(typhon_client_last_error(h.client)) > 0);

    // The realm the stream placed the session in.
    typhon_realm realm{};
    CHECK_EQ(static_cast<int>(typhon_client_realm(h.client, &realm)), static_cast<int>(TYPHON_OK));
    CHECK(h.seen.realms >= 1);
    CHECK_EQ(realm.realm_id, h.seen.realmId);
}

TEST(CApi_DeliversEventsThroughItsCallback)
{
    CapiHarness h;
    h.OpenAndStream(4);
    // Every event the golden snapshots list, in order, and the Ping's entityRef read by field index. The vector is held: a range-for
    // over a temporary's member would dangle.
    std::vector<std::string> expected;
    std::vector<double> from;
    const Value vector = GoldenJson("stream-kitchen-sink");
    for (const Value& snapshot : vector.Find("snapshots")->Items())
    {
        for (const Value& event : snapshot.Find("events")->Items())
        {
            expected.push_back(event.Find("type")->AsString());
            if (expected.back() == "Ping")
            {
                from.push_back(FromBits(event.Find("fields")->Find("from")->Items()[0].AsString()));
            }
        }
    }

    CHECK(!expected.empty());
    CHECK(h.seen.events == expected);
    CHECK(h.seen.pingFrom == from);
}

TEST(CApi_EvaluatesMotionAtTheClocksRenderTime)
{
    CapiHarness h;
    h.OpenAndStream();
    std::uint32_t drone = 0;
    typhon_archetype_index(h.client, "Drone", &drone);
    typhon_archetype_view view{};
    typhon_archetype_view_get(h.client, drone, &view);
    CHECK_EQ(view.live_count, 2u);
    CHECK_EQ(static_cast<int>(typhon_clock_update(h.client, 1000)), static_cast<int>(TYPHON_OK));
    std::int64_t tick = 0;
    double frac = 0;
    CHECK_EQ(static_cast<int>(typhon_clock_render_time(h.client, &tick, &frac)), static_cast<int>(TYPHON_OK));
    std::vector<double> all(view.live_count * 6u);
    CHECK_EQ(static_cast<int>(typhon_motion_evaluate_live(h.client, drone, all.data(), all.size())), static_cast<int>(TYPHON_OK));
    double one[TYPHON_MAX_MOTION_STRIDE];
    CHECK_EQ(static_cast<int>(typhon_motion_evaluate_slot(h.client, drone, view.live[0], one, 6)), static_cast<int>(TYPHON_OK));
    CHECK(std::equal(one, one + 6, all.begin()));
    // A buffer too small for every live entity is refused, not overrun.
    CHECK_EQ(static_cast<int>(typhon_motion_evaluate_live(h.client, drone, all.data(), all.size() - 1)),
             static_cast<int>(TYPHON_ERROR_OUT_OF_RANGE));
}

TEST(CApi_QueuesAndFlushesCommands)
{
    CapiHarness h;
    h.OpenAndStream();
    std::uint32_t steer = 0;
    CHECK_EQ(static_cast<int>(typhon_command_index(h.client, "Steer", &steer)), static_cast<int>(TYPHON_OK));
    const double heading = 0.25;
    const double boost = 1;
    const double speed = 0.5;
    const double stance = 1;
    const typhon_value values[] = {
        {"heading", &heading, 1, nullptr, 0, nullptr, 0}, {"boost", &boost, 1, nullptr, 0, nullptr, 0},
        {"speed", &speed, 1, nullptr, 0, nullptr, 0},     {"stance", &stance, 1, nullptr, 0, nullptr, 0},
        {"note", nullptr, 0, "go", 2, nullptr, 0},
    };
    std::int32_t seq = -1;
    CHECK_EQ(static_cast<int>(typhon_command_enqueue(h.client, steer, values, 5, &seq)), static_cast<int>(TYPHON_OK));
    CHECK_EQ(seq, 1);
    const std::size_t before = h.links.back()->sent.size();
    std::int32_t messages = 0;
    CHECK_EQ(static_cast<int>(typhon_commands_flush(h.client, &messages)), static_cast<int>(TYPHON_OK));
    CHECK_EQ(messages, 1);
    CHECK_EQ(h.links.back()->sent.size(), before + 1);
    CHECK_EQ(static_cast<int>(h.links.back()->sent.back()[0]), static_cast<int>(typhon::client::MessageType::Commands));

    // A value the codec cannot represent is a status, and nothing is queued.
    const double badStance = 3;
    typhon_value bad[5];
    std::copy(std::begin(values), std::end(values), bad);
    bad[3].numbers = &badStance;
    CHECK_EQ(static_cast<int>(typhon_command_enqueue(h.client, steer, bad, 5, &seq)), static_cast<int>(TYPHON_ERROR_OUT_OF_RANGE));
    CHECK_EQ(static_cast<int>(typhon_command_enqueue(h.client, 9999, values, 5, &seq)), static_cast<int>(TYPHON_ERROR_INVALID_ARGUMENT));

    CHECK_EQ(static_cast<int>(typhon_client_stop(h.client, 1000)), static_cast<int>(TYPHON_OK));
    CHECK_EQ(static_cast<int>(typhon_client_get_status(h.client)), static_cast<int>(TYPHON_CLIENT_STOPPED));
}
