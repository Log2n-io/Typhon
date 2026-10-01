// wire-refusals: byte sequences every decoder must reject, with the close code it must reject them with — the half a lenient decoder
// gets wrong silently. Then refusals the vector does not carry, and the primitives' edges.

#include <bit>
#include <cmath>
#include <functional>

#include "golden_support.hpp"
#include "test_framework.hpp"
#include "wire/errors.hpp"
#include "wire/field_codec.hpp"
#include "wire/messages.hpp"
#include "wire/reader.hpp"
#include "wire/writer.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

// The close code `action` refuses its input with; fails when it accepts it or throws anything else.
int CloseCodeOf(const std::function<void()>& action)
{
    try
    {
        action();
    }
    catch (const WireFormatError& e)
    {
        return e.CloseCode();
    }

    ::typhon::test::Fail(__FILE__, __LINE__, "the input was accepted");
}

}  // namespace

TEST(WireRefusals_EveryCaseIsRefusedWithItsCloseCode)
{
    const Value vector = GoldenJson("wire-refusals");
    CHECK_EQ(vector.Find("catalog")->AsString(), std::string("catalog-kitchen-sink"));
    const auto kitchen = PlanOf("catalog-kitchen-sink");
    const auto frame = FrameFromJson(vector.Find("frame"));
    const Catalog emptyCatalog;

    for (const Value& c : vector.Find("cases")->Items())
    {
        const std::string kind = c.Find("kind")->AsString();
        const std::string name = kind + ": " + c.Find("name")->AsString();
        const auto bytes = FromHex(c.Find("hex")->AsString());
        RecordingSink sink;
        int code = 0;
        try
        {
            code = CloseCodeOf(
                [&]
                {
                    if (kind == "codec")
                    {
                        CatalogField field;
                        field.name = "v";
                        field.codec = CodecFromJson(*c.Find("codec"));
                        FieldPlan plan("v", 0, &field, field.codec, emptyCatalog);
                        SectionPlan section({&plan});
                        WireReader reader(bytes);
                        ReadSection(reader, section, 0, sink);
                    }
                    else if (kind == "tick")
                    {
                        TickReader reader(kitchen);
                        const Value* held = c.Find("held");
                        reader.realm = held != nullptr && !held->AsBool() ? nullptr : frame;
                        reader.Read(bytes, sink);
                    }
                    else if (kind == "commands")
                    {
                        ReadCommands(bytes, *kitchen, sink, frame.get());
                    }
                    else
                    {
                        switch (static_cast<std::uint8_t>(c.Find("type")->AsNumber()))
                        {
                            case MessageType::Hello:
                                (void)ParseHello(bytes);
                                break;
                            case MessageType::Welcome:
                                (void)ParseWelcome(bytes);
                                break;
                            case MessageType::Ping:
                                (void)ParsePing(bytes);
                                break;
                            case MessageType::Pong:
                                (void)ParsePong(bytes);
                                break;
                            case MessageType::Kick:
                                (void)ParseKick(bytes);
                                break;
                            case MessageType::Bye:
                                (void)ParseBye(bytes);
                                break;
                            default:
                                throw std::logic_error("unknown message type in the vector");
                        }
                    }
                });
        }
        catch (const TestFailure& e)
        {
            ::typhon::test::Fail(__FILE__, __LINE__, name + ": " + e.what());
        }

        CHECK_MSG(code == static_cast<int>(c.Find("closeCode")->AsNumber()), name << ": closed with " << code);
    }
}

TEST(WireRefusals_ATruncatedTickIsMalformedWhereverItIsCut)
{
    const auto kitchen = PlanOf("catalog-kitchen-sink");
    const auto full = GoldenBin("tick-entities");
    const Value vector = GoldenJson("tick-entities");
    TickReader reader(PlanOf(vector.Find("catalog")->AsString()));
    reader.realm = FrameFromJson(vector.Find("frame"));
    for (const std::size_t cut : {std::size_t{1}, std::size_t{5}, std::size_t{7}, full.size() >> 1, full.size() - 1})
    {
        RecordingSink sink;
        CHECK_EQ(CloseCodeOf([&] { reader.Read(std::span<const std::uint8_t>(full.data(), cut), sink); }), CloseCode::MalformedPayload);
    }
}

