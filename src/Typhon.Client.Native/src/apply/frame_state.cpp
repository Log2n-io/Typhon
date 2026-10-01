#include "apply/frame_state.hpp"

#include <algorithm>
#include <limits>

namespace typhon::client {

namespace {

constexpr double NaN = std::numeric_limits<double>::quiet_NaN();

void Assign(Vec<std::uint8_t>& target, const std::uint8_t* data, std::size_t length) { target.assign(data, data + length); }

}  // namespace

void SelfState::Receive(const ArchetypePlan* owner, std::uint32_t id, std::uint32_t seq, std::uint8_t mask)
{
    if (owner == nullptr)
    {
        // Dropped only when there is something to drop: a spectator is acknowledged every frame it sends commands in.
        if (archetype != nullptr)
        {
            archetype = nullptr;
            Shape(nullptr);
        }
    }
    else if (archetype != owner || netId != id)
    {
        archetype = owner;
        Shape(owner);
    }

    netId = owner == nullptr ? 0 : id;
    lastSeq = seq;
    ownerMask = mask;
    received = true;
    version++;
}

void SelfState::Shape(const ArchetypePlan* owner)
{
    const std::size_t count = owner == nullptr ? 0 : owner->ownerFields.size();
    numbers_.resize(count);
    integers_.resize(count);
    values_.resize(count);
    present_.assign(count, 0);
    for (std::size_t i = 0; i < count; i++)
    {
        const FieldPlan& f = *owner->ownerFields[i];
        numbers_[i].assign(f.valueKind == ValueKind::Number ? static_cast<std::size_t>(f.components) : 0, 0.0);
        integers_[i].assign(f.valueKind == ValueKind::Integer64 ? static_cast<std::size_t>(f.components) : 0, 0);
        values_[i].clear();
    }
}

void SelfState::BeginFrame()
{
    received = false;
    ownerMask = 0;
}

void SelfState::Clear()
{
    archetype = nullptr;
    netId = 0;
    lastSeq = 0;
    received = false;
    ownerMask = 0;
    Shape(nullptr);
    version++;
}

void SelfState::SetNumber(const FieldPlan& field, const double* values)
{
    auto& target = numbers_[static_cast<std::size_t>(field.index)];
    std::copy_n(values, target.size(), target.begin());
    present_[static_cast<std::size_t>(field.index)] = 1;
}

void SelfState::SetInteger64(const FieldPlan& field, const std::uint64_t* values)
{
    auto& target = integers_[static_cast<std::size_t>(field.index)];
    std::copy_n(values, target.size(), target.begin());
    present_[static_cast<std::size_t>(field.index)] = 1;
}

void SelfState::SetText(const FieldPlan& field, std::string_view utf8)
{
    Assign(values_[static_cast<std::size_t>(field.index)], reinterpret_cast<const std::uint8_t*>(utf8.data()), utf8.size());
    present_[static_cast<std::size_t>(field.index)] = 1;
}

void SelfState::SetBytes(const FieldPlan& field, std::span<const std::uint8_t> data)
{
    Assign(values_[static_cast<std::size_t>(field.index)], data.data(), data.size());
    present_[static_cast<std::size_t>(field.index)] = 1;
}

std::span<const double> SelfState::Numbers(int field) const
{
    const auto& values = numbers_[static_cast<std::size_t>(field)];
    return {values.data(), values.size()};
}

std::span<const std::uint64_t> SelfState::Integers(int field) const
{
    const auto& values = integers_[static_cast<std::size_t>(field)];
    return {values.data(), values.size()};
}

std::string_view SelfState::Text(int field) const
{
    const auto& value = values_[static_cast<std::size_t>(field)];
    return {reinterpret_cast<const char*>(value.data()), value.size()};
}

std::span<const std::uint8_t> SelfState::Bytes(int field) const
{
    const auto& value = values_[static_cast<std::size_t>(field)];
    return {value.data(), value.size()};
}

double SelfState::Number(std::string_view name, int component) const
{
    if (archetype == nullptr)
    {
        return NaN;
    }

    for (const auto& f : archetype->ownerFields)
    {
        if (f->name == name)
        {
            const auto i = static_cast<std::size_t>(f->index);
            return present_[i] != 0 && component < static_cast<int>(numbers_[i].size()) ? numbers_[i][static_cast<std::size_t>(component)]
                                                                                       : NaN;
        }
    }

    return NaN;
}

EventRecord::EventRecord(const MessagePlan& type) : type_(&type)
{
    const auto& fields = type.body->fields;
    offsets_.assign(fields.size(), 0);
    counts_.assign(fields.size(), 0);
    values_.resize(fields.size());
    int size = 0;
    for (const FieldPlan* f : fields)
    {
        offsets_[static_cast<std::size_t>(f->index)] = size;
        size += f->valueKind == ValueKind::List                                                   ? f->maxCount * f->components
                : f->valueKind == ValueKind::Number || f->valueKind == ValueKind::Integer64 ? f->components
                                                                                            : 0;
    }

    numbers_.assign(static_cast<std::size_t>(size), 0.0);
    integers_.assign(static_cast<std::size_t>(size), 0);
}

std::span<const double> EventRecord::Numbers(int field) const
{
    const FieldPlan& f = *type_->fields[static_cast<std::size_t>(field)];
    const std::size_t at = static_cast<std::size_t>(offsets_[static_cast<std::size_t>(field)]);
    const int count = f.valueKind == ValueKind::List ? counts_[static_cast<std::size_t>(field)] * f.components
                      : f.valueKind == ValueKind::Number ? f.components
                                                         : 0;
    return {numbers_.data() + at, static_cast<std::size_t>(count)};
}

std::span<const std::uint64_t> EventRecord::Integers(int field) const
{
    const FieldPlan& f = *type_->fields[static_cast<std::size_t>(field)];
    const std::size_t at = static_cast<std::size_t>(offsets_[static_cast<std::size_t>(field)]);
    return {integers_.data() + at, f.valueKind == ValueKind::Integer64 ? static_cast<std::size_t>(f.components) : 0};
}

std::string_view EventRecord::Text(int field) const
{
    const auto& value = values_[static_cast<std::size_t>(field)];
    return {reinterpret_cast<const char*>(value.data()), value.size()};
}

std::span<const std::uint8_t> EventRecord::Bytes(int field) const
{
    const auto& value = values_[static_cast<std::size_t>(field)];
    return {value.data(), value.size()};
}

int EventRecord::FieldIndex(std::string_view name) const
{
    for (const FieldPlan* f : type_->body->fields)
    {
        if (f->name == name)
        {
            return f->index;
        }
    }

    return -1;
}

double EventRecord::Number(std::string_view name, int component) const
{
    const int index = FieldIndex(name);
    if (index < 0)
    {
        return NaN;
    }

    const auto values = Numbers(index);
    return component < static_cast<int>(values.size()) ? values[static_cast<std::size_t>(component)] : NaN;
}

void EventRecord::SetNumber(const FieldPlan& field, const double* values)
{
    std::copy_n(values, field.components, numbers_.begin() + offsets_[static_cast<std::size_t>(field.index)]);
}

void EventRecord::SetInteger64(const FieldPlan& field, const std::uint64_t* values)
{
    std::copy_n(values, field.components, integers_.begin() + offsets_[static_cast<std::size_t>(field.index)]);
}

void EventRecord::SetList(const FieldPlan& field, int count, const double* values)
{
    std::copy_n(values, count * field.components, numbers_.begin() + offsets_[static_cast<std::size_t>(field.index)]);
    counts_[static_cast<std::size_t>(field.index)] = count;
}

void EventRecord::SetText(const FieldPlan& field, std::string_view utf8)
{
    Assign(values_[static_cast<std::size_t>(field.index)], reinterpret_cast<const std::uint8_t*>(utf8.data()), utf8.size());
    counts_[static_cast<std::size_t>(field.index)] = static_cast<int>(utf8.size());
}

void EventRecord::SetBytes(const FieldPlan& field, std::span<const std::uint8_t> data)
{
    Assign(values_[static_cast<std::size_t>(field.index)], data.data(), data.size());
    counts_[static_cast<std::size_t>(field.index)] = static_cast<int>(data.size());
}

StatsState::StatsState(const CatalogPlan& plan) : plan_(&plan), values_(static_cast<std::size_t>(plan.MetricValueCount()), 0.0) {}

double StatsState::Value(std::string_view name, int index) const
{
    const MetricPlan* metric = plan_->MetricByName(name);
    return metric == nullptr || index < 0 || index >= metric->valueCount ? NaN : ValueOf(*metric, index);
}

}  // namespace typhon::client
