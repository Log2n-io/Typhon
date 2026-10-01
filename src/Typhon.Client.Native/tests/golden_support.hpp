#pragma once

#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "catalog/catalog_plan.hpp"
#include "json/json.hpp"
#include "wire/commands.hpp"
#include "wire/realm_frame.hpp"
#include "wire/tick_reader.hpp"

// Access to the golden vectors the C# reference encoder produced (test/Typhon.Protocol.Tests/Golden, 05 § 4), and the recording sink
// whose log must equal the C# RecordingSink's call for call, key for key — the TypeScript SDK's golden-support.ts, in C++.
namespace typhon::test {

using client::json::Value;

std::vector<std::uint8_t> GoldenBin(std::string_view name);
Value GoldenJson(std::string_view name);
std::vector<std::string> GoldenNames(std::string_view prefix);

// A double's IEEE bits as 16 lower-case hex digits, most significant first — the vectors' number format — or "nan".
std::string Bits(double value);
double FromBits(std::string_view hex);

std::string Hex(std::span<const std::uint8_t> bytes);
std::vector<std::uint8_t> FromHex(std::string_view hex);

// A realm frame as a vector carries it (C# CatalogSamples.FrameJson): every number as its IEEE bits.
Value FrameJson(const client::RealmFrame* frame);
std::shared_ptr<const client::RealmFrame> FrameFromJson(const Value* json);

// The catalog plan of a golden catalog vector.
std::shared_ptr<const client::CatalogPlan> PlanOf(std::string_view catalogName);

// Checks two logs are equal, reporting the first entry that differs.
void CheckLog(const std::vector<Value>& actual, const Value& expected, const std::string& context);

// Records every call a decoder makes, in the exact JSON shape of the C# RecordingSink.
class RecordingSink final : public client::TickSink, public client::CommandSink {
public:
    std::vector<Value> log;

    void BeginTick(std::uint32_t tick, std::uint8_t flags, std::uint32_t periodUs) override;
    void Realm(const client::RealmFrame* frame) override;
    void BeginEntities(const client::ArchetypePlan& archetype) override;
    void Enter(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
               std::uint8_t epoch) override;
    void Segment(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
                 std::uint8_t epoch) override;
    void State(std::uint32_t netId, std::uint8_t groupMask) override;
    void Leave(std::uint32_t netId) override;
    void Event(const client::MessagePlan& type) override;
    void Self(const client::ArchetypePlan* archetype, std::uint32_t netId, std::uint32_t lastSeq, std::uint8_t ownerMask) override;
    void Ack(std::uint32_t seq, std::uint8_t reason) override;
    void Source(std::uint32_t requestId, std::uint8_t status, std::uint32_t code) override;
    void BeginAggregate(const client::GridPlan& grid, bool reset) override;
    void AggregateCell(std::uint32_t cell, std::span<const std::uint32_t> counts) override;
    void Metric(const client::MetricPlan& metric, int valueIndex, double value) override;
    void Debug(std::uint8_t subType, std::span<const std::uint8_t> payload) override;
    void Ext(std::uint32_t appTypeId, std::span<const std::uint8_t> payload) override;
    void UnknownBlock(std::uint8_t blockType) override;
    void EndTick() override;
    void Command(const client::MessagePlan& type, std::uint32_t seq, std::uint32_t clientTick) override;
    void Number(const client::FieldPlan& field, const double* values) override;
    void Text(const client::FieldPlan& field, std::string_view utf8) override;
    void Bytes(const client::FieldPlan& field, std::span<const std::uint8_t> data) override;
    void List(const client::FieldPlan& field, int count, const double* values) override;

private:
    const client::ArchetypePlan* archetype_ = nullptr;
};

}  // namespace typhon::test
