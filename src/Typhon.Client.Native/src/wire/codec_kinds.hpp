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
};

inline constexpr int CodecKindCount = 30;

// The kind a wire token names; Unknown for an empty or unrecognised token.
CodecKind CodecKindOf(std::string_view token);

// The wire token of a kind; empty for Unknown.
std::string_view CodecToken(CodecKind kind);

// Whether a codec lives in its section's leading bit pack (W12).
constexpr bool IsPacked(CodecKind kind) { return kind == CodecKind::Bits || kind == CodecKind::Bool; }

// Whether a codec may be a list element: numeric, byte-aligned, and independent of the frame (W28).
bool IsListElement(CodecKind kind);

}  // namespace typhon::client
