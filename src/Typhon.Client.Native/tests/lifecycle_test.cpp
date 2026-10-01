// What the review of I-2 found, each as a test that fails on the defect: a client stopped, restarted or destroyed from its own callbacks; a
// resume token kept through a failed attempt; connect and catalog-skip refusals; the command batch at the count varint's widening and under a
// send that throws; a clock fed a time that is not finite; arena bytes freed by a release; and the C ABI's re-entry rules. Run under
// ASan/UBSan on the gate's Clang leg, where a use-after-free here is a failure rather than luck.

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

#include "capi/capi_internal.hpp"
#include "client/client.hpp"
#include "fake_transport.hpp"
#include "golden_support.hpp"
#include "store/archetype_store.hpp"
#include "test_framework.hpp"
#include "typhon/client.h"
#include "wire/commands.hpp"
#include "wire/realm_frame.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

std::vector<Bytes> StreamFrames(const char* name)
{
    const auto stream = GoldenBin(name);
    std::vector<Bytes> frames;
    for (std::size_t at = 0; at + 4 <= stream.size();)
    {
        std::uint32_t length;
        std::memcpy(&length, stream.data() + at, 4);
        frames.emplace_back(stream.begin() + static_cast<std::ptrdiff_t>(at + 4), stream.begin() + static_cast<std::ptrdiff_t>(at + 4 + length));
        at += 4 + length;
    }

    return frames;
}

// A client over in-memory transports whose callbacks can reach the client itself.
struct Fixture {
    std::vector<std::shared_ptr<Link>> links;
    double now = 0;
    std::vector<ConnectionClose> closes;
    std::unique_ptr<Client> client;

    explicit Fixture(const std::function<void(ClientOptions&, Fixture&)>& configure = {})
    {
        ClientOptions options;
        options.kind = "player";
        options.transport = [this]
        {
            links.push_back(std::make_shared<Link>());
            return std::make_unique<FakeTransport>(links.back());
        };
        options.now = [this] { return now; };
        options.random = [] { return 0.0; };
        options.onClose = [this](const ConnectionClose& c) { closes.push_back(c); };
        if (configure)
        {
            configure(options, *this);
        }

        client = std::make_unique<Client>(std::move(options));
    }

    Link& Last() { return *links.back(); }

    void Pump()
    {
        for (int i = 0; i < 64 && !links.empty() && !Last().incoming.empty(); i++)
        {
            client->Poll(0);
        }
    }

    void OpenWithKitchenSink(std::size_t frames)
    {
        client->Start();
        Last().Open();
        Last().Deliver(Welcome());
        const auto stream = StreamFrames("stream-kitchen-sink");
        for (std::size_t i = 0; i < frames && i < stream.size(); i++)
        {
            Last().Deliver(stream[i]);
        }

        Pump();
    }
};

}  // namespace

TEST(Lifecycle_StopFromOnFrameEndsTheSessionSafely)
{
    Fixture f([](ClientOptions& o, Fixture& self) { o.onFrame = [&self] { self.client->Stop(); }; });
    f.OpenWithKitchenSink(4);
    CHECK(f.client->Status() == ClientStatus::Stopped);
    CHECK_EQ(f.client->Applier()->World().frames, 1u);
    CHECK_EQ(ParseBye(f.Last().sent.back()), static_cast<std::uint32_t>(CloseCode::Normal));
    // The retired connection is released by the next poll, not under the callback that closed it.
    f.client->Poll(0);
    CHECK(f.client->Status() == ClientStatus::Stopped);
}

TEST(Lifecycle_StopFromOnEventEndsTheSessionSafely)
{
    int events = 0;
    Fixture f(
        [&events](ClientOptions& o, Fixture& self)
        {
            o.onEvent = [&events, &self](const EventRecord&)
            {
                events++;
                self.client->Stop();
            };
        });
    f.OpenWithKitchenSink(4);
    CHECK(events >= 1);
    CHECK(f.client->Status() == ClientStatus::Stopped);
}

