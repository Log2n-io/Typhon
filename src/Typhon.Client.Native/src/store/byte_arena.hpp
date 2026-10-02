#pragma once

#include <cstddef>
#include <cstdint>
#include <span>

#include "store/memory.hpp"

namespace typhon::client {

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

}  // namespace typhon::client
