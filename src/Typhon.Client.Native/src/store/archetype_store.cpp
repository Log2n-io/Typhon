#include "store/archetype_store.hpp"

#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace typhon::client {

namespace {

// One past the largest slot: 2^24 - 1 slots, so `archetype << 24 | slot` never reaches the world store's NotFound (all ones).
constexpr std::uint32_t MaxCapacity = (1u << 24) - 1;

NumericColumn MakeColumn(FieldKind kind)
{
    switch (kind)
    {
        case FieldKind::U8:
            return Vec<std::uint8_t>{};
        case FieldKind::I8:
            return Vec<std::int8_t>{};
        case FieldKind::U16:
            return Vec<std::uint16_t>{};
        case FieldKind::I16:
            return Vec<std::int16_t>{};
        case FieldKind::U32:
            return Vec<std::uint32_t>{};
        case FieldKind::I32:
            return Vec<std::int32_t>{};
        case FieldKind::U64:
            return Vec<std::uint64_t>{};
        case FieldKind::I64:
            return Vec<std::int64_t>{};
        case FieldKind::F32:
            return Vec<float>{};
        default:
            return Vec<double>{};
    }
}

}  // namespace

int SegmentHistoryFor(int tickPeriodUs, double maxRenderDelayMs)
{
    const double ticks = std::ceil(maxRenderDelayMs * 1000 / tickPeriodUs) + 2;
    return static_cast<int>(std::min(255.0, std::max(4.0, ticks)));
}

void ByteArena::Set(std::uint32_t slot, std::span<const std::uint8_t> value)
{
    if (value.size() > UINT32_MAX)
    {
        throw std::length_error("a text or bytes value above 4 GiB");
    }

    const auto length = static_cast<std::uint32_t>(value.size());
    if (length > reserved_[slot])
    {
        // The slot's old reservation is dead from here, whether or not the compaction below moves it.
        dead_ += reserved_[slot];
        reserved_[slot] = 0;
        length_[slot] = 0;
        if (data_.size() + length > data_.capacity() && dead_ >= data_.size() / 2)
        {
            Compact();
        }

        // Offsets are 32-bit: an arena past 4 GiB would wrap them and write over other slots.
        if (data_.size() + length > UINT32_MAX)
        {
            throw std::length_error("a text or bytes arena above 4 GiB");
        }

        offset_[slot] = static_cast<std::uint32_t>(data_.size());
        reserved_[slot] = length;
        data_.resize(data_.size() + length);
    }

    if (length > 0)
    {
        std::memcpy(data_.data() + offset_[slot], value.data(), length);
    }

    length_[slot] = length;
}

void ByteArena::Grow(std::size_t slots)
{
    offset_.resize(slots, 0);
    length_.resize(slots, 0);
    reserved_.resize(slots, 0);
}

void ByteArena::Compact()
{
    // Into the spare buffer, then swap: once both have grown to the working size, a compaction allocates nothing.
    spare_.clear();
    for (std::size_t slot = 0; slot < offset_.size(); slot++)
    {
        const std::uint32_t length = length_[slot];
        const std::uint32_t at = static_cast<std::uint32_t>(spare_.size());
        spare_.insert(spare_.end(), data_.begin() + offset_[slot], data_.begin() + offset_[slot] + length);
        offset_[slot] = at;
        reserved_[slot] = length;
    }

    data_.swap(spare_);
    dead_ = 0;
}

ArchetypeStore::ArchetypeStore(ArchetypeSchema schema, int segmentHistory, std::uint32_t initialCapacity) : schema_(std::move(schema))
{
    if (segmentHistory < 1 || segmentHistory > 255)
    {
        throw std::invalid_argument("Archetype '" + schema_.name + "': a ring of " + std::to_string(segmentHistory) +
                                    " segments; 1 to 255 expected");
    }

    if (schema_.position.has_value())
    {
        dims_ = schema_.position->dims;
        moving_ = schema_.position->moving;
        linear_ = moving_ && schema_.position->linear;
    }

    history_ = segmentHistory;
    headOffset_ = 4 * segmentHistory;
    countOffset_ = 4 * segmentHistory + 1;
    epochOffset_ = 4 * segmentHistory + 2;
    segmentsOffset_ = MotionSegmentsOffset(segmentHistory);
    recordBytes_ = dims_ > 0 ? static_cast<std::size_t>(MotionRecordBytes(dims_, segmentHistory)) : 0;
    for (const FieldSchema& field : schema_.fields)
    {
        components_.push_back(IsNumericKind(field.kind) ? field.components : 0);
        numeric_.push_back(MakeColumn(field.kind));
        arenas_.emplace_back();
    }

    Grow(std::max<std::uint32_t>(1, std::min(initialCapacity, MaxCapacity)));
}

