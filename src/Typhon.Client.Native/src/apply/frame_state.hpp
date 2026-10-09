#pragma once

#include <cstdint>
#include <span>
#include <string_view>

#include "catalog/catalog_plan.hpp"
#include "store/collection_value.hpp"
#include "store/memory.hpp"

// The per-frame and per-session state a TICK carries besides entities (the TypeScript SDK's apply/frame-state.ts): the controlled
// entity's owner values (SELF), command rejections (ACKS), source lifecycle (SOURCES), metrics (STATS), and the reused record an
// event is decoded into. Allocated from the catalog; applying a frame allocates nothing once each buffer reached its working size.
namespace typhon::client {

// The controlled entity's owner-only state (W17), accumulated across SELF blocks: a group a frame does not carry keeps its values.
class SelfState {
public:
    // The controlled entity's archetype; null before the first SELF, after a RESET, and while the session controls none (W17').
    const ArchetypePlan* archetype = nullptr;
    std::uint32_t netId = 0;
    // The highest command seq drained into a tick at or before the latest frame's (W31).
    std::uint32_t lastSeq = 0;
    // Whether the current frame carried a SELF block.
    bool received = false;
    // The owner groups the current frame carried (bit i <-> archetype->ownerGroups[i]).
    std::uint8_t ownerMask = 0;
    // Incremented on every SELF block and on a reset.
    std::uint64_t version = 0;

    // A SELF block begins. A different controlled entity starts from no values; null drops the owner state and keeps lastSeq.
    void Receive(const ArchetypePlan* owner, std::uint32_t id, std::uint32_t seq, std::uint8_t mask);
    void BeginFrame();
    void Clear();

    void SetNumber(const FieldPlan& field, const double* values);
    void SetInteger64(const FieldPlan& field, const std::uint64_t* values);
    void SetText(const FieldPlan& field, std::string_view utf8);
    void SetBytes(const FieldPlan& field, std::span<const std::uint8_t> data);

    // Whether an owner field (by FieldPlan::index) was received for the current controlled entity.
    bool Present(int field) const { return present_[static_cast<std::size_t>(field)] != 0; }
    std::span<const double> Numbers(int field) const;
    // A 64-bit integer owner field's components (W32), as bit patterns; empty for another kind.
    std::span<const std::uint64_t> Integers(int field) const;
    std::string_view Text(int field) const;
    std::span<const std::uint8_t> Bytes(int field) const;
    // An owner collection field's value (W34); null for another kind and before the first list of the current controlled entity.
    const CollectionValue* Collection(int field) const;
    // The decoder's write path: the owner collection a list is decoded into.
    CollectionValue& CollectionFor(const FieldPlan& field);

    // The first number of an owner field by name, or NaN when never received.
    double Number(std::string_view name, int component = 0) const;

private:
    void Shape(const ArchetypePlan* owner);

    Vec<Vec<double>> numbers_;
    Vec<Vec<std::uint64_t>> integers_;
    Vec<Vec<std::uint8_t>> values_;
    Vec<CollectionValue> collections_;
    Vec<std::uint8_t> present_;
};

// One event, decoded into buffers preallocated for its type; the same record is reused for every event of the type, so a handler that
// keeps values copies them.
class EventRecord {
public:
    explicit EventRecord(const MessagePlan& type);

    const MessagePlan& Type() const { return *type_; }
    // The tick of the frame that carried it.
    std::uint32_t tick = 0;

    // A numeric field's components, or a list's flattened elements (Count(i) x components).
    std::span<const double> Numbers(int field) const;
    // A 64-bit integer field's components (W32), as bit patterns; empty for another kind.
    std::span<const std::uint64_t> Integers(int field) const;
    // A list's element count, or a bytes field's length.
    int Count(int field) const { return counts_[static_cast<std::size_t>(field)]; }
    std::string_view Text(int field) const;
    std::span<const std::uint8_t> Bytes(int field) const;

    int FieldIndex(std::string_view name) const;
    // A numeric field's component by name, or NaN. Resolve FieldIndex once in a hot handler.
    double Number(std::string_view name, int component = 0) const;

    void SetNumber(const FieldPlan& field, const double* values);
    void SetInteger64(const FieldPlan& field, const std::uint64_t* values);
    void SetList(const FieldPlan& field, int count, const double* values);
    void SetText(const FieldPlan& field, std::string_view utf8);
    void SetBytes(const FieldPlan& field, std::span<const std::uint8_t> data);

private:
    const MessagePlan* type_;
    Vec<int> offsets_;
    Vec<double> numbers_;
    // 64-bit integer fields' components, at the same offsets as numbers_.
    Vec<std::uint64_t> integers_;
    Vec<int> counts_;
    // Per field: text or bytes, grown to the largest value received.
    Vec<Vec<std::uint8_t>> values_;
};

// The latest metric values (STATS, W25), flattened in catalog order at MetricPlan::offset.
class StatsState {
public:
    explicit StatsState(const CatalogPlan& plan);

    std::span<const double> Values() const { return {values_.data(), values_.size()}; }
    double ValueOf(const MetricPlan& metric, int index = 0) const { return values_[static_cast<std::size_t>(metric.offset + index)]; }
    // A metric's value by name and label index, or NaN.
    double Value(std::string_view name, int index = 0) const;
    void Set(const MetricPlan& metric, int index, double value) { values_[static_cast<std::size_t>(metric.offset + index)] = value; }

    // The tick of the latest STATS block, or -1.
    std::int64_t tick = -1;
    // Whether the current frame carried a STATS block.
    bool received = false;

private:
    const CatalogPlan* plan_;
    Vec<double> values_;
};

// This frame's command rejections (ACKS).
struct AckEntry {
    std::uint32_t seq;
    std::uint8_t reason;
};

// This frame's source lifecycle entries (SOURCES).
struct SourceEntry {
    std::uint32_t requestId;
    std::uint8_t status;
    std::uint32_t code;
};

}  // namespace typhon::client
