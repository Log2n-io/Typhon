// The net layer against an in-memory transport: the handshake, the caps, the close codes (03 §§ 3 and 10), the reconnect policy, the
// command queue and the ping loop — the TypeScript SDK's connection, reconnect, queue and ping tests, case for case where they apply to
// a TCP client. The test is the peer: every event the transport raises is raised by hand.

#include <algorithm>
#include <cmath>
#include <cstring>
#include <deque>

#include "client/client.hpp"
#include "fake_transport.hpp"
#include "golden_support.hpp"
#include "test_framework.hpp"
#include "wire/commands.hpp"
#include "wire/errors.hpp"
#include "wire/messages.hpp"
#include "wire/reader.hpp"
#include "wire/realm_frame.hpp"
#include "wire/writer.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

Bytes Kick(std::uint32_t code, const std::string& reason)
{
    WireWriter w(256);
    WriteKick(w, {code, reason});
    return {w.Written().begin(), w.Written().end()};
}

Bytes Pong(std::uint32_t clientMs, std::uint32_t tick)
{
    WireWriter w(32);
    WritePong(w, {clientMs, tick, 0});
    return {w.Written().begin(), w.Written().end()};
}

struct Recorder final : ConnectionListener {
    std::vector<SessionInfo> sessions;
    std::vector<Bytes> ticks;
    std::vector<double> tickTimes;
    std::vector<ConnectionClose> closes;
    std::vector<std::pair<int, std::string>> kicks;
    int messages = 0;

    void OnWelcome(const SessionInfo& s) override { sessions.push_back(s); }
    void OnTick(std::span<const std::uint8_t> m, double recvMs) override
    {
        ticks.emplace_back(m.begin(), m.end());
        tickTimes.push_back(recvMs);
    }

    void OnMessage(std::span<const std::uint8_t>, double) override { messages++; }
    void OnKick(int code, const std::string& reason) override { kicks.emplace_back(code, reason); }
    void OnClose(const ConnectionClose& c) override { closes.push_back(c); }
};

struct Harness {
    std::shared_ptr<Link> link = std::make_shared<Link>();
    Recorder recorder;
    double now = 0;
    std::unique_ptr<Connection> connection;

    explicit Harness(ConnectionOptions options = {})
    {
        if (options.kind.empty())
        {
            options.kind = "player";
            options.token = "opaque";
            options.caps = Capabilities::Stats;
        }

        connection = std::make_unique<Connection>(std::make_unique<FakeTransport>(link), std::move(options), recorder, [this] { return now; });
    }

    // Polls until the queued events are consumed.
    void Pump()
    {
        for (int i = 0; i < 64 && !link->incoming.empty(); i++)
        {
            connection->Poll(0);
        }
    }

    void OpenSession(Bytes welcome = Welcome())
    {
        connection->Connect();
        link->Open();
        link->Deliver(std::move(welcome));
        Pump();
    }
};

std::uint32_t ByeCode(const Bytes& message) { return ParseBye(message); }

}  // namespace

TEST(Connection_SendsHelloAsSoonAsTheTransportOpens)
{
    Harness h;
    h.connection->Connect();
    CHECK(h.link->opened);
    CHECK(h.link->sent.empty());
    h.link->Open();
    h.Pump();
    CHECK(h.connection->State() == ConnectionState::AwaitingWelcome);
    CHECK_EQ(h.link->sent.size(), 1u);
    const HelloMessage hello = ParseHello(h.link->sent[0]);
    CHECK_EQ(hello.major, 3u);
    CHECK_EQ(hello.kind, std::string("player"));
    CHECK_EQ(hello.token, std::string("opaque"));
    CHECK_EQ(hello.caps, Capabilities::Stats);
    CHECK(hello.clientCatalogHash == CatalogHash{});
    CHECK(hello.resumeToken == ResumeToken{});
    // HELLO itself went out under the 16 KiB first-message limit, and the inbound cap is WELCOME's until the limits arrive.
    CHECK(h.link->sent[0].size() <= static_cast<std::size_t>(protocol::HelloMaxBytes));
    CHECK_EQ(h.link->inboundCap, static_cast<std::uint32_t>(protocol::WelcomeMaxBytes));
}

