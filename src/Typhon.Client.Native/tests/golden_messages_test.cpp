// message-*: every message decodes to the committed object and re-encodes to the committed bytes. COMMANDS is encoded from its inputs
// and decoded to the committed server-side log.

#include "golden_support.hpp"
#include "test_framework.hpp"
#include "wire/messages.hpp"
#include "wire/writer.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

std::span<const std::uint8_t> Bytes(const std::string& s) { return {reinterpret_cast<const std::uint8_t*>(s.data()), s.size()}; }

double Number(const Value& message, const char* key) { return message.Find(key)->AsNumber(); }

const std::string& String(const Value& message, const char* key) { return message.Find(key)->AsString(); }

template <std::size_t N>
std::array<std::uint8_t, N> HashFromHex(const std::string& hex)
{
    // Display form: most significant byte first; wire form: little-endian.
    std::array<std::uint8_t, N> bytes{};
    const auto big = FromHex(hex);
    for (std::size_t i = 0; i < N; i++)
    {
        bytes[N - 1 - i] = big[i];
    }

    return bytes;
}

}  // namespace

TEST(GoldenMessages_Hello)
{
    for (const char* name : {"message-hello", "message-hello-minimal"})
    {
        const Value vector = GoldenJson(name);
        const Value& expected = *vector.Find("message");
        const auto bin = GoldenBin(name);
        CHECK_EQ(static_cast<double>(bin[0]), Number(vector, "type"));
        const HelloMessage m = ParseHello(bin);
        CHECK_EQ(static_cast<double>(m.major), Number(expected, "major"));
        CHECK_EQ(static_cast<double>(m.minor), Number(expected, "minor"));
        CHECK_EQ(static_cast<double>(m.caps), Number(expected, "caps"));
        CHECK_EQ(m.kind, String(expected, "kind"));
        CHECK_EQ(Hex(Bytes(m.token)), String(expected, "token"));
        CHECK_EQ(Hex(m.resumeToken), String(expected, "resumeToken"));
        CHECK_EQ(CatalogHashToHex(m.clientCatalogHash), String(expected, "clientCatalogHash"));
        CHECK_EQ(Hex(m.helloPayload), String(expected, "helloPayload"));

        WireWriter w;
        WriteHello(w, m);
        CHECK_EQ(Hex(w.Written()), Hex(bin));
    }
}

TEST(GoldenMessages_Welcome)
{
    const std::string swgHash = GoldenJson("catalog-swg").Find("hash")->AsString();
    for (const char* name : {"message-welcome", "message-welcome-skip"})
    {
        const Value vector = GoldenJson(name);
        const Value& expected = *vector.Find("message");
        const auto bin = GoldenBin(name);
        const WelcomeMessage m = ParseWelcome(bin);
        CHECK_EQ(static_cast<double>(m.major), Number(expected, "major"));
        CHECK_EQ(static_cast<double>(m.minor), Number(expected, "minor"));
        CHECK_EQ(static_cast<double>(m.capsGranted), Number(expected, "capsGranted"));
        CHECK_EQ(static_cast<double>(m.sessionId), Number(expected, "sessionId"));
        CHECK_EQ(Hex(m.resumeToken), String(expected, "resumeToken"));
        CHECK_EQ(static_cast<double>(m.tick), Number(expected, "tick"));
        CHECK_EQ(static_cast<double>(m.tickPeriodUs), Number(expected, "tickPeriodUs"));
        CHECK_EQ(CatalogHashToHex(m.catalogHash), String(expected, "catalogHash"));
        CHECK_EQ(static_cast<double>(m.catalogJson.size()), Number(expected, "catalogBytes"));
        CHECK_EQ(CatalogHashToHex(m.catalogHash), swgHash);
        if (!m.catalogJson.empty())
        {
            CHECK_EQ(Hex(m.catalogJson), Hex(GoldenBin("catalog-swg")));
            CHECK_EQ(ParseCatalog(std::span<const std::uint8_t>(m.catalogJson)).appName, std::string("SwgTatooine"));
        }

        WireWriter w;
        WriteWelcome(w, m);
        CHECK_EQ(Hex(w.Written()), Hex(bin));
    }

    CHECK(HashFromHex<8>(swgHash) == ParseWelcome(GoldenBin("message-welcome")).catalogHash);
}

