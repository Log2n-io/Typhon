#pragma once

#include <cstdint>
#include <string_view>

namespace typhon::client {

// The closed set of wire codecs (03 § 4), as small integers for a switch interpreter. Unknown is a newer server's token. Values match
// the TypeScript SDK's CodecKind.
enum class CodecKind : std::uint8_t
{
    Unknown = 0,
    Bool = 1,
    U8 = 2,
    I8 = 3,
    U16 = 4,
    I16 = 5,
    U32 = 6,
    I32 = 7,
    Varu = 8,
    Vari = 9,
    F32 = 10,
    F16 = 11,
    Quant = 12,
    Pos2 = 13,
    Pos3 = 14,
    Vec2 = 15,
    Vec3 = 16,
    Vel2 = 17,
    Vel3 = 18,
    Unorm = 19,
    Snorm = 20,
    Angle = 21,
    Quat3 = 22,
    Bits = 23,
    EntityRef = 24,
    Str = 25,
    Bytes = 26,
    Blob = 27,
    TickLo = 28,
    List = 29,
    // W32: decoded as 64-bit integers or binary64, never through a narrower number.
    U64 = 30,
    I64 = 31,
    F64 = 32,
    Varu64 = 33,
    Vari64 = 34,
};

inline constexpr int CodecKindCount = 35;

// The kind a wire token names; Unknown for an empty or unrecognised token.
CodecKind CodecKindOf(std::string_view token);

// The wire token of a kind; empty for Unknown.
std::string_view CodecToken(CodecKind kind);

// Whether a codec lives in its section's leading bit pack (W12).
constexpr bool IsPacked(CodecKind kind) { return kind == CodecKind::Bits || kind == CodecKind::Bool; }

// Whether a codec may be a list element: numeric, byte-aligned, and independent of the frame (W28).
bool IsListElement(CodecKind kind);

// Whether a codec may carry a count (W33): the byte-aligned scalar codecs.
bool TakesCount(CodecKind kind);

// Whether a codec decodes to 64-bit integers (W32).
constexpr bool IsInteger64(CodecKind kind)
{
    return kind == CodecKind::U64 || kind == CodecKind::I64 || kind == CodecKind::Varu64 || kind == CodecKind::Vari64;
}

// Whether a 64-bit integer codec is signed: its bit patterns are two's complement.
constexpr bool IsSigned64(CodecKind kind) { return kind == CodecKind::I64 || kind == CodecKind::Vari64; }

}  // namespace typhon::client
