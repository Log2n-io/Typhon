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

    // Between two held segments of one epoch the newer origin is authoritative, and both models reach for it -- `none` over the whole gap, because it has no
    // velocity to offer, and `linear` over the LAST TICK of it only.
    //
    // A linear segment's velocity is fitted over the run that ENDED at t0, so it describes travel already taken and predicts the next tick only while the
    // motion continues; the tick a newer segment was emitted FOR is by definition the one where it stopped doing so, since that tick's error is what triggered
    // the emit. Drawing the old velocity through it put a player's stop 220 mm past where it had stopped on a 10 Hz server, and 336 mm past it at the end of an
    // eight-tick gap, each followed by a snap back (design/SwgTatooine/15-client-prediction.md A14c). Blending over the whole gap instead removes the bound the
    // emit rule provides -- 0.29 m against a 0.05 m tolerance on a path turning every three ticks.
    //
    // Behind the `entry != head` test, as in the C# and TypeScript twins: a steady mover holds one segment for tens of ticks and is its own head, so it reads
    // no neighbouring entry at all.
    bool crossing = false;
    double span = 0;
    int next = 0;
    if (entry != store.HeadEntry(slot))
    {
        next = entry == store.SegmentHistory() - 1 ? 0 : entry + 1;

        // Widened before subtracting: both ticks are u32, so their unsigned difference WRAPS, and `span > 0` would then be true for a newer entry whose t0 is
        // below this one's -- reachable across the u32 tick wrap, which the pair of them says nothing about.
        span = static_cast<double>(static_cast<std::int64_t>(store.T0Of(slot, next)) - static_cast<std::int64_t>(t0));
        crossing = span > 0 && dt >= 0 && store.EpochOf(slot, next) == store.EpochOf(slot, entry);
    }

    if (store.Linear() || !store.Moving())
    {
        if (crossing && dt >= span - 1)
        {
            // Extrapolated to t1 - 1, then across the final tick to the origin the server reported for t1.
            const double u = dt - (span - 1);
            const double* n = store.SegmentOf(slot, next);
            for (int a = 0; a < dims; a++)
            {
                const double from = b[a] + b[dims + a] * (span - 1);
                const double delta = n[a] - from;
                out[a] = from + delta * (u > 1 ? 1 : u);
                out[dims + a] = delta;
            }

            return;
        }

        for (int a = 0; a < dims; a++)
        {
            const double v = b[dims + a];
            out[a] = b[a] + v * dt;
            out[dims + a] = v;
        }

        return;
    }

    if (crossing)
    {
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
