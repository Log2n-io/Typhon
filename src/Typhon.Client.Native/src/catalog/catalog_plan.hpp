#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

#include "catalog/catalog.hpp"
#include "wire/codec_kinds.hpp"

// A canonical catalog compiled into the plans every decoder and encoder works from (the TypeScript SDK's CatalogPlan and friends).
// Compilation is the decoder's trust boundary: a plan is built only from a catalog whose shape keeps decoding bounded — widths the
// readers accept, list buffers that fit, groups a u8 mask addresses — whether or not it went through ParseCatalog.
namespace typhon::client {

// What a decoded field value is made of.
enum class ValueKind : std::uint8_t
{
    // One to sixteen numbers (FieldPlan::components): integers up to 32 bits, floats, quantized scalars, vectors, a quaternion —
    // everything a binary64 holds exactly.
    Number = 0,
    Text = 1,
    Bytes = 2,
    // A counted sequence of numeric elements.
    List = 3,
    // A codec this library does not know, skipped by its declared width.
    Skipped = 4,
    // W32: one to sixteen 64-bit integers, as their bit patterns — a signed codec's as two's complement.
    Integer64 = 5,
    // W34: a collection — a total, then the elements sent, each one section of FieldPlan::elementSection.
    Collection = 6,
};

struct SectionPlan;

// One field compiled: its codec's parameters resolved into the numbers the arithmetic needs, and its place in its section.
struct FieldPlan {
    FieldPlan(std::string name, int index, const CatalogField* field, const CatalogCodec& codec, const Catalog& catalog);
    ~FieldPlan();

    std::string name;
    // Position in its record's field list (archetype public fields, owner fields, or a message body); -1 otherwise.
    int index = -1;
    // The catalog field; null for a position codec, a list element or a metric value.
    const CatalogField* field = nullptr;
    CatalogCodec codec;
    CodecKind kind = CodecKind::Unknown;
    bool packed = false;
    int bitOffset = 0;
    int bitCount = 0;
    ValueKind valueKind = ValueKind::Number;
    // Numbers per value (per element, for a list); 0 for text and bytes. A count field's count (W33).
    int components = 1;
    // W33: the codec's count, 1 when the catalog declares none.
    int count = 1;
    std::unique_ptr<FieldPlan> element;
    // W34: a collection's element fields, each's index its place in the element, and the element as one section.
    std::vector<std::unique_ptr<FieldPlan>> elementFields;
    std::unique_ptr<SectionPlan> elementSection;
    // W34: for a collection's element field, the collection; null otherwise.
    FieldPlan* parent = nullptr;
    int bits = 0;
    double min[1] = {0};
    double step[1] = {0};
    double top = 0;
    double limit = 0;
    double scale = 0;
    double velocityUnit = 0;
    int n = 0;
    int maxBytes = 0;
    int fixedBytes = 0;
    int minCount = 0;
    int maxCount = 0;
    // The enum's value names, or null.
    const std::vector<std::string>* enumNames = nullptr;
};

// A section compiled: its fields in wire order (packed first) and the size of its leading pack.
struct SectionPlan {
    explicit SectionPlan(std::vector<FieldPlan*> fields);

    std::vector<FieldPlan*> fields;
    int packedCount = 0;
    int packBytes = 0;
};

struct PositionPlan {
    PositionPlan(const CatalogPosition& position, const Catalog& catalog);

    bool moving = false;
    bool linear = false;
    int dims = 0;
    std::unique_ptr<FieldPlan> pos;
    std::unique_ptr<FieldPlan> vel;
};

struct ArchetypePlan {
    ArchetypePlan(const CatalogArchetype& archetype, const Catalog& catalog);