TEST(Connection_OpensTheSessionOnWelcomeTakingItsLimits)
{
    Harness h;
    h.OpenSession(Welcome(WelcomeParts().Caps(Capabilities::Stats)));
    CHECK(h.connection->State() == ConnectionState::Open);
    CHECK_EQ(h.recorder.sessions.size(), 1u);
    const SessionInfo& s = h.recorder.sessions[0];
    CHECK_EQ(s.sessionId, 7u);
    CHECK_EQ(s.capsGranted, Capabilities::Stats);
    CHECK(!s.catalogSkipped);
    CHECK(!s.resumed);
    CHECK(!s.resumeToken.has_value());
    CHECK(s.plan->CommandByName("Steer") != nullptr);
    CHECK_EQ(h.link->inboundCap, 262144u);
    // The catalog JSON is kept exactly as it arrived, for the next session to offer.
    CHECK(h.connection->Cache()->json == GoldenBin("catalog-kitchen-sink"));
    CHECK(h.connection->Cache()->hash == Hash);
}

TEST(Connection_EchoesTheCatalogHashAndAcceptsASkippedCatalog)
{
    Harness first;
    first.OpenSession();
    ConnectionOptions options;
    options.kind = "player";
    options.catalogCache = first.connection->Cache();
    Harness second(options);
    second.OpenSession(Welcome(WelcomeParts().SkipCatalog()));
    CHECK(ParseHello(second.link->sent[0]).clientCatalogHash == Hash);
    CHECK(second.recorder.sessions[0].catalogSkipped);
    CHECK(second.recorder.sessions[0].plan == first.connection->Cache()->plan);
}

TEST(Connection_PresentsAResumeTokenAndReportsItsDeadlineOnClose)
{
    ConnectionOptions options;
    options.kind = "player";
    options.resumeToken = TokenOf(9);
    Harness h(options);
    h.OpenSession(Welcome(WelcomeParts().Resume(TokenOf(40))));
    CHECK(ParseHello(h.link->sent[0]).resumeToken == TokenOf(9));
    CHECK(h.recorder.sessions[0].resumed);
    CHECK(h.recorder.sessions[0].resumeToken == TokenOf(40));
    h.now = 1000;
    h.link->ServerClose(CloseCode::GoingAway);
    h.Pump();
    CHECK_EQ(h.recorder.closes.size(), 1u);
    CHECK(h.recorder.closes[0].resumeToken == TokenOf(40));
    CHECK_EQ(h.recorder.closes[0].resumeDeadlineMs, 61000.0);
    CHECK(!h.recorder.closes[0].local);
}

TEST(Connection_ClosesWith4002WhenWelcomeIsLate)
{
    Harness h;
    h.connection->Connect();
    h.link->Open();
    h.Pump();
    h.now = 4999;
    h.connection->Poll(0);
    CHECK(h.recorder.closes.empty());
    h.now = 5000;
    h.connection->Poll(0);
    CHECK_EQ(h.recorder.closes.size(), 1u);
    CHECK_EQ(h.recorder.closes[0].code, CloseCode::HelloTimeout);
    // 4002 is a code a client may send: it goes in the close, not in a BYE 4004.
    CHECK_EQ(h.link->sent.size(), 1u);
    CHECK(h.link->closed);
}

TEST(Connection_HandsTicksOverInArrivalOrderWithTheirTime)
{
    Harness h;
    h.OpenSession();
    for (std::uint32_t t = 1; t <= 3; t++)
    {
        h.now = 10.0 * t;
        h.link->Deliver(Tick(t));
        h.connection->Poll(0);
    }

    CHECK_EQ(h.recorder.ticks.size(), 3u);
    for (std::size_t i = 0; i < 3; i++)
    {
        CHECK(h.recorder.ticks[i] == Tick(static_cast<std::uint32_t>(i + 1)));
        CHECK_EQ(h.recorder.tickTimes[i], 10.0 * static_cast<double>(i + 1));
    }

    // Every inbound message reached the recorder hook, WELCOME included.
    CHECK_EQ(h.recorder.messages, 4);
}

