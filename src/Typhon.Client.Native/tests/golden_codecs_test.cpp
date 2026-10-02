// codec-*: every codec at its boundaries (W1-W10). Per case, decoding the byte range must yield the committed IEEE bits exactly, and —
// when the case has an input — encoding it must yield the byte range exactly. A case without an input is decode-only: wire codes no
// encoder produces (an over-long varint, the code -2^(b-1)).

#include <set>

#include "golden_support.hpp"
#include "test_framework.hpp"
#include "wire/field_codec.hpp"
#include "wire/reader.hpp"
#include "wire/writer.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

std::vector<double> NumbersOf(const Value& bitsArray)
{
    std::vector<double> numbers;
    for (const Value& v : bitsArray.Items())
    {
        numbers.push_back(FromBits(v.AsString()));
    }

    return numbers;
}

Value BitsOf(const double* values, int count)
{
    std::vector<Value> items;
    for (int i = 0; i < count; i++)
    {
        items.push_back(Value::MakeString(Bits(values[i])));
    }

    return Value::MakeArray(std::move(items));
}

void RunCodecVector(const std::string& name)
{
    const Value vector = GoldenJson(name);
    const auto bin = GoldenBin(name);
    const Catalog emptyCatalog;
    CatalogField field;
    field.name = "v";
    field.codec = CodecFromJson(*vector.Find("codec"));
    FieldPlan plan("v", 0, &field, field.codec, emptyCatalog);
    std::vector<FieldPlan*> fields = {&plan};
    SectionPlan section(fields);
    const auto frame = FrameFromJson(vector.Find("frame"));
    const auto frameTick = static_cast<std::uint32_t>(vector.Find("frameTick")->AsNumber());

    std::size_t covered = 0;
    for (const Value& c : vector.Find("cases")->Items())
    {
        const auto offset = static_cast<std::size_t>(c.Find("offset")->AsNumber());
        const auto length = static_cast<std::size_t>(c.Find("length")->AsNumber());
        const std::string at = name + " @" + std::to_string(offset);
        const std::span<const std::uint8_t> range(bin.data() + offset, length);
        covered = std::max(covered, offset + length);
        RecordingSink sink;
        WireReader reader(range);
        WireWriter writer;
        bool encoded = true;

        switch (plan.kind)
        {
            case CodecKind::Str:
            {
                ReadSection(reader, section, frameTick, sink);
                Value expected = Value::MakeArray({Value::MakeObject({{"call", Value::MakeString("text")},
                                                                     {"field", Value::MakeString("v")},
                                                                     {"utf8", Value::MakeString(c.Find("text")->AsString())}})});
                CheckLog(sink.log, expected, at);
                const auto utf8 = FromHex(c.Find("text")->AsString());
                const std::string text(utf8.begin(), utf8.end());
                const NamedValue value{"v", FieldValue::OfText(text)};
                WriteSection(writer, section, {&value, 1});
                break;
            }
            case CodecKind::Blob:
            case CodecKind::Bytes:
            {
                ReadSection(reader, section, frameTick, sink);
                Value expected = Value::MakeArray({Value::MakeObject({{"call", Value::MakeString("bytes")},
                                                                     {"field", Value::MakeString("v")},
                                                                     {"bytes", Value::MakeString(c.Find("bytes")->AsString())}})});
                CheckLog(sink.log, expected, at);
                const auto bytes = FromHex(c.Find("bytes")->AsString());
                const NamedValue value{"v", FieldValue::OfBytes(bytes)};
                WriteSection(writer, section, {&value, 1});
                break;
            }
            case CodecKind::List:
            {
                ReadSection(reader, section, frameTick, sink, false, frame.get());
                Value expected = Value::MakeArray({Value::MakeObject({{"call", Value::MakeString("list")},
                                                                     {"field", Value::MakeString("v")},
                                                                     {"count", *c.Find("count")},
                                                                     {"values", *c.Find("decoded")}})});
                CheckLog(sink.log, expected, at);
                if (c.Find("input") == nullptr)
                {
                    encoded = false;
                }
                else
                {
                    const auto numbers = NumbersOf(*c.Find("input"));
                    const NamedValue value{"v", FieldValue::OfNumbers(numbers)};
                    WriteSection(writer, section, {&value, 1}, false, frame.get());
                }

                break;
            }
            case CodecKind::U64:
            case CodecKind::I64:
            case CodecKind::Varu64:
            case CodecKind::Vari64:
            {
                // W32: bit patterns, never a double.
                std::uint64_t out[protocol::MaxCount] = {};
                ReadInteger64(reader, plan, out);
                std::vector<Value> items;
                for (int i = 0; i < plan.components; i++)
                {
                    items.push_back(Value::MakeString(Bits64(out[i])));
                }

                const Value decoded = Value::MakeArray(std::move(items));
                CHECK_MSG(decoded.Equals(*c.Find("decoded")), at << ": decoded bits " << decoded.Dump() << ", expected " << c.Find("decoded")->Dump());
                if (c.Find("input") == nullptr)
                {
                    encoded = false;
                }
                else
                {
                    std::vector<std::uint64_t> input;
                    for (const Value& v : c.Find("input")->Items())
                    {
                        input.push_back(FromBits64(v.AsString()));
                    }

                    WriteInteger64(writer, plan, input);
                }

                break;
            }
            default:
            {
                // Sized for the widest count (W33): ReadNumber writes plan.components values.
                double out[protocol::MaxCount] = {};
                ReadNumber(reader, plan, frameTick, out, frame.get());
                const Value decoded = BitsOf(out, plan.components);
                CHECK_MSG(decoded.Equals(*c.Find("decoded")), at << ": decoded bits " << decoded.Dump() << ", expected " << c.Find("decoded")->Dump());
                if (c.Find("input") == nullptr)
                {
                    encoded = false;
                }
                else
                {
                    WriteNumber(writer, plan, NumbersOf(*c.Find("input")), frame.get());
                }

                break;
            }
        }

        CHECK_MSG(reader.IsAtEnd(), at << ": decode consumed the whole range");
        if (encoded)
        {
            CHECK_MSG(Hex(writer.Written()) == Hex(range), at << ": encoded " << Hex(writer.Written()) << ", expected " << Hex(range));
        }
    }

    CHECK_MSG(covered == bin.size(), name << ": the cases cover " << covered << " of " << bin.size() << " bytes");
}

}  // namespace

