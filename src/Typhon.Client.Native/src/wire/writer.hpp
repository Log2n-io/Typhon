#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <string_view>
#include <vector>

#include "store/memory.hpp"

namespace typhon::client {

// Writes the wire's primitives (03 § 2) into a growable buffer, canonically: minimal varints, little-endian integers, canonical NaNs.
// Integer writers take the low bits of their argument, so a signed code writes its two's complement; range checks belong to the
// field codec, which knows what a value means. Reusable: Reset rewinds without releasing the capacity.
class WireWriter {
public:
    explicit WireWriter(std::size_t initialCapacity = 256) { buffer_.reserve(initialCapacity < 16 ? 16 : initialCapacity); }

    std::size_t Position() const { return buffer_.size(); }
    std::span<const std::uint8_t> Written() const { return buffer_; }
    std::uint8_t* Data() { return buffer_.data(); }
    void Reset() { buffer_.clear(); }

    void U8(std::uint32_t value) { buffer_.push_back(static_cast<std::uint8_t>(value)); }
    void U16(std::uint32_t value);
    void U24(std::uint32_t value);
    void U32(std::uint32_t value);

    // The low `bits` in {8, 16, 24, 32} bits of `value`.
    void Bits(std::uint32_t value, int bits);

    // Minimal unsigned LEB128.
    void Varu(std::uint32_t value);

    // Zigzag, then varu.
    void Vari(std::int32_t value);

    // A little-endian u64 / i64 (W32), from its bit pattern.
    void U64(std::uint64_t value);

    // A varu64 (W32): minimal unsigned LEB128, 1-10 bytes.
    void Varu64(std::uint64_t value);

    // A vari64 (W32) from the two's-complement bit pattern of a signed value: zigzag64, then varu64.
    void Vari64(std::uint64_t bits);

    // An IEEE single, narrowed with ties to even; NaN as 0x7FC00000.
    void F32(double value);

    // An IEEE double; NaN as 0x7FF8000000000000.
    void F64(double value);

    // An IEEE half converted directly from `value` (W10).
    void F16(double value);

    void Raw(std::span<const std::uint8_t> bytes);

    // Reserves `count` zeroed bytes and returns their offset.
    std::size_t Zeroes(std::size_t count);

    // A blob: a varu length, then the bytes. Throws std::out_of_range over `maxBytes`.
    void Blob(std::span<const std::uint8_t> bytes, std::size_t maxBytes);

    // A str: a varu byte length, then the UTF-8 bytes. Throws std::out_of_range over `maxBytes` or on invalid UTF-8.
    void Str(std::string_view utf8, std::size_t maxBytes);

    // Reserves room for a varu length prefix and returns a mark for EndLengthPrefixed.
    std::size_t BeginLengthPrefixed() { return Zeroes(MaxVaruBytes); }

    // Writes the minimal varu length of everything since `mark` and moves the content down behind it.
    void EndLengthPrefixed(std::size_t mark);

    // Bytes a minimal varu of `value` takes: 1 to 5.
    static int VaruSize(std::uint32_t value);

private:
    static constexpr std::size_t MaxVaruBytes = 5;
    Vec<std::uint8_t> buffer_;
};

}  // namespace typhon::client
