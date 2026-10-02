#include "motion/motion.hpp"

#include <cmath>
#include <stdexcept>

namespace typhon::client {

namespace {

void RequirePosition(const ArchetypeStore& store)
{
    if (!store.HasPosition())
    {
        throw std::logic_error("Archetype '" + store.Schema().name + "' has no position");
    }
}

void Evaluate(const ArchetypeStore& store, std::uint32_t slot, int entry, std::int64_t renderTick, double frac, double* out)
{
    const int dims = store.Dims();
    const double* b = store.SegmentOf(slot, entry);
    const std::uint32_t t0 = store.T0Of(slot, entry);
    const double dt = static_cast<double>(renderTick - static_cast<std::int64_t>(t0)) + frac;

    if (store.Linear() || !store.Moving())
    {
        for (int a = 0; a < dims; a++)
        {
            const double v = b[dims + a];
            out[a] = b[a] + v * dt;
            out[dims + a] = v;
        }

        return;
    }

    // Samples: interpolate toward the next newer sample of the same epoch, when one is held and render time has left t0.
    const int head = store.HeadEntry(slot);
    const int next = entry == store.SegmentHistory() - 1 ? 0 : entry + 1;
    const std::uint32_t t1 = store.T0Of(slot, next);
    if (entry != head && t1 > t0 && dt >= 0 && store.EpochOf(slot, next) == store.EpochOf(slot, entry))
    {
        const double span = static_cast<double>(t1 - t0);
        const double u = dt < span ? dt / span : 1;
        const double* n = store.SegmentOf(slot, next);
        for (int a = 0; a < dims; a++)
        {
            const double p0 = b[a];
            const double delta = n[a] - p0;
            out[a] = p0 + delta * u;
            out[dims + a] = dt < span ? delta / span : 0;
        }

        return;
    }

    for (int a = 0; a < dims; a++)
    {
        out[a] = b[a];
        out[dims + a] = 0;
    }
}

void RequireOut(std::span<double> out, std::size_t needed)
{
    if (out.size() < needed)
    {
        throw std::out_of_range("the output buffer holds " + std::to_string(out.size()) + " values; " + std::to_string(needed) + " needed");
    }
}

}  // namespace

int SegmentEntryAt(const ArchetypeStore& store, std::uint32_t slot, std::int64_t renderTick)
{
    const int last = store.SegmentHistory() - 1;
    const int count = store.SegmentCount(slot);
    // Newest to oldest, wrapping by a branch; the oldest held applies when render time precedes them all.
    int entry = store.HeadEntry(slot);
    for (int k = 1; k < count; k++)
    {
        if (renderTick >= static_cast<std::int64_t>(store.T0Of(slot, entry)))
        {
            return entry;
        }

        entry = entry == 0 ? last : entry - 1;
    }

    return entry;
}

void EvaluateSlot(const ArchetypeStore& store, std::uint32_t slot, std::int64_t renderTick, double frac, std::span<double> out)
{
    RequirePosition(store);
    RequireOut(out, static_cast<std::size_t>(store.MotionStride()));
    Evaluate(store, slot, SegmentEntryAt(store, slot, renderTick), renderTick, frac, out.data());
}

std::uint8_t EpochAt(const ArchetypeStore& store, std::uint32_t slot, std::int64_t renderTick)
{
    RequirePosition(store);
    return store.EpochOf(slot, SegmentEntryAt(store, slot, renderTick));
}

void EvaluateLive(const ArchetypeStore& store, std::int64_t renderTick, double frac, std::span<double> out)
{
    RequirePosition(store);
    const std::size_t stride = static_cast<std::size_t>(store.MotionStride());
    const auto live = store.Live();
    RequireOut(out, live.size() * stride);
    for (std::size_t i = 0; i < live.size(); i++)
    {
        const std::uint32_t slot = live[i];
        Evaluate(store, slot, SegmentEntryAt(store, slot, renderTick), renderTick, frac, out.data() + i * stride);
    }
}

double HeadingOf(double u, double v, double fallback) { return u == 0 && v == 0 ? fallback : std::atan2(u, v); }

}  // namespace typhon::client