int ArchetypeStore::FieldIndex(std::string_view name) const
{
    for (std::size_t i = 0; i < schema_.fields.size(); i++)
    {
        if (schema_.fields[i].name == name)
        {
            return static_cast<int>(i);
        }
    }

    return -1;
}

void ArchetypeStore::RequireNumeric(int field) const
{
    if (field < 0 || static_cast<std::size_t>(field) >= schema_.fields.size() || !IsNumericKind(schema_.fields[static_cast<std::size_t>(field)].kind))
    {
        throw std::logic_error("Archetype '" + schema_.name + "' has no numeric field #" + std::to_string(field));
    }
}

const void* ArchetypeStore::ColumnData(int field) const
{
    RequireNumeric(field);
    return std::visit([](const auto& column) -> const void* { return column.data(); }, numeric_[static_cast<std::size_t>(field)]);
}

double ArchetypeStore::NumberAt(int field, std::uint32_t slot, int component) const
{
    RequireNumeric(field);
    const int components = components_[static_cast<std::size_t>(field)];
    if (slot >= capacity_ || component < 0 || component >= components)
    {
        throw std::out_of_range("slot " + std::to_string(slot) + ", component " + std::to_string(component) + " is beyond the column");
    }

    const std::size_t at = static_cast<std::size_t>(slot) * static_cast<std::size_t>(components) + static_cast<std::size_t>(component);
    return std::visit([at](const auto& column) { return static_cast<double>(column[at]); }, numeric_[static_cast<std::size_t>(field)]);
}

std::uint64_t ArchetypeStore::IntegerAt(int field, std::uint32_t slot, int component) const
{
    RequireNumeric(field);
    const int components = components_[static_cast<std::size_t>(field)];
    if (slot >= capacity_ || component < 0 || component >= components)
    {
        throw std::out_of_range("slot " + std::to_string(slot) + ", component " + std::to_string(component) + " is beyond the column");
    }

    const std::size_t at = static_cast<std::size_t>(slot) * static_cast<std::size_t>(components) + static_cast<std::size_t>(component);
    const NumericColumn& column = numeric_[static_cast<std::size_t>(field)];
    if (const auto* u = std::get_if<Vec<std::uint64_t>>(&column))
    {
        return (*u)[at];
    }

    if (const auto* s = std::get_if<Vec<std::int64_t>>(&column))
    {
        return static_cast<std::uint64_t>((*s)[at]);
    }

    throw std::logic_error("field " + std::to_string(field) + " is not a 64-bit integer column");
}

void ArchetypeStore::SetIntegers(int field, std::uint32_t slot, const std::uint64_t* values)
{
    const int components = components_[static_cast<std::size_t>(field)];
    const std::size_t base = static_cast<std::size_t>(slot) * static_cast<std::size_t>(components);
    NumericColumn& column = numeric_[static_cast<std::size_t>(field)];
    if (auto* u = std::get_if<Vec<std::uint64_t>>(&column))
    {
        for (int i = 0; i < components; i++)
        {
            (*u)[base + static_cast<std::size_t>(i)] = values[i];
        }
    }
    else
    {
        // A two's-complement pattern into a signed column: the conversion is modular since C++20, so it is the value.
        auto& s = std::get<Vec<std::int64_t>>(column);
        for (int i = 0; i < components; i++)
        {
            s[base + static_cast<std::size_t>(i)] = static_cast<std::int64_t>(values[i]);
        }
    }
}

std::string_view ArchetypeStore::TextAt(int field, std::uint32_t slot) const
{
    const auto bytes = arenas_[static_cast<std::size_t>(field)].At(slot);
    return {reinterpret_cast<const char*>(bytes.data()), bytes.size()};
}