TEST(Connection_ReportsAKickThenItsCodeAsTheCloseCode)
{
    Harness h;
    h.OpenSession();
    h.link->Deliver(Kick(4100, "maintenance"));
    h.link->ServerClose(CloseCode::GoingAway);
    h.Pump();
    CHECK_EQ(h.recorder.kicks.size(), 1u);
    CHECK_EQ(h.recorder.kicks[0].first, 4100);
    CHECK_EQ(h.recorder.closes.size(), 1u);
    CHECK_EQ(h.recorder.closes[0].code, 4100);
    CHECK_EQ(h.recorder.closes[0].reason, std::string("maintenance"));
}

TEST(Connection_SendsByeAndClosesOnACleanLeave)
{
    Harness h;
    h.OpenSession();
    h.connection->Close(CloseCode::Normal, "done");
    CHECK_EQ(h.link->sent.size(), 2u);
    CHECK_EQ(ByeCode(h.link->sent[1]), static_cast<std::uint32_t>(CloseCode::Normal));
    CHECK(h.link->closed);
    CHECK(h.recorder.closes[0].local);
    CHECK(h.recorder.closes[0].wasClean);
    CHECK(h.connection->State() == ConnectionState::Closed);
    CHECK_THROWS(std::logic_error, h.connection->Send(Bytes{1}));
    CHECK_THROWS(std::logic_error, h.connection->Connect());
}

TEST(Connection_RefusesAPeerThatBreaksTheProtocol)
{
    struct Case {
        const char* name;
        int code;
        std::function<void(Harness&)> run;
    };

    Bytes truncated = Welcome();
    truncated.resize(12);
    const std::vector<Case> cases = {
        {"a message before WELCOME", CloseCode::ProtocolError, [](Harness& h) { h.link->Deliver(Tick(1)); }},
        {"an unknown message type", CloseCode::ProtocolError,
         [](Harness& h)
         {
             h.link->Deliver(Welcome());
             h.link->Deliver(Bytes{0x7E, 1, 2});
         }},
        {"an empty message", CloseCode::ProtocolError, [](Harness& h) { h.link->Deliver(Bytes{}); }},
        {"a WELCOME of another major", CloseCode::ProtocolError, [](Harness& h) { h.link->Deliver(Welcome(WelcomeParts().Major(4))); }},
        {"a capability the client did not request", CloseCode::ProtocolError,
         [](Harness& h) { h.link->Deliver(Welcome(WelcomeParts().Caps(Capabilities::Stats | Capabilities::Debug))); }},
        {"a catalog skip with nothing cached", CloseCode::ProtocolError, [](Harness& h) { h.link->Deliver(Welcome(WelcomeParts().SkipCatalog())); }},
        {"a truncated WELCOME", CloseCode::MalformedPayload, [truncated](Harness& h) { h.link->Deliver(truncated); }},
        {"a catalog this client refuses", CloseCode::MalformedPayload, [](Harness& h) { h.link->Deliver(Welcome(WelcomeParts().CatalogJson({0x7B}))); }},
        {"a WELCOME above the protocol limit", CloseCode::MessageTooBig,
         [](Harness& h) { h.link->Deliver(Bytes(static_cast<std::size_t>(protocol::WelcomeMaxBytes) + 1)); }},
        {"a frame above the catalog limit", CloseCode::MessageTooBig,
         [](Harness& h)
         {
             h.link->Deliver(Welcome());
             h.link->Deliver(Bytes(262145));
         }},
    };

    for (const Case& c : cases)
    {
        Harness h;
        h.connection->Connect();
        h.link->Open();
        c.run(h);
        h.Pump();
        CHECK_MSG(h.recorder.closes.size() == 1, c.name << ": " << h.recorder.closes.size() << " closes");
        CHECK_MSG(h.recorder.closes[0].code == c.code, c.name << ": closed with " << h.recorder.closes[0].code);
        CHECK(h.recorder.ticks.empty());
        if (c.code != CloseCode::MessageTooBig)
        {
            // 1002 and 1007 are not codes a client may send (W24): the client says BYE 4004, and the real code stays in the report.
            CHECK_MSG(h.recorder.closes[0].local, c.name);
            CHECK_MSG(ByeCode(h.link->sent.back()) == static_cast<std::uint32_t>(CloseCode::ClientRefusedTheStream), c.name);
            CHECK(h.link->closed);
        }
    }
}

