#include "wire/field_codec.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <stdexcept>
#include <string>

#include "wire/errors.hpp"
#include "wire/math.hpp"
#include "wire/realm_frame.hpp"
#include "wire/reader.hpp"
#include "wire/writer.hpp"

namespace typhon::client {

namespace {

[[noreturn]] void NoRealm(const FieldPlan& f)
{
    throw ProtocolError("position '" + f.name + "' arrived while the session holds no realm");
}

void CheckEnum(const FieldPlan& f, double value)
{
    if (f.enumNames != nullptr && value >= static_cast<double>(f.enumNames->size()))
    {
        throw Malformed("field '" + f.name + "': enum value " + std::to_string(static_cast<long long>(value)) + " is outside its "
                        + std::to_string(f.enumNames->size()) + " name(s)");
    }
}

void ReadList(WireReader& r, const FieldPlan& f, std::uint32_t frameTick, FieldSink& sink, const RealmFrame* frame)
{
    const std::uint32_t count = r.VaruAtMost(static_cast<std::uint64_t>(f.maxCount), "list count");
    if (count < static_cast<std::uint32_t>(f.minCount))
    {
        throw Malformed("list '" + f.name + "' has " + std::to_string(count) + " element(s); at least " + std::to_string(f.minCount)
                        + " required");
    }

    double values[MaxListComponents];
    const FieldPlan& element = *f.element;
    const int stride = f.components;
    for (std::uint32_t e = 0; e < count; e++)
    {
        ReadNumber(r, element, frameTick, values + static_cast<std::size_t>(e) * static_cast<std::size_t>(stride), frame);
    }

    sink.List(f, static_cast<int>(count), values);
}

void ReadCollection(WireReader& r, const FieldPlan& f, std::uint32_t frameTick, FieldSink& sink, bool strictEnums, const RealmFrame* frame)
{
    const std::uint32_t total = r.Varu();
    const std::uint32_t sent = r.Varu();
    if (sent > total || sent > static_cast<std::uint32_t>(f.maxCount))
    {
        throw Malformed("coll '" + f.name + "' sends " + std::to_string(sent) + " of " + std::to_string(total) + " element(s); at most "
                        + std::to_string(f.maxCount) + ", and never more than its total");
    }

    // Every element is at least one byte: a count the message cannot hold is refused before a store sizes for it.
    if (sent > r.Remaining())
    {
        throw Malformed("coll '" + f.name + "' sends " + std::to_string(sent) + " element(s) with " + std::to_string(r.Remaining())
                        + " byte(s) left");
    }

    sink.Collection(f, static_cast<int>(std::min<std::uint32_t>(total, 0x7FFFFFFF)), static_cast<int>(sent));
    for (std::uint32_t e = 0; e < sent; e++)
    {
        sink.CollectionElement(f, static_cast<int>(e));
        ReadSection(r, *f.elementSection, frameTick, sink, strictEnums, frame);
    }
}

[[noreturn]] void OutOfRange(const FieldPlan& f, double v, const std::string& range)
{
    char buffer[40];
    std::snprintf(buffer, sizeof buffer, "%.17g", v);
    throw std::out_of_range("field '" + f.name + "': " + buffer + " is not an integer in " + range);
}

std::uint32_t ToUnsigned(double v, double max, const FieldPlan& f)
{
    if (!(v >= 0) || v > max || std::floor(v) != v)
    {
        OutOfRange(f, v, "[0, " + std::to_string(static_cast<long long>(max)) + "]");
    }

    return static_cast<std::uint32_t>(v);
}

std::int32_t ToSigned(double v, double min, double max, const FieldPlan& f)
{
    if (!(v >= min) || v > max || std::floor(v) != v)
    {
        OutOfRange(f, v, "[" + std::to_string(static_cast<long long>(min)) + ", " + std::to_string(static_cast<long long>(max)) + "]");
    }

    return static_cast<std::int32_t>(v);
}

void RefuseEnum(const FieldPlan& f, double value)
{
    if (f.enumNames != nullptr && value >= static_cast<double>(f.enumNames->size()))
    {
        throw std::out_of_range("field '" + f.name + "': enum value outside its " + std::to_string(f.enumNames->size()) + " name(s)");
    }
}

const FieldValue* Find(std::span<const NamedValue> values, const std::string& name)
{
    for (const NamedValue& v : values)
    {
        if (v.name == name)
        {
            return &v.value;
        }
    }

    return nullptr;
}

std::span<const double> NumbersOf(const FieldValue* value, const FieldPlan& f)
{
    if (value == nullptr || value->kind != FieldValue::Kind::Numbers || value->numbers.empty())
    {
        throw std::out_of_range("no numeric value supplied for field '" + f.name + "'");
    }

    return value->numbers;
}

void WriteList(WireWriter& w, const FieldPlan& f, std::span<const double> flattened, const RealmFrame* frame)
{
    const auto stride = static_cast<std::size_t>(f.components);
    if (stride == 0 || flattened.size() % stride != 0)
    {
        throw std::out_of_range("list '" + f.name + "' needs a multiple of " + std::to_string(stride) + " numbers");
    }

    const std::size_t count = flattened.size() / stride;
    if (count < static_cast<std::size_t>(f.minCount) || count > static_cast<std::size_t>(f.maxCount))
    {
        throw std::out_of_range("list '" + f.name + "' has " + std::to_string(count) + " element(s); " + std::to_string(f.minCount) + ".."
                                + std::to_string(f.maxCount) + " allowed");
    }

    w.Varu(static_cast<std::uint32_t>(count));
    for (std::size_t e = 0; e < count; e++)
    {
        WriteNumber(w, *f.element, flattened.subspan(e * stride, stride), frame);
    }
}

}  // namespace

void ReadSection(WireReader& r, const SectionPlan& section, std::uint32_t frameTick, FieldSink& sink, bool strictEnums, const RealmFrame* frame)
{
    double scalar[protocol::MaxCount];
    std::uint64_t wide[protocol::MaxCount];
    const auto& fields = section.fields;
    const int packedCount = section.packedCount;
    if (section.packBytes > 0)
    {
        const std::size_t at = r.Take(static_cast<std::size_t>(section.packBytes));
        const auto pack = r.Bytes().subspan(at, static_cast<std::size_t>(section.packBytes));
        for (int i = 0; i < packedCount; i++)
        {
            const FieldPlan& f = *fields[static_cast<std::size_t>(i)];
            const double value = ReadPackedBits(pack, f.bitOffset, f.bitCount);
            if (strictEnums)
            {
                CheckEnum(f, value);
            }

            scalar[0] = value;
            sink.Number(f, scalar);
        }
    }

    for (std::size_t i = static_cast<std::size_t>(packedCount); i < fields.size(); i++)
    {
        const FieldPlan& f = *fields[i];
        switch (f.valueKind)
        {
            case ValueKind::Number:
                ReadNumber(r, f, frameTick, scalar, frame);
                if (strictEnums)
                {
                    CheckEnum(f, scalar[0]);
                }

                sink.Number(f, scalar);
                break;
            case ValueKind::Integer64:
                ReadInteger64(r, f, wide);
                sink.Integer64(f, wide);
                break;
            case ValueKind::Text:
                sink.Text(f, r.Str(static_cast<std::size_t>(f.maxBytes)));
                break;
            case ValueKind::Bytes:
            {
                const std::size_t length = f.kind == CodecKind::Bytes ? static_cast<std::size_t>(f.n) : r.BlobLength(static_cast<std::size_t>(f.maxBytes));
                const std::size_t at = r.Take(length);
                sink.Bytes(f, r.Bytes().subspan(at, length));
                break;
            }
            case ValueKind::List:
                ReadList(r, f, frameTick, sink, frame);
                break;
            case ValueKind::Collection:
                ReadCollection(r, f, frameTick, sink, strictEnums, frame);
                break;
            case ValueKind::Skipped:
                r.Skip(static_cast<std::size_t>(f.fixedBytes));
                break;
        }
    }
}

namespace {

// One value of a scalar codec, the unit a count repeats (W33).
double ReadScalar(WireReader& r, const FieldPlan& f, std::uint32_t frameTick)
{
    switch (f.kind)
    {
        case CodecKind::U8:
            return r.U8();
        case CodecKind::I8:
            return r.I8();
        case CodecKind::U16:
            return r.U16();
        case CodecKind::I16:
            return r.I16();
        case CodecKind::U32:
            return r.U32();
        case CodecKind::I32:
            return r.I32();
        case CodecKind::Varu:
        case CodecKind::EntityRef:
            return r.Varu();
        case CodecKind::Vari:
            return r.Vari();
        case CodecKind::F32:
            return r.F32();
        case CodecKind::F16:
            return r.F16();
        case CodecKind::F64:
            return r.F64();
        case CodecKind::Quant:
            // One range whatever the count: every component over min[0] and step[0].
            return math::DecodeQuant(r.Unsigned(f.bits), f.min[0], f.step[0]);
        case CodecKind::Unorm:
            return math::DecodeUnorm(r.Unsigned(f.bits), f.top);
        case CodecKind::Snorm:
            return math::DecodeSnorm(r.Signed(f.bits), f.limit);
        case CodecKind::Angle:
            // q * tau / 2^bits, where 2^bits = top + 1 exactly.
            return (r.Signed(f.bits) * math::Tau) / (f.top + 1);
        case CodecKind::TickLo:
            return math::DecodeTickLo(r.U16(), frameTick);
        default:
            throw std::logic_error("'" + f.codec.t + "' is not a byte-aligned numeric codec");
    }
}

}  // namespace

void ReadNumber(WireReader& r, const FieldPlan& f, std::uint32_t frameTick, double* out, const RealmFrame* frame)
{
    switch (f.kind)
    {
        case CodecKind::Pos2:
        case CodecKind::Pos3:
        {
            // Realm-framed (typhon.3, SUB-30): width, bounds and step are the session's frame's.
            if (frame == nullptr)
            {
                NoRealm(f);
            }

            for (int i = 0; i < f.components; i++)
            {
                const auto a = static_cast<std::size_t>(i);
                out[i] = math::DecodeQuant(r.Unsigned(frame->positionBits), frame->min[a], frame->step[a]);
            }

            break;
        }
        case CodecKind::Vec2:
        case CodecKind::Vec3:
            for (int i = 0; i < f.components; i++)
            {
                out[i] = math::DecodeVec(r.Signed(f.bits), f.scale, f.limit);
            }

            break;
        case CodecKind::Vel2:
        case CodecKind::Vel3:
            for (int i = 0; i < f.components; i++)
            {
                out[i] = math::DecodeVel(r.Signed(f.bits), f.velocityUnit, f.limit);
            }

            break;
        case CodecKind::Quat3:
            math::DecodeQuat3(r.U32(), out);
            break;
        default:
            // A scalar codec: components is its count (W33), each value read in turn.
            for (int i = 0; i < f.components; i++)
            {
                out[i] = ReadScalar(r, f, frameTick);
            }

            break;
    }
}

void ReadInteger64(WireReader& r, const FieldPlan& f, std::uint64_t* out)
{
    for (int i = 0; i < f.components; i++)
    {
        switch (f.kind)
        {
            case CodecKind::U64:
            case CodecKind::I64:
                out[i] = r.U64();
                break;
            case CodecKind::Varu64:
                out[i] = r.Varu64();
                break;
            case CodecKind::Vari64:
                out[i] = r.Vari64();
                break;
            default:
                throw std::logic_error("'" + f.codec.t + "' is not a 64-bit integer codec");
        }
    }
}

std::uint32_t ReadPackedBits(std::span<const std::uint8_t> pack, int offset, int count)
{
    const std::size_t byteIndex = static_cast<std::size_t>(offset >> 3);
    std::uint32_t window = 0;
    for (std::size_t i = 0; i < 4 && byteIndex + i < pack.size(); i++)
    {
        window |= static_cast<std::uint32_t>(pack[byteIndex + i]) << (8 * i);
    }

    return (window >> (offset & 7)) & ((1u << count) - 1u);
}

void WritePackedBits(std::uint8_t* pack, int offset, int count, std::uint32_t value)
{
    for (int i = 0; i < count; i++)
    {
        if (((value >> i) & 1u) != 0)
        {
            const int bit = offset + i;
            pack[bit >> 3] = static_cast<std::uint8_t>(pack[bit >> 3] | (1 << (bit & 7)));
        }
    }
}

void WriteSection(WireWriter& w, const SectionPlan& section, std::span<const NamedValue> values, bool strictEnums, const RealmFrame* frame)
{
    const auto& fields = section.fields;
    if (section.packBytes > 0)
    {
        const std::size_t at = w.Zeroes(static_cast<std::size_t>(section.packBytes));
        for (int i = 0; i < section.packedCount; i++)
        {
            const FieldPlan& f = *fields[static_cast<std::size_t>(i)];
            const double v = NumbersOf(Find(values, f.name), f)[0];
            std::uint32_t code;
            if (f.kind == CodecKind::Bool)
            {
                code = v != 0 ? 1 : 0;
            }
            else
            {
                code = ToUnsigned(v, static_cast<double>((1u << f.bitCount) - 1u), f);
                if (strictEnums)
                {
                    RefuseEnum(f, code);
                }
            }

            WritePackedBits(w.Data() + at, f.bitOffset, f.bitCount, code);
        }
    }

    for (std::size_t i = static_cast<std::size_t>(section.packedCount); i < fields.size(); i++)
    {
        const FieldPlan& f = *fields[i];
        const FieldValue* value = Find(values, f.name);
        switch (f.valueKind)
        {
            case ValueKind::Number:
            {
                const auto numbers = NumbersOf(value, f);
                if (strictEnums && f.enumNames != nullptr)
                {
                    RefuseEnum(f, numbers[0]);
                }

                WriteNumber(w, f, numbers, frame);
                break;
            }
            case ValueKind::Integer64:
                if (value == nullptr || value->kind != FieldValue::Kind::Integers || value->integers.empty())
                {
                    throw std::out_of_range("no 64-bit integer value supplied for field '" + f.name + "'");
                }

                WriteInteger64(w, f, value->integers);
                break;
            case ValueKind::Text:
                if (value == nullptr || value->kind != FieldValue::Kind::Text)
                {
                    throw std::out_of_range("field '" + f.name + "' needs a string");
                }

                w.Str(value->text, static_cast<std::size_t>(f.maxBytes));
                break;
            case ValueKind::Bytes:
                if (value == nullptr || value->kind != FieldValue::Kind::Bytes)
                {
                    throw std::out_of_range("field '" + f.name + "' needs bytes");
                }

                if (f.kind == CodecKind::Bytes)
                {
                    if (value->bytes.size() != static_cast<std::size_t>(f.n))
                    {
                        throw std::out_of_range("field '" + f.name + "' needs exactly " + std::to_string(f.n) + " bytes");
                    }

                    w.Raw(value->bytes);
                }
                else
                {
                    w.Blob(value->bytes, static_cast<std::size_t>(f.maxBytes));
                }

                break;
            case ValueKind::List:
                WriteList(w, f, NumbersOf(value, f), frame);
                break;
            case ValueKind::Collection:
            {
                if (value == nullptr || value->kind != FieldValue::Kind::Elements)
                {
                    throw std::out_of_range("coll '" + f.name + "' needs elements");
                }

                const auto sent = static_cast<std::uint32_t>(value->elements.size());
                WriteCollectionHeader(w, f, std::max(static_cast<std::uint32_t>(std::max(value->total, 0)), sent), sent);
                for (const auto& element : value->elements)
                {
                    WriteSection(w, *f.elementSection, element, strictEnums, frame);
                }

                break;
            }
            case ValueKind::Skipped:
                // A codec newer than this library: only its width is known, so the caller supplies the encoded bytes verbatim.
                if (value == nullptr || value->kind != FieldValue::Kind::Bytes || value->bytes.size() != static_cast<std::size_t>(f.fixedBytes))
                {
                    throw std::out_of_range("field '" + f.name + "' has an unknown codec; supply exactly " + std::to_string(f.fixedBytes)
                                            + " encoded bytes");
                }

                w.Raw(value->bytes);
                break;
        }
    }
}

void WriteCollectionHeader(WireWriter& w, const FieldPlan& f, std::uint32_t total, std::uint32_t sent)
{
    if (sent > total || sent > static_cast<std::uint32_t>(f.maxCount))
    {
        throw std::out_of_range("coll '" + f.name + "' cannot send " + std::to_string(sent) + " of " + std::to_string(total)
                                + " element(s); at most " + std::to_string(f.maxCount));
    }

    w.Varu(total);
    w.Varu(sent);
}

namespace {

// One value of a scalar codec, encoded: the unit a count repeats (W33).
void WriteScalar(WireWriter& w, const FieldPlan& f, double v)
{
    switch (f.kind)
    {
        case CodecKind::U8:
            w.U8(ToUnsigned(v, 0xFF, f));
            break;
        case CodecKind::I8:
            w.U8(static_cast<std::uint32_t>(ToSigned(v, -0x80, 0x7F, f)));
            break;
        case CodecKind::U16:
            w.U16(ToUnsigned(v, 0xFFFF, f));
            break;
        case CodecKind::I16:
            w.U16(static_cast<std::uint32_t>(ToSigned(v, -0x8000, 0x7FFF, f)));
            break;
        case CodecKind::U32:
            w.U32(ToUnsigned(v, 4294967295.0, f));
            break;
        case CodecKind::I32:
            w.U32(static_cast<std::uint32_t>(ToSigned(v, -2147483648.0, 2147483647.0, f)));
            break;
        case CodecKind::Varu:
        case CodecKind::EntityRef:
            w.Varu(ToUnsigned(v, 4294967295.0, f));
            break;
        case CodecKind::Vari:
            w.Vari(ToSigned(v, -2147483648.0, 2147483647.0, f));
            break;
        case CodecKind::F32:
            w.F32(v);
            break;
        case CodecKind::F16:
            w.F16(v);
            break;
        case CodecKind::F64:
            w.F64(v);
            break;
        case CodecKind::Quant:
            w.Bits(static_cast<std::uint32_t>(math::EncodeQuant(v, f.min[0], f.step[0], f.top)), f.bits);
            break;
        case CodecKind::Unorm:
            w.Bits(static_cast<std::uint32_t>(math::EncodeUnorm(v, f.top)), f.bits);
            break;
        case CodecKind::Snorm:
            w.Bits(static_cast<std::uint32_t>(static_cast<std::int32_t>(math::EncodeSnorm(v, f.limit))), f.bits);
            break;
        case CodecKind::Angle:
            w.Bits(static_cast<std::uint32_t>(static_cast<std::int64_t>(math::EncodeAngle(v, f.bits))), f.bits);
            break;
        case CodecKind::TickLo:
            w.U16(ToUnsigned(v, 4294967295.0, f) & 0xFFFFu);
            break;
        default:
            throw std::logic_error("'" + f.codec.t + "' is not a byte-aligned numeric codec");
    }
}

}  // namespace

void WriteNumber(WireWriter& w, const FieldPlan& f, std::span<const double> c, const RealmFrame* frame)
{
    if (c.size() < static_cast<std::size_t>(f.components))
    {
        throw std::out_of_range("field '" + f.name + "' needs " + std::to_string(f.components) + " component(s), got "
                                + std::to_string(c.size()));
    }

    switch (f.kind)
    {
        case CodecKind::Pos2:
        case CodecKind::Pos3:
            if (frame == nullptr)
            {
                throw std::logic_error("position '" + f.name + "' is realm-framed (typhon.3) and no realm frame was given to encode it over");
            }

            for (int i = 0; i < f.components; i++)
            {
                const auto a = static_cast<std::size_t>(i);
                w.Bits(static_cast<std::uint32_t>(math::EncodeQuant(c[a], frame->min[a], frame->step[a], frame->top)), frame->positionBits);
            }

            break;
        case CodecKind::Vec2:
        case CodecKind::Vec3:
            for (int i = 0; i < f.components; i++)
            {
                w.Bits(static_cast<std::uint32_t>(static_cast<std::int32_t>(math::EncodeVec(c[static_cast<std::size_t>(i)], f.scale, f.limit))),
                       f.bits);
            }

            break;
        case CodecKind::Vel2:
        case CodecKind::Vel3:
            for (int i = 0; i < f.components; i++)
            {
                w.Bits(static_cast<std::uint32_t>(
                           static_cast<std::int32_t>(math::EncodeVel(c[static_cast<std::size_t>(i)], f.velocityUnit, f.limit))),
                       f.bits);
            }

            break;
        case CodecKind::Quat3:
            w.U32(math::EncodeQuat3(c[0], c[1], c[2], c[3]));
            break;
        default:
            // A scalar codec: components is its count (W33), each value written in turn.
            for (int i = 0; i < f.components; i++)
            {
                WriteScalar(w, f, c[static_cast<std::size_t>(i)]);
            }

            break;
    }
}

void WriteInteger64(WireWriter& w, const FieldPlan& f, std::span<const std::uint64_t> c)
{
    if (c.size() < static_cast<std::size_t>(f.components))
    {
        throw std::out_of_range("field '" + f.name + "' needs " + std::to_string(f.components) + " 64-bit integer(s), got "
                                + std::to_string(c.size()));
    }

    for (int i = 0; i < f.components; i++)
    {
        const std::uint64_t v = c[static_cast<std::size_t>(i)];
        switch (f.kind)
        {
            case CodecKind::U64:
            case CodecKind::I64:
                w.U64(v);
                break;
            case CodecKind::Varu64:
                w.Varu64(v);
                break;
            case CodecKind::Vari64:
                w.Vari64(v);
                break;
            default:
                throw std::logic_error("'" + f.codec.t + "' is not a 64-bit integer codec");
        }
    }
}

}  // namespace typhon::client