    const CatalogArchetype* archetype = nullptr;
    int idx = 0;
    std::string name;
    std::unique_ptr<PositionPlan> position;
    std::vector<std::string> groups;
    std::unique_ptr<SectionPlan> onEnter;
    std::vector<SectionPlan> groupSections;
    // Every public field, in wire order; FieldPlan::index is the position here.
    std::vector<std::unique_ptr<FieldPlan>> fields;
    std::vector<std::string> ownerGroups;
    std::vector<SectionPlan> ownerSections;
    std::vector<std::unique_ptr<FieldPlan>> ownerFields;
};

// An event or command type compiled: its index and its single body section.
struct MessagePlan {
    MessagePlan(int idx, std::string name, const std::vector<CatalogField>& fields, const Catalog& catalog);

    int idx = 0;
    std::string name;
    std::vector<std::unique_ptr<FieldPlan>> fields;
    std::unique_ptr<SectionPlan> body;
};

struct MetricPlan {
    MetricPlan(const CatalogMetric& metric, int offset, const Catalog& catalog);

    const CatalogMetric* metric = nullptr;
    int idx = 0;
    std::string name;
    bool session = false;
    int valueCount = 1;
    int offset = 0;
    std::unique_ptr<FieldPlan> value;
};

struct GridPlan {
    GridPlan(const CatalogGrid& grid, std::size_t archetypeCount);

    const CatalogGrid* grid = nullptr;
    int idx = 0;
    int tileCells = 1;
    // One count per archetype of the grid; reused for every cell a decoder reads.
    std::vector<std::uint32_t> counts;
};

class CatalogPlan {
public:
    // Compiles a catalog. Throws CatalogError for a shape that would make decoding unbounded or ill-defined.
    static std::shared_ptr<const CatalogPlan> Compile(std::shared_ptr<const Catalog> catalog);

    const Catalog& GetCatalog() const { return *catalog_; }
    const std::vector<std::unique_ptr<ArchetypePlan>>& Archetypes() const { return archetypes_; }
    const std::vector<MetricPlan*>& ServerMetrics() const { return serverMetrics_; }
    const std::vector<MetricPlan*>& SessionMetrics() const { return sessionMetrics_; }
    const std::vector<std::unique_ptr<MetricPlan>>& Metrics() const { return metrics_; }
    int MetricValueCount() const { return metricValueCount_; }
    std::vector<std::unique_ptr<GridPlan>>& Grids() const { return grids_; }
    const std::vector<std::string>& RealmKinds() const { return realmKinds_; }

    // The plan at a wire index read from a peer; throws 1007 when there is none.
    const ArchetypePlan& Archetype(std::uint32_t idx) const;
    const MessagePlan& Event(std::uint32_t idx) const;
    const MessagePlan& Command(std::uint32_t idx) const;
    GridPlan& Grid(std::uint32_t idx) const;

    const ArchetypePlan* ArchetypeByName(std::string_view name) const;
    const MessagePlan* EventByName(std::string_view name) const;
    const MessagePlan* CommandByName(std::string_view name) const;
    const MetricPlan* MetricByName(std::string_view name) const;

    // The built-in ClientRegion command, when the server enables it.
    const MessagePlan* ClientRegion() const;

private:
    explicit CatalogPlan(std::shared_ptr<const Catalog> catalog);

    std::shared_ptr<const Catalog> catalog_;
    std::vector<std::unique_ptr<ArchetypePlan>> archetypes_;
    std::vector<std::unique_ptr<MessagePlan>> eventPlans_;
    std::vector<std::unique_ptr<MessagePlan>> commandPlans_;
    // Sparse by wire index: event and command indices start at reserved bases (W27).
    std::vector<const MessagePlan*> events_;
    std::vector<const MessagePlan*> commands_;
    std::vector<std::unique_ptr<MetricPlan>> metrics_;
    std::vector<MetricPlan*> serverMetrics_;
    std::vector<MetricPlan*> sessionMetrics_;
    int metricValueCount_ = 0;
    // Mutable: a grid's count buffer is decoder scratch, reused per cell.
    mutable std::vector<std::unique_ptr<GridPlan>> grids_;
    std::vector<std::string> realmKinds_;
};

}  // namespace typhon::client
