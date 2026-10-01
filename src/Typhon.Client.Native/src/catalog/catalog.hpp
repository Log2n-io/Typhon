#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// The catalog: the canonical JSON a server sends in WELCOME (03 § 4), typed exactly as the C# model serializes it. The catalog arrives
// canonical — every array in wire order, every idx assigned — and a client never sorts: re-sorting would be a second source of truth
// for the wire order. The TypeScript SDK's protocol/catalog.ts model, member for member.
namespace typhon::client {

namespace json {
class Value;
}

struct CatalogCodec {
    std::string t;
    std::optional<int> bits;
    std::optional<std::vector<double>> min;
    std::optional<std::vector<double>> max;
    std::optional<double> scale;
    std::optional<int> unitExp;
    std::optional<int> n;
    std::optional<int> maxBytes;
    std::shared_ptr<const CatalogCodec> of;
    std::optional<int> minCount;
    std::optional<int> maxCount;
    std::optional<int> fixedBytes;
};

struct CatalogField {
    std::string name;
    CatalogCodec codec;
    std::optional<std::string> group;
    std::optional<bool> onEnter;
    std::optional<std::string> enumName;
    std::optional<std::string> smoothing;

    bool IsOnEnter() const { return onEnter.value_or(false); }
};

struct CatalogPosition {
    std::string kind;
    std::optional<std::string> model;
    CatalogCodec pos;
    std::optional<CatalogCodec> vel;
};

struct CatalogOwner {
    std::vector<std::string> groups;
    std::vector<CatalogField> fields;
};

struct CatalogArchetype {
    int idx = 0;
    std::string name;
    std::vector<std::string> groups;
    std::optional<CatalogPosition> position;
    std::vector<CatalogField> fields;
    std::optional<CatalogOwner> owner;
};

struct CatalogEvent {
    int idx = 0;
    std::string name;
    std::string scope;
    std::vector<CatalogField> fields;
};

struct CatalogCommandRate {
    int perSec = 0;
    int burst = 0;
};

struct CatalogCommand {
    int idx = 0;
    std::string name;
    std::string delivery;
    std::optional<CatalogCommandRate> rate;
    std::vector<CatalogField> fields;
};

struct CatalogGrid {
    int idx = 0;
    int tileCells = 0;
    std::vector<int> archetypes;
};

struct CatalogMetric {
    int idx = 0;
    std::string name;
    std::string unit;
    CatalogCodec codec;
    std::optional<std::string> scope;
    std::optional<std::string> kind;
    std::optional<std::vector<std::string>> labels;
};

struct Catalog {
    int protocolMajor = 0;
    int protocolMinor = 0;
    std::string appName;
    int appRevision = 0;
    int tickPeriodUs = 0;
    int pingHz = 0;
    int frameBytes = 0;
    int clientMessageBytes = 0;
    int resumeGraceMs = 0;
    std::vector<std::string> sessionKinds;
    std::optional<std::vector<std::string>> realmKinds;
    std::vector<CatalogArchetype> archetypes;
    // In document order; a key repeated in the JSON keeps its last value, as JSON.parse does.
    std::vector<std::pair<std::string, std::vector<std::string>>> enums;
    std::vector<CatalogEvent> events;
    std::vector<CatalogCommand> commands;
    std::vector<CatalogGrid> grids;
    std::vector<CatalogMetric> metrics;

    // The names of enum `name`, or nullptr.
    const std::vector<std::string>* Enum(std::string_view name) const;
};

// A catalog breaks a wire rule, or is not a catalog at all. Problems() lists every problem found.
class CatalogError : public std::runtime_error {
public:
    explicit CatalogError(std::vector<std::string> problems);

    const std::vector<std::string>& Problems() const { return problems_; }

private:
    std::vector<std::string> problems_;
};

// Parses and validates catalog JSON received in WELCOME, and refuses it unless it is canonical. Throws CatalogError listing every
// problem: a client refuses a catalog it cannot decode at WELCOME, never mid-stream.
Catalog ParseCatalog(std::span<const std::uint8_t> utf8Json);
Catalog ParseCatalog(std::string_view utf8Json);

// Reads one codec object as the catalog reader does; throws CatalogError on a problem. For tools and tests.
CatalogCodec CodecFromJson(const json::Value& codec);

// The validation steps, exposed for tests: every wire rule the catalog breaks, and every canonical-order rule.
void ValidateCatalog(const Catalog& catalog, std::vector<std::string>& problems);
void CheckCanonical(const Catalog& catalog, std::vector<std::string>& problems);

}  // namespace typhon::client
