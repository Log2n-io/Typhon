#include "apply/frame_applier.hpp"

#include "clock/clock.hpp"
#include "store/schema.hpp"
#include "wire/constants.hpp"
#include "wire/realm_frame.hpp"

namespace typhon::client {

namespace {

WorldStoreOptions StoreOptions(const FrameApplierOptions& options)
{
    WorldStoreOptions store;
    store.maxNetId = options.maxNetId;
    // The ring follows the clock that sets render time, unless told otherwise.
    if (options.maxRenderDelayMs.has_value())
    {
        store.maxRenderDelayMs = *options.maxRenderDelayMs;
    }
    else if (options.clock != nullptr)
    {
        store.maxRenderDelayMs = options.clock->MaxDelayMs();
    }

    return store;
}

}  // namespace

// The second pass: reads only EVENTS blocks, fills each type's reused record and dispatches it once complete.
class FrameApplier::EventPass final : public TickSink {
public:
    EventPass(const CatalogPlan& plan, const std::function<void(const EventRecord&)>& handler) : handler_(handler)
    {
        for (const CatalogEvent& e : plan.GetCatalog().events)
        {
            const auto idx = static_cast<std::size_t>(e.idx);
            if (records_.size() <= idx)
            {
                records_.resize(idx + 1);
            }

            records_[idx] = std::make_unique<EventRecord>(plan.Event(static_cast<std::uint32_t>(e.idx)));
        }
    }

    std::uint32_t tick = 0;

    void Event(const MessagePlan& type) override
    {
        Flush();
        EventRecord* record = records_[static_cast<std::size_t>(type.idx)].get();
        record->tick = tick;
        pending_ = record;
    }

    void EndTick() override { Flush(); }
    void BeginTick(std::uint32_t, std::uint8_t, std::uint32_t) override { pending_ = nullptr; }

    void Number(const FieldPlan& field, const double* values) override
    {
        if (pending_ != nullptr)
        {
            pending_->SetNumber(field, values);
        }
    }

    void Integer64(const FieldPlan& field, const std::uint64_t* values) override
    {
        if (pending_ != nullptr)
        {
            pending_->SetInteger64(field, values);
        }
    }

    void Text(const FieldPlan& field, std::string_view utf8) override
    {
        if (pending_ != nullptr)
        {
            pending_->SetText(field, utf8);
        }
    }

    void Bytes(const FieldPlan& field, std::span<const std::uint8_t> data) override
    {
        if (pending_ != nullptr)
        {
            pending_->SetBytes(field, data);
        }
    }

    void List(const FieldPlan& field, int count, const double* values) override
    {
        if (pending_ != nullptr)
        {
            pending_->SetList(field, count, values);
        }
    }

    // The first pass applied the REALM; the reader already holds it for this pass's positions.
    void Realm(const RealmFrame*) override {}
    void BeginEntities(const ArchetypePlan&) override {}
    void Enter(std::uint32_t, std::span<const double>, std::span<const double>, std::uint32_t, std::uint8_t) override {}
    void Segment(std::uint32_t, std::span<const double>, std::span<const double>, std::uint32_t, std::uint8_t) override {}
    void State(std::uint32_t, std::uint8_t) override {}
    void Leave(std::uint32_t) override {}
    void Self(const ArchetypePlan*, std::uint32_t, std::uint32_t, std::uint8_t) override {}
    void Ack(std::uint32_t, std::uint8_t) override {}
    void Source(std::uint32_t, std::uint8_t, std::uint32_t) override {}
    void BeginAggregate(const GridPlan&, bool) override {}
    void AggregateCell(std::uint32_t, std::span<const std::uint32_t>) override {}
    void Metric(const MetricPlan&, int, double) override {}
    void Debug(std::uint8_t, std::span<const std::uint8_t>) override {}
    void Ext(std::uint32_t, std::span<const std::uint8_t>) override {}
    void UnknownBlock(std::uint8_t) override {}

private:
    void Flush()
    {
        EventRecord* record = pending_;
        pending_ = nullptr;
        if (record != nullptr && handler_)
        {
            handler_(*record);
        }
    }