TEST(GoldenMessages_PingPong)
{
    const auto pingBin = GoldenBin("message-ping");
    const Value ping = *GoldenJson("message-ping").Find("message");
    const PingMessage p = ParsePing(pingBin);
    CHECK_EQ(static_cast<double>(p.clientMs), Number(ping, "clientMs"));
    CHECK_EQ(static_cast<double>(p.lastAppliedTick), Number(ping, "lastAppliedTick"));
    WireWriter w;
    WritePing(w, p);
    CHECK_EQ(Hex(w.Written()), Hex(pingBin));

    const auto pongBin = GoldenBin("message-pong");
    const Value pong = *GoldenJson("message-pong").Find("message");
    const PongMessage q = ParsePong(pongBin);
    CHECK_EQ(static_cast<double>(q.clientMs), Number(pong, "clientMs"));
    CHECK_EQ(static_cast<double>(q.tick), Number(pong, "tick"));
    CHECK_EQ(static_cast<double>(q.usIntoTick), Number(pong, "usIntoTick"));
    w.Reset();
    WritePong(w, q);
    CHECK_EQ(Hex(w.Written()), Hex(pongBin));
}

TEST(GoldenMessages_KickTruncatesTheReasonAtACodePoint)
{
    const auto bin = GoldenBin("message-kick");
    const Value expected = *GoldenJson("message-kick").Find("message");
    const KickMessage m = ParseKick(bin);
    CHECK_EQ(static_cast<double>(m.code), Number(expected, "code"));
    CHECK_EQ(Hex(Bytes(m.reason)), String(expected, "reason"));

    WireWriter w;
    WriteKick(w, {1013, std::string(122, 'a') + "\xE2\x82\xAC lagging"});
    CHECK_EQ(Hex(w.Written()), Hex(bin));
}

TEST(GoldenMessages_Bye)
{
    const auto bin = GoldenBin("message-bye");
    const std::uint32_t code = ParseBye(bin);
    CHECK_EQ(static_cast<double>(code), Number(*GoldenJson("message-bye").Find("message"), "code"));
    WireWriter w;
    WriteBye(w, code);
    CHECK_EQ(Hex(w.Written()), Hex(bin));
    CHECK_THROWS(std::out_of_range, WriteBye(w, 1001));
}

namespace {

void RunCommandsVector(const std::string& name)
{
    const Value vector = GoldenJson(name);
    const auto plan = PlanOf(vector.Find("catalog")->AsString());
    const auto frame = FrameFromJson(vector.Find("frame"));

    // Storage first, views second: every view must outlive the encode.
    std::vector<std::vector<double>> numbers;
    std::vector<std::vector<std::uint64_t>> integers;
    std::vector<std::string> texts;
    std::vector<std::vector<NamedValue>> valueSets;
    const auto& inputs = vector.Find("inputs")->Items();
    numbers.reserve(64);
    integers.reserve(64);
    texts.reserve(64);
    for (const Value& input : inputs)
    {
        const MessagePlan& type = *plan->CommandByName(input.Find("type")->AsString());
        std::vector<NamedValue> values;
        for (const FieldPlan* field : type.body->fields)
        {
            const Value& raw = *input.Find("values")->Find(field->name);
            if (field->valueKind == ValueKind::Text)
            {
                const auto utf8 = FromHex(raw.AsString());
                texts.emplace_back(utf8.begin(), utf8.end());
                values.push_back({field->name, FieldValue::OfText(texts.back())});
            }
            else if (field->valueKind == ValueKind::Integer64)
            {
                // W32: bit patterns, as a C application holds them in uint64_t / int64_t.
                std::vector<std::uint64_t> components;
                for (const Value& bits : raw.Items())
                {
                    components.push_back(FromBits64(bits.AsString()));
                }

                integers.push_back(std::move(components));
                values.push_back({field->name, FieldValue::OfIntegers(integers.back())});
            }
            else
            {
                std::vector<double> components;
                for (const Value& bits : raw.Items())
                {
                    components.push_back(FromBits(bits.AsString()));
                }

                numbers.push_back(std::move(components));
                values.push_back({field->name, FieldValue::OfNumbers(numbers.back())});
            }
        }

        valueSets.push_back(std::move(values));
    }

    std::vector<CommandInput> commands;
    for (std::size_t i = 0; i < inputs.size(); i++)
    {
        commands.push_back({plan->CommandByName(inputs[i].Find("type")->AsString()), static_cast<std::uint32_t>(inputs[i].Find("seq")->AsNumber()),
                            valueSets[i]});
    }

    WireWriter w;
    WriteCommands(w, static_cast<std::uint32_t>(vector.Find("clientTick")->AsNumber()), commands, frame.get());
    const auto bin = GoldenBin(name);
    CHECK_MSG(Hex(w.Written()) == Hex(bin), name << ": encoded " << Hex(w.Written()));

    RecordingSink sink;
    ReadCommands(bin, *plan, sink, frame.get());
    CheckLog(sink.log, *vector.Find("log"), name);
}

}  // namespace

TEST(GoldenMessages_CommandsEncodeFromInputsAndDecodeToTheServerLog)
{
    RunCommandsVector("message-commands");
    RunCommandsVector("message-commands-exact");
}
