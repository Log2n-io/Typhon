#include "store/collection_value.hpp"

#include <algorithm>
#include <stdexcept>
#include <string>

#include "catalog/catalog_plan.hpp"

namespace typhon::client {

void CollectionValue::Bind(const FieldPlan& field)
{
    if (field.valueKind != ValueKind::Collection || field.elementSection == nullptr)
    {
        throw std::invalid_argument("field '" + field.name + "' is not a collection");
    }

    field_ = &field;
    const std::size_t fields = field.elementFields.size();
    numbers_.assign(fields, Vec<double>{});
    integers_.assign(fields, Vec<std::uint64_t>{});
    texts_.assign(fields, ByteArena{});
    capacity_ = 0;
    Reset();
    Grow(1);
}

int CollectionValue::ElementField(std::string_view name) const
{
    if (field_ != nullptr)
    {
        for (const auto& f : field_->elementFields)
        {
            if (f->name == name)
            {
                return f->index;
            }
        }
    }

    return -1;
}

void CollectionValue::RequireField(int elementField) const
{
    if (elementField < 0 || static_cast<std::size_t>(elementField) >= numbers_.size())
    {
        throw std::out_of_range("element field " + std::to_string(elementField) + " does not exist");
    }
}

std::span<const double> CollectionValue::Numbers(int elementField) const
{
    RequireField(elementField);
    const auto& column = numbers_[static_cast<std::size_t>(elementField)];
    return {column.data(), column.size()};
}

std::span<const std::uint64_t> CollectionValue::Integers(int elementField) const
{
    RequireField(elementField);
    const auto& column = integers_[static_cast<std::size_t>(elementField)];
    return {column.data(), column.size()};
}

std::string_view CollectionValue::TextAt(int elementField, std::uint32_t index) const
{
    RequireField(elementField);
    if (index >= count_)
    {
        throw std::out_of_range("the collection holds " + std::to_string(count_) + " element(s)");
    }

    const auto bytes = texts_[static_cast<std::size_t>(elementField)].At(index);
    return {reinterpret_cast<const char*>(bytes.data()), bytes.size()};
}

const FieldPlan& CollectionValue::Find(std::uint32_t index, std::string_view name) const
{
    if (index >= count_)
    {
        throw std::out_of_range("the collection holds " + std::to_string(count_) + " element(s)");
    }

    const int at = ElementField(name);
    if (at < 0)
    {
        throw std::invalid_argument("collection '" + field_->name + "' has no element field '" + std::string(name) + "'");
    }

    return *field_->elementFields[static_cast<std::size_t>(at)];
}

double CollectionValue::NumberAt(std::uint32_t index, std::string_view name, int component) const
{
    const FieldPlan& f = Find(index, name);
    if (f.valueKind != ValueKind::Number || component < 0 || component >= f.components)
    {
        throw std::invalid_argument("element field '" + f.name + "' has no number component " + std::to_string(component));
    }

    return numbers_[static_cast<std::size_t>(f.index)][static_cast<std::size_t>(index) * f.components + component];
}

std::uint64_t CollectionValue::IntegerAt(std::uint32_t index, std::string_view name, int component) const
{
    const FieldPlan& f = Find(index, name);
    if (f.valueKind != ValueKind::Integer64 || component < 0 || component >= f.components)
    {
        throw std::invalid_argument("element field '" + f.name + "' has no 64-bit integer component " + std::to_string(component));
    }

    return integers_[static_cast<std::size_t>(f.index)][static_cast<std::size_t>(index) * f.components + component];
}

std::string_view CollectionValue::TextAt(std::uint32_t index, std::string_view name) const
{
    const FieldPlan& f = Find(index, name);
    if (f.valueKind != ValueKind::Text)
    {
        throw std::invalid_argument("element field '" + f.name + "' is not text");
    }

    return TextAt(f.index, index);
}

void CollectionValue::Begin(std::uint32_t total, std::uint32_t sent)
{
    if (sent > capacity_)
    {
        std::uint32_t capacity = std::max<std::uint32_t>(capacity_, 1);
        while (capacity < sent)
        {
            capacity *= 2;
        }

        Grow(capacity);
    }

    total_ = total;
    count_ = sent;
}

void CollectionValue::SetNumbers(const FieldPlan& field, std::uint32_t index, const double* values)
{
    auto& column = numbers_[static_cast<std::size_t>(field.index)];
    std::copy_n(values, field.components, column.begin() + static_cast<std::ptrdiff_t>(index) * field.components);
}

void CollectionValue::SetIntegers(const FieldPlan& field, std::uint32_t index, const std::uint64_t* values)
{
    auto& column = integers_[static_cast<std::size_t>(field.index)];
    std::copy_n(values, field.components, column.begin() + static_cast<std::ptrdiff_t>(index) * field.components);
}

void CollectionValue::SetText(const FieldPlan& field, std::uint32_t index, std::string_view utf8)
{
    texts_[static_cast<std::size_t>(field.index)].Set(index, {reinterpret_cast<const std::uint8_t*>(utf8.data()), utf8.size()});
}

void CollectionValue::Grow(std::uint32_t capacity)
{
    for (const auto& f : field_->elementFields)
    {
        const auto i = static_cast<std::size_t>(f->index);
        const std::size_t length = static_cast<std::size_t>(capacity) * static_cast<std::size_t>(f->components);
        if (f->valueKind == ValueKind::Number)
        {
            numbers_[i].resize(length, 0.0);
        }
        else if (f->valueKind == ValueKind::Integer64)
        {
            integers_[i].resize(length, 0);
        }
        else if (f->valueKind == ValueKind::Text)
        {
            texts_[i].Grow(capacity);
        }
    }

    capacity_ = capacity;
}

}  // namespace typhon::client