TEST(GoldenCodecs_EveryByteAlignedKindHasAVector)
{
    std::set<CodecKind> covered;
    for (const std::string& name : GoldenNames("codec-"))
    {
        covered.insert(CodecKindOf(GoldenJson(name).Find("codec")->Find("t")->AsString()));
    }

    for (int k = 1; k < CodecKindCount; k++)
    {
        const auto kind = static_cast<CodecKind>(k);
        // A collection's element is a catalog section, not a codec: the tick-coll vector covers it.
        CHECK_MSG(IsPacked(kind) || kind == CodecKind::Coll || covered.count(kind) != 0, "no codec vector for " << CodecToken(kind));
    }
}

TEST(GoldenCodecs_EveryVectorDecodesAndEncodesBitForBit)
{
    const auto names = GoldenNames("codec-");
    CHECK(names.size() > 40);
    for (const std::string& name : names)
    {
        RunCodecVector(name);
    }
}

// section-packs: one implicit pack per section, least significant bit first, packed fields ahead of byte-aligned ones (W12).
TEST(GoldenSectionPacks_DecodeEncodeAndIgnorePadding)
{
    const Value vector = GoldenJson("section-packs");
    const auto bin = GoldenBin("section-packs");
    const Catalog emptyCatalog;
    std::vector<CatalogField> declared;
    for (const Value& f : vector.Find("fields")->Items())
    {
        CatalogField field;
        field.name = f.Find("name")->AsString();
        field.codec = CodecFromJson(*f.Find("codec"));
        declared.push_back(std::move(field));
    }

    std::vector<std::unique_ptr<FieldPlan>> plans;
    std::vector<FieldPlan*> fields;
    for (std::size_t i = 0; i < declared.size(); i++)
    {
        plans.push_back(std::make_unique<FieldPlan>(declared[i].name, static_cast<int>(i), &declared[i], declared[i].codec, emptyCatalog));
        fields.push_back(plans.back().get());
    }

    SectionPlan section(fields);
    CHECK_EQ(section.packBytes, static_cast<int>(vector.Find("packBytes")->AsNumber()));
    const auto& expectedFields = vector.Find("fields")->Items();
    int packedBits = 0;
    for (std::size_t i = 0; i < fields.size(); i++)
    {
        const int expectedOffset = static_cast<int>(expectedFields[i].Find("bitOffset")->AsNumber());
        CHECK_EQ(fields[i]->packed ? fields[i]->bitOffset : -1, expectedOffset);
        if (fields[i]->packed)
        {
            packedBits = std::max(packedBits, fields[i]->bitOffset + fields[i]->bitCount);
        }
    }

    const int used = packedBits - 8 * (section.packBytes - 1);
    const auto padding = static_cast<std::uint8_t>(0xFF & ~((1 << used) - 1));
    for (const Value& c : vector.Find("cases")->Items())
    {
        const auto offset = static_cast<std::size_t>(c.Find("offset")->AsNumber());
        const auto length = static_cast<std::size_t>(c.Find("length")->AsNumber());
        const std::span<const std::uint8_t> range(bin.data() + offset, length);

        for (int pass = 0; pass < 2; pass++)
        {
            std::vector<std::uint8_t> bytes(range.begin(), range.end());
            if (pass == 1)
            {
                // Non-zero padding bits are ignored on decode.
                bytes[static_cast<std::size_t>(section.packBytes - 1)] |= padding;
            }

            RecordingSink sink;
            WireReader reader(bytes);
            ReadSection(reader, section, 0, sink);
            CHECK(reader.IsAtEnd());
            for (const Value& entry : sink.log)
            {
                const std::string& fieldName = entry.Find("field")->AsString();
                CHECK_MSG(entry.Find("values")->Items()[0].Equals(*c.Find("values")->Find(fieldName)), fieldName << " pass " << pass);
            }

            CHECK_EQ(sink.log.size(), c.Find("values")->Members().size());
        }

        std::vector<std::vector<double>> storage;
        std::vector<NamedValue> values;
        for (const auto& [fieldName, bits] : c.Find("values")->Members())
        {
            storage.push_back({FromBits(bits.AsString())});
        }

        std::size_t k = 0;
        for (const auto& [fieldName, bits] : c.Find("values")->Members())
        {
            values.push_back({fieldName, FieldValue::OfNumbers(storage[k++])});
        }

        WireWriter writer;
        WriteSection(writer, section, values);
        CHECK_EQ(Hex(writer.Written()), Hex(range));
    }
}
