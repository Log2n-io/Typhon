#include "wire/codec_kinds.hpp"

#include <array>

namespace typhon::client {

namespace {

constexpr std::array<std::string_view, CodecKindCount> Tokens = {
    "",     "bool", "u8",    "i8",    "u16",   "i16",   "u32",   "i32",  "varu",  "vari",      "f32", "f16",   "quant", "pos2",   "pos3",
    "vec2", "vec3", "vel2",  "vel3",  "unorm", "snorm", "angle", "quat3", "bits", "entityRef", "str", "bytes", "blob",  "tickLo", "list",
};

}  // namespace

CodecKind CodecKindOf(std::string_view token)
{
    if (token.empty())
    {
        return CodecKind::Unknown;
    }

    for (std::size_t i = 1; i < Tokens.size(); i++)
    {
        if (Tokens[i] == token)
        {
            return static_cast<CodecKind>(i);
        }
    }

    return CodecKind::Unknown;
}

std::string_view CodecToken(CodecKind kind) { return Tokens[static_cast<std::size_t>(kind)]; }

bool IsListElement(CodecKind kind)
{
    switch (kind)
    {
        case CodecKind::U8:
        case CodecKind::I8:
        case CodecKind::U16:
        case CodecKind::I16:
        case CodecKind::U32:
        case CodecKind::I32:
        case CodecKind::Varu:
        case CodecKind::Vari:
        case CodecKind::EntityRef:
        case CodecKind::F32:
        case CodecKind::F16:
        case CodecKind::Quant:
        case CodecKind::Pos2:
        case CodecKind::Pos3:
        case CodecKind::Vec2:
        case CodecKind::Vec3:
        case CodecKind::Unorm:
        case CodecKind::Snorm:
        case CodecKind::Angle:
        case CodecKind::Quat3:
            return true;
        default:
            return false;
    }
}

}  // namespace typhon::client
