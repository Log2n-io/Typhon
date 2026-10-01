#include "catalog/catalog_plan.hpp"

#include <algorithm>
#include <string>

#include "wire/constants.hpp"
#include "wire/errors.hpp"
#include "wire/math.hpp"

namespace typhon::client {

namespace {

CatalogError Refuse(const std::string& problem) { return CatalogError({problem}); }

bool IsByteWidth(int bits) { return bits == 8 || bits == 16 || bits == 24 || bits == 32; }

std::unique_ptr<SectionPlan> BuildSection(const std::vector<CatalogField>& declared, const auto& belongs,
                                          std::vector<std::unique_ptr<FieldPlan>>& all, const Catalog& catalog)
{
    std::vector<FieldPlan*> fields;
    for (const CatalogField& f : declared)
    {
        if (belongs(f))
        {
            all.push_back(std::make_unique<FieldPlan>(f.name, static_cast<int>(all.size()), &f, f.codec, catalog));
            fields.push_back(all.back().get());
        }
    }

    return std::make_unique<SectionPlan>(std::move(fields));
}

}  // namespace

FieldPlan::FieldPlan(std::string fieldName, int fieldIndex, const CatalogField* catalogField, const CatalogCodec& catalogCodec,
                     const Catalog& catalog)
    : name(std::move(fieldName)), index(fieldIndex), field(catalogField), codec(catalogCodec)
{
    kind = CodecKindOf(codec.t);
    packed = IsPacked(kind);
    bitCount = kind == CodecKind::Bool ? 1 : kind == CodecKind::Bits ? codec.n.value_or(0) : 0;
    bits = codec.bits.value_or(0);
    scale = codec.scale.value_or(0);
    n = codec.n.value_or(0);
    maxBytes = codec.maxBytes.value_or(0);
    fixedBytes = codec.fixedBytes.value_or(0);
    minCount = codec.minCount.value_or(0);
    maxCount = codec.maxCount.value_or(0);
    if (field != nullptr && field->enumName.has_value())
    {
        enumNames = catalog.Enum(*field->enumName);
    }

    // A count sizes the decoder's buffers, so it is bounded here whether or not the catalog was validated (W33). A codec newer than
    // this library is skipped by its fixedBytes, whatever count it carries. 0 is absent, as the validator reads it. A value no field
    // holds (a list element, a metric, a position) is one value: its buffers are sized for that.
    const bool declared = kind != CodecKind::Unknown && codec.count.has_value() && *codec.count != 0;
    if (declared && !(field != nullptr && TakesCount(kind) && *codec.count >= 2 && *codec.count <= protocol::MaxCount))
    {
        throw Refuse("field '" + name + "' carries count " + std::to_string(*codec.count) + " on '" + codec.t + "'");
    }

    count = declared ? *codec.count : 1;
    components = count;
    switch (kind)
    {
        case CodecKind::U64:
        case CodecKind::I64:
        case CodecKind::Varu64:
        case CodecKind::Vari64:
            valueKind = ValueKind::Integer64;
            break;
        case CodecKind::Pos2:
        case CodecKind::Vec2:
        case CodecKind::Vel2:
            components = 2;
            break;
        case CodecKind::Pos3:
        case CodecKind::Vec3:
        case CodecKind::Vel3:
            components = 3;
            break;
        case CodecKind::Quat3:
            components = 4;
            break;
        case CodecKind::Str:
            valueKind = ValueKind::Text;
            components = 0;
            break;
        case CodecKind::Bytes:
        case CodecKind::Blob:
            valueKind = ValueKind::Bytes;
            components = 0;
            break;
        case CodecKind::List:
            valueKind = ValueKind::List;
            components = 0;
            break;
        case CodecKind::Unknown:
            valueKind = ValueKind::Skipped;
            components = 0;
            break;
        default:
            break;
    }

    // Refuses a width no reader accepts, so a decode never meets one.
    switch (kind)
    {
        case CodecKind::Quant:
        case CodecKind::Vec2:
        case CodecKind::Vec3:
        case CodecKind::Vel2:
        case CodecKind::Vel3:
        case CodecKind::Unorm:
        case CodecKind::Snorm:
        case CodecKind::Angle:
            if (!IsByteWidth(bits))
            {
                throw Refuse("field '" + name + "': " + codec.t + " needs bits of 8, 16, 24 or 32");
            }

            break;
        case CodecKind::Bits:
            if (!(n >= 1 && n <= 24))
            {
                throw Refuse("field '" + name + "': bits.n must be in [1, 24]");
            }

            break;
        case CodecKind::Bytes:
            if (n < 0)
            {
                throw Refuse("field '" + name + "': bytes.n must be a non-negative integer");
            }

            break;
        case CodecKind::Str:
        case CodecKind::Blob:
            if (maxBytes < 0)
            {
                throw Refuse("field '" + name + "': maxBytes must be a non-negative integer");
            }

            break;
        case CodecKind::Unknown:
            if (fixedBytes <= 0)
            {
                throw Refuse("field '" + name + "': codec '" + codec.t + "' is unknown and declares no fixedBytes");
            }

            break;
        default:
            break;
    }

    if (kind == CodecKind::List)
    {
        // A list decodes into a buffer sized for 255 elements of at most 4 numbers: its element must be numeric and its count capped.
        if (codec.of == nullptr || !IsListElement(CodecKindOf(codec.of->t)) || !(maxCount >= 0 && maxCount <= 255))
        {
            throw Refuse("list '" + name + "' needs a numeric element codec and at most 255 elements");
        }

        element = std::make_unique<FieldPlan>(name, -1, nullptr, *codec.of, catalog);
        components = element->components;
    }

    // A position's quantum is the realm frame's (typhon.3, SUB-30): RealmFrame.step, per frame.
    if (kind == CodecKind::Quant)
    {
        if (!codec.min.has_value() || !codec.max.has_value() || codec.min->size() != 1 || codec.max->size() != 1)
        {
            throw Refuse("field '" + name + "': " + codec.t + " needs 1 min and max value(s)");
        }

        min[0] = (*codec.min)[0];
        step[0] = math::QuantStep((*codec.min)[0], (*codec.max)[0], bits);
    }

    if (kind == CodecKind::Vel2 || kind == CodecKind::Vel3)
    {
        const std::optional<int> e = codec.unitExp;
        if (!e.has_value() || *e < protocol::MinVelocityUnitExp || *e > protocol::MaxVelocityUnitExp)
        {
            throw Refuse("field '" + name + "': a velocity needs unitExp in [" + std::to_string(protocol::MinVelocityUnitExp) + ", "
                         + std::to_string(protocol::MaxVelocityUnitExp) + "]");
        }

        velocityUnit = math::Pow2(*e);
    }

    const bool byteAligned = IsByteWidth(bits);
    top = byteAligned ? math::UnsignedTop(bits) : 0;
    limit = byteAligned ? math::SymmetricLimit(bits) : 0;
}

SectionPlan::SectionPlan(std::vector<FieldPlan*> sectionFields) : fields(std::move(sectionFields))
{
    int bitsUsed = 0;
    int packedSoFar = 0;
    for (std::size_t i = 0; i < fields.size(); i++)
    {
        FieldPlan& f = *fields[i];
        if (f.packed)
        {
            if (static_cast<std::size_t>(packedSoFar) != i)
            {
                throw Refuse("field '" + f.name + "' is packed but follows a byte-aligned field: not in wire order (W11)");
            }

            f.bitOffset = bitsUsed;
            bitsUsed += f.bitCount;
            packedSoFar++;
        }
    }

    packedCount = packedSoFar;
    packBytes = (bitsUsed + 7) >> 3;
}

PositionPlan::PositionPlan(const CatalogPosition& position, const Catalog& catalog)
{
    moving = position.kind == "motion";
    linear = moving && position.model == "linear";
    pos = std::make_unique<FieldPlan>("position", -1, nullptr, position.pos, catalog);
    if (pos->kind != CodecKind::Pos2 && pos->kind != CodecKind::Pos3)
    {
        throw Refuse("a position needs a pos2 or pos3 codec, not '" + position.pos.t + "'");
    }

    dims = pos->components;
    if (linear)
    {
        const CodecKind velKind = position.vel.has_value() ? CodecKindOf(position.vel->t) : CodecKind::Unknown;
        if (velKind != CodecKind::Vel2 && velKind != CodecKind::Vel3)
        {
            throw Refuse("the linear model needs a vel2 or vel3 codec");
        }

        vel = std::make_unique<FieldPlan>("velocity", -1, nullptr, *position.vel, catalog);
        if (vel->components != dims)
        {
            throw Refuse("the linear model needs a vel codec matching pos's dimensions");
        }
    }
}

ArchetypePlan::ArchetypePlan(const CatalogArchetype& a, const Catalog& catalog) : archetype(&a), idx(a.idx), name(a.name), groups(a.groups)
{
    if (a.groups.size() > 8 || (a.owner.has_value() && a.owner->groups.size() > 8))
    {
        throw Refuse("archetype '" + a.name + "' has more groups than a u8 mask addresses");
    }

    if (a.position.has_value())
    {
        position = std::make_unique<PositionPlan>(*a.position, catalog);
    }

    onEnter = BuildSection(a.fields, [](const CatalogField& f) { return f.IsOnEnter(); }, fields, catalog);
    for (const std::string& group : a.groups)
    {
        auto section = BuildSection(a.fields, [&](const CatalogField& f) { return !f.IsOnEnter() && f.group == group; }, fields, catalog);
        groupSections.push_back(std::move(*section));
    }

    if (a.owner.has_value())
    {
        ownerGroups = a.owner->groups;
        for (const std::string& group : ownerGroups)
        {
            auto section = BuildSection(a.owner->fields, [&](const CatalogField& f) { return f.group == group; }, ownerFields, catalog);
            ownerSections.push_back(std::move(*section));
        }
    }
}

MessagePlan::MessagePlan(int messageIdx, std::string messageName, const std::vector<CatalogField>& declared, const Catalog& catalog)
    : idx(messageIdx), name(std::move(messageName))
{
    std::vector<FieldPlan*> sectionFields;
    for (std::size_t i = 0; i < declared.size(); i++)
    {
        fields.push_back(std::make_unique<FieldPlan>(declared[i].name, static_cast<int>(i), &declared[i], declared[i].codec, catalog));
        sectionFields.push_back(fields.back().get());
    }

    body = std::make_unique<SectionPlan>(std::move(sectionFields));
}

MetricPlan::MetricPlan(const CatalogMetric& m, int valueOffset, const Catalog& catalog)
    : metric(&m), idx(m.idx), name(m.name), session(m.scope == "session"), offset(valueOffset)
{
    valueCount = m.labels.has_value() ? static_cast<int>(m.labels->size()) : 1;
    value = std::make_unique<FieldPlan>(m.name, -1, nullptr, m.codec, catalog);
    // A metric is a byte-aligned number: STATS has no bit pack, so bool and bits have nowhere to travel.
    if ((value->valueKind != ValueKind::Number && value->valueKind != ValueKind::Skipped) || value->packed)
    {
        throw Refuse("metric '" + m.name + "': codec '" + m.codec.t + "' is not a byte-aligned number");
    }
}

GridPlan::GridPlan(const CatalogGrid& g, std::size_t archetypeCount) : grid(&g), idx(g.idx), tileCells(g.tileCells)
{
    if (g.tileCells < 1)
    {
        throw Refuse("grid " + std::to_string(g.idx) + " needs a tile of at least one replication cell");
    }

    for (const int a : g.archetypes)
    {
        if (a < 0 || static_cast<std::size_t>(a) >= archetypeCount)
        {
            throw Refuse("grid " + std::to_string(g.idx) + " counts archetype " + std::to_string(a) + ", which does not exist");
        }
    }

    counts.assign(g.archetypes.size(), 0);
}

std::shared_ptr<const CatalogPlan> CatalogPlan::Compile(std::shared_ptr<const Catalog> catalog)
{
    return std::shared_ptr<const CatalogPlan>(new CatalogPlan(std::move(catalog)));
}

CatalogPlan::CatalogPlan(std::shared_ptr<const Catalog> catalog) : catalog_(std::move(catalog))
{
    const Catalog& c = *catalog_;
    // The store sizes each slot's motion ring from it.
    if (c.tickPeriodUs <= 0)
    {
        throw Refuse("tick period of " + std::to_string(c.tickPeriodUs) + " us; a positive integer expected");
    }

    if (c.archetypes.size() > 255)
    {
        throw Refuse(std::to_string(c.archetypes.size()) + " archetypes; a store addresses at most 255");
    }

    for (std::size_t i = 0; i < c.archetypes.size(); i++)
    {
        if (c.archetypes[i].idx != static_cast<int>(i))
        {
            throw Refuse("archetype '" + c.archetypes[i].name + "' has index " + std::to_string(c.archetypes[i].idx) + " at position "
                         + std::to_string(i));
        }

        archetypes_.push_back(std::make_unique<ArchetypePlan>(c.archetypes[i], c));
    }

    const auto indexed = [](std::vector<std::unique_ptr<MessagePlan>>& plans, std::vector<const MessagePlan*>& byIndex)
    {
        int max = -1;
        for (const auto& p : plans)
        {
            if (p->idx < 0 || p->idx > protocol::MaxMessageIndex)
            {
                throw Refuse("wire index " + std::to_string(p->idx) + " is outside [0, " + std::to_string(protocol::MaxMessageIndex) + "]");
            }

            max = std::max(max, p->idx);
        }

        byIndex.assign(static_cast<std::size_t>(max + 1), nullptr);
        for (const auto& p : plans)
        {
            if (byIndex[static_cast<std::size_t>(p->idx)] != nullptr)
            {
                throw Refuse("wire index " + std::to_string(p->idx) + " is assigned twice");
            }

            byIndex[static_cast<std::size_t>(p->idx)] = p.get();
        }
    };

    for (const CatalogEvent& e : c.events)
    {
        eventPlans_.push_back(std::make_unique<MessagePlan>(e.idx, e.name, e.fields, c));
    }

    indexed(eventPlans_, events_);
    for (const CatalogCommand& cmd : c.commands)
    {
        commandPlans_.push_back(std::make_unique<MessagePlan>(cmd.idx, cmd.name, cmd.fields, c));
    }

    indexed(commandPlans_, commands_);

    int offset = 0;
    int previousIdx = -1;
    for (const CatalogMetric& m : c.metrics)
    {
        if (!(m.idx > previousIdx && m.idx <= protocol::MaxMessageIndex))
        {
            throw Refuse("metric '" + m.name + "' is out of index order: STATS segments are laid out in index order");
        }

        previousIdx = m.idx;
        metrics_.push_back(std::make_unique<MetricPlan>(m, offset, c));
        offset += metrics_.back()->valueCount;
    }

    metricValueCount_ = offset;
    for (const auto& m : metrics_)
    {
        (m->session ? sessionMetrics_ : serverMetrics_).push_back(m.get());
    }

    realmKinds_ = c.realmKinds.has_value() && !c.realmKinds->empty() ? *c.realmKinds : std::vector<std::string>{""};
    for (std::size_t i = 0; i < c.grids.size(); i++)
    {
        if (c.grids[i].idx != static_cast<int>(i))
        {
            throw Refuse("grid at position " + std::to_string(i) + " has index " + std::to_string(c.grids[i].idx));
        }

        grids_.push_back(std::make_unique<GridPlan>(c.grids[i], c.archetypes.size()));
    }
}

const ArchetypePlan& CatalogPlan::Archetype(std::uint32_t idx) const
{
    if (idx >= archetypes_.size())
    {
        throw Malformed("archetype index " + std::to_string(idx) + " does not exist");
    }

    return *archetypes_[idx];
}

const MessagePlan& CatalogPlan::Event(std::uint32_t idx) const
{
    if (idx >= events_.size() || events_[idx] == nullptr)
    {
        throw Malformed("event index " + std::to_string(idx) + " does not exist");
    }

    return *events_[idx];
}

const MessagePlan& CatalogPlan::Command(std::uint32_t idx) const
{
    if (idx >= commands_.size() || commands_[idx] == nullptr)
    {
        throw Malformed("command index " + std::to_string(idx) + " does not exist");
    }

    return *commands_[idx];
}

GridPlan& CatalogPlan::Grid(std::uint32_t idx) const
{
    if (idx >= grids_.size())
    {
        throw Malformed("grid index " + std::to_string(idx) + " does not exist");
    }

    return *grids_[idx];
}

const ArchetypePlan* CatalogPlan::ArchetypeByName(std::string_view name) const
{
    for (const auto& a : archetypes_)
    {
        if (a->name == name)
        {
            return a.get();
        }
    }

    return nullptr;
}

const MessagePlan* CatalogPlan::EventByName(std::string_view name) const
{
    for (const auto& e : eventPlans_)
    {
        if (e->name == name)
        {
            return e.get();
        }
    }

    return nullptr;
}

const MessagePlan* CatalogPlan::CommandByName(std::string_view name) const
{
    for (const auto& c : commandPlans_)
    {
        if (c->name == name)
        {
            return c.get();
        }
    }

    return nullptr;
}

const MetricPlan* CatalogPlan::MetricByName(std::string_view name) const
{
    for (const auto& m : metrics_)
    {
        if (m->name == name)
        {
            return m.get();
        }
    }

    return nullptr;
}

const MessagePlan* CatalogPlan::ClientRegion() const
{
    return static_cast<std::size_t>(BuiltIn::ClientRegionIdx) < commands_.size() ? commands_[BuiltIn::ClientRegionIdx] : nullptr;
}

}  // namespace typhon::client