std::span<const std::uint8_t> ArchetypeStore::BytesAt(int field, std::uint32_t slot) const
{
    return arenas_[static_cast<std::size_t>(field)].At(slot);
}

void ArchetypeStore::SetNumbers(int field, std::uint32_t slot, const double* values)
{
    const int components = components_[static_cast<std::size_t>(field)];
    const std::size_t base = static_cast<std::size_t>(slot) * components;
    // One conversion per element type: the variant dispatch happens once per value, not once per component.
    std::visit(
        [&](auto& column)
        {
            using T = typename std::remove_reference_t<decltype(column)>::value_type;
            for (int i = 0; i < components; i++)
            {
                column[base + i] = static_cast<T>(values[i]);
            }
        },
        numeric_[static_cast<std::size_t>(field)]);
}

void ArchetypeStore::SetText(int field, std::uint32_t slot, std::string_view utf8)
{
    arenas_[static_cast<std::size_t>(field)].Set(slot, {reinterpret_cast<const std::uint8_t*>(utf8.data()), utf8.size()});
}

void ArchetypeStore::SetBytes(int field, std::uint32_t slot, std::span<const std::uint8_t> data)
{
    arenas_[static_cast<std::size_t>(field)].Set(slot, data);
}

void ArchetypeStore::BeginFrame()
{
    for (std::uint32_t i = 0; i < pendingFreeCount_; i++)
    {
        free_[freeCount_++] = pendingFree_[i];
    }

    pendingFreeCount_ = 0;
    ClearChanges();
}

std::uint32_t ArchetypeStore::Allocate(std::uint32_t netId)
{
    if (freeCount_ == 0)
    {
        if (capacity_ == MaxCapacity)
        {
            throw std::length_error("Archetype '" + schema_.name + "' would exceed " + std::to_string(MaxCapacity) + " slots");
        }

        Grow(std::min(capacity_ * 2, MaxCapacity));
    }

    const std::uint32_t slot = free_[--freeCount_];
    netIds_[slot] = netId;
    liveIndex_[slot] = static_cast<std::int32_t>(liveCount_);
    live_[liveCount_++] = slot;
    for (std::size_t f = 0; f < numeric_.size(); f++)
    {
        if (IsNumericKind(schema_.fields[f].kind))
        {
            const std::size_t components = static_cast<std::size_t>(components_[f]);
            std::visit([&](auto& column) { std::fill_n(column.begin() + slot * components, components, typename std::remove_reference_t<decltype(column)>::value_type{}); }, numeric_[f]);
        }
        else
        {
            arenas_[f].Clear(slot);
        }
    }

    if (dims_ > 0)
    {
        // Head, count and entry 0 only: evaluation never reads an entry beyond `count`, and the record is large at a high tick rate.
        std::uint8_t* record = MotionRecord(slot);
        record[headOffset_] = 0;
        record[countOffset_] = 1;
        record[epochOffset_] = 0;
        std::memset(record, 0, 4);
        std::fill_n(motion_.begin() + (static_cast<std::size_t>(slot) * recordBytes_ + segmentsOffset_) / 8, MotionStride(), 0.0);
    }

    entered_[enteredCount_++] = slot;
    return slot;
}

void ArchetypeStore::Release(std::uint32_t slot, bool immediate)
{
    if (!IsLive(slot))
    {
        throw std::logic_error("Archetype '" + schema_.name + "': slot " + std::to_string(slot) + " is not live");
    }

    const auto index = static_cast<std::uint32_t>(liveIndex_[slot]);
    const std::uint32_t lastSlot = live_[--liveCount_];
    live_[index] = lastSlot;
    liveIndex_[lastSlot] = static_cast<std::int32_t>(index);
    liveIndex_[slot] = -1;
    left_[leftCount_++] = netIds_[slot];
    netIds_[slot] = 0;
    // A departed entity's text and bytes are dead: a compaction must not keep copying them until the slot is reused.
    for (std::size_t f = 0; f < arenas_.size(); f++)
    {
        if (!IsNumericKind(schema_.fields[f].kind))
        {
            arenas_[f].Release(slot);
        }
    }

    updateMask_[slot] = 0;
    if (immediate)
    {
        free_[freeCount_++] = slot;
    }
    else
    {
        pendingFree_[pendingFreeCount_++] = slot;
    }
}