    const std::function<void(const EventRecord&)>& handler_;
    std::vector<std::unique_ptr<EventRecord>> records_;
    EventRecord* pending_ = nullptr;
};

FrameApplier::FrameApplier(std::shared_ptr<const CatalogPlan> plan, FrameApplierOptions options)
    : plan_(std::move(plan)),
      options_(std::move(options)),
      world_(WorldSchemaFromCatalog(*plan_), StoreOptions(options_)),
      stats_(*plan_),
      reader_(plan_)
{
    for (const auto& a : plan_->Archetypes())
    {
        const ArchetypeStore& store = world_.Archetype(static_cast<std::size_t>(a->idx));
        std::vector<int> map;
        for (const auto& f : a->fields)
        {
            map.push_back(store.FieldIndex(f->name));
        }

        storeFieldOf_.push_back(std::move(map));
    }

    // Placeholders until the first REALM lays each grid over its realm (typhon.3): an AGG before one is refused by the reader.
    for (const auto& g : plan_->Grids())
    {
        grids_.push_back(std::make_unique<AggregateGrid>(GridSchemaFromCatalog(*g->grid, nullptr)));
    }

    eventPass_ = std::make_unique<EventPass>(*plan_, options_.onEvent);
    leaves_.reserve(256);
    leaveArchetypes_.reserve(256);
    if (options_.initialRealm != nullptr)
    {
        reader_.realm = options_.initialRealm;
        heldRealm_ = options_.initialRealm;
        for (std::size_t i = 0; i < grids_.size(); i++)
        {
            grids_[i]->Reframe(GridSchemaFromCatalog(*plan_->Grids()[i]->grid, options_.initialRealm.get()));
        }
    }
}

FrameApplier::~FrameApplier() = default;

void FrameApplier::Apply(std::span<const std::uint8_t> message, std::optional<double> recvMs)
{
    reader_.Read(message, *this, BlockMask::All & ~BlockMask::Events);
    eventPass_->tick = static_cast<std::uint32_t>(tick_);
    reader_.Read(message, *eventPass_, BlockMask::Events);

    for (std::size_t i = 0; i < leaves_.size(); i++)
    {
        world_.Leave(leaves_[i], leaveArchetypes_[i]);
    }

    leaves_.clear();
    leaveArchetypes_.clear();
    world_.EndFrame();

    Clock* clock = options_.clock;
    if (clock != nullptr)
    {
        // A RESET frame going back in time is a restarted server: the clock's timeline belongs to the old one.
        if ((flags_ & TickFlags::Reset) != 0 && tick_ < clock->LatestTick())
        {
            clock->Reset();
        }

        if (periodUs_ != 0)
        {
            // PERIOD in frame N + 1 is the duration of [N, N + 1] (§ 3).
            clock->OnPeriodChange(tick_ - 1, periodUs_ / 1000.0);
        }

        if (recvMs.has_value())
        {
            clock->OnFrame(tick_, *recvMs);
        }
    }
}

void FrameApplier::BeginTick(std::uint32_t tick, std::uint8_t flags, std::uint32_t periodUs)
{
    tick_ = tick;
    flags_ = flags;
    periodUs_ = periodUs;
    target_ = Target::None;
    leaves_.clear();
    leaveArchetypes_.clear();
    acks_.clear();
    sources_.clear();
    stats_.received = false;
    self_.BeginFrame();
    const bool reset = (flags & TickFlags::Reset) != 0;
    world_.BeginFrame(tick, reset);
    for (const auto& grid : grids_)
    {
        grid->BeginFrame();
    }

    if (reset)
    {
        world_.Reset();
        for (const auto& grid : grids_)
        {
            grid->Reset();
        }

        self_.Clear();

        // After the clear and before any record applies, so a consumer that drops its netIds here is never handed one from the refill.
        if (options_.onReset)
        {
            options_.onReset();
        }
    }
}

void FrameApplier::Realm(const RealmFrame* frame)
{
    target_ = Target::None;
    std::shared_ptr<const RealmFrame> previous = std::move(heldRealm_);
    heldRealm_ = reader_.realm;
    for (std::size_t i = 0; i < grids_.size(); i++)
    {
        grids_[i]->Reframe(GridSchemaFromCatalog(*plan_->Grids()[i]->grid, frame));
    }

    const bool changed = frame == nullptr ? previous != nullptr : previous == nullptr || !frame->Equals(*previous);
    if (changed && options_.onRealmChanged)
    {
        options_.onRealmChanged(previous.get(), frame);
    }
}

void FrameApplier::BeginEntities(const ArchetypePlan& archetype)
{
    archetype_ = static_cast<std::uint32_t>(archetype.idx);
    store_ = &world_.Archetype(archetype_);
    target_ = Target::None;
    collection_ = nullptr;
}

void FrameApplier::Enter(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
                         std::uint8_t epoch)
{
    target_ = Target::None;
    if (netId == 0 || netId > world_.MaxNetId())
    {
        world_.anomalies++;
        return;
    }

    slot_ = world_.Enter(archetype_, netId);
    if (store_->HasPosition())
    {
        store_->ResetMotion(slot_, position, store_->Linear() ? velocity : std::span<const double>{}, t0, epoch);
    }

    target_ = Target::Entity;
}

void FrameApplier::Segment(std::uint32_t netId, std::span<const double> position, std::span<const double> velocity, std::uint32_t t0,
                           std::uint8_t epoch)
{
    target_ = Target::None;
    const std::uint32_t location = world_.Locate(netId);
    if (location == NotFound || ArchetypeOf(location) != archetype_)
    {
        world_.anomalies++;
        return;
    }

    store_->PushSegment(SlotOf(location), position, store_->Linear() ? velocity : std::span<const double>{}, t0, epoch);
}

void FrameApplier::State(std::uint32_t netId, std::uint8_t groupMask)
{
    target_ = Target::None;
    const std::uint32_t location = world_.Locate(netId);
    if (location == NotFound || ArchetypeOf(location) != archetype_)
    {
        world_.anomalies++;
        return;
    }

    slot_ = SlotOf(location);
    store_->MarkUpdated(slot_, groupMask);
    target_ = Target::Entity;
}

void FrameApplier::Leave(std::uint32_t netId)
{
    target_ = Target::None;
    leaves_.push_back(netId);
    leaveArchetypes_.push_back(static_cast<std::uint8_t>(archetype_));
}

void FrameApplier::Event(const MessagePlan&)
{
    // Events are read by the second pass (EventPass); the first pass never selects their blocks.
}

void FrameApplier::Self(const ArchetypePlan* archetype, std::uint32_t netId, std::uint32_t lastSeq, std::uint8_t ownerMask)
{
    self_.Receive(archetype, netId, lastSeq, ownerMask);
    target_ = Target::Owner;
}

void FrameApplier::Ack(std::uint32_t seq, std::uint8_t reason) { acks_.push_back({seq, reason}); }

void FrameApplier::Source(std::uint32_t requestId, std::uint8_t status, std::uint32_t code) { sources_.push_back({requestId, status, code}); }

void FrameApplier::BeginAggregate(const GridPlan& grid, bool reset)
{
    target_ = Target::None;
    grid_ = grids_[static_cast<std::size_t>(grid.idx)].get();
    if (reset)
    {
        grid_->Reset();
    }
}

void FrameApplier::AggregateCell(std::uint32_t cell, std::span<const std::uint32_t> counts) { grid_->SetCell(cell, counts); }

void FrameApplier::Metric(const MetricPlan& metric, int valueIndex, double value)
{
    stats_.Set(metric, valueIndex, value);
    stats_.tick = tick_;
    stats_.received = true;
}

void FrameApplier::Debug(std::uint8_t subType, std::span<const std::uint8_t> payload)
{
    target_ = Target::None;
    if (options_.onDebug)
    {
        options_.onDebug(subType, payload);
    }
}

void FrameApplier::Ext(std::uint32_t appTypeId, std::span<const std::uint8_t> payload)
{
    target_ = Target::None;
    if (options_.onExt)
    {
        options_.onExt(appTypeId, payload);
    }
}

void FrameApplier::UnknownBlock(std::uint8_t) { target_ = Target::None; }

void FrameApplier::EndTick()
{
    target_ = Target::None;
    collection_ = nullptr;
}

void FrameApplier::Number(const FieldPlan& field, const double* values)
{
    if (field.parent != nullptr)
    {
        if (collection_ != nullptr)
        {
            collection_->SetNumbers(field, element_, values);
        }
    }
    else if (target_ == Target::Entity)
    {
        const int index = StoreField(field);
        if (index >= 0)
        {
            store_->SetNumbers(index, slot_, values);
        }
    }
    else if (target_ == Target::Owner)
    {
        self_.SetNumber(field, values);
    }
}

void FrameApplier::Integer64(const FieldPlan& field, const std::uint64_t* values)
{
    if (field.parent != nullptr)
    {
        if (collection_ != nullptr)
        {
            collection_->SetIntegers(field, element_, values);
        }
    }
    else if (target_ == Target::Entity)
    {
        const int index = StoreField(field);
        if (index >= 0)
        {
            store_->SetIntegers(index, slot_, values);
        }
    }
    else if (target_ == Target::Owner)
    {
        self_.SetInteger64(field, values);
    }
}

void FrameApplier::Text(const FieldPlan& field, std::string_view utf8)
{
    if (field.parent != nullptr)
    {
        if (collection_ != nullptr)
        {
            collection_->SetText(field, element_, utf8);
        }
    }
    else if (target_ == Target::Entity)
    {
        const int index = StoreField(field);
        if (index >= 0)
        {
            store_->SetText(index, slot_, utf8);
        }
    }
    else if (target_ == Target::Owner)
    {
        self_.SetText(field, utf8);
    }
}

void FrameApplier::Bytes(const FieldPlan& field, std::span<const std::uint8_t> data)
{
    if (field.parent != nullptr)
    {
        // A plan refuses bytes in a collection's element; never routed by the element's index into the record's fields.
        return;
    }

    if (target_ == Target::Entity)
    {
        const int index = StoreField(field);
        if (index >= 0)
        {
            store_->SetBytes(index, slot_, data);
        }
    }
    else if (target_ == Target::Owner)
    {
        self_.SetBytes(field, data);
    }
}

void FrameApplier::List(const FieldPlan&, int, const double*)
{
    // Lists are event and command fields only: the catalog refuses them on archetypes and owner sections.
}

void FrameApplier::Collection(const FieldPlan& field, int total, int sent)
{
    // An entity's or the controlled entity's collection, overwritten whole into the columns it already has.
    collection_ = nullptr;
    if (target_ == Target::Entity)
    {
        const int index = StoreField(field);
        collection_ = index >= 0 ? &store_->CollectionFor(index, slot_, field) : nullptr;
    }
    else if (target_ == Target::Owner)
    {
        collection_ = &self_.CollectionFor(field);
    }

    if (collection_ != nullptr)
    {
        collection_->Begin(static_cast<std::uint32_t>(total), static_cast<std::uint32_t>(sent));
    }
}

void FrameApplier::CollectionElement(const FieldPlan&, int index) { element_ = static_cast<std::uint32_t>(index); }

}  // namespace typhon::client
