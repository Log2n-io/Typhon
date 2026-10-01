#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

// The shape of the client-side world as a store needs it (the TypeScript SDK's store/schema.ts): which archetypes exist, how each is
// positioned, and the storage of each decoded field. No codec information: decoding is the protocol layer's job.
namespace typhon::client {

class CatalogPlan;
struct FieldPlan;

// The storage of one decoded field: a typed numeric column, or a per-field arena of text or bytes (13 § 7).
enum class FieldKind : std::uint8_t
{
    U8,
    I8,
    U16,
    I16,
    U32,
    I32,
    U64,
    I64,
    F32,
    F64,
    Text,
    Bytes,
};

constexpr bool IsNumericKind(FieldKind kind) { return kind != FieldKind::Text && kind != FieldKind::Bytes; }

struct FieldSchema {
    std::string name;
    FieldKind kind = FieldKind::F64;
    // Numbers per value of a numeric field, 1 to 4; ignored for text and bytes.
    int components = 1;
    // Index into ArchetypeSchema::groups; -1 for an onEnter field (W15): enter records carry it, state records never do.
    int group = -1;
};

struct PositionSchema {
    // Segments are replicated (motion), rather than one position on enter (static).
    bool moving = false;
    // For motion: segments carry a velocity and are extrapolated; otherwise they are samples, interpolated between.
    bool linear = true;
    // 2 or 3.
    int dims = 2;
};

struct ArchetypeSchema {
    // The wire index; equal to the position in WorldSchema::archetypes.
    int index = 0;
    std::string name;
    std::optional<PositionSchema> position;
    // Change groups, at most MaxGroups: bit i of an update mask is groups[i] (W14).
    std::vector<std::string> groups;
    std::vector<FieldSchema> fields;
};

struct WorldSchema {
    // The catalog's tick.periodUs: it sizes the motion ring each slot keeps.
    int tickPeriodUs = 0;
    std::vector<ArchetypeSchema> archetypes;
};

// Change groups per archetype: the width of a state record's u8 mask.
inline constexpr int MaxGroups = 8;

// The update-mask bit a motion segment sets: motion is not a change group (W15), so it sits just above the eight group bits.
inline constexpr std::uint32_t MotionChangeBit = 1u << MaxGroups;

// Throws std::invalid_argument when a schema is internally inconsistent: a store built on it would misfile data.
void ValidateSchema(const WorldSchema& schema);

// The storage a decoded field needs, or nullopt for a codec newer than this library (skipped by its width, nothing to store).
// Integers keep their own width; varu, entityRef and tickLo a u32, vari an i32; f32 and f16 a f32 (every half and single is exact in
// one); every dequantized value a f64 — decoded values are bit-exact binary64 (W1), and narrowing them is a renderer's choice.
std::optional<FieldKind> FieldKindOf(const FieldPlan& field);

// The store schema a catalog describes: one archetype per catalog archetype, in index order, its public fields in wire order. Owner
// fields are not stored per slot: they belong to the one controlled entity (SelfState).
WorldSchema WorldSchemaFromCatalog(const CatalogPlan& plan);

}  // namespace typhon::client