TEST(WireRefusals_ARefusedCommandsMessageDeliversNothing)
{
    const auto kitchen = PlanOf("catalog-kitchen-sink");
    const MessagePlan& steer = *kitchen->CommandByName("Steer");
    const double zero = 0;
    const double one = 1;
    const double half = 0.5;
    const NamedValue first[] = {
        {"heading", {FieldValue::Kind::Numbers, {&zero, 1}, {}, {}}}, {"boost", {FieldValue::Kind::Numbers, {&one, 1}, {}, {}}},
        {"speed", {FieldValue::Kind::Numbers, {&half, 1}, {}, {}}},   {"stance", {FieldValue::Kind::Numbers, {&one, 1}, {}, {}}},
        {"note", {FieldValue::Kind::Text, {}, "ok", {}}},
    };
    const NamedValue second[] = {
        {"heading", {FieldValue::Kind::Numbers, {&zero, 1}, {}, {}}}, {"boost", {FieldValue::Kind::Numbers, {&zero, 1}, {}, {}}},
        {"speed", {FieldValue::Kind::Numbers, {&zero, 1}, {}, {}}},   {"stance", {FieldValue::Kind::Numbers, {&zero, 1}, {}, {}}},
        {"note", {FieldValue::Kind::Text, {}, "", {}}},
    };
    const CommandInput commands[] = {{&steer, 1, first}, {&steer, 2, second}};
    WireWriter w;
    WriteCommands(w, 7, commands);
    std::vector<std::uint8_t> bytes(w.Written().begin(), w.Written().end());
    // The last command ends with its pack, heading (1 B), empty note (1 B) and speed (1 B): stance 3 is one past its names.
    bytes[bytes.size() - 4] = 3 << 1;
    RecordingSink sink;
    CHECK_EQ(CloseCodeOf([&] { ReadCommands(bytes, *kitchen, sink); }), CloseCode::MalformedPayload);
    CHECK(sink.log.empty());

    // A client refuses to encode an enum value outside its names, and an over-cap string.
    const double three = 3;
    NamedValue bad[5] = {first[0], first[1], first[2], {"stance", {FieldValue::Kind::Numbers, {&three, 1}, {}, {}}}, first[4]};
    const CommandInput badEnum[] = {{&steer, 1, bad}};
    WireWriter refused;
    CHECK_THROWS(std::out_of_range, WriteCommands(refused, 0, badEnum));
    const std::string longNote(17, 'x');
    NamedValue overCap[5] = {first[0], first[1], first[2], first[3], {"note", {FieldValue::Kind::Text, {}, longNote, {}}}};
    const CommandInput overCapCommand[] = {{&steer, 1, overCap}};
    CHECK_THROWS(std::out_of_range, WriteCommands(refused, 0, overCapCommand));
}

TEST(WirePrimitives_F32NanIsCanonicalBothWays)
{
    for (const std::uint32_t pattern : {0x7FC00000u, 0xFFC00000u, 0x7F800001u, 0xFFF00001u})
    {
        const std::uint8_t bytes[4] = {static_cast<std::uint8_t>(pattern), static_cast<std::uint8_t>(pattern >> 8),
                                       static_cast<std::uint8_t>(pattern >> 16), static_cast<std::uint8_t>(pattern >> 24)};
        WireReader r(bytes);
        const double v = r.F32();
        CHECK(v != v);
    }

    WireWriter w;
    w.F32(-std::nan(""));
    w.F32(std::nan(""));
    CHECK_EQ(Hex(w.Written()), std::string("0000c07f0000c07f"));
}

TEST(WirePrimitives_FixedWidthEdgesRoundTrip)
{
    WireWriter w;
    w.U8(255);
    w.U8(static_cast<std::uint32_t>(-128));
    w.U16(65535);
    w.U16(static_cast<std::uint32_t>(-32768));
    w.U24(0xFFFFFF);
    w.U24(static_cast<std::uint32_t>(-0x800000));
    w.U32(0xFFFFFFFFu);
    w.U32(0x80000000u);
    WireReader r(w.Written());
    CHECK_EQ(static_cast<int>(r.U8()), 255);
    CHECK_EQ(static_cast<int>(r.I8()), -128);
    CHECK_EQ(static_cast<int>(r.U16()), 65535);
    CHECK_EQ(static_cast<int>(r.I16()), -32768);
    CHECK_EQ(r.U24(), 0xFFFFFFu);
    CHECK_EQ(r.I24(), -0x800000);
    CHECK_EQ(r.U32(), 0xFFFFFFFFu);
    CHECK_EQ(r.I32(), static_cast<std::int32_t>(0x80000000u));
    CHECK(r.IsAtEnd());
}

TEST(WirePrimitives_ALengthPrefixIsPatchedInMinimalForm)
{
    WireWriter w(16);
    const std::size_t mark = w.BeginLengthPrefixed();
    for (std::uint32_t i = 0; i < 200; i++)
    {
        w.U8(i);
    }

    w.EndLengthPrefixed(mark);
    CHECK_EQ(w.Position(), 202u);
    WireReader r(w.Written());
    CHECK_EQ(r.Varu(), 200u);
    CHECK_EQ(static_cast<int>(r.U8()), 0);
    CHECK_EQ(static_cast<int>(w.Written()[201]), 199);
}

TEST(WirePrimitives_AStringKeepsALeadingByteOrderMark)
{
    const std::uint8_t bytes[] = {4, 0xEF, 0xBB, 0xBF, 0x41};
    WireReader r(bytes);
    CHECK_EQ(std::string(r.Str(8)), std::string("\xEF\xBB\xBF" "A"));
}