TEST(Connection_SendsNothingButHelloBeforeWelcomeAndCapsWhatItSends)
{
    Harness h;
    h.connection->Connect();
    h.link->Open();
    h.Pump();
    CHECK_THROWS(std::logic_error, h.connection->Send(Bytes{0x84}));
    h.link->Deliver(Welcome());
    h.Pump();
    h.connection->Send(Bytes(1024, 0x83));
    CHECK_THROWS(std::length_error, h.connection->Send(Bytes(1025, 0x83)));
}

TEST(Reconnect_FollowsTheCloseCodeTable)
{
    for (const int code : {1001, 1011, 1013, 1008, 4001, 4002, 1006, 0})
    {
        CHECK_MSG(ReconnectRuleOf(code) == ReconnectRule::Reconnect, code);
    }

    for (const int code : {1002, 1007, 1009, 4003, 4004, 4005, 4099})
    {
        CHECK_MSG(ReconnectRuleOf(code) == ReconnectRule::Never, code);
    }

    for (const int code : {1000, 4100, 4999})
    {
        CHECK_MSG(ReconnectRuleOf(code) == ReconnectRule::Application, code);
    }
}

TEST(Reconnect_BackoffDoublesToItsCeilingWithinItsJitterBand)
{
    double r = 0;
    Backoff backoff({}, [&] { return r; });
    CHECK_EQ(backoff.Next(), 500.0);
    CHECK_EQ(backoff.Next(), 1000.0);
    CHECK_EQ(backoff.Next(), 2000.0);
    for (int i = 0; i < 10; i++)
    {
        backoff.Next();
    }

    CHECK_EQ(backoff.Next(), 30000.0);
    r = 1;
    CHECK_EQ(backoff.Next(), 15000.0);
    backoff.Reset();
    CHECK_EQ(backoff.Attempt(), 0);
    CHECK_EQ(backoff.Next(), 250.0);

    Backoff random;
    for (int i = 0; i < 100; i++)
    {
        random.Reset();
        const double wait = random.Next();
        CHECK(wait >= 250 && wait <= 500);
    }
}

namespace {

struct ClientHarness {
    std::vector<std::shared_ptr<Link>> links;
    double now = 0;
    std::vector<ConnectionClose> closes;
    std::vector<ReconnectRule> gaveUp;
    std::unique_ptr<Client> client;