TEST(Lifecycle_AStopInTheRecorderHookAppliesNothingMore)
{
    int ticks = 0;
    Fixture f(
        [&ticks](ClientOptions& o, Fixture& self)
        {
            o.onMessage = [&ticks, &self](std::span<const std::uint8_t> m, double)
            {
                if (m[0] == MessageType::Tick && ++ticks == 2)
                {
                    self.client->Stop();
                }
            };
        });
    f.OpenWithKitchenSink(4);
    CHECK_EQ(f.client->Applier()->World().frames, 1u);
}

TEST(Lifecycle_AStartInsideOnCloseKeepsTheNewConnection)
{
    bool restarted = false;
    Fixture f(
        [&restarted](ClientOptions& o, Fixture& self)
        {
            o.onClose = [&restarted, &self](const ConnectionClose&)
            {
                if (!restarted)
                {
                    restarted = true;
                    self.client->Start();
                }
            };
        });
    f.OpenWithKitchenSink(1);
    // Stop closes the session, and the close callback starts a new connection: the stop must not discard it, nor the rule replace it.
    f.client->Stop();
    CHECK(restarted);
    CHECK_EQ(f.links.size(), 2u);
    CHECK(f.client->Status() == ClientStatus::Connecting);
    f.Last().Open();
    f.Last().Deliver(Welcome(WelcomeParts().SkipCatalog()));
    f.Pump();
    CHECK(f.client->Status() == ClientStatus::Open);
}

TEST(Lifecycle_StopStartStopInOneCallbackLeavesNothingDangling)
{
    Fixture f(
        [](ClientOptions& o, Fixture& self)
        {
            o.onFrame = [&self]
            {
                self.client->Stop();
                self.client->Start();
                self.client->Stop();
            };
        });
    f.OpenWithKitchenSink(2);
    CHECK(f.client->Status() == ClientStatus::Stopped);
    CHECK_EQ(f.links.size(), 2u);
    f.client->Poll(0);
}

TEST(Lifecycle_AFailedAttemptKeepsTheResumeTokenWithinItsGrace)
{
    Fixture f;
    f.client->Start();
    f.Last().Open();
    f.Last().Deliver(Welcome(WelcomeParts().Resume(TokenOf(3))));
    f.Pump();
    f.Last().ServerClose(CloseCode::GoingAway);
    f.Pump();
    // Attempt 1 fails before the transport opens: no session, no token in its close.
    f.now = 500;
    f.client->Poll(0);
    f.Last().ServerClose(CloseCode::GoingAway);
    f.Pump();
    CHECK(!f.closes.back().resumeToken.has_value());
    // Attempt 2, still within the 60 s grace, presents the first session's token.
    f.now = 2000;
    f.client->Poll(0);
    f.Last().Open();
    f.Pump();
    CHECK(ParseHello(f.Last().sent[0]).resumeToken == TokenOf(3));
}

TEST(Lifecycle_AConnectThatNeverOpensTimesOut)
{
    auto link = std::make_shared<Link>();
    double now = 0;
    struct Closes final : ConnectionListener {
        std::vector<ConnectionClose> all;
        void OnClose(const ConnectionClose& c) override { all.push_back(c); }
    } listener;
    ConnectionOptions options;
    Connection connection(std::make_unique<FakeTransport>(link), options, listener, [&now] { return now; });
    connection.Connect();
    now = 4999;
    connection.Poll(0);
    CHECK(listener.all.empty());
    now = 5000;
    connection.Poll(0);
    CHECK_EQ(listener.all.size(), 1u);
    CHECK_EQ(listener.all[0].code, CloseCode::GoingAway);
    CHECK(listener.all[0].local);
    CHECK(ReconnectRuleOf(listener.all[0].code) == ReconnectRule::Reconnect);
}

