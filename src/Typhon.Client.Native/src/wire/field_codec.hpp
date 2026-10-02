#pragma once

#include <cstdint>
#include <span>
#include <string_view>

#include "catalog/catalog_plan.hpp"
#include "wire/constants.hpp"

namespace typhon::client {

class RealmFrame;
class WireReader;
class WireWriter;

// Receives the values of a decoded section, one call per field, in wire order (C# IFieldSink, TS FieldSink). Every pointer, view and
// span it is handed is the decoder's or the message's: valid only for the duration of the call.
class FieldSink {
public:
    virtual ~FieldSink() = default;

    // A numeric field: values[0 .. field.components).
    virtual void Number(const FieldPlan& field, const double* values) = 0;

    // A 64-bit integer field (W32): values[0 .. field.components) as bit patterns, a signed codec's as two's complement.
    virtual void Integer64(const FieldPlan& field, const std::uint64_t* values) = 0;

    // A text field, already validated as UTF-8.
    virtual void Text(const FieldPlan& field, std::string_view utf8) = 0;

    // A bytes or blob field: a view of the message.
    virtual void Bytes(const FieldPlan& field, std::span<const std::uint8_t> data) = 0;

    // A list: `count` elements of field.components numbers each, flattened.
    virtual void List(const FieldPlan& field, int count, const double* values) = 0;

    // A collection (W34): `sent` elements follow, each opened by CollectionElement and made of its element fields' calls (fields whose
    // FieldPlan::parent is `field`). sent < total is a truncation the server made. A no-op by default: an event or a command carries none.
    virtual void Collection(const FieldPlan& field, int total, int sent)
    {
        (void)field;
        (void)total;
        (void)sent;
    }

    // Opens element `index` of `field`: the calls that follow, up to the next element, are its fields.
    virtual void CollectionElement(const FieldPlan& field, int index)
    {
        (void)field;
        (void)index;
    }
};

// The largest number of components one decode yields, a list's included.
inline constexpr int MaxListComponents = protocol::MaxListCount * 4;

struct NamedValue;

// A value to encode: numbers (one for a scalar or a boolean; the components of a vector, a quaternion or a count; a list's flattened
// elements), 64-bit integers as bit patterns (W32), UTF-8 text, or bytes (bytes, blob, or an unknown codec written verbatim).
struct FieldValue {
    enum class Kind : std::uint8_t
    {
        Absent,
        Numbers,
        Text,
        Bytes,
        Integers,
        Elements,
    };

    Kind kind = Kind::Absent;
    std::span<const double> numbers;
    std::string_view text;
    std::span<const std::uint8_t> bytes;
    std::span<const std::uint64_t> integers;
    // W34: a collection's elements, each its fields' values by name, and the entity's total — at least elements.size(); 0 means exactly
    // those.
    std::span<const std::span<const NamedValue>> elements;
    int total = 0;

    // One factory per kind: a positional brace initializer silently changes meaning when a member is added.
    static FieldValue OfNumbers(std::span<const double> values)
    {
        FieldValue v;
        v.kind = Kind::Numbers;
        v.numbers = values;
        return v;
    }

    static FieldValue OfText(std::string_view utf8)
    {
        FieldValue v;
        v.kind = Kind::Text;
        v.text = utf8;
        return v;
    }

    static FieldValue OfBytes(std::span<const std::uint8_t> data)
    {
        FieldValue v;
        v.kind = Kind::Bytes;
        v.bytes = data;
        return v;
    }

    static FieldValue OfIntegers(std::span<const std::uint64_t> values)
    {
        FieldValue v;
        v.kind = Kind::Integers;
        v.integers = values;
        return v;
    }

    static FieldValue OfElements(std::span<const std::span<const NamedValue>> elements, int total = 0)
    {
        FieldValue v;
        v.kind = Kind::Elements;
        v.elements = elements;
        v.total = total;
        return v;
    }
};

// A field's value by wire name.
struct NamedValue {
    std::string_view name;
    FieldValue value;
};

// Decodes a section (§ 5, W11-W13): the leading bit pack, then each byte-aligned field in wire order. `strictEnums` applies the
// client -> server rule — an enum value outside its names is malformed (1007); server -> client it decodes as the bare integer.
void ReadSection(WireReader& r, const SectionPlan& section, std::uint32_t frameTick, FieldSink& sink, bool strictEnums = false,
                 const RealmFrame* frame = nullptr);

// Decodes one numeric value of a byte-aligned field (or list element, or position codec) into `out`: field.components values.
void ReadNumber(WireReader& r, const FieldPlan& f, std::uint32_t frameTick, double* out, const RealmFrame* frame = nullptr);

// Decodes a 64-bit integer field (W32) into `out`: field.components bit patterns.
void ReadInteger64(WireReader& r, const FieldPlan& f, std::uint64_t* out);

// Encodes a 64-bit integer field (W32) from its components' bit patterns.
void WriteInteger64(WireWriter& w, const FieldPlan& f, std::span<const std::uint64_t> c);

// Extracts `count` <= 24 bits at bit `offset` of a pack, least significant bit first.
std::uint32_t ReadPackedBits(std::span<const std::uint8_t> pack, int offset, int count);

// Stores `count` bits of `value` at bit `offset` of a zeroed pack.
void WritePackedBits(std::uint8_t* pack, int offset, int count, std::uint32_t value);

// Encodes a section from named values. `strictEnums` refuses an enum value outside its names (W13). A value that cannot be represented
// throws std::out_of_range: a bug on this side, never peer input.
void WriteSection(WireWriter& w, const SectionPlan& section, std::span<const NamedValue> values, bool strictEnums = false,
                  const RealmFrame* frame = nullptr);

// Writes a collection's header, varu total | varu sent (W34); its elements follow, each a WriteSection over FieldPlan::elementSection.
// Throws std::out_of_range unless sent <= total and sent <= the codec's maxCount.
void WriteCollectionHeader(WireWriter& w, const FieldPlan& f, std::uint32_t total, std::uint32_t sent);

// Encodes one numeric value from `c`.
void WriteNumber(WireWriter& w, const FieldPlan& f, std::span<const double> c, const RealmFrame* frame = nullptr);

}  // namespace typhon::client
