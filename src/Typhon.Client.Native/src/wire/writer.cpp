#include "wire/writer.hpp"

#include <bit>
#include <cstring>
#include <stdexcept>
#include <string>

#include "wire/math.hpp"
#include "wire/utf8.hpp"

namespace typhon::client {

void WireWriter::U16(std::uint32_t value)
{
    buffer_.push_back(static_cast<std::uint8_t>(value));
    buffer_.push_back(static_cast<std::uint8_t>(value >> 8));
}

void WireWriter::U24(std::uint32_t value)
{
    buffer_.push_back(static_cast<std::uint8_t>(value));
    buffer_.push_back(static_cast<std::uint8_t>(value >> 8));
    buffer_.push_back(static_cast<std::uint8_t>(value >> 16));
}

void WireWriter::U32(std::uint32_t value)
{
    buffer_.push_back(static_cast<std::uint8_t>(value));
    buffer_.push_back(static_cast<std::uint8_t>(value >> 8));
    buffer_.push_back(static_cast<std::uint8_t>(value >> 16));
    buffer_.push_back(static_cast<std::uint8_t>(value >> 24));
}

void WireWriter::Bits(std::uint32_t value, int bits)
{
    switch (bits)
    {
        case 8:
            U8(value);
            break;
        case 16:
            U16(value);
            break;
        case 24:
            U24(value);
            break;
        case 32:
            U32(value);
            break;
        default:
            throw std::out_of_range("a byte-aligned width is 8, 16, 24 or 32 bits, not " + std::to_string(bits));
    }
}

void WireWriter::Varu(std::uint32_t value)
{
    while (value >= 0x80)
    {
        buffer_.push_back(static_cast<std::uint8_t>((value & 0x7F) | 0x80));
        value >>= 7;
    }

    buffer_.push_back(static_cast<std::uint8_t>(value));
}

void WireWriter::Vari(std::int32_t value)
{
    const auto u = static_cast<std::uint32_t>(value);
    Varu((u << 1) ^ (0u - (u >> 31)));
}

void WireWriter::U64(std::uint64_t value)
{
    for (int i = 0; i < 8; i++)
    {
        buffer_.push_back(static_cast<std::uint8_t>(value >> (8 * i)));
    }
}

void WireWriter::Varu64(std::uint64_t value)
{
    while (value >= 0x80)
    {
        buffer_.push_back(static_cast<std::uint8_t>((value & 0x7F) | 0x80));
        value >>= 7;
    }

    buffer_.push_back(static_cast<std::uint8_t>(value));
}

void WireWriter::Vari64(std::uint64_t bits)
{
    // zigzag64(v) = (v << 1) ^ (v >> 63), on the two's-complement pattern.
    Varu64((bits << 1) ^ (0 - (bits >> 63)));
}

void WireWriter::F32(double value)
{
    if (value != value)
    {
        U32(0x7FC00000u);
        return;
    }

    U32(std::bit_cast<std::uint32_t>(static_cast<float>(value)));
}

void WireWriter::F64(double value)
{
    const std::uint64_t bits = value != value ? 0x7FF8000000000000ull : std::bit_cast<std::uint64_t>(value);
    U32(static_cast<std::uint32_t>(bits));
    U32(static_cast<std::uint32_t>(bits >> 32));
}

void WireWriter::F16(double value) { U16(math::EncodeF16(value)); }

void WireWriter::Raw(std::span<const std::uint8_t> bytes) { buffer_.insert(buffer_.end(), bytes.begin(), bytes.end()); }

std::size_t WireWriter::Zeroes(std::size_t count)
{
    const std::size_t at = buffer_.size();
    buffer_.resize(at + count, 0);
    return at;
}

void WireWriter::Blob(std::span<const std::uint8_t> bytes, std::size_t maxBytes)
{
    if (bytes.size() > maxBytes)
    {
        throw std::out_of_range("blob of " + std::to_string(bytes.size()) + " bytes exceeds its cap of " + std::to_string(maxBytes));
    }

    Varu(static_cast<std::uint32_t>(bytes.size()));
    Raw(bytes);
}

void WireWriter::Str(std::string_view utf8, std::size_t maxBytes)
{
    const std::span<const std::uint8_t> bytes(reinterpret_cast<const std::uint8_t*>(utf8.data()), utf8.size());
    if (!utf8::IsValid(bytes))
    {
        throw std::out_of_range("a str field needs valid UTF-8");
    }

    if (bytes.size() > maxBytes)
    {
        throw std::out_of_range("string of " + std::to_string(bytes.size()) + " UTF-8 bytes exceeds its cap of " + std::to_string(maxBytes));
    }

    Varu(static_cast<std::uint32_t>(bytes.size()));
    Raw(bytes);
}

void WireWriter::EndLengthPrefixed(std::size_t mark)
{
    const std::size_t contentStart = mark + MaxVaruBytes;
    const std::size_t length = buffer_.size() - contentStart;
    const int prefix = VaruSize(static_cast<std::uint32_t>(length));
    std::memmove(buffer_.data() + mark + prefix, buffer_.data() + contentStart, length);
    auto v = static_cast<std::uint32_t>(length);
    std::size_t at = mark;
    while (v >= 0x80)
    {
        buffer_[at++] = static_cast<std::uint8_t>((v & 0x7F) | 0x80);
        v >>= 7;
    }

    buffer_[at] = static_cast<std::uint8_t>(v);
    buffer_.resize(mark + static_cast<std::size_t>(prefix) + length);
}

int WireWriter::VaruSize(std::uint32_t value)
{
    return value < 0x80 ? 1 : value < 0x4000 ? 2 : value < 0x200000 ? 3 : value < 0x10000000 ? 4 : 5;
}

}  // namespace typhon::client