    explicit ClientHarness(std::function<void(ClientOptions&)> configure = {})
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
        options.onGiveUp = [this](const ConnectionClose&, ReconnectRule rule) { gaveUp.push_back(rule); };
        if (configure)
        {
            configure(options);
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

    void OpenSession(Bytes welcome = Welcome())
    {
        Last().Open();
        Last().Deliver(std::move(welcome));
        Pump();
    }
};

}  // namespace

TEST(Client_ReconnectsAfterATransientCloseOfferingTheCatalogItHolds)
{
    ClientHarness h;
    h.client->Start();
    h.OpenSession();
    CHECK(h.client->Status() == ClientStatus::Open);
    h.Last().ServerClose(CloseCode::GoingAway);
    h.Pump();
    CHECK(h.client->Status() == ClientStatus::WaitingToReconnect);
    CHECK_EQ(h.links.size(), 1u);
    h.now = 499;
    h.client->Poll(0);
    CHECK_EQ(h.links.size(), 1u);
    h.now = 500;
    h.client->Poll(0);
    CHECK_EQ(h.links.size(), 2u);
    h.OpenSession(Welcome(WelcomeParts().SkipCatalog()));
    CHECK(ParseHello(h.Last().sent[0]).clientCatalogHash == Hash);
    CHECK_EQ(h.client->SessionsOpened(), 2u);
    CHECK_EQ(h.client->Attempt(), 0);
}

TEST(Client_PresentsTheResumeTokenOnlyWhileItsGraceLasts)
{
    ClientHarness h;
    h.client->Start();
    h.OpenSession(Welcome(WelcomeParts().Resume(TokenOf(3))));
    h.Last().ServerClose(CloseCode::GoingAway);
    h.Pump();
    h.now = 500;
    h.client->Poll(0);
    h.Last().Open();
    h.Pump();
    CHECK(ParseHello(h.Last().sent[0]).resumeToken == TokenOf(3));
    // This attempt fails before WELCOME: the token stays the old session's, and its grace (60 s) runs out.
    h.Last().ServerClose(CloseCode::GoingAway);
    h.Pump();
    h.now = 70000;
    h.client->Poll(0);
    h.Last().Open();
    h.Pump();
    CHECK(ParseHello(h.Last().sent[0]).resumeToken == ResumeToken{});
}

TEST(Client_BacksOffWhileRefusedAndStopsAtMaxAttempts)
{
    ClientHarness h([](ClientOptions& o) { o.maxAttempts = 2; });
    h.client->Start();
    std::vector<double> starts;
    for (int i = 0; i < 3; i++)
    {
        h.Last().ServerClose(CloseCode::TryAgainLater);
        h.Pump();
        if (h.client->Status() != ClientStatus::WaitingToReconnect)
        {
            break;
        }

        const std::size_t before = h.links.size();
        while (h.links.size() == before)
        {
            h.now += 1;
            h.client->Poll(0);
        }

        starts.push_back(h.now);
    }

    CHECK((starts == std::vector<double>{500, 1500}));
    CHECK(h.client->Status() == ClientStatus::GaveUp);
    CHECK_EQ(h.gaveUp.size(), 1u);
}

TEST(Client_StaysDownAfterAProtocolFailureARejectionOrACleanClose)
{
    for (const int code : {CloseCode::ProtocolError, CloseCode::AuthenticationRejected, CloseCode::Normal})
    {
        ClientHarness h;
        h.client->Start();
        h.OpenSession();
        h.Last().Deliver(Kick(static_cast<std::uint32_t>(code), ""));
        h.Last().ServerClose(CloseCode::GoingAway);
        h.Pump();
        CHECK_MSG(h.client->Status() == ClientStatus::GaveUp, code);
        CHECK_EQ(h.closes.back().code, code);
    }
}

TEST(Client_LetsTheApplicationDecideForItsOwnCodes)
{
    ClientHarness h([](ClientOptions& o) { o.shouldReconnect = [](const ConnectionClose& c) { return c.code == 4200; }; });
    h.client->Start();
    h.OpenSession();
    h.Last().Deliver(Kick(4200, ""));
    h.Last().ServerClose();
    h.Pump();
    CHECK(h.client->Status() == ClientStatus::WaitingToReconnect);
    h.now = 500;
    h.client->Poll(0);
    h.OpenSession();
    h.Last().Deliver(Kick(4300, ""));
    h.Last().ServerClose();
    h.Pump();
    CHECK(h.client->Status() == ClientStatus::GaveUp);
}

TEST(Client_StopsOnDemandSendingByeAndStaysStopped)
{
    ClientHarness h;
    h.client->Start();
    h.OpenSession();
    h.client->Stop();
    CHECK(h.client->Status() == ClientStatus::Stopped);
    CHECK_EQ(ByeCode(h.Last().sent.back()), static_cast<std::uint32_t>(CloseCode::Normal));
    h.now = 100000;
    h.client->Poll(0);
    CHECK_EQ(h.links.size(), 1u);
}

TEST(Client_ComesBackAfterAHelloThatTimedOut)
{
    ClientHarness h;
    h.client->Start();
    h.Last().Open();
    h.Pump();
    h.now = 5000;
    h.client->Poll(0);
    CHECK_EQ(h.closes.back().code, CloseCode::HelloTimeout);
    CHECK(h.client->Status() == ClientStatus::WaitingToReconnect);
}

TEST(Client_AppliesTheStreamAndPingsWithTheNewestAppliedTick)
{
    ClientHarness h;
    h.client->Start();
    h.OpenSession();
    // The first PING goes at once, before any frame.
    h.client->Poll(0);
    CHECK_EQ(h.Last().sent.size(), 2u);
    CHECK_EQ(ParsePing(h.Last().sent[1]).lastAppliedTick, 0u);

    // The kitchen-sink stream: the same catalog the WELCOME carried.
    const auto stream = GoldenBin("stream-kitchen-sink");
    std::uint32_t frames = 0;
    for (std::size_t at = 0; at + 4 <= stream.size();)
    {
        std::uint32_t length;
        std::memcpy(&length, stream.data() + at, 4);
        h.Last().Deliver(Bytes(stream.begin() + static_cast<std::ptrdiff_t>(at + 4), stream.begin() + static_cast<std::ptrdiff_t>(at + 4 + length)));
        at += 4 + length;
        frames++;
    }

    h.Pump();
    FrameApplier& applier = *h.client->Applier();
    CHECK_EQ(applier.World().frames, static_cast<std::uint64_t>(frames));
    CHECK(h.client->Status() == ClientStatus::Open);

    // 250 ms later (pingHz 4) the next PING carries the newest applied tick.
    h.now = 250;
    h.client->Poll(0);
    CHECK_EQ(h.Last().sent.size(), 3u);
    CHECK_EQ(static_cast<std::int64_t>(ParsePing(h.Last().sent[2]).lastAppliedTick), applier.Tick());

    h.Last().Deliver(Pong(0, 7));
    h.now = 260;
    h.Pump();
    CHECK_EQ(h.client->Ping()->PongsReceived(), 1u);
}

TEST(Client_ClosesWith1007OnAMalformedTick)
{
    ClientHarness h;
    h.client->Start();
    h.OpenSession();
    // A block whose declared length runs past the message.
    Bytes tick = Tick(1);
    tick.push_back(0x01);
    tick.push_back(0x40);
    h.Last().Deliver(tick);
    h.Pump();
    CHECK_EQ(h.closes.back().code, CloseCode::MalformedPayload);
    CHECK_EQ(ByeCode(h.Last().sent.back()), static_cast<std::uint32_t>(CloseCode::ClientRefusedTheStream));
    CHECK(h.client->Status() == ClientStatus::GaveUp);
}

namespace {

std::shared_ptr<const RealmFrame> Swg()
{
    const double min[3] = {-8192, -8192, 0};
    const double max[3] = {8192, 8192, 256};
    return std::make_shared<const RealmFrame>(0, 0, 0, 0, 24, 256.0, false, min, max);
}

struct SteerValues {
    double heading;
    double boost = 1;
    double speed = 0.5;
    double stance = 1;
    std::vector<NamedValue> values;

