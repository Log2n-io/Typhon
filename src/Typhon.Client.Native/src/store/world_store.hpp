#pragma once

#include <cstdint>
#include <memory>
#include <vector>

#include "store/archetype_store.hpp"
#include "store/memory.hpp"
#include "store/schema.hpp"

namespace typhon::client {

// WorldStore::Locate's answer for an unknown netId.
inline constexpr std::uint32_t NotFound = 0xFFFFFFFFu;

constexpr std::uint32_t ArchetypeOf(std::uint32_t location) { return location >> 24; }
constexpr std::uint32_t SlotOf(std::uint32_t location) { return location & 0xFFFFFFu; }

struct WorldStoreOptions {
    // Largest netId accepted. netIds are dense on the server, so the netId -> slot map is a flat array of at most this many entries;
    // a corrupt id beyond it is refused instead of allocated for.
    std::uint32_t maxNetId = 1u << 22;
    // The largest render delay motion is evaluated at; it sizes each slot's segment ring. A Clock with a larger maxDelayMs must pass
    // the same value here, or render time can fall behind the ring.
    double maxRenderDelayMs = DefaultMaxRenderDelayMs;
};

// The client's replica of what the server has shown it (the TypeScript WorldStore): one ArchetypeStore per archetype and a map from
// netId to its archetype and slot. Frames apply strictly in order: BeginFrame, enters and updates, events, leaves, EndFrame.
//
// netId reuse (03 § 10): an enter for a held netId is an anomaly — counted, and the enter replaces the holder. A leave for a netId
// nobody holds, or held by another archetype, is an anomaly and changes nothing.
class WorldStore {
public:
    explicit WorldStore(const WorldSchema& schema, const WorldStoreOptions& options = {});

    std::size_t ArchetypeCount() const { return archetypes_.size(); }
    ArchetypeStore& Archetype(std::size_t index);
    const ArchetypeStore& Archetype(std::size_t index) const;

    std::uint32_t MaxNetId() const { return maxNetId_; }
    std::uint64_t EntityCount() const;

    // Tick of the frame most recently begun; -1 before the first.
    std::int64_t tick = -1;
    // Frames applied so far.
    std::uint64_t frames = 0;
    // Protocol inconsistencies absorbed instead of thrown: unknown netIds, a tick that did not advance outside a RESET frame.
    std::uint64_t anomalies = 0;
    // Whether the current frame began with a reset: every slot-keyed state a consumer holds is void.
    bool resetThisFrame = false;

    // Begins a frame. A tick at or below the previous frame's is an anomaly unless the frame is a RESET (a restarted server).
    void BeginFrame(std::uint32_t tick, bool reset = false);
    void EndFrame() { frames++; }

    // Adds an entering entity and returns its slot. Throws std::out_of_range for a netId outside 1..maxNetId.
    std::uint32_t Enter(std::uint32_t archetype, std::uint32_t netId);

    // Removes the holder of `netId`, only when it is of `archetype` (when given). False, and an anomaly, when there is no such holder.
    bool Leave(std::uint32_t netId, std::int32_t archetype = -1);

    // `archetype << 24 | slot` of a held netId, or NotFound.
    std::uint32_t Locate(std::uint32_t netId) const { return netId < locations_.size() ? locations_[netId] : NotFound; }

    // Drops every entity (a RESET frame); the dropped entities are not listed as left: resetThisFrame voids every slot and netId.
    void Reset();

private:
    void EnsureLocations(std::uint32_t netId);

    std::vector<std::unique_ptr<ArchetypeStore>> archetypes_;
    std::uint32_t maxNetId_;
    Vec<std::uint32_t> locations_;
};

}  // namespace typhon::client