TEST(Lifecycle_ASkipOfAnotherCatalogIsRefused)
{
    Fixture f;
    f.OpenWithKitchenSink(0);
    f.Last().ServerClose(CloseCode::GoingAway);
    f.Pump();
    f.now = 500;
    f.client->Poll(0);
    f.Last().Open();
    f.Last().Deliver(Welcome(WelcomeParts().SkipCatalog().WithHash({9, 9, 9, 9, 9, 9, 9, 9})));
    f.Pump();
    CHECK_EQ(f.closes.back().code, CloseCode::ProtocolError);
}

TEST(Lifecycle_PollFromItsOwnCallbackIsRefused)
{
    bool refused = false;
    Fixture f(
        [&refused](ClientOptions& o, Fixture& self)
        {
            o.onFrame = [&refused, &self]
            {
                try
                {
                    self.client->Poll(0);
                }
                catch (const std::logic_error&)
                {
                    refused = true;
                }
            };
        });
    f.OpenWithKitchenSink(1);
    CHECK(refused);
    CHECK(f.client->Status() == ClientStatus::Open);
}

namespace {

std::shared_ptr<const RealmFrame> Swg()
{
    const double min[3] = {-8192, -8192, 0};
    const double max[3] = {8192, 8192, 256};
    return std::make_shared<const RealmFrame>(0, 0, 0, 0, 24, 256.0, false, min, max);
}

struct Steer {
    double heading = 0.25;
    double one = 1;
    double half = 0.5;
    NamedValue values[5];

    Steer()
        : values{{"heading", FieldValue::OfNumbers({&heading, 1})},
                 {"boost", FieldValue::OfNumbers({&one, 1})},
                 {"speed", FieldValue::OfNumbers({&half, 1})},
                 {"stance", FieldValue::OfNumbers({&one, 1})},
                 {"note", FieldValue::OfText("go")}}
    {
    }
};

}  // namespace

TEST(Batching_NeverExceedsTheCapWhereTheCountVarintWidens)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    const auto frame = Swg();
    const MessagePlan& steer = *plan->CommandByName("Steer");
    Steer values;

    // One command's encoded size, from a one-command message: type, clientTick and a 1-byte count are 6 bytes.
    CommandQueue probe(plan, [] { return 0.0; });
    probe.Enqueue(steer, values.values);
    std::size_t single = 0;
    probe.Flush(1, [&single](std::span<const std::uint8_t> m) { single = m.size(); }, frame.get());
    const std::size_t perCommand = single - 6;

    // A cap that fits exactly 128 commands under a 1-byte count — which 128 commands do not have: their count takes 2 bytes.
    const auto cap = static_cast<std::uint32_t>(6 + 128 * perCommand);
    CommandQueue queue(plan, [] { return 0.0; }, cap);
    for (int i = 0; i < 300; i++)
    {
        queue.Enqueue(steer, values.values);
    }

    std::vector<std::size_t> sizes;
    std::size_t decoded = 0;
    queue.Flush(
        2,
        [&](std::span<const std::uint8_t> m)
        {
            sizes.push_back(m.size());
            struct Counter final : CommandSink {
                std::size_t n = 0;
                void Command(const MessagePlan&, std::uint32_t, std::uint32_t) override { n++; }
                void Number(const FieldPlan&, const double*) override {}
                void Integer64(const FieldPlan&, const std::uint64_t*) override {}
                void Text(const FieldPlan&, std::string_view) override {}
                void Bytes(const FieldPlan&, std::span<const std::uint8_t>) override {}
                void List(const FieldPlan&, int, const double*) override {}
            } counter;
            ReadCommands(m, *plan, counter, frame.get());
            decoded += counter.n;
        },
        frame.get());
    for (const std::size_t size : sizes)
    {
        CHECK_MSG(size <= cap, "a message of " << size << " B under a " << cap << " B cap");
    }

    CHECK_EQ(decoded, 300u);
}

