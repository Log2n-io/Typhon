#pragma once

#include <cstdint>
#include <cstring>
#include <span>
#include <string>
#include <string_view>
#include <variant>

#include "store/memory.hpp"
#include "store/schema.hpp"

namespace typhon::client {

// The largest render delay the motion ring is sized for by default: the Clock's own default maxDelayMs (05 § 1).
inline constexpr double DefaultMaxRenderDelayMs = 300;

// Segments kept per slot: render time trails the newest frame by up to maxRenderDelayMs and each tick brings at most one segment, so
// max(4, ceil(maxRenderDelayMs * 1000 / tickPeriodUs) + 2), capped at 255 (the ring's u8 head and count). Taken from the catalog's tick
// period, never from one application's rate.
int SegmentHistoryFor(int tickPeriodUs, double maxRenderDelayMs = DefaultMaxRenderDelayMs);

// Where the segments start in a motion record of `history` entries: after t0s, head, count and epochs, 8-aligned.
constexpr int MotionSegmentsOffset(int history) { return (5 * history + 2 + 7) & ~7; }

// One slot's motion record for `history` ring entries (H) in `dims` dimensions, contiguous — the TypeScript layout:
//   0 .. 4H           t0 of each entry (u32, integer ticks)
//   4H                head: ring index of the newest segment (u8)
//   4H + 1            count: valid segments, 1..H (u8)
//   4H + 2 .. 5H + 2  epoch of each entry (u8)
//   padding to a multiple of 8
//   segments          H x p0[dims] v[dims] (f64: metres and metres per tick)
constexpr int MotionRecordBytes(int dims, int history) { return MotionSegmentsOffset(history) + history * 2 * dims * 8; }

// The text or bytes values of one field, one per slot, in a per-field arena (13 § 7): a slot keeps its reservation and rewrites in
// place while a new value fits; a larger value is appended, and the arena is compacted into its spare buffer once half of it is dead.
// After warm-up a changed value allocates nothing.
class ByteArena {
public:
    std::span<const std::uint8_t> At(std::uint32_t slot) const
    {
        return {data_.data() + offset_[slot], length_[slot]};
    }

    void Set(std::uint32_t slot, std::span<const std::uint8_t> value);

    // Empties a slot's value, keeping its reservation for the next occupant.
    void Clear(std::uint32_t slot) { length_[slot] = 0; }

    // Gives a slot's reservation back: its bytes are dead, and a compaction no longer copies them.
    void Release(std::uint32_t slot)
    {
        dead_ += reserved_[slot];
        reserved_[slot] = 0;
        length_[slot] = 0;
    }
    void Grow(std::size_t slots);

    // Bytes held by the arena, live and dead: for tests and diagnostics.
    std::size_t ArenaBytes() const { return data_.size(); }

private:
    void Compact();

    Vec<std::uint8_t> data_;
    Vec<std::uint8_t> spare_;
    Vec<std::uint32_t> offset_;
    Vec<std::uint32_t> length_;
    Vec<std::uint32_t> reserved_;
    std::size_t dead_ = 0;
};

// One numeric column: `capacity x components` values of the field's decoded type. The alternative index is the FieldKind.
using NumericColumn = std::variant<Vec<std::uint8_t>, Vec<std::int8_t>, Vec<std::uint16_t>, Vec<std::int16_t>, Vec<std::uint32_t>,
                                   Vec<std::int32_t>, Vec<std::uint64_t>, Vec<std::int64_t>, Vec<float>, Vec<double>>;

// Every entity of one archetype the client holds, as structure-of-arrays at stable slots (the TypeScript ArchetypeStore):
// - Stable slots: an entity keeps its slot from enter to leave. A slot freed during a frame is not reused before the next frame
//   begins, so a change list never names a slot that changed owner inside the frame it describes.
// - Dense iteration: Live() lists the occupied slots (swap-remove), so a per-frame pass never walks holes.
// - Growth: capacity doubles when the free list runs out; every column is then reallocated, and Version() moves.
class ArchetypeStore {
public:
    ArchetypeStore(ArchetypeSchema schema, int segmentHistory, std::uint32_t initialCapacity = 16);

    const ArchetypeSchema& Schema() const { return schema_; }
    bool HasPosition() const { return dims_ > 0; }
    // Position dimensions: 0 when not spatial, else 2 or 3.
    int Dims() const { return dims_; }
    bool Moving() const { return moving_; }
    bool Linear() const { return linear_; }
    int SegmentHistory() const { return history_; }
    // Values per slot an evaluation writes: p[dims] v[dims].
    int MotionStride() const { return 2 * dims_; }
    std::uint32_t Capacity() const { return capacity_; }
    // Incremented whenever the columns are reallocated: a consumer holding a pointer into one re-reads it.
    std::uint32_t Version() const { return version_; }

    // netId per slot; 0 when the slot is free.
    std::span<const std::uint32_t> NetIds() const { return {netIds_.data(), capacity_}; }
    std::span<const std::uint32_t> Live() const { return {live_.data(), liveCount_}; }
    std::uint32_t LiveCount() const { return liveCount_; }
    bool IsLive(std::uint32_t slot) const { return slot < capacity_ && liveIndex_[slot] >= 0; }

    // This frame's changes. A slot that entered and left this frame is no longer live; a consumer filters with IsLive.
    std::span<const std::uint32_t> Entered() const { return {entered_.data(), enteredCount_}; }
    // Slots updated this frame; their groups in UpdateMask(), plus MotionChangeBit when a segment arrived.
    std::span<const std::uint32_t> Updated() const { return {updated_.data(), updatedCount_}; }
    std::uint32_t UpdateMask(std::uint32_t slot) const { return updateMask_[slot]; }
    // netIds that left this frame.
    std::span<const std::uint32_t> Left() const { return {left_.data(), leftCount_}; }

