#include "wire/realm_frame.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>
#include <stdexcept>
#include <string>

#include "wire/constants.hpp"
#include "wire/errors.hpp"
#include "wire/math.hpp"
#include "wire/reader.hpp"
#include "wire/writer.hpp"

namespace typhon::client {

namespace {

constexpr std::uint8_t FlagNone = 1;
constexpr std::uint8_t FlagDeep = 2;

std::string Num(double v)
{
    char buffer[40];
    std::snprintf(buffer, sizeof buffer, "%.17g", v);
    return buffer;
}

// What § 5.2 refuses, or an empty string.
std::string FrameProblem(std::uint32_t realmId, std::uint32_t kindIdx, std::size_t realmKindCount, int bits, double cellM, const double min[3],
                         const double max[3])
{
    if (realmId == RealmFrame::NoRealm)
    {
        return "realm id 65535 is REALM(NONE)'s";
    }

    if (kindIdx >= realmKindCount)
    {
        return "realm kind index " + std::to_string(kindIdx) + " is out of range (" + std::to_string(realmKindCount) + " kind(s))";
    }

    if (bits != 16 && bits != 24 && bits != 32)
    {
        return "REALM posBits " + std::to_string(bits) + " is not 16, 24 or 32";
    }

    if (!(std::isfinite(cellM) && cellM > 0))
    {
        return "REALM cellM " + Num(cellM) + " is not a positive finite number";
    }

    for (int i = 0; i < 3; i++)
    {
        const double lo = min[i];
        const double hi = max[i];
        if (!(std::isfinite(lo) && std::isfinite(hi) && lo < hi))
        {
            return "REALM bounds on axis " + std::to_string(i) + " are not a finite min < max (" + Num(lo) + ", " + Num(hi) + ")";
        }

        // The quantum must be a positive finite number: an extent past the f64 range makes it infinite, a subnormal one zero.
        const double step = (hi - lo) / math::Pow2(bits);
        if (!(std::isfinite(step) && step > 0))
        {
            return "REALM bounds on axis " + std::to_string(i) + " give no usable " + std::to_string(bits) + "-bit quantum";
        }
    }

    return {};
}

}  // namespace

RealmFrame::RealmFrame(std::uint32_t id, std::uint32_t gen, std::uint32_t kind, std::uint32_t tag, int bits, double cell, bool isDeep,
                       const double lower[3], const double upper[3])
    : realmId(id), generation(gen), kindIdx(kind), appTag(tag), positionBits(bits), cellM(cell), deep(isDeep), min{}, max{}, step{}, top(0)
{
    const std::string problem = FrameProblem(id, kind, std::numeric_limits<std::size_t>::max(), bits, cell, lower, upper);
    if (!problem.empty())
    {
        throw std::out_of_range(problem);
    }

    for (int i = 0; i < 3; i++)
    {
        min[static_cast<std::size_t>(i)] = lower[i];
        max[static_cast<std::size_t>(i)] = upper[i];
        step[static_cast<std::size_t>(i)] = math::QuantStep(lower[i], upper[i], bits);
    }

    top = math::UnsignedTop(bits);
}

std::shared_ptr<const RealmFrame> RealmFrame::ForCommands() const
{
    if (commands_ == nullptr)
    {
        commands_ = std::make_shared<const RealmFrame>(realmId, generation, kindIdx, appTag, protocol::CommandPositionBits, cellM, deep,
                                                       min.data(), max.data());
    }

    return commands_;
}

double RealmFrame::AggregateDim(int axis, int tileCells) const
{
    if (axis == 2 && !deep)
    {
        return 1;
    }

    const auto a = static_cast<std::size_t>(axis);
    const double dims = std::ceil((max[a] - min[a]) / (tileCells * cellM));
    return dims < 1 ? 1 : std::min(dims, 2147483647.0);
}

double RealmFrame::AggregateCellCount(int tileCells) const
{
    double count = 1;
    for (int axis = 0; axis < 3; axis++)
    {
        count *= AggregateDim(axis, tileCells);
        if (count > 2147483647.0)
        {
            return std::numeric_limits<double>::infinity();
        }
    }

    return count;
}

bool RealmFrame::Equals(const RealmFrame& other) const
{
    return min == other.min && max == other.max && realmId == other.realmId && generation == other.generation && kindIdx == other.kindIdx
           && appTag == other.appTag && positionBits == other.positionBits && cellM == other.cellM && deep == other.deep;
}

std::shared_ptr<const RealmFrame> RealmFrame::Read(WireReader& r, std::size_t realmKindCount)
{
    const std::uint32_t realmId = r.U16();
    const std::uint32_t generation = r.U16();
    const std::uint8_t flags = r.U8();
    if ((flags & ~(FlagNone | FlagDeep)) != 0)
    {
        throw Malformed("REALM flags set a reserved bit");
    }

    if ((flags & FlagNone) != 0)
    {
        if (flags != FlagNone)
        {
            throw Malformed("REALM(NONE) sets another flag");
        }

        if (realmId != NoRealm || generation != 0)
        {
            throw Malformed("REALM(NONE) names realm " + std::to_string(realmId) + " generation " + std::to_string(generation)
                            + "; NONE is 65535, generation 0");
        }

        return nullptr;
    }

    const std::uint32_t kindIdx = r.Varu();
    const std::uint32_t appTag = r.U32();
    const int bits = r.U8();
    const double cellM = r.F64();
    const double min[3] = {r.F64(), r.F64(), r.F64()};
    const double max[3] = {r.F64(), r.F64(), r.F64()};
    const std::string problem = FrameProblem(realmId, kindIdx, realmKindCount, bits, cellM, min, max);
    if (!problem.empty())
    {
        throw Malformed(problem);
    }

    return std::make_shared<const RealmFrame>(realmId, generation, kindIdx, appTag, bits, cellM, (flags & FlagDeep) != 0, min, max);
}

void RealmFrame::Write(WireWriter& w) const
{
    if ((realmId >> 16) != 0 || (generation >> 16) != 0)
    {
        throw std::out_of_range("realm id and generation are both u16");
    }

    w.U16(realmId);
    w.U16(generation);
    w.U8(deep ? FlagDeep : 0);
    w.Varu(kindIdx);
    w.U32(appTag);
    w.U8(static_cast<std::uint32_t>(positionBits));
    w.F64(cellM);
    for (const double v : min)
    {
        w.F64(v);
    }

    for (const double v : max)
    {
        w.F64(v);
    }
}

void RealmFrame::WriteNone(WireWriter& w)
{
    w.U16(NoRealm);
    w.U16(0);
    w.U8(FlagNone);
}

}  // namespace typhon::client
