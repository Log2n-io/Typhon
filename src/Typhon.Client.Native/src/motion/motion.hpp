#pragma once

#include <cstdint>
#include <span>

#include "store/archetype_store.hpp"

// Motion segment evaluation (03 § 6), in 2 or 3 dimensions — the TypeScript SDK's motion/motion.ts, operation for operation, so both
// produce the same IEEE bits (motion-eval vectors).
//
// Time is an integer tick plus a fraction: dt = (renderTick - t0) + frac subtracts the integers first, so no precision is lost however
// long the server has run. Which segment: the newest whose start tick render time has reached; before all of them, the oldest held.
// Per model: linear extrapolates p0 + v * dt (backwards before the oldest); none interpolates toward the next newer sample of the same
// epoch, else holds with zero velocity; static is the position entered with, zero velocity.
namespace typhon::client {

// The largest ArchetypeStore::MotionStride(): a buffer this long fits any archetype's evaluated motion.
inline constexpr int MaxMotionStride = 6;

// Ring entry of the segment that applies at `renderTick`.
int SegmentEntryAt(const ArchetypeStore& store, std::uint32_t slot, std::int64_t renderTick);

// Position and velocity of one slot at render time: p[dims] then v[dims] into `out`. Throws std::logic_error when not spatial.
void EvaluateSlot(const ArchetypeStore& store, std::uint32_t slot, std::int64_t renderTick, double frac, std::span<double> out);

// Motion epoch of the segment that applies at `renderTick`: a change means the entity teleported.
std::uint8_t EpochAt(const ArchetypeStore& store, std::uint32_t slot, std::int64_t renderTick);

// Evaluates every live entity, in live order: entry i describes Live()[i]. `out` holds LiveCount() x MotionStride() doubles.
void EvaluateLive(const ArchetypeStore& store, std::int64_t renderTick, double frac, std::span<double> out);

// Heading in radians of a velocity in a plane, atan2(u, v); `fallback` when both are zero.
double HeadingOf(double u, double v, double fallback);

}  // namespace typhon::client
