#include "aggregates/aggregate_grid.hpp"

#include <algorithm>
#include <cmath>
#include <stdexcept>
#include <string>

#include "catalog/catalog.hpp"
#include "wire/constants.hpp"
#include "wire/realm_frame.hpp"

namespace typhon::client {

namespace {

std::uint32_t CellsOf(const GridSchema& schema)
{
    const std::string where = "Grid " + std::to_string(schema.index);
    const std::size_t axes = schema.dims.size();
    if ((axes != 2 && axes != 3) || schema.origin.size() != axes)
    {
        throw std::invalid_argument(where + " needs 2 or 3 axes, with one origin per axis");
    }

    if (!(schema.cell > 0 && std::isfinite(schema.cell)) || !std::all_of(schema.origin.begin(), schema.origin.end(), [](double o) {
            return std::isfinite(o);
        }))
    {
        throw std::invalid_argument(where + " needs a finite origin and a positive finite cell");
    }

    std::uint64_t cells = 1;
    for (const std::uint32_t d : schema.dims)
    {
        if (d < 1)
        {
            throw std::invalid_argument(where + ": every dimension must be at least 1");
        }

        cells = std::min<std::uint64_t>(cells * d, static_cast<std::uint64_t>(protocol::MaxGridCells) + 1);
    }

    if (cells > static_cast<std::uint64_t>(protocol::MaxGridCells))
    {
        throw std::invalid_argument(where + " has more than " + std::to_string(protocol::MaxGridCells) + " cells");
    }

    return static_cast<std::uint32_t>(cells);
}

}  // namespace

GridSchema GridSchemaFromCatalog(const CatalogGrid& grid, const RealmFrame* frame)
{
    GridSchema schema;
    schema.index = grid.idx;
    schema.archetypes = grid.archetypes;
    if (frame == nullptr)
    {
        schema.origin = {0, 0};
        schema.dims = {1, 1};
        return schema;
    }

    const int t = grid.tileCells;
    const int axes = frame->deep ? 3 : 2;
    for (int axis = 0; axis < axes; axis++)
    {
        schema.origin.push_back(frame->min[static_cast<std::size_t>(axis)]);
        // Capped before the cast, so a realm too large for a grid is refused by CellsOf rather than overflowing the conversion.
        const double dim = std::min(frame->AggregateDim(axis, t), static_cast<double>(protocol::MaxGridCells) + 1);
        schema.dims.push_back(static_cast<std::uint32_t>(dim));
    }

    schema.cell = t * frame->cellM;
    return schema;
}

AggregateGrid::AggregateGrid(GridSchema schema)
    : schema_(std::move(schema)), cellCount_(CellsOf(schema_)), archetypeCount_(static_cast<std::uint32_t>(schema_.archetypes.size()))
{
}

void AggregateGrid::Reframe(GridSchema schema)
{
    const std::uint32_t cells = CellsOf(schema);
    if (schema.index != schema_.index || schema.archetypes.size() != archetypeCount_)
    {
        throw std::logic_error("Grid " + std::to_string(schema_.index) + ": a new realm changes its geometry, never its index or archetypes");
    }

    schema_ = std::move(schema);
    if (allocated_ && cells == cellCount_)
    {
        std::fill(counts_.begin(), counts_.end(), 0u);
        std::fill(stamps_.begin(), stamps_.end(), 0u);
    }
    else
    {
        counts_ = {};
        changed_ = {};
        stamps_ = {};
        allocated_ = false;
    }

    cellCount_ = cells;
    changedCount_ = 0;
    wasReset_ = true;
    version_++;
}

void AggregateGrid::BeginFrame()
{
    frame_++;
    changedCount_ = 0;
    wasReset_ = false;
}

void AggregateGrid::Reset()
{
    std::fill(counts_.begin(), counts_.end(), 0u);
    wasReset_ = true;
    version_++;
}

void AggregateGrid::SetCell(std::uint32_t cell, std::span<const std::uint32_t> values)
{
    if (cell >= cellCount_)
    {
        throw std::out_of_range("Grid " + std::to_string(schema_.index) + ": cell " + std::to_string(cell) + " is out of range");
    }

    if (values.size() < archetypeCount_)
    {
        throw std::out_of_range("Grid " + std::to_string(schema_.index) + ": " + std::to_string(archetypeCount_) + " counts expected");
    }

    if (!allocated_)
    {
        counts_.assign(static_cast<std::size_t>(cellCount_) * archetypeCount_, 0u);
        changed_.assign(cellCount_, 0u);
        stamps_.assign(cellCount_, 0u);
        allocated_ = true;
    }

    std::copy_n(values.begin(), archetypeCount_, counts_.begin() + static_cast<std::ptrdiff_t>(cell) * archetypeCount_);
    if (stamps_[cell] != frame_)
    {
        stamps_[cell] = frame_;
        changed_[changedCount_++] = cell;
    }

    version_++;
}

std::uint32_t AggregateGrid::Count(std::uint32_t cell, std::uint32_t archetypeSlot) const
{
    const std::size_t at = static_cast<std::size_t>(cell) * archetypeCount_ + archetypeSlot;
    return at < counts_.size() ? counts_[at] : 0;
}

int AggregateGrid::SlotOfArchetype(int archetype) const
{
    const auto at = std::find(schema_.archetypes.begin(), schema_.archetypes.end(), archetype);
    return at == schema_.archetypes.end() ? -1 : static_cast<int>(at - schema_.archetypes.begin());
}

std::int64_t AggregateGrid::CellAt(double a0, double a1, double a2) const
{
    const auto index = [this](double a, std::size_t axis) -> std::int64_t
    {
        const double i = std::floor((a - schema_.origin[axis]) / schema_.cell);
        return i >= 0 && i < schema_.dims[axis] ? static_cast<std::int64_t>(i) : -1;
    };

    const std::int64_t i0 = index(a0, 0);
    const std::int64_t i1 = index(a1, 1);
    if (i0 < 0 || i1 < 0)
    {
        return -1;
    }

    if (schema_.dims.size() == 2)
    {
        return i0 + static_cast<std::int64_t>(schema_.dims[0]) * i1;
    }

    const std::int64_t i2 = index(a2, 2);
    return i2 < 0 ? -1 : i0 + static_cast<std::int64_t>(schema_.dims[0]) * (i1 + static_cast<std::int64_t>(schema_.dims[1]) * i2);
}

}  // namespace typhon::client
