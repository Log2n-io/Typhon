#include "store/world_store.hpp"

#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace typhon::client {

WorldStore::WorldStore(const WorldSchema& schema, const WorldStoreOptions& options) : maxNetId_(options.maxNetId)
{
    ValidateSchema(schema);
    if (options.maxNetId < 1 || options.maxNetId == NotFound)
    {
        throw std::invalid_argument("maxNetId must be in [1, 2^32 - 2]");
    }

    if (!(options.maxRenderDelayMs > 0 && std::isfinite(options.maxRenderDelayMs)))
    {
        throw std::invalid_argument("maxRenderDelayMs must be positive and finite");
    }

    const int history = SegmentHistoryFor(schema.tickPeriodUs, options.maxRenderDelayMs);
    for (const ArchetypeSchema& archetype : schema.archetypes)
    {
        archetypes_.push_back(std::make_unique<ArchetypeStore>(archetype, history));
    }
}

ArchetypeStore& WorldStore::Archetype(std::size_t index)
{
    if (index >= archetypes_.size())
    {
        throw std::out_of_range("Unknown archetype " + std::to_string(index));
    }

    return *archetypes_[index];
}

const ArchetypeStore& WorldStore::Archetype(std::size_t index) const
{
    if (index >= archetypes_.size())
    {
        throw std::out_of_range("Unknown archetype " + std::to_string(index));
    }

    return *archetypes_[index];
}

std::uint64_t WorldStore::EntityCount() const
{
    std::uint64_t n = 0;
    for (const auto& a : archetypes_)
    {
        n += a->LiveCount();
    }

    return n;
}

void WorldStore::BeginFrame(std::uint32_t frameTick, bool reset)
{
    if (!reset && static_cast<std::int64_t>(frameTick) <= tick)
    {
        anomalies++;
    }

    tick = frameTick;
    resetThisFrame = false;
    for (const auto& a : archetypes_)
    {
        a->BeginFrame();
    }
}

std::uint32_t WorldStore::Enter(std::uint32_t archetype, std::uint32_t netId)
{
    if (netId == 0 || netId > maxNetId_)
    {
        throw std::out_of_range("netId " + std::to_string(netId) + " is out of range (1.." + std::to_string(maxNetId_) + ")");
    }

    ArchetypeStore& store = Archetype(archetype);
    const std::uint32_t previous = Locate(netId);
    if (previous != NotFound)
    {
        // An enter for a held netId never happens in a well-formed stream (§ 10): count it, and let the enter replace.
        anomalies++;
        Archetype(ArchetypeOf(previous)).Release(SlotOf(previous));
    }

    const std::uint32_t slot = store.Allocate(netId);
    EnsureLocations(netId);
    locations_[netId] = (archetype << 24) | slot;
    return slot;
}

bool WorldStore::Leave(std::uint32_t netId, std::int32_t archetype)
{
    const std::uint32_t location = Locate(netId);
    if (location == NotFound || (archetype >= 0 && ArchetypeOf(location) != static_cast<std::uint32_t>(archetype)))
    {
        anomalies++;
        return false;
    }

    Archetype(ArchetypeOf(location)).Release(SlotOf(location));
    locations_[netId] = NotFound;
    return true;
}

void WorldStore::Reset()
{
    resetThisFrame = true;
    for (const auto& store : archetypes_)
    {
        for (const std::uint32_t slot : store->Live())
        {
            locations_[store->NetIds()[slot]] = NotFound;
        }

        store->Clear();
    }
}

void WorldStore::EnsureLocations(std::uint32_t netId)
{
    if (netId < locations_.size())
    {
        return;
    }

    std::uint64_t length = std::max<std::uint64_t>(1024, locations_.size());
    while (length <= netId)
    {
        length *= 2;
    }

    locations_.resize(static_cast<std::size_t>(std::min<std::uint64_t>(length, static_cast<std::uint64_t>(maxNetId_) + 1)), NotFound);
}

}  // namespace typhon::client
