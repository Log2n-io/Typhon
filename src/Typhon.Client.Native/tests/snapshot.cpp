#include "snapshot.hpp"

#include <algorithm>
#include <cstring>

#include "store/schema.hpp"
#include "wire/codec_kinds.hpp"

namespace typhon::test {

using namespace typhon::client;

Value Num(double v) { return Value::MakeNumber(v); }

Value Str(std::string s) { return Value::MakeString(std::move(s)); }

Value BitsOf(std::span<const double> values)
{
    std::vector<Value> items;
    for (const double v : values)
    {
        items.push_back(Str(Bits(v)));
    }

    return Value::MakeArray(std::move(items));
}

Value HexOf(std::string_view text) { return Str(Hex({reinterpret_cast<const std::uint8_t*>(text.data()), text.size()})); }

// A TCP stream's messages: `u32 len` little-endian, excluding itself (03 § 10, W31).
std::vector<std::vector<std::uint8_t>> Unframe(const std::vector<std::uint8_t>& stream)
{
    std::vector<std::vector<std::uint8_t>> messages;
    for (std::size_t at = 0; at + 4 <= stream.size();)
    {
        std::uint32_t length;
        std::memcpy(&length, stream.data() + at, 4);
        messages.emplace_back(stream.begin() + static_cast<std::ptrdiff_t>(at + 4), stream.begin() + static_cast<std::ptrdiff_t>(at + 4 + length));
        at += 4 + length;
    }

    return messages;
}

// An event as dispatched; an entityRef also records whether the store resolves it then (the apply order, observable).
Value RenderEvent(const EventRecord& event, const FrameApplier& applier)
{
    Value fields = Value::MakeObject();
    Value known;
    for (const FieldPlan* f : event.Type().body->fields)
    {
        switch (f->valueKind)
        {
            case ValueKind::Number:
                fields.Set(f->name, BitsOf(event.Numbers(f->index)));
                if (f->kind == CodecKind::EntityRef)
                {
                    if (known.IsNull())
                    {
                        known = Value::MakeObject();
                    }

                    const auto netId = static_cast<std::uint32_t>(event.Numbers(f->index)[0]);
                    known.Set(f->name, Value::MakeBool(applier.World().Locate(netId) != NotFound));
                }

                break;
            case ValueKind::List:
                fields.Set(f->name, Value::MakeObject({{"count", Num(event.Count(f->index))}, {"values", BitsOf(event.Numbers(f->index))}}));
                break;
            case ValueKind::Text:
                fields.Set(f->name, HexOf(event.Text(f->index)));
                break;
            case ValueKind::Bytes:
                fields.Set(f->name, Str(Hex(event.Bytes(f->index))));
                break;
            default:
                break;
        }
    }

    Value entry = Value::MakeObject({{"type", Str(event.Type().name)}, {"fields", std::move(fields)}});
    if (!known.IsNull())
    {
        entry.Set("known", std::move(known));
    }

    return entry;
}

static std::vector<Value> SortedNumbers(std::vector<std::uint32_t> values)
{
    std::sort(values.begin(), values.end());
    std::vector<Value> items;
    for (const std::uint32_t v : values)
    {
        items.push_back(Num(v));
    }

    return items;
}

Value Render(const FrameApplier& applier, std::vector<Value> events)
{
    const CatalogPlan& plan = applier.Plan();
    Value archetypes = Value::MakeObject();
    for (const auto& a : plan.Archetypes())
    {
        const ArchetypeStore& store = applier.World().Archetype(static_cast<std::size_t>(a->idx));
        std::vector<std::pair<std::uint32_t, Value>> entities;
        for (const std::uint32_t slot : store.Live())
        {
            Value fields = Value::MakeObject();
            for (const auto& f : a->fields)
            {
                const int index = store.FieldIndex(f->name);
                if (index < 0)
                {
                    continue;
                }

                if (f->valueKind == ValueKind::Number)
                {
                    std::vector<double> values;
                    for (int c = 0; c < f->components; c++)
                    {
                        values.push_back(store.NumberAt(index, slot, c));
                    }

                    fields.Set(f->name, BitsOf(values));
                }
                else if (f->valueKind == ValueKind::Text)
                {
                    fields.Set(f->name, HexOf(store.TextAt(index, slot)));
                }
                else if (f->valueKind == ValueKind::Bytes)
                {
                    fields.Set(f->name, Str(Hex(store.BytesAt(index, slot))));
                }
            }

            const std::uint32_t netId = store.NetIds()[slot];
            Value entity = Value::MakeObject({{"netId", Num(netId)}});
            if (store.Dims() > 0)
            {
                const int head = store.HeadEntry(slot);
                const double* segment = store.SegmentOf(slot, head);
                const auto dims = static_cast<std::size_t>(store.Dims());
                entity.Set("position", BitsOf({segment, dims}));
                entity.Set("velocity", BitsOf({segment + dims, dims}));
                entity.Set("t0", Num(store.T0Of(slot, head)));
                entity.Set("epoch", Num(store.EpochOf(slot, head)));
            }

            entity.Set("fields", std::move(fields));
            entities.emplace_back(netId, std::move(entity));
        }

        std::sort(entities.begin(), entities.end(), [](const auto& x, const auto& y) { return x.first < y.first; });
        std::vector<Value> entityItems;
        for (auto& e : entities)
        {
            entityItems.push_back(std::move(e.second));
        }

        std::vector<std::uint32_t> entered;
        for (const std::uint32_t slot : store.Entered())
        {
            if (store.IsLive(slot))
            {
                entered.push_back(store.NetIds()[slot]);
            }
        }

        std::vector<std::uint32_t> updatedSlots;
        for (const std::uint32_t slot : store.Updated())
        {
            if (store.IsLive(slot))
            {
                updatedSlots.push_back(slot);
            }
        }

        std::sort(updatedSlots.begin(), updatedSlots.end(), [&](std::uint32_t x, std::uint32_t y) { return store.NetIds()[x] < store.NetIds()[y]; });
        std::vector<Value> updated;
        for (const std::uint32_t slot : updatedSlots)
        {
            const std::uint32_t mask = store.UpdateMask(slot);
            updated.push_back(Value::MakeObject({{"netId", Num(store.NetIds()[slot])},
                                                 {"groups", Num(mask & 0xFF)},
                                                 {"moved", Value::MakeBool((mask & MotionChangeBit) != 0)}}));
        }

        const auto left = store.Left();
        archetypes.Set(a->name, Value::MakeObject({{"entities", Value::MakeArray(std::move(entityItems))},
                                                   {"entered", Value::MakeArray(SortedNumbers(entered))},
                                                   {"updated", Value::MakeArray(std::move(updated))},
                                                   {"left", Value::MakeArray(SortedNumbers({left.begin(), left.end()}))}}));
    }

    const SelfState& self = applier.Self();
    Value selfJson;
    if (self.archetype != nullptr)
    {
        Value fields = Value::MakeObject();
        for (const auto& f : self.archetype->ownerFields)
        {
            if (!self.Present(f->index))
            {
                continue;
            }

            if (f->valueKind == ValueKind::Number)
            {
                fields.Set(f->name, BitsOf(self.Numbers(f->index)));
            }
            else if (f->valueKind == ValueKind::Text)
            {
                fields.Set(f->name, HexOf(self.Text(f->index)));
            }
            else if (f->valueKind == ValueKind::Bytes)
            {
                fields.Set(f->name, Str(Hex(self.Bytes(f->index))));
            }
        }

        selfJson = Value::MakeObject({{"archetype", Str(self.archetype->name)},
                                      {"netId", Num(self.netId)},
                                      {"lastSeq", Num(self.lastSeq)},
                                      {"received", Value::MakeBool(self.received)},
                                      {"ownerMask", Num(self.ownerMask)},
                                      {"fields", std::move(fields)}});
    }

    std::vector<Value> acks;
    for (const AckEntry& ack : applier.Acks())
    {
        acks.push_back(Value::MakeObject({{"seq", Num(ack.seq)}, {"reason", Num(ack.reason)}}));
    }

    std::vector<Value> sources;
    for (const SourceEntry& s : applier.Sources())
    {
        sources.push_back(Value::MakeObject({{"requestId", Num(s.requestId)}, {"status", Num(s.status)}, {"code", Num(s.code)}}));
    }

    std::vector<Value> aggregates;
    for (const auto& g : applier.Grids())
    {
        std::vector<Value> cells;
        const auto counts = g->Counts();
        for (std::uint32_t cell = 0; cell < g->CellCount() && !counts.empty(); cell++)
        {
            const auto row = counts.subspan(static_cast<std::size_t>(cell) * g->ArchetypeCount(), g->ArchetypeCount());
            if (std::any_of(row.begin(), row.end(), [](std::uint32_t c) { return c != 0; }))
            {
                std::vector<Value> values;
                for (const std::uint32_t c : row)
                {
                    values.push_back(Num(c));
                }

                cells.push_back(Value::MakeObject({{"cell", Num(cell)}, {"counts", Value::MakeArray(std::move(values))}}));
            }
        }

        const auto changed = g->Changed();
        aggregates.push_back(Value::MakeObject({{"grid", Num(g->Index())},
                                                {"cells", Value::MakeArray(std::move(cells))},
                                                {"changed", Value::MakeArray(SortedNumbers({changed.begin(), changed.end()}))}}));
    }

    Value metrics = Value::MakeObject();
    for (const auto& m : plan.Metrics())
    {
        metrics.Set(m->name, BitsOf(applier.Stats().Values().subspan(static_cast<std::size_t>(m->offset), static_cast<std::size_t>(m->valueCount))));
    }

    return Value::MakeObject({{"tick", Num(static_cast<double>(applier.Tick()))},
                              {"flags", Num(applier.Flags())},
                              {"periodUs", Num(applier.PeriodUs())},
                              {"archetypes", std::move(archetypes)},
                              {"events", Value::MakeArray(std::move(events))},
                              {"self", std::move(selfJson)},
                              {"acks", Value::MakeArray(std::move(acks))},
                              {"sources", Value::MakeArray(std::move(sources))},
                              {"aggregates", Value::MakeArray(std::move(aggregates))},
                              {"metrics", std::move(metrics)},
                              {"anomalies", Num(static_cast<double>(applier.World().anomalies))}});
}

}  // namespace typhon::test
