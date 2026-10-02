#pragma once

#include <cstdint>
#include <memory>
#include <span>
#include <vector>

#include "catalog/catalog_plan.hpp"
#include "wire/field_codec.hpp"
#include "wire/reader.hpp"

namespace typhon::client {

class RealmFrame;

// Receives a decoded TICK, block by block, in stream order (C# ITickSink, TS TickSink). Field values arrive through the FieldSink
// members between the record call that opened them and the next record call. Every pointer and view is valid only during the call.
class TickSink : public virtual FieldSink {
public:
    // A frame begins. `periodUs` is the elapsed interval's period when TickFlags::Period is set, otherwise 0.
    virtual void BeginTick(std::uint32_t tick, std::uint8_t flags, std::uint32_t periodUs) = 0;

    // A REALM block (typhon.3): the session's frame from here on, or null for REALM(NONE). Delivered after the reader adopted it.
    virtual void Realm(const RealmFrame* frame) = 0;

    // An ENTITIES block begins; the records that follow belong to `archetype`.
    virtual void BeginEntities(const ArchetypePlan& archetype) = 0;

    // An enter record: its position, then every public field in wire order through the field members. `position` holds the
    // archetype's position.dims values (none when it is not spatial), `velocity` as many for the linear model and none otherwise;
    // `t0` and `epoch` are 0 unless it moves.
    virtual void Enter(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
                       std::uint8_t epoch) = 0;

    // A motion segment starting at tick t0; `velocity` is empty for the none model.
    virtual void Segment(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
                         std::uint8_t epoch) = 0;

    // A state record: the fields of the groups in `groupMask` follow, ascending.
    virtual void State(std::uint32_t netId, std::uint8_t groupMask) = 0;

    // A leave. A store applies it last in the frame, so an event can still resolve the entity.
    virtual void Leave(std::uint32_t netId) = 0;

    // An event: its fields follow.
    virtual void Event(const MessagePlan& type) = 0;

    // The SELF block: the owner groups in `ownerMask` follow. `archetype` is null and `netId` 0 when the session controls no entity.
    virtual void Self(const ArchetypePlan* archetype, std::uint32_t netId, std::uint32_t lastSeq, std::uint8_t ownerMask) = 0;

    virtual void Ack(std::uint32_t seq, std::uint8_t reason) = 0;
    virtual void Source(std::uint32_t requestId, std::uint8_t status, std::uint32_t code) = 0;
    virtual void BeginAggregate(const GridPlan& grid, bool reset) = 0;
    virtual void AggregateCell(std::uint32_t cell, std::span<const std::uint32_t> counts) = 0;
    virtual void Metric(const MetricPlan& metric, int valueIndex, double value) = 0;
    virtual void Debug(std::uint8_t subType, std::span<const std::uint8_t> payload) = 0;
    virtual void Ext(std::uint32_t appTypeId, std::span<const std::uint8_t> payload) = 0;
    virtual void UnknownBlock(std::uint8_t blockType) = 0;
    virtual void EndTick() = 0;
};

// Block selection for TickReader::Read: one bit per block type.
namespace BlockMask {
inline constexpr std::uint32_t Entities = 1u << 1;
inline constexpr std::uint32_t Events = 1u << 2;
inline constexpr std::uint32_t Self = 1u << 3;
inline constexpr std::uint32_t Agg = 1u << 4;
inline constexpr std::uint32_t Stats = 1u << 5;
inline constexpr std::uint32_t Debug = 1u << 6;
inline constexpr std::uint32_t Acks = 1u << 7;
inline constexpr std::uint32_t Sources = 1u << 8;
inline constexpr std::uint32_t Realm = 1u << 9;
inline constexpr std::uint32_t Ext = 1u << 10;
inline constexpr std::uint32_t Unknown = 1u << 11;
inline constexpr std::uint32_t All = 0xFFE;
}  // namespace BlockMask

// Decodes TICK messages against one catalog plan: the header, then every block, each isolated by its declared length so it can neither
// read past its end nor leave bytes unread. Reusable and allocation-free per record. Not re-entrant: a sink must not start another
// read from its callbacks.
class TickReader {
public:
    explicit TickReader(std::shared_ptr<const CatalogPlan> plan);

    const CatalogPlan& Plan() const { return *plan_; }

    // The session's realm frame (typhon.3, SUB-30), held across messages: null before the first REALM and after a REALM(NONE).
    std::shared_ptr<const RealmFrame> realm;

    // Decodes one TICK message, type byte included. `blocks` selects which block types reach the sink; the others are skipped by
    // their length, unvalidated — except a REALM, which every pass adopts.
    void Read(std::span<const std::uint8_t> message, TickSink& sink, std::uint32_t blocks = BlockMask::All);

private:
    void ReadMessage(std::span<const std::uint8_t> message, TickSink& sink, std::uint32_t blocks);
    std::shared_ptr<const RealmFrame> ReadRealm();
    void ReadEntities(std::uint32_t tick, TickSink& sink);
    void ReadSelf(std::uint32_t tick, TickSink& sink);
    void ReadAggregate(TickSink& sink);
    void ReadMetrics(const std::vector<MetricPlan*>& metrics, std::uint32_t tick, TickSink& sink);

    std::shared_ptr<const CatalogPlan> plan_;
    WireReader r_;
    std::vector<std::uint32_t> entitiesSeenAt_;
    std::uint32_t reads_ = 0;
};

}  // namespace typhon::client