void ArchetypeStore::Clear()
{
    while (liveCount_ > 0)
    {
        Release(live_[liveCount_ - 1], true);
    }

    ClearChanges();
}

void ArchetypeStore::MarkUpdated(std::uint32_t slot, std::uint32_t groupMask)
{
    if (groupMask == 0 || !IsLive(slot))
    {
        return;
    }

    if (updateMask_[slot] == 0)
    {
        updated_[updatedCount_++] = slot;
    }

    updateMask_[slot] |= groupMask;
}

void ArchetypeStore::ResetMotion(std::uint32_t slot, std::span<const double> p, std::span<const double> v, std::uint32_t t0,
                                 std::uint8_t epoch)
{
    std::uint8_t* record = MotionRecord(slot);
    record[headOffset_] = 0;
    record[countOffset_] = 1;
    WriteSegment(slot, 0, p, v, t0, epoch);
}

void ArchetypeStore::PushSegment(std::uint32_t slot, std::span<const double> p, std::span<const double> v, std::uint32_t t0,
                                 std::uint8_t epoch)
{
    if (!IsLive(slot))
    {
        return;
    }

    std::uint8_t* record = MotionRecord(slot);
    const int head = (record[headOffset_] + 1) % history_;
    record[headOffset_] = static_cast<std::uint8_t>(head);
    if (record[countOffset_] < history_)
    {
        record[countOffset_]++;
    }

    WriteSegment(slot, head, p, v, t0, epoch);
    MarkUpdated(slot, MotionChangeBit);
}

void ArchetypeStore::WriteSegment(std::uint32_t slot, int entry, std::span<const double> p, std::span<const double> v, std::uint32_t t0,
                                  std::uint8_t epoch)
{
    std::uint8_t* record = MotionRecord(slot);
    std::memcpy(record + 4 * entry, &t0, 4);
    record[epochOffset_ + entry] = epoch;
    double* segment = motion_.data() + (static_cast<std::size_t>(slot) * recordBytes_ + segmentsOffset_) / 8 + entry * 2 * dims_;
    for (int i = 0; i < dims_; i++)
    {
        segment[i] = p[static_cast<std::size_t>(i)];
        segment[dims_ + i] = v.empty() ? 0.0 : v[static_cast<std::size_t>(i)];
    }
}

void ArchetypeStore::ClearChanges()
{
    for (std::uint32_t i = 0; i < updatedCount_; i++)
    {
        updateMask_[updated_[i]] = 0;
    }

    enteredCount_ = 0;
    updatedCount_ = 0;
    leftCount_ = 0;
}

void ArchetypeStore::Grow(std::uint32_t newCapacity)
{
    const std::uint32_t old = capacity_;
    capacity_ = newCapacity;
    version_++;

    netIds_.resize(newCapacity, 0);
    live_.resize(newCapacity, 0);
    liveIndex_.resize(newCapacity, -1);
    motion_.resize(static_cast<std::size_t>(newCapacity) * recordBytes_ / 8, 0.0);
    for (std::size_t f = 0; f < numeric_.size(); f++)
    {
        if (IsNumericKind(schema_.fields[f].kind))
        {
            const std::size_t length = static_cast<std::size_t>(newCapacity) * static_cast<std::size_t>(components_[f]);
            std::visit([length](auto& column) { column.resize(length); }, numeric_[f]);
        }
        else
        {
            arenas_[f].Grow(newCapacity);
        }
    }

    entered_.resize(newCapacity, 0);
    updated_.resize(newCapacity, 0);
    updateMask_.resize(newCapacity, 0);
    left_.resize(newCapacity, 0);
    pendingFree_.resize(newCapacity, 0);

    // New slots go on the free stack highest first, so allocation hands out the lowest slot first.
    free_.resize(newCapacity, 0);
    for (std::uint32_t slot = newCapacity; slot-- > old;)
    {
        free_[freeCount_++] = slot;
    }
}

}  // namespace typhon::client
