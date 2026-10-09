#include "wire/tick_reader.hpp"

#include <algorithm>
#include <cstdio>
#include <string>

#include "wire/constants.hpp"
#include "wire/errors.hpp"
#include "wire/realm_frame.hpp"

namespace typhon::client {

namespace {

// The DEBUG sub-block whose payload is push geometry, in realm metres (09 § 15).
constexpr std::uint8_t DebugPushGeometry = 0x80;

std::uint32_t BlockBit(std::uint8_t type)
{
    return type >= BlockType::Entities && type <= BlockType::Realm ? 1u << type : type == BlockType::Ext ? BlockMask::Ext : BlockMask::Unknown;
}

std::string Hex(unsigned v)
{
    char text[16];
    std::snprintf(text, sizeof text, "%x", v);
    return text;
}

// One sub-list run's record count, which the grammar forbids to be zero.
std::uint32_t RunLength(WireReader& r)
{
    const std::uint32_t n = r.Varu();
    if (n == 0)
    {
        throw Malformed("an ENTITIES sub-list run carries no record");
    }

    return n;
}

// netIds in a run are ascending: each is the previous plus one plus a varu gap, starting from -1.
std::uint32_t NextNetId(WireReader& r, std::int64_t prev)
{
    const std::int64_t next = prev + 1 + static_cast<std::int64_t>(r.Varu());
    if (next > 0xFFFFFFFFll)
    {
        throw Malformed("netId gap overflows 32 bits");
    }

    return static_cast<std::uint32_t>(next);
}

std::uint32_t T0(std::uint32_t tick, std::uint32_t low) { return tick - ((tick - low) & 0xFFFFu); }

}  // namespace

TickReader::TickReader(std::shared_ptr<const CatalogPlan> plan) : plan_(std::move(plan)), entitiesSeenAt_(plan_->Archetypes().size(), 0) {}

void TickReader::Read(std::span<const std::uint8_t> message, TickSink& sink, std::uint32_t blocks)
{
    try
    {
        ReadMessage(message, sink, blocks);
    }
    catch (...)
    {
        r_.Release();
        throw;
    }

    r_.Release();
}

void TickReader::ReadMessage(std::span<const std::uint8_t> message, TickSink& sink, std::uint32_t blocks)
{
    // A stamp per read rather than a cleared set: at most one ENTITIES block per archetype per message (1007).
    reads_++;
    if (reads_ == 0)
    {
        std::fill(entitiesSeenAt_.begin(), entitiesSeenAt_.end(), 0u);
        reads_ = 1;
    }

    WireReader& r = r_;
    r.Reset(message);
    if (r.U8() != MessageType::Tick)
    {
        throw ProtocolError("not a TICK message");
    }

    const std::uint32_t tick = r.U32();
    const std::uint8_t flags = r.U8();
    const std::uint32_t periodUs = (flags & TickFlags::Period) != 0 ? r.U32() : 0;
    sink.BeginTick(tick, flags, periodUs);

    bool first = true;
    while (!r.IsAtEnd())
    {
        const std::uint8_t type = r.U8();
        const std::size_t length = r.VaruAtMost(r.Remaining(), "block length");

        // Only a RESET frame carries a REALM, as its first block (12-realms § 5.2).
        if (type == BlockType::Realm && (!first || (flags & TickFlags::Reset) == 0))
        {
            throw Malformed(first ? "a REALM block in a frame without RESET" : "a REALM block that is not the frame's first");
        }

        first = false;
        if ((blocks & BlockBit(type)) == 0)
        {
            // A REALM is adopted by every pass, reaching the sink or not: the blocks this pass reads decode over it (SUB-30).
            if (type == BlockType::Realm)
            {
                const std::size_t skipped = r.PushLimit(length);
                auto next = ReadRealm();
                // A second pass over the same message re-reads the same REALM: the frame already held is kept, so a pointer handed out
                // with it (onRealmChanged, the applier's Realm()) stays the session's frame, and its command-width cache stays warm.
                const bool same = next == nullptr ? realm == nullptr : realm != nullptr && next->Equals(*realm);
                if (!same)
                {
                    realm = std::move(next);
                }

                const std::size_t trailing = r.PopLimit(skipped);
                if (trailing != 0)
                {
                    throw Malformed("block 0x" + Hex(type) + ": " + std::to_string(trailing) + " unread byte(s) after its content");
                }
            }
            else
            {
                r.Skip(length);
            }

            continue;
        }

        const std::size_t saved = r.PushLimit(length);
        switch (type)
        {
            case BlockType::Realm:
                realm = ReadRealm();
                sink.Realm(realm.get());
                break;
            case BlockType::Entities:
                ReadEntities(tick, sink);
                break;
            case BlockType::Events:
                for (std::uint32_t n = r.Varu(); n > 0; n--)
                {
                    const MessagePlan& eventType = plan_->Event(r.Varu());
                    sink.Event(eventType);
                    ReadSection(r, *eventType.body, tick, sink, false, realm.get());
                }

                break;
            case BlockType::Self:
                ReadSelf(tick, sink);
                break;
            case BlockType::Agg:
                ReadAggregate(sink);
                break;
            case BlockType::Stats:
                ReadMetrics(plan_->ServerMetrics(), tick, sink);
                ReadMetrics(plan_->SessionMetrics(), tick, sink);
                break;
            case BlockType::Debug:
                while (r.Remaining() > 0)
                {
                    const std::uint8_t subType = r.U8();
                    // Push geometry is in realm metres (12-realms § 5.2): no realm, no geometry.
                    if (subType == DebugPushGeometry && realm == nullptr)
                    {
                        throw ProtocolError("a DEBUG push geometry while the session holds no realm");
                    }

                    const std::size_t size = r.VaruAtMost(r.Remaining(), "DEBUG sub-block length");
                    const std::size_t at = r.Take(size);
                    sink.Debug(subType, r.Bytes().subspan(at, size));
                }

                break;
            case BlockType::Acks:
                for (std::uint32_t n = r.Varu(); n > 0; n--)
                {
                    const std::uint32_t seq = r.U16();
                    sink.Ack(seq, r.U8());
                }

                break;
            case BlockType::Sources:
                for (std::uint32_t n = r.Varu(); n > 0; n--)
                {
                    const std::uint32_t requestId = r.U16();
                    const std::uint8_t status = r.U8();
                    if (status > SourceStatus::Error)
                    {
                        throw Malformed("SOURCES status " + std::to_string(status) + " is unknown");
                    }

                    sink.Source(requestId, status, status == SourceStatus::Error ? r.U16() : 0);
                }

                break;
            case BlockType::Ext:
            {
                const std::uint32_t appTypeId = r.Varu();
                const std::size_t size = r.Remaining();
                const std::size_t at = r.Take(size);
                sink.Ext(appTypeId, r.Bytes().subspan(at, size));
                break;
            }
            default:
                r.Skip(r.Remaining());
                sink.UnknownBlock(type);
                break;
        }

        const std::size_t unread = r.PopLimit(saved);
        if (unread != 0)
        {
            // A block shorter than its declared length is malformed, not padded — STATS included (§ 10 precisions).
            throw Malformed("block 0x" + Hex(type) + ": " + std::to_string(unread) + " unread byte(s) after its content");
        }
    }

    sink.EndTick();
}

std::shared_ptr<const RealmFrame> TickReader::ReadRealm()
{
    auto frame = RealmFrame::Read(r_, plan_->RealmKinds().size());

    // An AGG grid over this frame must stay within the cell bound a store allocates for (W28): refused with the frame.
    if (frame != nullptr)
    {
        for (const auto& grid : plan_->Grids())
        {
            if (frame->AggregateCellCount(grid->tileCells) > protocol::MaxGridCells)
            {
                throw Malformed("grid " + std::to_string(grid->idx) + " over this REALM has more than " + std::to_string(protocol::MaxGridCells)
                                + " cells");
            }
        }
    }

    return frame;
}

void TickReader::ReadEntities(std::uint32_t tick, TickSink& sink)
{
    WireReader& r = r_;
    const ArchetypePlan& archetype = plan_->Archetype(r.Varu());
    const auto idx = static_cast<std::size_t>(archetype.idx);
    if (entitiesSeenAt_[idx] == reads_)
    {
        throw Malformed("a second ENTITIES block for archetype '" + archetype.name + "'");
    }

    entitiesSeenAt_[idx] = reads_;
    const PositionPlan* position = archetype.position.get();
    const RealmFrame* frame = realm.get();
    if (position != nullptr && frame == nullptr)
    {
        throw ProtocolError("an ENTITIES block for positioned archetype '" + archetype.name + "' while the session holds no realm");
    }

    double p[3] = {0, 0, 0};
    double v[3] = {0, 0, 0};
    const std::size_t dims = position == nullptr ? 0 : static_cast<std::size_t>(position->dims);
    const std::size_t velDims = position == nullptr || position->vel == nullptr ? 0 : dims;
    const std::span<const double> positionView(p, dims);
    const std::span<const double> velocityView(v, velDims);
    const auto& sections = archetype.groupSections;
    sink.BeginEntities(archetype);

    // ── enters
    for (std::uint32_t runs = r.Varu(); runs > 0; runs--)
    {
        std::int64_t prev = -1;
        for (std::uint32_t n = RunLength(r); n > 0; n--)
        {
            prev = NextNetId(r, prev);
            std::uint32_t t0 = 0;
            std::uint8_t epoch = 0;
            if (position != nullptr)
            {
                ReadNumber(r, *position->pos, tick, p, frame);
                if (position->moving)
                {
                    if (position->vel != nullptr)
                    {
                        ReadNumber(r, *position->vel, tick, v);
                    }

                    t0 = T0(tick, r.U16());
                    epoch = r.U8();
                }
            }

            sink.Enter(static_cast<std::uint32_t>(prev), positionView, velocityView, t0, epoch);
            ReadSection(r, *archetype.onEnter, tick, sink, false, frame);
            for (const SectionPlan& section : sections)
            {
                ReadSection(r, section, tick, sink, false, frame);
            }
        }
    }

    // ── segments
    const std::uint32_t segmentRuns = r.Varu();
    if (segmentRuns > 0 && (position == nullptr || !position->moving))
    {
        throw Malformed("archetype '" + archetype.name + "' does not move but its block carries segment(s)");
    }

    for (std::uint32_t runs = segmentRuns; runs > 0; runs--)
    {
        std::int64_t prev = -1;
        for (std::uint32_t n = RunLength(r); n > 0; n--)
        {
            prev = NextNetId(r, prev);
            ReadNumber(r, *position->pos, tick, p, frame);
            if (position->vel != nullptr)
            {
                ReadNumber(r, *position->vel, tick, v);
            }

            const std::uint32_t t0 = T0(tick, r.U16());
            sink.Segment(static_cast<std::uint32_t>(prev), positionView, velocityView, t0, r.U8());
        }
    }

    // ── states
    const auto groupCount = static_cast<int>(archetype.groups.size());
    for (std::uint32_t runs = r.Varu(); runs > 0; runs--)
    {
        std::int64_t prev = -1;
        for (std::uint32_t n = RunLength(r); n > 0; n--)
        {
            prev = NextNetId(r, prev);
            const std::uint8_t mask = r.U8();
            if (mask == 0 || (mask >> groupCount) != 0)
            {
                throw Malformed("state record mask 0x" + Hex(mask) + " is invalid for " + std::to_string(groupCount) + " group(s)");
            }

            sink.State(static_cast<std::uint32_t>(prev), mask);
            for (int g = 0; g < groupCount; g++)
            {
                if ((mask & (1 << g)) != 0)
                {
                    ReadSection(r, sections[static_cast<std::size_t>(g)], tick, sink, false, frame);
                }
            }
        }
    }

    // ── leaves
    for (std::uint32_t runs = r.Varu(); runs > 0; runs--)
    {
        std::int64_t prev = -1;
        for (std::uint32_t n = RunLength(r); n > 0; n--)
        {
            prev = NextNetId(r, prev);
            sink.Leave(static_cast<std::uint32_t>(prev));
        }
    }
}

void TickReader::ReadSelf(std::uint32_t tick, TickSink& sink)
{
    WireReader& r = r_;
    const std::uint32_t archetypeIdx = r.Varu();
    const std::uint32_t netId = r.Varu();
    const std::uint32_t lastSeq = r.U16();
    const std::uint8_t mask = r.U8();

    // netId 0 is never an entity: it is "no controlled entity" (W17'), an acknowledgement only.
    if (netId == 0)
    {
        if (archetypeIdx != 0 || mask != 0)
        {
            throw Malformed("SELF with no controlled entity must name archetype 0 and no owner group (archetype " + std::to_string(archetypeIdx)
                            + ", mask 0x" + Hex(mask) + ")");
        }

        sink.Self(nullptr, 0, lastSeq, 0);
        return;
    }

    const ArchetypePlan& archetype = plan_->Archetype(archetypeIdx);
    const auto groupCount = static_cast<int>(archetype.ownerGroups.size());
    if ((mask >> groupCount) != 0)
    {
        throw Malformed("SELF owner mask 0x" + Hex(mask) + " is invalid for " + std::to_string(groupCount) + " owner group(s)");
    }

    sink.Self(&archetype, netId, lastSeq, mask);
    for (int g = 0; g < groupCount; g++)
    {
        if ((mask & (1 << g)) != 0)
        {
            ReadSection(r, archetype.ownerSections[static_cast<std::size_t>(g)], tick, sink, false, realm.get());
        }
    }
}

void TickReader::ReadAggregate(TickSink& sink)
{
    WireReader& r = r_;
    GridPlan& grid = plan_->Grid(r.Varu());
    const std::uint8_t flags = r.U8();

    // The grid's dimensions are the frame's (typhon.3, 12-realms § 5.3): no realm, no grid.
    if (realm == nullptr)
    {
        throw ProtocolError("an AGG block for grid " + std::to_string(grid.idx) + " while the session holds no realm");
    }

    const double cellCount = realm->AggregateCellCount(grid.tileCells);
    sink.BeginAggregate(grid, (flags & 1) != 0);
    std::int64_t prev = -1;
    for (std::uint32_t n = r.Varu(); n > 0; n--)
    {
        const std::int64_t cell = prev + 1 + static_cast<std::int64_t>(r.Varu());
        if (static_cast<double>(cell) >= cellCount)
        {
            throw Malformed("AGG cell " + std::to_string(cell) + " is outside the grid's cells");
        }

        prev = cell;
        for (std::uint32_t& count : grid.counts)
        {
            count = r.Varu();
        }

        sink.AggregateCell(static_cast<std::uint32_t>(cell), grid.counts);
    }
}

void TickReader::ReadMetrics(const std::vector<MetricPlan*>& metrics, std::uint32_t tick, TickSink& sink)
{
    double value[4];
    for (const MetricPlan* m : metrics)
    {
        for (int i = 0; i < m->valueCount; i++)
        {
            if (m->value->valueKind == ValueKind::Skipped)
            {
                r_.Skip(static_cast<std::size_t>(m->value->fixedBytes));
                continue;
            }

            ReadNumber(r_, *m->value, tick, value);
            sink.Metric(*m, i, value[0]);
        }
    }
}

}  // namespace typhon::client
