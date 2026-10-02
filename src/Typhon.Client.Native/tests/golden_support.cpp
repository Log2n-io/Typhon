#include "golden_support.hpp"

#include <algorithm>
#include <bit>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>

#include "test_framework.hpp"

namespace typhon::test {

namespace {

std::filesystem::path GoldenDir() { return std::filesystem::path(TYPHON_GOLDEN_DIR); }

Value Num(double v) { return Value::MakeNumber(v); }

Value Str(std::string s) { return Value::MakeString(std::move(s)); }

Value BitsArray(std::span<const double> values)
{
    std::vector<Value> items;
    for (const double v : values)
    {
        items.push_back(Str(Bits(v)));
    }

    return Value::MakeArray(std::move(items));
}

Value Entry(const char* call, std::vector<Value::Member> members)
{
    members.insert(members.begin(), {"call", Str(call)});
    return Value::MakeObject(std::move(members));
}

}  // namespace

std::vector<std::uint8_t> GoldenBin(std::string_view name)
{
    const auto path = GoldenDir() / (std::string(name) + ".bin");
    std::ifstream in(path, std::ios::binary);
    if (!in)
    {
        throw std::runtime_error("golden vector not found: " + path.string());
    }

    return {std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
}

Value GoldenJson(std::string_view name)
{
    const auto path = GoldenDir() / (std::string(name) + ".json");
    std::ifstream in(path, std::ios::binary);
    if (!in)
    {
        throw std::runtime_error("golden expectation not found: " + path.string());
    }

    const std::string text{std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
    return client::json::Parse(text);
}

std::vector<std::string> GoldenNames(std::string_view prefix)
{
    std::vector<std::string> names;
    for (const auto& entry : std::filesystem::directory_iterator(GoldenDir()))
    {
        const std::string file = entry.path().filename().string();
        if (file.size() > 5 && file.compare(0, prefix.size(), prefix) == 0 && file.ends_with(".json"))
        {
            names.push_back(file.substr(0, file.size() - 5));
        }
    }

    std::sort(names.begin(), names.end());
    return names;
}

std::string Bits(double value)
{
    if (value != value)
    {
        return "nan";
    }

    char text[17];
    std::snprintf(text, sizeof text, "%016llx", static_cast<unsigned long long>(std::bit_cast<std::uint64_t>(value)));
    return text;
}

std::string Bits64(std::uint64_t value)
{
    char text[17];
    std::snprintf(text, sizeof text, "%016llx", static_cast<unsigned long long>(value));
    return text;
}

std::uint64_t FromBits64(std::string_view hex) { return std::stoull(std::string(hex), nullptr, 16); }

double FromBits(std::string_view hex)
{
    if (hex == "nan")
    {
        return std::bit_cast<double>(0x7FF8000000000000ull);
    }

    return std::bit_cast<double>(std::stoull(std::string(hex), nullptr, 16));
}

std::string Hex(std::span<const std::uint8_t> bytes)
{
    std::string text;
    for (const std::uint8_t b : bytes)
    {
        char two[3];
        std::snprintf(two, sizeof two, "%02x", b);
        text += two;
    }

    return text;
}

std::vector<std::uint8_t> FromHex(std::string_view hex)
{
    std::vector<std::uint8_t> bytes;
    for (std::size_t i = 0; i + 1 < hex.size(); i += 2)
    {
        bytes.push_back(static_cast<std::uint8_t>(std::stoul(std::string(hex.substr(i, 2)), nullptr, 16)));
    }

    return bytes;
}

Value FrameJson(const client::RealmFrame* frame)
{
    if (frame == nullptr)
    {
        return {};
    }

    return Value::MakeObject({
        {"realmId", Num(frame->realmId)},
        {"generation", Num(frame->generation)},
        {"kindIdx", Num(frame->kindIdx)},
        {"appTag", Num(frame->appTag)},
        {"posBits", Num(frame->positionBits)},
        {"cellM", Str(Bits(frame->cellM))},
        {"deep", Value::MakeBool(frame->deep)},
        {"min", BitsArray(frame->min)},
        {"max", BitsArray(frame->max)},
    });
}

std::shared_ptr<const client::RealmFrame> FrameFromJson(const Value* json)
{
    if (json == nullptr || json->IsNull())
    {
        return nullptr;
    }

    const auto number = [&](const char* key) { return static_cast<std::uint32_t>(json->Find(key)->AsNumber()); };
    double min[3];
    double max[3];
    for (std::size_t i = 0; i < 3; i++)
    {
        min[i] = FromBits(json->Find("min")->Items()[i].AsString());
        max[i] = FromBits(json->Find("max")->Items()[i].AsString());
    }

    return std::make_shared<const client::RealmFrame>(number("realmId"), number("generation"), number("kindIdx"), number("appTag"),
                                                      static_cast<int>(number("posBits")), FromBits(json->Find("cellM")->AsString()),
                                                      json->Find("deep")->AsBool(), min, max);
}

std::shared_ptr<const client::CatalogPlan> PlanOf(std::string_view catalogName)
{
    const auto bin = GoldenBin(catalogName);
    auto catalog = std::make_shared<const client::Catalog>(client::ParseCatalog(std::span<const std::uint8_t>(bin)));
    return client::CatalogPlan::Compile(std::move(catalog));
}

void CheckLog(const std::vector<Value>& actual, const Value& expected, const std::string& context)
{
    const auto& want = expected.Items();
    const std::size_t n = std::min(actual.size(), want.size());
    for (std::size_t i = 0; i < n; i++)
    {
        CHECK_MSG(actual[i].Equals(want[i]), context << ": entry " << i << " is " << actual[i].Dump() << ", expected " << want[i].Dump());
    }

    CHECK_MSG(actual.size() == want.size(), context << ": " << actual.size() << " entries, expected " << want.size());
}

void RecordingSink::BeginTick(std::uint32_t tick, std::uint8_t flags, std::uint32_t periodUs)
{
    log.push_back(Entry("beginTick", {{"tick", Num(tick)}, {"flags", Num(flags)}, {"periodUs", Num(periodUs)}}));
}

void RecordingSink::Realm(const client::RealmFrame* frame) { log.push_back(Entry("realm", {{"frame", FrameJson(frame)}})); }

void RecordingSink::BeginEntities(const client::ArchetypePlan& archetype)
{
    archetype_ = &archetype;
    log.push_back(Entry("beginEntities", {{"archetype", Str(archetype.name)}}));
}

void RecordingSink::Enter(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
                          std::uint8_t epoch)
{
    log.push_back(Entry("enter", {{"netId", Num(netId)},
                                  {"position", BitsArray(position)},
                                  {"velocity", BitsArray(velocity)},
                                  {"t0", Num(t0)},
                                  {"epoch", Num(epoch)}}));
}

void RecordingSink::Segment(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
                            std::uint8_t epoch)
{
    log.push_back(Entry("segment", {{"netId", Num(netId)},
                                    {"position", BitsArray(position)},
                                    {"velocity", BitsArray(velocity)},
                                    {"t0", Num(t0)},
                                    {"epoch", Num(epoch)}}));
}

void RecordingSink::State(std::uint32_t netId, std::uint8_t groupMask)
{
    log.push_back(Entry("state", {{"netId", Num(netId)}, {"groupMask", Num(groupMask)}}));
}

void RecordingSink::Leave(std::uint32_t netId) { log.push_back(Entry("leave", {{"netId", Num(netId)}})); }

void RecordingSink::Event(const client::MessagePlan& type)
{
    log.push_back(Entry("event", {{"type", Str(type.name)}, {"idx", Num(type.idx)}}));
}

void RecordingSink::Self(const client::ArchetypePlan* archetype, std::uint32_t netId, std::uint32_t lastSeq, std::uint8_t ownerMask)
{
    log.push_back(Entry("self", {{"archetype", archetype == nullptr ? Value() : Str(archetype->name)},
                                 {"netId", Num(netId)},
                                 {"lastSeq", Num(lastSeq)},
                                 {"ownerMask", Num(ownerMask)}}));
}

void RecordingSink::Ack(std::uint32_t seq, std::uint8_t reason) { log.push_back(Entry("ack", {{"seq", Num(seq)}, {"reason", Num(reason)}})); }

void RecordingSink::Source(std::uint32_t requestId, std::uint8_t status, std::uint32_t code)
{
    log.push_back(Entry("source", {{"requestId", Num(requestId)}, {"status", Num(status)}, {"code", Num(code)}}));
}

void RecordingSink::BeginAggregate(const client::GridPlan& grid, bool reset)
{
    log.push_back(Entry("beginAggregate", {{"grid", Num(grid.idx)}, {"reset", Value::MakeBool(reset)}}));
}

void RecordingSink::AggregateCell(std::uint32_t cell, std::span<const std::uint32_t> counts)
{
    std::vector<Value> items;
    for (const std::uint32_t c : counts)
    {
        items.push_back(Num(c));
    }

    log.push_back(Entry("aggregateCell", {{"cell", Num(cell)}, {"counts", Value::MakeArray(std::move(items))}}));
}

void RecordingSink::Metric(const client::MetricPlan& metric, int valueIndex, double value)
{
    log.push_back(Entry("metric", {{"name", Str(metric.name)}, {"index", Num(valueIndex)}, {"value", Str(Bits(value))}}));
}

void RecordingSink::Debug(std::uint8_t subType, std::span<const std::uint8_t> payload)
{
    log.push_back(Entry("debug", {{"subType", Num(subType)}, {"payload", Str(Hex(payload))}}));
}

void RecordingSink::Ext(std::uint32_t appTypeId, std::span<const std::uint8_t> payload)
{
    log.push_back(Entry("ext", {{"appTypeId", Num(appTypeId)}, {"payload", Str(Hex(payload))}}));
}

void RecordingSink::UnknownBlock(std::uint8_t blockType) { log.push_back(Entry("unknownBlock", {{"blockType", Num(blockType)}})); }

void RecordingSink::EndTick() { log.push_back(Entry("endTick", {})); }

void RecordingSink::Command(const client::MessagePlan& type, std::uint32_t seq, std::uint32_t clientTick)
{
    log.push_back(Entry("command", {{"type", Str(type.name)}, {"idx", Num(type.idx)}, {"seq", Num(seq)}, {"clientTick", Num(clientTick)}}));
}

void RecordingSink::Number(const client::FieldPlan& field, const double* values)
{
    log.push_back(Entry("number", {{"field", Str(field.name)},
                                   {"values", BitsArray({values, static_cast<std::size_t>(field.components)})}}));
}

void RecordingSink::Integer64(const client::FieldPlan& field, const std::uint64_t* values)
{
    std::vector<Value> items;
    for (int i = 0; i < field.components; i++)
    {
        items.push_back(Str(Bits64(values[i])));
    }

    log.push_back(Entry("integer64", {{"field", Str(field.name)}, {"values", Value::MakeArray(std::move(items))}}));
}

void RecordingSink::Text(const client::FieldPlan& field, std::string_view utf8)
{
    log.push_back(Entry("text", {{"field", Str(field.name)},
                                 {"utf8", Str(Hex({reinterpret_cast<const std::uint8_t*>(utf8.data()), utf8.size()}))}}));
}

void RecordingSink::Bytes(const client::FieldPlan& field, std::span<const std::uint8_t> data)
{
    log.push_back(Entry("bytes", {{"field", Str(field.name)}, {"bytes", Str(Hex(data))}}));
}

void RecordingSink::List(const client::FieldPlan& field, int count, const double* values)
{
    log.push_back(Entry("list", {{"field", Str(field.name)},
                                 {"count", Num(count)},
                                 {"values", BitsArray({values, static_cast<std::size_t>(count * field.components)})}}));
}

void RecordingSink::Collection(const client::FieldPlan& field, int total, int sent)
{
    log.push_back(Entry("collection", {{"field", Str(field.name)}, {"total", Num(total)}, {"sent", Num(sent)}}));
}

void RecordingSink::CollectionElement(const client::FieldPlan& field, int index)
{
    log.push_back(Entry("collectionElement", {{"field", Str(field.name)}, {"index", Num(index)}}));
}

}  // namespace typhon::test