    int FieldIndex(std::string_view name) const;
    int FieldComponents(int field) const { return components_[static_cast<std::size_t>(field)]; }

    // A numeric column's values, `capacity x components`, typed; throws std::logic_error for a text or bytes field and
    // std::bad_variant_access for another element type.
    template <class T>
    std::span<const T> Column(int field) const
    {
        RequireNumeric(field);
        const auto& column = std::get<Vec<T>>(numeric_[static_cast<std::size_t>(field)]);
        return {column.data(), column.size()};
    }

    // A numeric column's base address, for a binding that reads it by the field's kind.
    const void* ColumnData(int field) const;

    // One component of a numeric field at a slot, widened to double. Throws std::logic_error for a text or bytes field, std::out_of_range
    // for a slot or component beyond the column.
    double NumberAt(int field, std::uint32_t slot, int component = 0) const;

    std::string_view TextAt(int field, std::uint32_t slot) const;
    std::span<const std::uint8_t> BytesAt(int field, std::uint32_t slot) const;

    // The decoder's write paths.
    void SetNumbers(int field, std::uint32_t slot, const double* values);
    void SetText(int field, std::uint32_t slot, std::string_view utf8);
    void SetBytes(int field, std::uint32_t slot, std::span<const std::uint8_t> data);

    // Starts a frame: releases the slots freed by the previous one and clears the change lists.
    void BeginFrame();

    // Allocates a slot for an entering entity, its fields and motion zeroed: nothing inherits the previous occupant's values.
    std::uint32_t Allocate(std::uint32_t netId);

    // Frees a slot; it stays unusable until the next BeginFrame unless `immediate`.
    void Release(std::uint32_t slot, bool immediate = false);

    // Drops every entity at once (a RESET frame): slots are reusable immediately and nothing is reported in the change lists.
    void Clear();

    // Records that some groups of a live slot changed this frame.
    void MarkUpdated(std::uint32_t slot, std::uint32_t groupMask);

    // Sets the only segment of a slot: the position an entity enters with. `v` is empty for a static position or a none sample.
    void ResetMotion(std::uint32_t slot, std::span<const double> p, std::span<const double> v, std::uint32_t t0, std::uint8_t epoch);

    // Appends a segment to the ring and marks MotionChangeBit; older ones stay, so a render time trailing the newest frame finds its own.
    void PushSegment(std::uint32_t slot, std::span<const double> p, std::span<const double> v, std::uint32_t t0, std::uint8_t epoch);

    // The motion ring of a slot.
    std::uint8_t HeadEntry(std::uint32_t slot) const { return MotionByte(slot, headOffset_); }
    std::uint8_t SegmentCount(std::uint32_t slot) const { return MotionByte(slot, countOffset_); }
    std::uint8_t EpochOf(std::uint32_t slot, int entry) const { return MotionByte(slot, epochOffset_ + entry); }
    std::uint32_t T0Of(std::uint32_t slot, int entry) const
    {
        std::uint32_t t0;
        std::memcpy(&t0, MotionRecord(slot) + 4 * entry, 4);
        return t0;
    }

    // A ring entry's p0[dims], then v[dims].
    const double* SegmentOf(std::uint32_t slot, int entry) const
    {
        return motion_.data() + (static_cast<std::size_t>(slot) * recordBytes_ + segmentsOffset_) / 8 + entry * 2 * dims_;
    }

private:
    void RequireNumeric(int field) const;

    const std::uint8_t* MotionRecord(std::uint32_t slot) const
    {
        return reinterpret_cast<const std::uint8_t*>(motion_.data()) + static_cast<std::size_t>(slot) * recordBytes_;
    }

    std::uint8_t* MotionRecord(std::uint32_t slot)
    {
        return reinterpret_cast<std::uint8_t*>(motion_.data()) + static_cast<std::size_t>(slot) * recordBytes_;
    }

    std::uint8_t MotionByte(std::uint32_t slot, int offset) const { return MotionRecord(slot)[offset]; }
    void WriteSegment(std::uint32_t slot, int entry, std::span<const double> p, std::span<const double> v, std::uint32_t t0,
                      std::uint8_t epoch);
    void ClearChanges();
    void Grow(std::uint32_t newCapacity);

    ArchetypeSchema schema_;
    int dims_ = 0;
    bool moving_ = false;
    bool linear_ = false;
    int history_ = 0;
    int headOffset_ = 0;
    int countOffset_ = 0;
    int epochOffset_ = 0;
    int segmentsOffset_ = 0;
    std::size_t recordBytes_ = 0;
    std::vector<int> components_;
    // Per field: its numeric column, or its arena; the other is unused.
    std::vector<NumericColumn> numeric_;
    std::vector<ByteArena> arenas_;

    std::uint32_t capacity_ = 0;
    std::uint32_t version_ = 0;
    Vec<std::uint32_t> netIds_;
    Vec<std::uint32_t> live_;
    std::uint32_t liveCount_ = 0;
    Vec<std::int32_t> liveIndex_;
    // Motion records: doubles, so the segments are aligned; the t0s and bytes before them are read through the record's bytes.
    Vec<double> motion_;
    Vec<std::uint32_t> entered_;
    std::uint32_t enteredCount_ = 0;
    Vec<std::uint32_t> updated_;
    std::uint32_t updatedCount_ = 0;
    Vec<std::uint32_t> updateMask_;
    Vec<std::uint32_t> left_;
    std::uint32_t leftCount_ = 0;
    Vec<std::uint32_t> free_;
    std::uint32_t freeCount_ = 0;
    Vec<std::uint32_t> pendingFree_;
    std::uint32_t pendingFreeCount_ = 0;
};

}  // namespace typhon::client
