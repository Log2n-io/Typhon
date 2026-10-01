#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <string_view>

namespace typhon::client {

// Reads the wire's primitives (03 § 2) from a byte span: little-endian fixed-width integers, LEB128 varints, half, single and double
// floats, and length-prefixed strings and blobs. The TypeScript SDK's WireReader, read for read.
//
// - Every read is bounds-checked against the current limit and throws WireFormatError (1007) when the input is short.
// - Blocks are isolated without allocation: PushLimit narrows the readable range to a block's declared length, PopLimit restores it
//   and reports what the block left unread.
// - Byte payloads and strings are handed out as views into the message, never copied. They are valid while the message is.
// - Varints are read leniently: an over-long form that fits 32 bits in five bytes is accepted; encoders write the minimal one.
class WireReader {
public:
    WireReader() = default;
    explicit WireReader(std::span<const std::uint8_t> bytes) { Reset(bytes); }

    void Reset(std::span<const std::uint8_t> bytes);

    // Lets go of the message: any read after it throws.
    void Release();

    std::size_t Position() const { return pos_; }
    std::span<const std::uint8_t> Bytes() const { return message_; }
    std::size_t Remaining() const { return limit_ - pos_; }
    bool IsAtEnd() const { return pos_ == limit_; }

    std::uint8_t U8();
    std::int8_t I8();
    std::uint16_t U16();
    std::int16_t I16();
    std::uint32_t U24();
    std::int32_t I24();
    std::uint32_t U32();
    std::int32_t I32();

    // An unsigned integer of 8, 16, 24 or 32 bits.
    std::uint32_t Unsigned(int bits);

    // A two's-complement integer of 8, 16, 24 or 32 bits, sign-extended.
    std::int32_t Signed(int bits);

    // Unsigned LEB128: at most five bytes, fitting 32 bits.
    std::uint32_t Varu();

    // Zigzag-mapped varu, in [-2^31, 2^31).
    std::int32_t Vari();

    // A varu that must not exceed `max`: a count, a length or an index.
    std::uint32_t VaruAtMost(std::uint64_t max, const char* what);

    // A little-endian IEEE double; any NaN decodes as the canonical NaN.
    double F64();

    // A little-endian IEEE single, widened exactly; any NaN decodes as the canonical NaN.
    double F32();

    // A little-endian IEEE half, widened exactly; any NaN decodes as the canonical NaN.
    double F16();

    // Advances past `count` bytes and returns the offset of the first in Bytes().
    std::size_t Take(std::size_t count);

    void Skip(std::size_t count) { Take(count); }

    // A str: a varu byte length, at most `maxBytes`, then that many bytes of valid UTF-8, as a view into the message.
    std::string_view Str(std::size_t maxBytes);

    // A blob's length: a varu of at most `maxBytes`. The bytes follow; Take them.
    std::size_t BlobLength(std::size_t maxBytes);

    // Narrows the readable range to the next `length` bytes and returns the previous limit for PopLimit.
    std::size_t PushLimit(std::size_t length);

    // Restores the limit PushLimit returned and reports how many bytes of the block were left unread.
    std::size_t PopLimit(std::size_t saved);

    // Throws 1007 unless every byte of the message has been consumed.
    void ExpectEnd(const char* what) const;

private:
    void CheckHeld() const;

    std::span<const std::uint8_t> message_{};
    std::size_t pos_ = 0;
    std::size_t limit_ = 0;
    bool held_ = false;
};

}  // namespace typhon::client
