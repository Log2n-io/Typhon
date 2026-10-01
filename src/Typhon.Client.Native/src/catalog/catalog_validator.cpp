// The rules a client checks at WELCOME, as CatalogValidator.cs and the TypeScript SDK's catalog-validator.ts state them (03 § 10,
// "Precisions settled while building Phase 0"): a broken or hostile server fails at the handshake, never mid-stream.

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <set>
#include <string>
#include <tuple>

#include "catalog/catalog.hpp"
#include "wire/codec_kinds.hpp"
#include "wire/constants.hpp"
#include "wire/math.hpp"
#include "wire/utf8.hpp"

namespace typhon::client {

namespace {

constexpr long long MaxSafeInteger = 9007199254740991LL;

using Problems = std::vector<std::string>;

struct BuiltInMetric {
    const char* name;
    const char* unit;
    CodecKind codec;
    const char* kind;
    const char* scope;
    bool labelled;
};

constexpr std::array<BuiltInMetric, 11> BuiltInMetrics = {{
    {"typhon.tick.p50", "ms", CodecKind::F16, "gauge", "server", false},
    {"typhon.tick.p99", "ms", CodecKind::F16, "gauge", "server", false},
    {"typhon.system.mean", "ms", CodecKind::F16, "gauge", "server", true},
    {"typhon.archetype.entities", "count", CodecKind::Varu, "gauge", "server", true},
    {"typhon.sessions", "count", CodecKind::Varu, "gauge", "server", false},
    {"typhon.net.outBytesPerSec", "B/s", CodecKind::Varu, "gauge", "server", false},
    {"typhon.subscriptions.track.p99", "ms", CodecKind::F16, "gauge", "server", false},
    {"typhon.durability.wait.p99", "ms", CodecKind::F16, "gauge", "server", false},
    {"typhon.session.outBytesPerSec", "B/s", CodecKind::Varu, "gauge", "session", false},
    {"typhon.session.skippedFrames", "count", CodecKind::Varu, "counter", "session", false},
    {"typhon.session.droppedCommands", "count", CodecKind::Varu, "counter", "session", false},
}};

bool Less(const std::string& a, const std::string& b) { return utf8::CompareUtf16(a, b) < 0; }

int ReservedCommandIdx(const std::string& name)
{
    return name == BuiltIn::ClientRegion ? BuiltIn::ClientRegionIdx : name == BuiltIn::SubscribeRequest ? BuiltIn::SubscribeRequestIdx : -1;
}

int ReservedEventIdx(const std::string& name) { return name == BuiltIn::EventsLost ? BuiltIn::EventsLostIdx : -1; }

int ReservedMetricIdx(const std::string& name)
{
    for (std::size_t i = 0; i < BuiltInMetrics.size(); i++)
    {
        if (name == BuiltInMetrics[i].name)
        {
            return static_cast<int>(i);
        }
    }

    return -1;
}

bool Ascending(const std::vector<std::string>& values)
{
    for (std::size_t i = 1; i < values.size(); i++)
    {
        if (!Less(values[i - 1], values[i]))
        {
            return false;
        }
    }

    return true;
}

bool Contains(const std::vector<std::string>& values, const std::string& v) { return std::find(values.begin(), values.end(), v) != values.end(); }

int IndexOf(const std::vector<std::string>& values, const std::string& v)
{
    const auto it = std::find(values.begin(), values.end(), v);
    return it == values.end() ? -1 : static_cast<int>(it - values.begin());
}

// Fields ordered by (section, packed first, ordinal name) — W11; section 0 is onEnter, group g is section 1 + g.
bool FieldsInLayoutOrder(const std::vector<CatalogField>& fields, const std::vector<std::string>& groups)
{
    const auto key = [&](const CatalogField& f)
    {
        const int section = f.IsOnEnter() ? 0 : 1 + IndexOf(groups, f.group.value_or(""));
        const int packed = IsPacked(CodecKindOf(f.codec.t)) ? 0 : 1;
        return std::pair<int, int>(section, packed);
    };

    for (std::size_t i = 1; i < fields.size(); i++)
    {
        const auto [s0, p0] = key(fields[i - 1]);
        const auto [s1, p1] = key(fields[i]);
        const bool ordered = s0 != s1 ? s0 < s1 : p0 != p1 ? p0 < p1 : Less(fields[i - 1].name, fields[i].name);
        if (!ordered)
        {
            return false;
        }
    }

    return true;
}

int CompareGrids(const CatalogGrid& a, const CatalogGrid& b)
{
    if (a.tileCells != b.tileCells)
    {
        return a.tileCells < b.tileCells ? -1 : 1;
    }

    const std::size_t n = std::min(a.archetypes.size(), b.archetypes.size());
    for (std::size_t i = 0; i < n; i++)
    {
        if (a.archetypes[i] != b.archetypes[i])
        {
            return a.archetypes[i] < b.archetypes[i] ? -1 : 1;
        }
    }

    return static_cast<int>(a.archetypes.size()) - static_cast<int>(b.archetypes.size());
}

template <typename Item, typename Reserved>
void CheckReservedOrder(const std::vector<Item>& items, Reserved reservedIdx, int base, const std::string& what, Problems& p)
{
    int appRank = 0;
    const std::string* previousApp = nullptr;
    for (std::size_t i = 0; i < items.size(); i++)
    {
        const Item& item = items[i];
        if (i > 0 && !(items[i - 1].idx < item.idx))
        {
            p.push_back("the catalog is not canonical: " + what + "s are not in index order");
        }

        const int reserved = reservedIdx(item.name);
        if (reserved >= 0)
        {
            if (item.idx != reserved)
            {
                p.push_back("the catalog is not canonical: built-in " + what + " '" + item.name + "' is not at its reserved index "
                            + std::to_string(reserved));
            }

            continue;
        }

        if (item.idx != base + appRank || (previousApp != nullptr && !Less(*previousApp, item.name)))
        {
            p.push_back("the catalog is not canonical: " + what + " '" + item.name + "' is not at its ordinal index from " + std::to_string(base));
        }

        previousApp = &item.name;
        appRank++;
    }
}

void CheckStrings(const std::string& where, const std::vector<std::string>& values, long long maxUtf8Bytes, Problems& p)
{
    std::set<std::string> seen;
    for (const std::string& v : values)
    {
        if (v.empty() || static_cast<long long>(v.size()) > maxUtf8Bytes || seen.count(v) != 0)
        {
            p.push_back(where + ": '" + v + "' is empty, longer than " + std::to_string(maxUtf8Bytes) + " UTF-8 bytes, or listed twice");
        }

        seen.insert(v);
    }
}

void CheckRealmKinds(const std::optional<std::vector<std::string>>& kinds, Problems& p)
{
    if (!kinds.has_value())
    {
        return;
    }

    if (kinds->size() > static_cast<std::size_t>(protocol::MaxRealmKinds))
    {
        p.push_back("realmKinds: " + std::to_string(kinds->size()) + " kinds; at most " + std::to_string(protocol::MaxRealmKinds));
    }

    std::set<std::string> seen;
    for (const std::string& k : *kinds)
    {
        if (k.size() > static_cast<std::size_t>(protocol::SessionKindMaxBytes) || seen.count(k) != 0)
        {
            p.push_back("realmKinds: '" + k + "' is longer than " + std::to_string(protocol::SessionKindMaxBytes)
                        + " UTF-8 bytes, or listed twice");
        }

        seen.insert(k);
    }
}

template <typename Item>
void UniqueNames(const std::vector<Item>& items, const std::string& what, Problems& p)
{
    std::set<std::string> seen;
    for (const Item& item : items)
    {
        if (item.name.empty())
        {
            p.push_back("a " + what + " needs a name");
        }
        else if (seen.count(item.name) != 0)
        {
            p.push_back(what + " '" + item.name + "' is declared twice");
        }

        seen.insert(item.name);
    }
}

template <typename Item>
void UniqueIndices(const std::vector<Item>& items, const std::string& what, Problems& p)
{
    std::set<int> seen;
    for (const Item& item : items)
    {
        if (item.idx < 0 || item.idx > protocol::MaxMessageIndex)
        {
            p.push_back(what + " '" + item.name + "' has index " + std::to_string(item.idx) + ", outside 0.."
                        + std::to_string(protocol::MaxMessageIndex));
        }
        else if (seen.count(item.idx) != 0)
        {
            p.push_back(what + " index " + std::to_string(item.idx) + " is used twice");
        }

        seen.insert(item.idx);
    }
}

enum Parameter : int
{
    PBits = 1,
    PBounds = 2,
    PScale = 4,
    PUnitExp = 8,
    PN = 16,
    PMaxBytes = 32,
    PList = 64,
    PFixedBytes = 128,
    PCount = 256,
};

int ReadKindParameters(CodecKind kind);

int ReadParameters(CodecKind kind) { return ReadKindParameters(kind) | (TakesCount(kind) ? PCount : 0); }

int ReadKindParameters(CodecKind kind)
{
    switch (kind)
    {
        case CodecKind::Quant:
            return PBits | PBounds;
        case CodecKind::Pos2:
        case CodecKind::Pos3:
            // Realm-framed (typhon.3, SUB-30): bits and bounds are the REALM block's.
            return 0;
        case CodecKind::Vec2:
        case CodecKind::Vec3:
            return PBits | PScale;
        case CodecKind::Vel2:
        case CodecKind::Vel3:
            return PBits | PUnitExp;
        case CodecKind::Unorm:
        case CodecKind::Snorm:
        case CodecKind::Angle:
            return PBits;
        case CodecKind::Bits:
        case CodecKind::Bytes:
            return PN;
        case CodecKind::Str:
        case CodecKind::Blob:
            return PMaxBytes;
        case CodecKind::List:
            return PList;
        default:
            return 0;
    }
}

bool NonZero(const std::optional<int>& v) { return v.has_value() && *v != 0; }

bool IsByteWidth(const std::optional<int>& bits) { return bits.has_value() && (*bits == 8 || *bits == 16 || *bits == 24 || *bits == 32); }

void CheckBits(const std::string& at, const std::optional<int>& bits, Problems& p)
{
    if (!IsByteWidth(bits))
    {
        p.push_back(at + ": bits must be 8, 16, 24 or 32");
    }
}

// The gap from a finite, non-negative double to the next one up: C#'s Math.BitIncrement(x) - x.
double Ulp(double x) { return std::nextafter(x, std::numeric_limits<double>::infinity()) - x; }

void CheckBounds(const std::string& at, const CatalogCodec& codec, std::size_t axes, Problems& p)
{
    if (!codec.min.has_value() || !codec.max.has_value() || codec.min->size() != axes || codec.max->size() != axes)
    {
        p.push_back(at + ": min and max need " + std::to_string(axes) + " value(s) each");
        return;
    }

    if (!IsByteWidth(codec.bits))
    {
        return;
    }

    for (std::size_t i = 0; i < axes; i++)
    {
        const double lo = (*codec.min)[i];
        const double hi = (*codec.max)[i];
        if (!(std::isfinite(lo) && std::isfinite(hi) && hi > lo && std::isfinite(hi - lo)))
        {
            p.push_back(at + ": axis " + std::to_string(i) + " needs finite bounds with max > min and a finite range");
            continue;
        }

        // Below this step, min absorbs q * step and decoding stops round-tripping (W2).
        const double magnitude = std::max(std::fabs(lo), std::fabs(hi));
        if (math::QuantStep(lo, hi, *codec.bits) < math::Pow2(8) * Ulp(magnitude))
        {
            p.push_back(at + ": axis " + std::to_string(i) + " step is below 2^8 ulps of its bounds, so quantization would not round-trip");
        }
    }
}

void CheckCodec(const std::string& at, const CatalogCodec& codec, long long maxBytes, Problems& p)
{
    const CodecKind kind = CodecKindOf(codec.t);
    if (kind != CodecKind::Unknown)
    {
        // A parameter the kind does not read would be ignored here and honoured by another decoder: refused.
        const int present = (NonZero(codec.bits) ? PBits : 0) | (codec.min.has_value() || codec.max.has_value() ? PBounds : 0)
                            | (codec.scale.has_value() && *codec.scale != 0 ? PScale : 0) | (codec.unitExp.has_value() ? PUnitExp : 0)
                            | (NonZero(codec.n) ? PN : 0) | (NonZero(codec.maxBytes) ? PMaxBytes : 0)
                            | (codec.of != nullptr || NonZero(codec.minCount) || NonZero(codec.maxCount) ? PList : 0)
                            | (NonZero(codec.fixedBytes) ? PFixedBytes : 0) | (NonZero(codec.count) ? PCount : 0);
        if ((present & ~ReadParameters(kind)) != 0)
        {
            p.push_back(at + ": codec '" + codec.t + "' carries a parameter its kind does not read");
        }

        // Absent is one value, so a count of 1 has no spelling: it would hash differently from the same field without one.
        if (NonZero(codec.count) && TakesCount(kind) && !(*codec.count >= 2 && *codec.count <= protocol::MaxCount))
        {
            p.push_back(at + ": count must be 2.." + std::to_string(protocol::MaxCount) + "; leave it out for one value");
        }
    }

    switch (kind)
    {
        case CodecKind::Unknown:
        {
            const long long fixed = codec.fixedBytes.value_or(0);
            if (codec.t.empty() || !(fixed > 0 && fixed <= maxBytes))
            {
                p.push_back(at + ": codec '" + codec.t + "' is unknown and declares no usable fixedBytes, so it cannot be skipped");
            }

            break;
        }
        case CodecKind::Quant:
            CheckBits(at, codec.bits, p);
            CheckBounds(at, codec, 1, p);
            break;
        case CodecKind::Pos2:
        case CodecKind::Pos3:
            break;
        case CodecKind::Vec2:
        case CodecKind::Vec3:
            CheckBits(at, codec.bits, p);
            if (!(codec.scale.has_value() && *codec.scale > 0 && std::isfinite(*codec.scale)))
            {
                p.push_back(at + ": scale must be positive and finite");
            }

            break;
        case CodecKind::Vel2:
        case CodecKind::Vel3:
            CheckBits(at, codec.bits, p);
            if (!(codec.unitExp.has_value() && *codec.unitExp >= protocol::MinVelocityUnitExp && *codec.unitExp <= protocol::MaxVelocityUnitExp))
            {
                p.push_back(at + ": unitExp must be an integer in [" + std::to_string(protocol::MinVelocityUnitExp) + ", "
                            + std::to_string(protocol::MaxVelocityUnitExp) + "]");
            }

            break;
        case CodecKind::Unorm:
        case CodecKind::Snorm:
        case CodecKind::Angle:
            CheckBits(at, codec.bits, p);
            break;
        case CodecKind::Bits:
            if (!(codec.n.has_value() && *codec.n >= 1 && *codec.n <= protocol::MaxPackedBits))
            {
                p.push_back(at + ": bits.n must be in [1, " + std::to_string(protocol::MaxPackedBits) + "]");
            }

            break;
        case CodecKind::Str:
        case CodecKind::Blob:
            if (!(codec.maxBytes.has_value() && *codec.maxBytes > 0 && *codec.maxBytes <= maxBytes))
            {
                p.push_back(at + ": maxBytes must be positive and within limits.frameBytes");
            }

            break;
        case CodecKind::Bytes:
            if (!(codec.n.has_value() && *codec.n > 0 && *codec.n <= maxBytes))
            {
                p.push_back(at + ": bytes.n must be positive and within limits.frameBytes");
            }

            break;
        case CodecKind::List:
        {
            if (codec.of == nullptr)
            {
                p.push_back(at + ": a list needs an element codec");
                break;
            }

            if (!IsListElement(CodecKindOf(codec.of->t)))
            {
                p.push_back(at + ": a list element must be a numeric byte-aligned codec, not '" + codec.of->t + "'");
            }
            else if (NonZero(codec.of->count))
            {
                p.push_back(at + ": a list element is one value; a count belongs on a field");
            }
            else
            {
                CheckCodec(at + " element", *codec.of, maxBytes, p);
            }

            const int min = codec.minCount.value_or(0);
            const int max = codec.maxCount.value_or(0);
            if (min < 0 || min > max || max > protocol::MaxListCount)
            {
                p.push_back(at + ": list counts must satisfy 0 <= minCount <= maxCount <= " + std::to_string(protocol::MaxListCount));
            }

            break;
        }
        default:
            break;
    }
}

void CheckField(const std::string& at, const CatalogField& f, const Catalog& c, bool allowList, long long maxBytes, Problems& p)
{
    if (f.name.empty())
    {
        p.push_back(at + ": a field needs a name");
    }

    const CodecKind kind = CodecKindOf(f.codec.t);
    if (kind == CodecKind::Vel2 || kind == CodecKind::Vel3)
    {
        p.push_back(at + ": a vel codec is only valid inside a position");
    }

    if (kind == CodecKind::List && !allowList)
    {
        p.push_back(at + ": a list is only valid in event and command fields");
    }

    CheckCodec(at, f.codec, maxBytes, p);
    if (f.shape.has_value() && (f.shape->empty() || f.shape->size() > static_cast<std::size_t>(protocol::ShapeMaxBytes)))
    {
        // Any other value is accepted: a shape is a hint, and one this library does not know is ignored (W33).
        p.push_back(at + ": shape must be 1.." + std::to_string(protocol::ShapeMaxBytes) + " UTF-8 bytes");
    }

    if (!f.enumName.has_value() || f.enumName->empty())
    {
        return;
    }

    const std::vector<std::string>* names = c.Enum(*f.enumName);
    const std::string& t = f.codec.t;
    if (t != "bits" && t != "u8" && t != "u16" && t != "varu")
    {
        p.push_back(at + ": an enum is allowed only on bits, u8, u16 and varu, not '" + t + "'");
    }
    else if (NonZero(f.codec.count))
    {
        p.push_back(at + ": an enum names one value; it cannot carry a count");
    }
    else if (names == nullptr)
    {
        p.push_back(at + ": enum '" + *f.enumName + "' is not declared");
    }
    else
    {
        const int n = f.codec.n.value_or(0);
        const double capacity = kind == CodecKind::Bits ? math::Pow2(std::min(std::max(n, 1), 24)) : kind == CodecKind::U8 ? 256 : 65536;
        if (static_cast<double>(names->size()) > capacity)
        {
            p.push_back(at + ": enum '" + *f.enumName + "' has " + std::to_string(names->size()) + " names; the codec holds "
                        + std::to_string(static_cast<long long>(capacity)));
        }
    }
}

void CheckGroups(const std::string& where, const std::vector<std::string>& groups, bool allowEmpty, Problems& p)
{
    if (groups.size() > static_cast<std::size_t>(protocol::MaxGroups) || (!allowEmpty && groups.empty()))
    {
        p.push_back(where + ": " + std::to_string(groups.size()) + " groups; " + (allowEmpty ? "0" : "1") + ".."
                    + std::to_string(protocol::MaxGroups) + " allowed");
    }

    const std::set<std::string> unique(groups.begin(), groups.end());
    if (unique.size() != groups.size() || Contains(groups, ""))
    {
        p.push_back(where + ": a group is empty or declared twice");
    }
}

void CheckPosition(const std::string& where, const CatalogPosition& position, Problems& p)
{
    const CodecKind posKind = CodecKindOf(position.pos.t);
    const int dims = posKind == CodecKind::Pos2 ? 2 : posKind == CodecKind::Pos3 ? 3 : 0;
    if (dims == 0)
    {
        p.push_back(where + " position: pos must be a pos2 or pos3 codec");
    }
    else
    {
        CheckCodec(where + " position.pos", position.pos, MaxSafeInteger, p);
    }

    if (position.kind == "static")
    {
        if (position.model.has_value() || position.vel.has_value())
        {
            p.push_back(where + " position: a static position has no model and no vel");
        }
    }
    else if (position.kind == "motion")
    {
        if (position.model == "linear")
        {
            const CodecKind velKind = position.vel.has_value() ? CodecKindOf(position.vel->t) : CodecKind::Unknown;
            const int velDims = velKind == CodecKind::Vel2 ? 2 : velKind == CodecKind::Vel3 ? 3 : 0;
            if (velDims == 0 || velDims != dims)
            {
                p.push_back(where + " position: the linear model needs a vel codec matching pos's dimensions");
            }
            else
            {
                CheckCodec(where + " position.vel", *position.vel, MaxSafeInteger, p);
            }
        }
        else if (position.model == "none")
        {
            if (position.vel.has_value())
            {
                p.push_back(where + " position: the none model carries no vel");
            }
        }
        else
        {
            p.push_back(where + " position: model must be 'linear' or 'none'");
        }
    }
    else
    {
        p.push_back(where + " position: kind must be 'motion' or 'static'");
    }
}

void CheckArchetype(const CatalogArchetype& a, const Catalog& c, long long maxBytes, Problems& p)
{
    const std::string where = "archetype '" + a.name + "'";
    CheckGroups(where, a.groups, true, p);
    std::set<std::string> names;
    for (const CatalogField& f : a.fields)
    {
        const std::string at = where + " field '" + f.name + "'";
        if (names.count(f.name) != 0)
        {
            p.push_back(at + " is declared twice");
        }

        names.insert(f.name);
        const bool hasGroup = f.group.has_value() && !f.group->empty();
        if (hasGroup == f.IsOnEnter())
        {
            p.push_back(at + " must set exactly one of group and onEnter");
        }
        else if (hasGroup && !Contains(a.groups, *f.group))
        {
            p.push_back(at + " names group '" + *f.group + "', which the archetype does not declare");
        }

        CheckField(at, f, c, false, maxBytes, p);
    }

    if (a.position.has_value())
    {
        CheckPosition(where, *a.position, p);
    }

    if (a.owner.has_value())
    {
        CheckGroups(where + " owner", a.owner->groups, false, p);
        for (const CatalogField& f : a.owner->fields)
        {
            const std::string at = where + " owner field '" + f.name + "'";
            if (names.count(f.name) != 0)
            {
                p.push_back(at + " is declared twice");
            }

            names.insert(f.name);
            if (f.IsOnEnter() || !f.group.has_value() || !Contains(a.owner->groups, *f.group))
            {
                p.push_back(at + " must name one of the owner groups, and cannot be onEnter");
            }

            CheckField(at, f, c, false, maxBytes, p);
        }
    }
}

void CheckMessageFields(const std::string& where, const std::vector<CatalogField>& fields, const Catalog& c, long long maxBytes, Problems& p)
{
    std::set<std::string> names;
    for (const CatalogField& f : fields)
    {
        const std::string at = where + " field '" + f.name + "'";
        if (names.count(f.name) != 0)
        {
            p.push_back(at + " is declared twice");
        }

        names.insert(f.name);
        if ((f.group.has_value() && !f.group->empty()) || f.IsOnEnter())
        {
            p.push_back(at + ": event and command fields carry no group and no onEnter");
        }

        CheckField(at, f, c, true, maxBytes, p);
    }
}

// ClientRegion is recognised by shape, not by name (W28).
bool HasClientRegionShape(const CatalogCommand& cmd)
{
    const auto field = [&](const char* name) -> const CatalogCodec*
    {
        for (const CatalogField& f : cmd.fields)
        {
            if (f.name == name)
            {
                return &f.codec;
            }
        }

        return nullptr;
    };

    const CatalogCodec* vertices = field(BuiltIn::RegionVerticesField);
    const CatalogCodec* altitude = field(BuiltIn::RegionAltitudeField);
    const CatalogCodec* budget = field(BuiltIn::RegionBudgetField);
    bool plain = true;
    for (const CatalogField& f : cmd.fields)
    {
        plain = plain && !f.group.has_value() && !f.IsOnEnter() && !f.enumName.has_value();
    }

    return cmd.fields.size() == 3 && cmd.delivery == "latest" && cmd.rate.has_value() && cmd.rate->perSec == 5 && cmd.rate->burst == 5
           && vertices != nullptr && vertices->t == "list" && vertices->of != nullptr && vertices->of->t == "pos3"
           && vertices->minCount == 3 && vertices->maxCount == 16 && altitude != nullptr && altitude->t == "f16" && budget != nullptr
           && budget->t == "u16" && plain;
}

void CheckCommand(const CatalogCommand& cmd, const Catalog& c, long long maxBytes, Problems& p)
{
    if (cmd.delivery != "queued" && cmd.delivery != "latest")
    {
        p.push_back("command '" + cmd.name + "': delivery must be 'queued' or 'latest'");
    }

    if (cmd.rate.has_value() && (cmd.rate->perSec <= 0 || cmd.rate->burst <= 0))
    {
        p.push_back("command '" + cmd.name + "': rate.perSec and rate.burst must be positive");
    }

    if (cmd.name == BuiltIn::ClientRegion && !HasClientRegionShape(cmd))
    {
        p.push_back("command '" + cmd.name + "' is a built-in whose shape does not match its definition");
    }
    else if (cmd.name == BuiltIn::SubscribeRequest)
    {
        p.push_back("command '" + cmd.name + "' is reserved; its shape is decided when source subscriptions are built");
    }

    CheckMessageFields("command '" + cmd.name + "'", cmd.fields, c, maxBytes, p);
}

void CheckMetric(const CatalogMetric& m, Problems& p)
{
    const std::string where = "metric '" + m.name + "'";
    const int reserved = ReservedMetricIdx(m.name);
    if (reserved >= 0)
    {
        const BuiltInMetric& b = BuiltInMetrics[static_cast<std::size_t>(reserved)];
        const bool shaped = m.unit == b.unit && CodecKindOf(m.codec.t) == b.codec && m.kind.value_or("gauge") == b.kind
                            && m.scope.value_or("server") == b.scope && m.labels.has_value() == b.labelled;
        if (!shaped)
        {
            p.push_back(where + " is a built-in whose shape does not match its definition");
        }
    }
    else if (m.name.rfind("typhon.", 0) == 0)
    {
        p.push_back(where + ": the 'typhon.' prefix is reserved for built-in metrics");
    }

    if (m.scope.has_value() && *m.scope != "server" && *m.scope != "session")
    {
        p.push_back(where + ": scope must be 'server' or 'session'");
    }

    if (m.kind.has_value() && *m.kind != "gauge" && *m.kind != "counter")
    {
        p.push_back(where + ": kind must be 'gauge' or 'counter'");
    }

    if (m.labels.has_value())
    {
        if (m.labels->empty())
        {
            p.push_back(where + ": a vector metric needs at least one label");
        }

        CheckStrings(where + " labels", *m.labels, MaxSafeInteger, p);
    }

    switch (CodecKindOf(m.codec.t))
    {
        case CodecKind::U8:
        case CodecKind::U16:
        case CodecKind::U32:
        case CodecKind::I8:
        case CodecKind::I16:
        case CodecKind::I32:
        case CodecKind::Varu:
        case CodecKind::Vari:
        case CodecKind::F16:
        case CodecKind::F32:
        case CodecKind::Unorm:
        case CodecKind::Quant:
        case CodecKind::Unknown:
            break;
        default:
            p.push_back(where + ": codec '" + m.codec.t + "' is not a metric codec");
            break;
    }

    if (NonZero(m.codec.count))
    {
        p.push_back(where + ": a metric is one value per label; it cannot carry a count");
    }

    CheckCodec(where, m.codec, MaxSafeInteger, p);
}

void CheckGrids(const std::vector<CatalogGrid>& grids, std::size_t archetypeCount, Problems& p)
{
    std::vector<std::pair<std::string, std::size_t>> shapes;
    for (std::size_t i = 0; i < grids.size(); i++)
    {
        const CatalogGrid& g = grids[i];
        const std::string where = "grid " + std::to_string(i);
        if (g.tileCells < 1)
        {
            p.push_back(where + ": tileCells must be at least 1");
        }

        std::set<int> seen;
        for (const int idx : g.archetypes)
        {
            if (idx < 0 || static_cast<std::size_t>(idx) >= archetypeCount || seen.count(idx) != 0)
            {
                p.push_back(where + ": archetype index " + std::to_string(idx) + " does not exist or is listed twice");
            }

            seen.insert(idx);
        }

        std::string key = std::to_string(g.tileCells) + "#";
        for (std::size_t k = 0; k < g.archetypes.size(); k++)
        {
            key += (k == 0 ? "" : ",") + std::to_string(g.archetypes[k]);
        }

        bool duplicate = false;
        for (const auto& [shape, first] : shapes)
        {
            if (shape == key)
            {
                p.push_back(where + " duplicates grid " + std::to_string(first));
                duplicate = true;
                break;
            }
        }

        if (!duplicate)
        {
            shapes.emplace_back(key, i);
        }
    }
}

}  // namespace

void ValidateCatalog(const Catalog& c, Problems& p)
{
    if (c.protocolMajor != protocol::Major || c.protocolMinor < 0)
    {
        p.push_back("protocol " + std::to_string(c.protocolMajor) + "." + std::to_string(c.protocolMinor) + "; this client speaks major "
                    + std::to_string(protocol::Major));
    }

    if (c.appName.empty())
    {
        p.push_back("app.name is missing");
    }

    if (c.tickPeriodUs <= 0 || c.pingHz <= 0)
    {
        p.push_back("tick.periodUs and tick.pingHz must be positive");
    }

    long long maxBytes = MaxSafeInteger;
    if (c.frameBytes <= 0 || c.clientMessageBytes <= 0 || c.resumeGraceMs < 0)
    {
        p.push_back("limits.frameBytes and limits.clientMessageBytes must be positive, limits.resumeGraceMs non-negative");
    }
    else
    {
        maxBytes = c.frameBytes;
    }

    CheckStrings("sessionKinds", c.sessionKinds, protocol::SessionKindMaxBytes, p);
    CheckRealmKinds(c.realmKinds, p);
    for (const auto& [name, names] : c.enums)
    {
        if (name.empty())
        {
            p.push_back("an enum needs a name");
        }

        CheckStrings("enum '" + name + "'", names, MaxSafeInteger, p);
        if (names.empty())
        {
            p.push_back("enum '" + name + "' has no names");
        }
    }

    if (c.archetypes.size() > static_cast<std::size_t>(protocol::MaxArchetypes))
    {
        p.push_back(std::to_string(c.archetypes.size()) + " archetypes; at most " + std::to_string(protocol::MaxArchetypes));
    }

    UniqueNames(c.archetypes, "archetype", p);
    for (const CatalogArchetype& a : c.archetypes)
    {
        CheckArchetype(a, c, maxBytes, p);
    }

    UniqueNames(c.events, "event", p);
    UniqueIndices(c.events, "event", p);
    for (const CatalogEvent& e : c.events)
    {
        if (e.scope.empty())
        {
            p.push_back("event '" + e.name + "' needs a scope");
        }

        CheckMessageFields("event '" + e.name + "'", e.fields, c, maxBytes, p);
    }

    UniqueNames(c.commands, "command", p);
    UniqueIndices(c.commands, "command", p);
    for (const CatalogCommand& cmd : c.commands)
    {
        CheckCommand(cmd, c, maxBytes, p);
    }

    UniqueNames(c.metrics, "metric", p);
    UniqueIndices(c.metrics, "metric", p);
    for (const CatalogMetric& m : c.metrics)
    {
        CheckMetric(m, p);
    }

    CheckGrids(c.grids, c.archetypes.size(), p);
}

void CheckCanonical(const Catalog& c, Problems& p)
{
    const auto fail = [&](const std::string& what) { p.push_back("the catalog is not canonical: " + what); };

    if (c.realmKinds.has_value() && !Ascending(*c.realmKinds))
    {
        fail("realmKinds are not in ordinal order");
    }

    if (!Ascending(c.sessionKinds))
    {
        fail("sessionKinds are not in ordinal order");
    }

    // Enum key order is not checked: it never affects decoding (a field names its enum), and JSON.parse reorders integer-like keys.

    for (std::size_t i = 0; i < c.archetypes.size(); i++)
    {
        const CatalogArchetype& a = c.archetypes[i];
        if (a.idx != static_cast<int>(i) || (i > 0 && !Less(c.archetypes[i - 1].name, a.name)))
        {
            fail("archetype '" + a.name + "' is not at its ordinal position, or its idx is not that position");
        }

        if (!Ascending(a.groups) || !FieldsInLayoutOrder(a.fields, a.groups))
        {
            fail("archetype '" + a.name + "' groups or fields are not in wire order");
        }

        if (a.owner.has_value() && (!Ascending(a.owner->groups) || !FieldsInLayoutOrder(a.owner->fields, a.owner->groups)))
        {
            fail("archetype '" + a.name + "' owner groups or fields are not in wire order");
        }
    }

    static const std::vector<std::string> NoGroups;
    CheckReservedOrder(c.events, ReservedEventIdx, protocol::FirstAppEventIdx, "event", p);
    for (const CatalogEvent& e : c.events)
    {
        if (!FieldsInLayoutOrder(e.fields, NoGroups))
        {
            fail("event '" + e.name + "' fields are not in wire order");
        }
    }

    CheckReservedOrder(c.commands, ReservedCommandIdx, protocol::FirstAppCommandIdx, "command", p);
    for (const CatalogCommand& cmd : c.commands)
    {
        if (!FieldsInLayoutOrder(cmd.fields, NoGroups))
        {
            fail("command '" + cmd.name + "' fields are not in wire order");
        }
    }

    CheckReservedOrder(c.metrics, ReservedMetricIdx, protocol::FirstAppMetricIdx, "metric", p);
    for (const CatalogMetric& m : c.metrics)
    {
        if (m.scope == "server" || m.kind == "gauge")
        {
            fail("metric '" + m.name + "' spells out a default scope or kind");
        }
    }

    for (std::size_t i = 0; i < c.grids.size(); i++)
    {
        const CatalogGrid& g = c.grids[i];
        if (g.idx != static_cast<int>(i) || (i > 0 && CompareGrids(c.grids[i - 1], g) >= 0))
        {
            fail("grid " + std::to_string(g.idx) + " is not at its sorted position, or duplicates the grid before it");
        }
    }
}

}  // namespace typhon::client