TEST(Batching_ASendThatThrowsDropsTheBatchInsteadOfResendingIt)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    const auto frame = Swg();
    Steer values;
    CommandQueue queue(plan, [] { return 0.0; });
    for (int i = 0; i < 200; i++)
    {
        queue.Enqueue(*plan->CommandByName("Steer"), values.values);
    }

    int sent = 0;
    CHECK_THROWS(std::length_error, queue.Flush(
                                        1,
                                        [&sent](std::span<const std::uint8_t>)
                                        {
                                            if (++sent == 2)
                                            {
                                                throw std::length_error("the connection refused the second message");
                                            }
                                        },
                                        frame.get()));
    CHECK_EQ(queue.PendingCount(), 0u);
    CHECK_EQ(queue.Flush(2, [](std::span<const std::uint8_t>) {}, frame.get()), 0);
}

TEST(Batching_AnUnknownFieldIsAnInvalidArgument)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    CommandQueue queue(plan, [] { return 0.0; });
    const double v = 1;
    const NamedValue values[] = {{"nope", FieldValue::OfNumbers({&v, 1})}};
    CHECK_THROWS(std::invalid_argument, queue.Enqueue(*plan->CommandByName("Steer"), values));
    CHECK_EQ(queue.PendingCount(), 0u);
}

TEST(Clock_RefusesATimeThatIsNotFinite)
{
    ClockOptions options;
    options.tickPeriodMs = 50;
    Clock clock(options);
    clock.OnFrame(10, 1000);
    CHECK_THROWS(std::invalid_argument, clock.Update(std::numeric_limits<double>::quiet_NaN()));
    CHECK_THROWS(std::invalid_argument, clock.OnFrame(11, std::numeric_limits<double>::infinity()));
    CHECK_THROWS(std::invalid_argument, clock.Update(1e300));
    CHECK_THROWS(std::invalid_argument, clock.OnPeriodChange(11, std::numeric_limits<double>::infinity()));
    // Still usable, and not poisoned.
    clock.Update(1100);
    CHECK(std::isfinite(clock.RenderFrac()));
}

TEST(Arena_AReleaseFreesItsBytesForTheNextCompaction)
{
    ByteArena arena;
    arena.Grow(4);
    arena.Set(0, Bytes(1000, 0xAA));
    CHECK_EQ(arena.ArenaBytes(), 1000u);
    arena.Release(0);
    // The next append past capacity compacts: the released kilobyte is not copied along.
    arena.Set(1, Bytes(2000, 0xBB));
    CHECK(std::ranges::equal(arena.At(1), Bytes(2000, 0xBB)));
    CHECK(arena.At(0).empty());
    CHECK_MSG(arena.ArenaBytes() <= 2000u, "the arena holds " << arena.ArenaBytes() << " B for 2000 live");
}

TEST(Arena_TheReplacedReservationCountsAsDeadThroughACompaction)
{
    ByteArena arena;
    arena.Grow(2);
    // Each Set outgrows the slot's reservation: the old copy is dead the moment it is replaced, so the arena stays bounded by a small multiple
    // of the live value instead of growing with every replacement.
    std::size_t largest = 0;
    for (std::size_t n = 1; n <= 400; n++)
    {
        arena.Set(0, Bytes(n, static_cast<std::uint8_t>(n)));
        largest = std::max(largest, arena.ArenaBytes());
    }

    CHECK(std::ranges::equal(arena.At(0), Bytes(400, static_cast<std::uint8_t>(400 & 0xFF))));
    CHECK_MSG(largest <= 4 * 400, "the arena reached " << largest << " B for a 400 B value");
}