    explicit SteerValues(double h) : heading(h)
    {
        values = {{"heading", FieldValue::OfNumbers({&heading, 1})},
                  {"boost", FieldValue::OfNumbers({&boost, 1})},
                  {"speed", FieldValue::OfNumbers({&speed, 1})},
                  {"stance", FieldValue::OfNumbers({&stance, 1})},
                  {"note", FieldValue::OfText("go")}};
    }
};

struct RegionValues {
    double altitude = 10;
    double budget = 64;
    std::vector<double> vertices;
    std::vector<NamedValue> values;

    explicit RegionValues(double x) : vertices{x, 0, 0, x + 10, 0, 0, x + 10, 10, 0}
    {
        values = {{"altitudeM", FieldValue::OfNumbers({&altitude, 1})},
                  {"budgetKiBps", FieldValue::OfNumbers({&budget, 1})},
                  {"vertices", FieldValue::OfNumbers(vertices)}};
    }
};

struct DecodedCommands final : CommandSink {
    std::vector<std::uint32_t> seqs;
    std::vector<std::uint32_t> clientTicks;
    std::vector<std::string> types;
    void Command(const MessagePlan& type, std::uint32_t seq, std::uint32_t clientTick) override
    {
        types.push_back(type.name);
        seqs.push_back(seq);
        clientTicks.push_back(clientTick);
    }

    void Number(const FieldPlan&, const double*) override {}
    void Integer64(const FieldPlan&, const std::uint64_t*) override {}
    void Text(const FieldPlan&, std::string_view) override {}
    void Bytes(const FieldPlan&, std::span<const std::uint8_t>) override {}
    void List(const FieldPlan&, int, const double*) override {}
};

DecodedCommands Decode(const std::vector<Bytes>& messages, const CatalogPlan& plan)
{
    DecodedCommands decoded;
    for (const Bytes& m : messages)
    {
        CHECK_EQ(static_cast<int>(m[0]), static_cast<int>(MessageType::Commands));
        ReadCommands(m, plan, decoded, Swg().get());
    }

    return decoded;
}

std::function<void(std::span<const std::uint8_t>)> Into(std::vector<Bytes>& messages)
{
    return [&messages](std::span<const std::uint8_t> m) { messages.emplace_back(m.begin(), m.end()); };
}

}  // namespace

TEST(CommandQueue_BatchesAFrameFromOneSequenceSpace)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    CommandQueue queue(plan, [] { return 0.0; });
    const MessagePlan& steer = *plan->CommandByName("Steer");
    CHECK_EQ(queue.Enqueue(steer, SteerValues(0).values), 1);
    CHECK_EQ(queue.Enqueue(steer, SteerValues(0.5).values), 2);
    CHECK_EQ(queue.PendingCount(), 2u);
    std::vector<Bytes> messages;
    CHECK_EQ(queue.Flush(77, Into(messages), Swg().get()), 1);
    CHECK_EQ(queue.PendingCount(), 0u);
    const auto decoded = Decode(messages, *plan);
    CHECK((decoded.seqs == std::vector<std::uint32_t>{1, 2}));
    CHECK((decoded.clientTicks == std::vector<std::uint32_t>{77, 77}));
    CHECK_EQ(queue.Flush(78, Into(messages), Swg().get()), 0);
}

