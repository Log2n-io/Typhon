#pragma once

#include <cstdint>
#include <span>
#include <vector>

#include "store/memory.hpp"

namespace typhon::client {

class RealmFrame;
struct CatalogGrid;

// A grid of per-cell entity counts, as a catalog grid laid over a realm describes it.
struct GridSchema {
    int index = 0;
    // World coordinates of the minimum corner, 2 or 3 axes.
    std::vector<double> origin;
    // Edge of a cell on every axis.
    double cell = 1;
    // Cells per axis, 2 or 3 axes.
    std::vector<std::uint32_t> dims;
    // Archetype indices counted per cell, in the order the counts travel.
    std::vector<int> archetypes;
};

// The geometry of a catalog grid over a realm's frame (typhon.3, 12-realms § 5.3); before any realm, a one-cell placeholder.
GridSchema GridSchemaFromCatalog(const CatalogGrid& grid, const RealmFrame* frame);

// Per-cell counts per archetype (the far tier, AGG): Counts()[cell * ArchetypeCount() + slot], cells row-major, axis 0 fastest.
// Nothing is allocated before the grid's first AGG: a catalog may declare 2^24 cells. A renderer keys on Version(), since several
// frames can apply between two renders; Changed() lists the current frame's cells only.
class AggregateGrid {
public:
    explicit AggregateGrid(GridSchema schema);

    const GridSchema& Schema() const { return schema_; }
    int Index() const { return schema_.index; }
    std::uint32_t CellCount() const { return cellCount_; }
    std::uint32_t ArchetypeCount() const { return archetypeCount_; }
    std::span<const std::uint32_t> Counts() const { return {counts_.data(), counts_.size()}; }
    std::span<const std::uint32_t> Changed() const { return {changed_.data(), changedCount_}; }
    bool WasReset() const { return wasReset_; }
    std::uint64_t Version() const { return version_; }

    // Lays the grid over a new realm's geometry: every count is dropped; buffers are kept when the cell count is unchanged.
    void Reframe(GridSchema schema);
    void BeginFrame();
    // Clears every cell (an AGG with RESET, or a RESET frame).
    void Reset();
    // Writes one cell's counts.
    void SetCell(std::uint32_t cell, std::span<const std::uint32_t> values);

    // Count of one archetype slot in a cell, 0 before the first AGG.
    std::uint32_t Count(std::uint32_t cell, std::uint32_t archetypeSlot) const;
    // Position of an archetype in this grid's counts, or -1.
    int SlotOfArchetype(int archetype) const;
    // Cell containing a world point, or -1 outside the grid (or for a non-finite coordinate). A two-axis grid ignores a2.
    std::int64_t CellAt(double a0, double a1, double a2 = 0) const;

private:
    GridSchema schema_;
    std::uint32_t cellCount_;
    std::uint32_t archetypeCount_;
    Vec<std::uint32_t> counts_;
    Vec<std::uint32_t> changed_;
    std::uint32_t changedCount_ = 0;
    bool wasReset_ = false;
    std::uint64_t version_ = 0;
    Vec<std::uint32_t> stamps_;
    bool allocated_ = false;
    std::uint32_t frame_ = 1;
};

}  // namespace typhon::client
