#include "store/schema.hpp"

#include <algorithm>
#include <stdexcept>
#include <unordered_set>

#include "catalog/catalog_plan.hpp"

namespace typhon::client {

void ValidateSchema(const WorldSchema& schema)
{
    if (schema.tickPeriodUs <= 0)
    {
        throw std::invalid_argument("A world needs a positive tick period in microseconds, got " + std::to_string(schema.tickPeriodUs));
    }

    if (schema.archetypes.size() > 255)
    {
        throw std::invalid_argument("A world holds at most 255 archetypes, got " + std::to_string(schema.archetypes.size()));
    }

    for (std::size_t i = 0; i < schema.archetypes.size(); i++)
    {
        const ArchetypeSchema& archetype = schema.archetypes[i];
        const std::string where = "Archetype '" + archetype.name + "'";
        if (archetype.index != static_cast<int>(i))
        {
            throw std::invalid_argument(where + " has index " + std::to_string(archetype.index) + " at position " + std::to_string(i));
        }

        if (archetype.groups.size() > MaxGroups)
        {
            throw std::invalid_argument(where + " declares " + std::to_string(archetype.groups.size()) + " groups; the update mask holds 8");
        }

        if (archetype.position.has_value() && archetype.position->dims != 2 && archetype.position->dims != 3)
        {
            throw std::invalid_argument(where + " has a position of " + std::to_string(archetype.position->dims) +
                                        " dimensions; 2 or 3 expected");
        }

        std::unordered_set<std::string> names;
        for (const FieldSchema& field : archetype.fields)
        {
            if (!names.insert(field.name).second)
            {
                throw std::invalid_argument(where + " declares field '" + field.name + "' twice");
            }

            if (field.group < -1 || field.group >= static_cast<int>(archetype.groups.size()))
            {
                throw std::invalid_argument("Field '" + archetype.name + "." + field.name + "' names group " + std::to_string(field.group) +
                                            ", which does not exist");
            }

            if (IsNumericKind(field.kind) && (field.components < 1 || field.components > 4))
            {
                throw std::invalid_argument("Field '" + archetype.name + "." + field.name + "' has " + std::to_string(field.components) +
                                            " components; 1 to 4 expected");
            }
        }
    }
}

std::optional<FieldKind> FieldKindOf(const FieldPlan& field)
{
    switch (field.kind)
    {
        case CodecKind::Bool:
        case CodecKind::U8:
            return FieldKind::U8;
        case CodecKind::Bits:
            return field.bitCount <= 8 ? FieldKind::U8 : field.bitCount <= 16 ? FieldKind::U16 : FieldKind::U32;
        case CodecKind::I8:
            return FieldKind::I8;
        case CodecKind::U16:
            return FieldKind::U16;
        case CodecKind::I16:
            return FieldKind::I16;
        case CodecKind::U32:
        case CodecKind::Varu:
        case CodecKind::EntityRef:
        case CodecKind::TickLo:
            return FieldKind::U32;
        case CodecKind::I32:
        case CodecKind::Vari:
            return FieldKind::I32;
        case CodecKind::F32:
        case CodecKind::F16:
            return FieldKind::F32;
        case CodecKind::Str:
            return FieldKind::Text;
        case CodecKind::Bytes:
        case CodecKind::Blob:
            return FieldKind::Bytes;
        case CodecKind::Unknown:
            return std::nullopt;
        default:
            return FieldKind::F64;
    }
}

WorldSchema WorldSchemaFromCatalog(const CatalogPlan& plan)
{
    WorldSchema schema;
    schema.tickPeriodUs = plan.GetCatalog().tickPeriodUs;
    for (const auto& a : plan.Archetypes())
    {
        ArchetypeSchema archetype;
        archetype.index = a->idx;
        archetype.name = a->name;
        archetype.groups = a->groups;
        for (const auto& f : a->fields)
        {
            const std::optional<FieldKind> kind = FieldKindOf(*f);
            if (!kind.has_value())
            {
                continue;
            }

            FieldSchema field;
            field.name = f->name;
            field.kind = *kind;
            field.components = IsNumericKind(*kind) ? f->components : 1;
            if (f->field != nullptr && !f->field->IsOnEnter() && f->field->group.has_value())
            {
                const auto at = std::find(a->groups.begin(), a->groups.end(), *f->field->group);
                field.group = at == a->groups.end() ? -1 : static_cast<int>(at - a->groups.begin());
            }

            archetype.fields.push_back(std::move(field));
        }

        if (a->position != nullptr)
        {
            archetype.position = PositionSchema{a->position->moving, a->position->moving && a->position->linear,
                                                a->position->dims == 3 ? 3 : 2};
        }

        schema.archetypes.push_back(std::move(archetype));
    }

    return schema;
}

}  // namespace typhon::client