TEST(CommandQueue_WrapsItsSequenceAt2To16)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    CommandQueue queue(plan, [] { return 0.0; }, 0, 65534);
    const MessagePlan& steer = *plan->CommandByName("Steer");
    CHECK_EQ(queue.Enqueue(steer, SteerValues(0).values), 65534);
    CHECK_EQ(queue.Enqueue(steer, SteerValues(0).values), 65535);
    CHECK_EQ(queue.Enqueue(steer, SteerValues(0).values), 0);
    CHECK_EQ(queue.NextSeq(), 1u);
}

TEST(CommandQueue_RefusesWhatTheBucketCannotPayForAndRefills)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    double now = 0;
    CommandQueue queue(plan, [&] { return now; });
    const MessagePlan& region = *plan->ClientRegion();
    const MessagePlan& steer = *plan->CommandByName("Steer");
    // ClientRegion allows 5 a second, burst 5.
    for (int i = 0; i < 5; i++)
    {
        CHECK(queue.Enqueue(region, RegionValues(i).values) >= 0);
    }

    CHECK_EQ(queue.Enqueue(region, RegionValues(9).values), CommandRateLimited);
    CHECK_EQ(queue.RateLimitedCount(), 1u);
    now = 200;
    CHECK(queue.Enqueue(region, RegionValues(9).values) >= 0);
    // A type with no rate is unlimited.
    for (int i = 0; i < 50; i++)
    {
        CHECK(queue.Enqueue(steer, SteerValues(0).values) >= 0);
    }
}

TEST(CommandQueue_KeepsOnlyTheNewestOfALatestCommand)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    double now = 0;
    CommandQueue queue(plan, [&] { return now; });
    queue.Enqueue(*plan->CommandByName("Steer"), SteerValues(0).values);
    const std::int32_t first = queue.Enqueue(*plan->ClientRegion(), RegionValues(0).values);
    now = 1000;
    const std::int32_t second = queue.Enqueue(*plan->ClientRegion(), RegionValues(500).values);
    CHECK(second != first);
    CHECK_EQ(queue.PendingCount(), 2u);
    CHECK_EQ(queue.CoalescedCount(), 1u);
    std::vector<Bytes> messages;
    queue.Flush(5, Into(messages), Swg().get());
    const auto decoded = Decode(messages, *plan);
    CHECK((decoded.types == std::vector<std::string>{"Steer", "ClientRegion"}));
    CHECK((decoded.seqs == std::vector<std::uint32_t>{1, static_cast<std::uint32_t>(second)}));
}

TEST(CommandQueue_SplitsABatchUnderTheMessageLimitKeepingOneClientTick)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    CommandQueue queue(plan, [] { return 0.0; });
    for (int i = 0; i < 200; i++)
    {
        queue.Enqueue(*plan->CommandByName("Steer"), SteerValues(i / 1000.0).values);
    }

    std::vector<Bytes> messages;
    CHECK(queue.Flush(9, Into(messages), Swg().get()) > 1);
    for (const Bytes& m : messages)
    {
        CHECK(m.size() <= 1024);
    }

    const auto decoded = Decode(messages, *plan);
    CHECK_EQ(decoded.seqs.size(), 200u);
    CHECK(std::all_of(decoded.clientTicks.begin(), decoded.clientTicks.end(), [](std::uint32_t t) { return t == 9; }));
}

