#include "wire/reader.hpp"

#include <bit>
#include <limits>
#include <string>

#include "wire/errors.hpp"
#include "wire/math.hpp"
#include "wire/utf8.hpp"

namespace typhon::client {

namespace {

WireFormatError Short(std::size_t needed, std::size_t left)
{
    return Malformed("needed " + std::to_string(needed) + " byte(s), " + std::to_string(left) + " left");
}

}  // namespace

void WireReader::Reset(std::span<const std::uint8_t> bytes)
{
    message_ = bytes;
    pos_ = 0;
    limit_ = bytes.size();
    held_ = true;
}

void WireReader::Release()
{
    message_ = {};
    pos_ = 0;
    limit_ = 0;
    held_ = false;
}

void WireReader::CheckHeld() const
{
    if (!held_)
    {
        // Not the peer's fault: a read after Release, from a callback that re-entered a decode.
        throw std::logic_error("the reader holds no message: a read re-entered a decode that already ended");
    }
}

std::size_t WireReader::Take(std::size_t count)
{
    CheckHeld();
    if (count > limit_ - pos_)
    {
        throw Short(count, Remaining());
    }

    const std::size_t at = pos_;
    pos_ += count;
    return at;
}

std::uint8_t WireReader::U8() { return message_[Take(1)]; }

std::int8_t WireReader::I8() { return static_cast<std::int8_t>(U8()); }

std::uint16_t WireReader::U16()
{
    const std::size_t at = Take(2);
    return static_cast<std::uint16_t>(message_[at] | (message_[at + 1] << 8));
}

std::int16_t WireReader::I16() { return static_cast<std::int16_t>(U16()); }

std::uint32_t WireReader::U24()
{
    const std::size_t at = Take(3);
    return static_cast<std::uint32_t>(message_[at]) | (static_cast<std::uint32_t>(message_[at + 1]) << 8)
           | (static_cast<std::uint32_t>(message_[at + 2]) << 16);
}

std::int32_t WireReader::I24()
{
    const std::uint32_t u = U24();
    return (u & 0x800000u) != 0 ? static_cast<std::int32_t>(u) - 0x1000000 : static_cast<std::int32_t>(u);
}

std::uint32_t WireReader::U32()
{
    const std::size_t at = Take(4);
    return static_cast<std::uint32_t>(message_[at]) | (static_cast<std::uint32_t>(message_[at + 1]) << 8)
           | (static_cast<std::uint32_t>(message_[at + 2]) << 16) | (static_cast<std::uint32_t>(message_[at + 3]) << 24);
}

std::int32_t WireReader::I32() { return static_cast<std::int32_t>(U32()); }

std::uint32_t WireReader::Unsigned(int bits)
{
    switch (bits)
    {
        case 8:
            return U8();
        case 16:
            return U16();
        case 24:
            return U24();
        case 32:
            return U32();
        default:
            throw std::out_of_range("a byte-aligned width is 8, 16, 24 or 32 bits, not " + std::to_string(bits));
    }
}

std::int32_t WireReader::Signed(int bits)
{
    switch (bits)
    {
        case 8:
            return I8();
        case 16:
            return I16();
        case 24:
            return I24();
        case 32:
            return I32();
        default:
            throw std::out_of_range("a byte-aligned width is 8, 16, 24 or 32 bits, not " + std::to_string(bits));
    }
}

std::uint32_t WireReader::Varu()
{
    std::uint8_t b = U8();
    std::uint32_t result = b & 0x7Fu;
    if ((b & 0x80) == 0)
    {
        return result;
    }

    for (int shift = 7; shift < 28; shift += 7)
    {
        b = U8();
        result |= static_cast<std::uint32_t>(b & 0x7Fu) << shift;
        if ((b & 0x80) == 0)
        {
            return result;
        }
    }

    b = U8();
    if (b > 0x0F)
    {
        throw Malformed("varu does not fit 32 bits");
    }

    return result | (static_cast<std::uint32_t>(b) << 28);
}

std::int32_t WireReader::Vari()
{
    const std::uint32_t u = Varu();
    return static_cast<std::int32_t>((u >> 1) ^ (0u - (u & 1u)));
}

std::uint32_t WireReader::VaruAtMost(std::uint64_t max, const char* what)
{
    const std::uint32_t value = Varu();
    if (value > max)
    {
        throw Malformed(std::string(what) + " " + std::to_string(value) + " exceeds " + std::to_string(max));
    }

    return value;
}

std::uint64_t WireReader::U64()
{
    const std::size_t at = Take(8);
    std::uint64_t bits = 0;
    for (int i = 7; i >= 0; i--)
    {
        bits = (bits << 8) | message_[at + static_cast<std::size_t>(i)];
    }

    return bits;
}

std::uint64_t WireReader::Varu64()
{
    std::uint64_t result = 0;
    for (int shift = 0; shift < 70; shift += 7)
    {
        const std::uint8_t b = U8();
        if (shift == 63 && b > 0x01)
        {
            throw Malformed("varu64 does not fit 64 bits");
        }

        result |= static_cast<std::uint64_t>(b & 0x7Fu) << shift;
        if ((b & 0x80) == 0)
        {
            break;
        }
    }

    return result;
}

std::uint64_t WireReader::Vari64()
{
    const std::uint64_t u = Varu64();
    return (u >> 1) ^ (0 - (u & 1));
}

double WireReader::F64()
{
    const std::size_t at = Take(8);
    std::uint64_t bits = 0;
    for (int i = 7; i >= 0; i--)
    {
        bits = (bits << 8) | message_[at + static_cast<std::size_t>(i)];
    }

    const double value = std::bit_cast<double>(bits);
    return value == value ? value : std::numeric_limits<double>::quiet_NaN();
}

double WireReader::F32()
{
    const float value = std::bit_cast<float>(U32());
    return value == value ? static_cast<double>(value) : std::numeric_limits<double>::quiet_NaN();
}

double WireReader::F16() { return math::DecodeF16(U16()); }

std::string_view WireReader::Str(std::size_t maxBytes)
{
    const std::size_t length = VaruAtMost(maxBytes, "string length");
    const std::size_t at = Take(length);
    if (length == 0)
    {
        return {};
    }

    const auto bytes = message_.subspan(at, length);
    if (!utf8::IsValid(bytes))
    {
        throw Malformed("string is not valid UTF-8");
    }

    return {reinterpret_cast<const char*>(bytes.data()), length};
}

std::size_t WireReader::BlobLength(std::size_t maxBytes) { return VaruAtMost(maxBytes, "blob length"); }

std::size_t WireReader::PushLimit(std::size_t length)
{
    if (length > limit_ - pos_)
    {
        throw Short(length, Remaining());
    }

    const std::size_t saved = limit_;
    limit_ = pos_ + length;
    return saved;
}

std::size_t WireReader::PopLimit(std::size_t saved)
{
    CheckHeld();
    const std::size_t unread = limit_ - pos_;
    limit_ = saved;
    return unread;
}

void WireReader::ExpectEnd(const char* what) const
{
    if (pos_ != limit_)
    {
        throw Malformed(std::string(what) + ": " + std::to_string(Remaining()) + " unread byte(s) after the declared content");
    }
}

}  // namespace typhon::client
