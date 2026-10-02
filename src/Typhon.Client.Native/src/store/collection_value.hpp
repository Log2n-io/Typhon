#pragma once

#include <cstdint>
#include <span>
#include <string_view>

#include "store/byte_arena.hpp"
#include "store/memory.hpp"

namespace typhon::client {

struct FieldPlan;

// One entity's collection (W34): how many elements it holds, how many the server sent, and the elements as structure-of-arrays over the
// element's fields (the TypeScript CollectionValue).
// - A collection is re-sent whole when it changes, so a decode overwrites it: Count() elements are the list, nothing past them means
//   anything, and Total() above Count() says the server cut it at its maxCount.
// - Per element field: numbers in one column of `capacity x components` doubles, 64-bit integers in one of `capacity x components` bit
//   patterns, text in an arena with one entry per element.
// - The capacity grows to the largest list received and is kept, so a steady stream of lists of one size allocates nothing.
class CollectionValue {
public:
    // Binds it to a collection field and empties it; a bound value is rebound only by its field's owner.
    void Bind(const FieldPlan& field);

    // The collection field; null before Bind.
    const FieldPlan* Field() const { return field_; }
    std::uint32_t Total() const { return total_; }
    std::uint32_t Count() const { return count_; }
    std::uint32_t Capacity() const { return capacity_; }
    bool Truncated() const { return count_ < total_; }

    // An element field's index (its FieldPlan::index), or -1.
    int ElementField(std::string_view name) const;

    // An element field's column: `Capacity() x components` values, the first `Count() x components` meaningful; empty for another kind.
    // Throws std::out_of_range for an element field that does not exist, and TextAt for an element at or past Count().
    std::span<const double> Numbers(int elementField) const;
    std::span<const std::uint64_t> Integers(int elementField) const;
    std::string_view TextAt(int elementField, std::uint32_t index) const;

    // By name, for a test or a tool, not a loop: throws std::out_of_range for an element at or past Count(), std::invalid_argument for a
    // name the element does not declare or of another kind.
    double NumberAt(std::uint32_t index, std::string_view name, int component = 0) const;
    std::uint64_t IntegerAt(std::uint32_t index, std::string_view name, int component = 0) const;
    std::string_view TextAt(std::uint32_t index, std::string_view name) const;

    // The decoder's write paths: a list begins with `sent` of `total` elements, then each element's fields arrive.
    void Begin(std::uint32_t total, std::uint32_t sent);
    void SetNumbers(const FieldPlan& field, std::uint32_t index, const double* values);
    void SetIntegers(const FieldPlan& field, std::uint32_t index, const std::uint64_t* values);
    void SetText(const FieldPlan& field, std::uint32_t index, std::string_view utf8);

    // Empties it for the slot's next occupant, keeping its columns.
    void Reset()
    {
        total_ = 0;
        count_ = 0;
    }

private:
    void RequireField(int elementField) const;
    const FieldPlan& Find(std::uint32_t index, std::string_view name) const;
    void Grow(std::uint32_t capacity);

    const FieldPlan* field_ = nullptr;
    std::uint32_t total_ = 0;
    std::uint32_t count_ = 0;
    std::uint32_t capacity_ = 0;
    Vec<Vec<double>> numbers_;
    Vec<Vec<std::uint64_t>> integers_;
    Vec<ByteArena> texts_;
};

}  // namespace typhon::client
