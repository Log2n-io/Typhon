#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <span>
#include <vector>

#include "aggregates/aggregate_grid.hpp"
#include "apply/frame_state.hpp"
#include "store/memory.hpp"
#include "store/world_store.hpp"
#include "wire/tick_reader.hpp"

namespace typhon::client {

class Clock;

struct FrameApplierOptions {
    // Largest netId the store accepts; an enter beyond it is counted as an anomaly.
    std::uint32_t maxNetId = 1u << 22;
    // The largest render delay motion is evaluated at, sizing the segment rings. Default: the clock's maxDelayMs, else 300 ms.
    std::optional<double> maxRenderDelayMs;
    // Receives PERIOD changes, and each frame's receive time when Apply is given one. Not owned; must outlive the applier.
    Clock* clock = nullptr;
    // Called once per event, after every other block of its frame applied and before its leaves, with a record reused for every event
    // of its type. A throw propagates out of Apply: the store then holds a partial frame and the session must be closed.
    std::function<void(const EventRecord&)> onEvent;
    // Called per DEBUG sub-block and per EXT block, with a view valid only during the call.
    std::function<void(std::uint8_t subType, std::span<const std::uint8_t> payload)> onDebug;
    std::function<void(std::uint32_t appTypeId, std::span<const std::uint8_t> payload)> onExt;
    // Called when a REALM block changes the session's realm, after the RESET that carried it cleared the store and before any of the
    // frame's records apply.
    std::function<void(const RealmFrame* previous, const RealmFrame* current)> onRealmChanged;
    // Called when a frame carries RESET, after the store, the grids and SELF are cleared: every netId the consumer holds is invalid.
    std::function<void()> onReset;
    // The realm the session is already in, for an applier that starts mid-stream (a replay, a test).
    std::shared_ptr<const RealmFrame> initialRealm;
};

// Decodes TICK messages straight into a WorldStore (the TypeScript SDK's FrameApplier): one copy, from the wire into the typed columns.
//
// Apply order (03 § 10): every block but EVENTS, then EVENTS, then leaves, whatever order the blocks travel in — a first pass applies
// everything but events and collects leaves, a second reads only the EVENTS blocks, then the leaves apply. An event handler sees the
// whole frame and still resolves every entity that leaves in it.
//
// Inconsistencies are absorbed and counted in the world's anomalies (§ 10). A malformed message throws WireFormatError; the store may
// then hold part of the frame, and the connection must be closed with the error's code.
class FrameApplier final : public TickSink {
public:
    FrameApplier(std::shared_ptr<const CatalogPlan> plan, FrameApplierOptions options = {});
    ~FrameApplier() override;

    FrameApplier(const FrameApplier&) = delete;
    FrameApplier& operator=(const FrameApplier&) = delete;

    const CatalogPlan& Plan() const { return *plan_; }
    WorldStore& World() { return world_; }
    const WorldStore& World() const { return world_; }
    const std::vector<std::unique_ptr<AggregateGrid>>& Grids() const { return grids_; }
    const SelfState& Self() const { return self_; }
    std::span<const AckEntry> Acks() const { return {acks_.data(), acks_.size()}; }
    std::span<const SourceEntry> Sources() const { return {sources_.data(), sources_.size()}; }
    const StatsState& Stats() const { return stats_; }

    // The current frame's tick; -1 before the first.
    std::int64_t Tick() const { return tick_; }
    std::uint8_t Flags() const { return flags_; }
    // The elapsed interval's period in microseconds when the frame carried PERIOD, otherwise 0.
    std::uint32_t PeriodUs() const { return periodUs_; }

    // The session's realm, or null before the first REALM and after a REALM(NONE).
    const RealmFrame* Realm() const { return reader_.realm.get(); }

    // Decodes and applies one TICK message, type byte included. `recvMs`, its local receive time, goes to the clock when one is set.
    void Apply(std::span<const std::uint8_t> message, std::optional<double> recvMs = std::nullopt);

    // TickSink: the first pass.
    void BeginTick(std::uint32_t tick, std::uint8_t flags, std::uint32_t periodUs) override;
    void Realm(const RealmFrame* frame) override;
    void BeginEntities(const ArchetypePlan& archetype) override;
    void Enter(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
               std::uint8_t epoch) override;
    void Segment(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
                 std::uint8_t epoch) override;
    void State(std::uint32_t netId, std::uint8_t groupMask) override;
    void Leave(std::uint32_t netId) override;
    void Event(const MessagePlan& type) override;
    void Self(const ArchetypePlan* archetype, std::uint32_t netId, std::uint32_t lastSeq, std::uint8_t ownerMask) override;
    void Ack(std::uint32_t seq, std::uint8_t reason) override;
    void Source(std::uint32_t requestId, std::uint8_t status, std::uint32_t code) override;
    void BeginAggregate(const GridPlan& grid, bool reset) override;
    void AggregateCell(std::uint32_t cell, std::span<const std::uint32_t> counts) override;
    void Metric(const MetricPlan& metric, int valueIndex, double value) override;
    void Debug(std::uint8_t subType, std::span<const std::uint8_t> payload) override;
    void Ext(std::uint32_t appTypeId, std::span<const std::uint8_t> payload) override;
    void UnknownBlock(std::uint8_t blockType) override;
    void EndTick() override;
    void Number(const FieldPlan& field, const double* values) override;
    void Integer64(const FieldPlan& field, const std::uint64_t* values) override;
    void Text(const FieldPlan& field, std::string_view utf8) override;
    void Bytes(const FieldPlan& field, std::span<const std::uint8_t> data) override;
    void List(const FieldPlan& field, int count, const double* values) override;
    void Collection(const FieldPlan& field, int total, int sent) override;
    void CollectionElement(const FieldPlan& field, int index) override;

private:
    class EventPass;

    enum class Target : std::uint8_t
    {
        None,
        Entity,
        Owner,
    };

    // The store field index of a public field, or -1 when nothing is stored for it.
    int StoreField(const FieldPlan& field) const { return storeFieldOf_[static_cast<std::size_t>(archetype_)][static_cast<std::size_t>(field.index)]; }

    std::shared_ptr<const CatalogPlan> plan_;
    FrameApplierOptions options_;
    WorldStore world_;
    std::vector<std::unique_ptr<AggregateGrid>> grids_;
    SelfState self_;
    Vec<AckEntry> acks_;
    Vec<SourceEntry> sources_;
    StatsState stats_;
    TickReader reader_;
    std::unique_ptr<EventPass> eventPass_;
    // Per archetype: the store field index of each public FieldPlan::index, or -1.
    std::vector<std::vector<int>> storeFieldOf_;

    std::int64_t tick_ = -1;
    std::uint8_t flags_ = 0;
    std::uint32_t periodUs_ = 0;
    Target target_ = Target::None;
    std::uint32_t archetype_ = 0;
    ArchetypeStore* store_ = nullptr;
    std::uint32_t slot_ = 0;
    // The collection being decoded (W34) and its element: an element field's value goes there, whatever the target.
    CollectionValue* collection_ = nullptr;
    std::uint32_t element_ = 0;
    AggregateGrid* grid_ = nullptr;
    // This frame's leaves, applied last: netId and the archetype of the block that carried it.
    Vec<std::uint32_t> leaves_;
    Vec<std::uint8_t> leaveArchetypes_;
    // The realm the last REALM block set, for onRealmChanged.
    std::shared_ptr<const RealmFrame> heldRealm_;
};

}  // namespace typhon::client