TEST(Store_NumberAtRefusesTextAndOutOfRangeReads)
{
    ArchetypeSchema schema;
    schema.name = "T";
    schema.fields = {{"n", FieldKind::F64, 2}, {"t", FieldKind::Text}};
    ArchetypeStore store(schema, 4);
    const std::uint32_t slot = store.Allocate(1);
    CHECK_EQ(store.NumberAt(0, slot, 1), 0.0);
    CHECK_THROWS(std::logic_error, store.NumberAt(1, slot));
    CHECK_THROWS(std::out_of_range, store.NumberAt(0, slot, 2));
    CHECK_THROWS(std::out_of_range, store.NumberAt(0, store.Capacity(), 0));
}

namespace {

struct CapiState {
    typhon_client* client = nullptr;
    typhon_status reentrant = TYPHON_OK;
    int frames = 0;
    bool destroyInCallback = false;
};

std::shared_ptr<Link> g_capiLink;

typhon_client* CreateCapi(CapiState& state)
{
    typhon_client_config config{};
    config.struct_size = sizeof config;
    config.kind = "player";
    config.user = &state;
    config.on_frame = [](void* user, std::uint32_t)
    {
        auto* s = static_cast<CapiState*>(user);
        s->frames++;
        s->reentrant = typhon_client_poll(s->client, 0, nullptr);
        if (s->destroyInCallback)
        {
            typhon_client_destroy(s->client);
        }
    };
    typhon_client* client = nullptr;
    const auto status = capi::CreateWithTransport(
        &config,
        []
        {
            g_capiLink = std::make_shared<Link>();
            return std::make_unique<FakeTransport>(g_capiLink);
        },
        [] { return 0.0; }, &client);
    CHECK_EQ(static_cast<int>(status), static_cast<int>(TYPHON_OK));
    state.client = client;
    return client;
}

void FeedKitchenSink(typhon_client* client, std::size_t frames)
{
    CHECK_EQ(static_cast<int>(typhon_client_start(client)), static_cast<int>(TYPHON_OK));
    g_capiLink->Open();
    g_capiLink->Deliver(Welcome());
    const auto stream = StreamFrames("stream-kitchen-sink");
    for (std::size_t i = 0; i < frames; i++)
    {
        g_capiLink->Deliver(stream[i]);
    }
}

}  // namespace

TEST(CApi_APollFromACallbackIsAStateError)
{
    CapiState state;
    typhon_client* client = CreateCapi(state);
    FeedKitchenSink(client, 2);
    for (int i = 0; i < 8; i++)
    {
        typhon_client_poll(client, 0, nullptr);
    }

    CHECK_EQ(state.frames, 2);
    CHECK_EQ(static_cast<int>(state.reentrant), static_cast<int>(TYPHON_ERROR_STATE));
    CHECK_EQ(static_cast<int>(typhon_client_get_status(client)), static_cast<int>(TYPHON_CLIENT_OPEN));
    // The allocator may not change under a live client.
    const typhon_allocator hooks{[](std::size_t size, std::size_t, void*) -> void* { return std::malloc(size); },
                                 [](void* p, void*) { std::free(p); }, nullptr};
    CHECK_EQ(static_cast<int>(typhon_set_allocator(&hooks)), static_cast<int>(TYPHON_ERROR_STATE));
    typhon_client_destroy(client);
    // No client alive: restoring the default is allowed again.
    CHECK_EQ(static_cast<int>(typhon_set_allocator(nullptr)), static_cast<int>(TYPHON_OK));
}

TEST(CApi_ADestroyFromACallbackWaitsForItsPollToReturn)
{
    CapiState state;
    state.destroyInCallback = true;
    typhon_client* client = CreateCapi(state);
    FeedKitchenSink(client, 3);
    // The poll that applies the first frame runs the callback that destroys the client: the client lives until that poll returns, and the
    // handle is gone after it. Nothing touches it again — under ASan, any use after the deferred delete is a failure.
    CHECK_EQ(static_cast<int>(typhon_client_poll(client, 0, nullptr)), static_cast<int>(TYPHON_OK));
    CHECK_EQ(state.frames, 1);
}