TEST(CommandQueue_RefusesAnOversizedCommandABadValueAndAForeignType)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    CommandQueue small(plan, [] { return 0.0; }, 12);
    CHECK_THROWS(std::length_error, small.Enqueue(*plan->CommandByName("Steer"), SteerValues(0).values));
    CHECK_EQ(small.NextSeq(), 1u);

    CommandQueue queue(plan, [] { return 0.0; });
    SteerValues bad(0);
    bad.stance = 3;
    CHECK_THROWS(std::out_of_range, queue.Enqueue(*plan->CommandByName("Steer"), bad.values));
    CHECK_EQ(queue.PendingCount(), 0u);
    CHECK_EQ(queue.NextSeq(), 1u);
    const auto other = PlanOf("catalog-kitchen-sink");
    CHECK_THROWS(std::invalid_argument, queue.Enqueue(*other->CommandByName("Steer"), SteerValues(0).values));
}

TEST(CommandQueue_CopiesValuesAtEnqueue)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    CommandQueue queue(plan, [] { return 0.0; });
    {
        SteerValues values(0.25);
        queue.Enqueue(*plan->CommandByName("Steer"), values.values);
        values.heading = 0.75;
    }

    std::vector<Bytes> messages;
    queue.Flush(1, Into(messages), Swg().get());
    struct Heading final : CommandSink {
        double heading = -1;
        void Command(const MessagePlan&, std::uint32_t, std::uint32_t) override {}
        void Number(const FieldPlan& f, const double* v) override
        {
            if (f.name == "heading")
            {
                heading = v[0];
            }
        }

        void Integer64(const FieldPlan&, const std::uint64_t*) override {}
        void Text(const FieldPlan&, std::string_view) override {}
        void Bytes(const FieldPlan&, std::span<const std::uint8_t>) override {}
        void List(const FieldPlan&, int, const double*) override {}
    } sink;
    ReadCommands(messages[0], *plan, sink, Swg().get());
    CHECK(std::abs(sink.heading - 0.25) < 0.01);
}

TEST(CommandQueue_WithNoRealmDropsTheRealmFramedCommandsAndSendsTheOthers)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    CommandQueue queue(plan, [] { return 0.0; });
    queue.Enqueue(*plan->ClientRegion(), RegionValues(0).values);
    queue.Enqueue(*plan->CommandByName("Steer"), SteerValues(3).values);
    std::vector<Bytes> messages;
    CHECK_EQ(queue.Flush(12, Into(messages), nullptr), 1);
    CHECK((Decode(messages, *plan).types == std::vector<std::string>{"Steer"}));
    CHECK_EQ(queue.DroppedWithoutRealm(), 1u);
    CHECK_EQ(queue.PendingCount(), 0u);
    queue.Enqueue(*plan->CommandByName("Steer"), SteerValues(0).values);
    queue.Clear();
    CHECK_EQ(queue.Flush(1, Into(messages), Swg().get()), 0);
}

TEST(Ping_PingsAtOnceThenAtTheRate)
{
    PingScheduler ping(4);
    PingMessage m;
    CHECK(!ping.Due(0, 0, m));
    ping.Start(1000);
    CHECK(ping.Due(1000, 42, m));
    CHECK_EQ(m.lastAppliedTick, 42u);
    CHECK_EQ(m.clientMs, 1000u);
    CHECK(!ping.Due(1249, 42, m));
    CHECK(ping.Due(1250, 43, m));
    // A poll that fell far behind owes one ping, not a burst.
    CHECK(ping.Due(5000, 44, m));
    CHECK(!ping.Due(5000, 44, m));
    CHECK_EQ(ping.PingsSent(), 3u);
    CHECK_THROWS(std::invalid_argument, PingScheduler(0));
}

TEST(Ping_MeasuresAndSmoothsTheRoundTripAcrossAClientMsWrap)
{
    PingScheduler ping(4);
    ping.OnPong({1000, 5, 0}, 1080);
    CHECK_EQ(ping.RttMs(), 80.0);
    ping.OnPong({2000, 6, 0}, 2160);
    CHECK_EQ(ping.LastRttMs(), 160.0);
    CHECK_EQ(ping.RttMs(), 90.0);
    CHECK_EQ(ping.ServerTick(), 6);

    PingScheduler wrapped(4);
    wrapped.OnPong({0xFFFFFFF0u, 1, 0}, 4294967296.0 + 0x10);
    CHECK_EQ(wrapped.RttMs(), 32.0);
}
