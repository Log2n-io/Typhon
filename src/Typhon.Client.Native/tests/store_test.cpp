// The store: slots, the netId map, change lists, growth, reset, the text/bytes arenas — the TypeScript SDK's store.test.ts, plus the
// arena this SDK adds (13 § 7).

#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>

#include "motion/motion.hpp"
#include "store/world_store.hpp"
#include "test_framework.hpp"

using namespace typhon::client;

namespace {

WorldSchema Schema()
{
    WorldSchema schema;
    schema.tickPeriodUs = 100000;
    schema.archetypes.push_back({0, "Rock", PositionSchema{false, false, 2}, {}, {{"kind", FieldKind::U8}}});
    schema.archetypes.push_back(
        {1, "Critter", PositionSchema{true, true, 2}, {"state"}, {{"template", FieldKind::U8}, {"hp", FieldKind::F32, 1, 0}}});
    schema.archetypes.push_back({2,
                                 "Probe",
                                 PositionSchema{true, true, 3},
                                 {"look", "tags"},
                                 {{"rotation", FieldKind::F64, 4, 0}, {"label", FieldKind::Text, 1, 1}, {"serial", FieldKind::Bytes}}});
    return schema;
}

std::span<const std::uint8_t> AsBytes(std::string_view s) { return {reinterpret_cast<const std::uint8_t*>(s.data()), s.size()}; }

}  // namespace

TEST(Store_MapsNetIdsToArchetypeAndSlotAndForgetsThemOnLeave)
{
    WorldStore world(Schema());
    world.BeginFrame(1);
    const std::uint32_t a = world.Enter(1, 10);
    const std::uint32_t b = world.Enter(0, 11);
    world.EndFrame();
    CHECK_EQ(ArchetypeOf(world.Locate(10)), 1u);
    CHECK_EQ(SlotOf(world.Locate(10)), a);
    CHECK_EQ(ArchetypeOf(world.Locate(11)), 0u);
    CHECK_EQ(SlotOf(world.Locate(11)), b);
    CHECK_EQ(world.EntityCount(), 2u);

    world.BeginFrame(2);
    CHECK(world.Leave(10));
    CHECK_EQ(world.Locate(10), NotFound);
    CHECK_EQ(world.Archetype(1).Left().size(), 1u);
    CHECK_EQ(world.Archetype(1).Left()[0], 10u);
    CHECK_EQ(world.EntityCount(), 1u);
}

TEST(Store_NeverReusesASlotInsideTheFrameThatFreedIt)
{
    WorldStore world(Schema());
    world.BeginFrame(1);
    const std::uint32_t first = world.Enter(1, 1);
    world.EndFrame();
    world.BeginFrame(2);
    world.Leave(1);
    CHECK(world.Enter(1, 2) != first);
    world.EndFrame();
    world.BeginFrame(3);
    world.Leave(2);
    CHECK_EQ(world.Enter(1, 3), first);
}

TEST(Store_AppliesNetIdReuse_AnEnterForAHeldNetIdReplacesItAsAnAnomaly)
{
    WorldStore world(Schema());
    world.BeginFrame(1);
    world.Enter(1, 5);
    world.EndFrame();

    // A reuse spread over two frames: the leave, then the enter. No anomaly.
    world.BeginFrame(2);
    CHECK(world.Leave(5));
    world.BeginFrame(3);
    world.Enter(0, 5);
    CHECK_EQ(world.anomalies, 0u);

    // An enter for a netId still held replaces the holder, which is listed as left.
    world.BeginFrame(4);
    const std::uint32_t slot = world.Enter(1, 5);
    CHECK_EQ(world.anomalies, 1u);
    CHECK_EQ(ArchetypeOf(world.Locate(5)), 1u);
    CHECK_EQ(SlotOf(world.Locate(5)), slot);
    CHECK_EQ(world.Archetype(0).Left().size(), 1u);
    CHECK_EQ(world.EntityCount(), 1u);

    // A leave naming another archetype changes nothing; one naming the holder's, or none, removes it.
    CHECK(!world.Leave(5, 0));
    CHECK_EQ(world.anomalies, 2u);
    CHECK(world.Leave(5, 1));
    CHECK_EQ(world.EntityCount(), 0u);
    CHECK(!world.Leave(5));
    CHECK_EQ(world.anomalies, 3u);
}

TEST(Store_KeepsLiveSlotsDenseThroughSwapRemove)
{
    WorldStore world(Schema());
    const ArchetypeStore& store = world.Archetype(1);
    world.BeginFrame(1);
    for (std::uint32_t id = 1; id <= 5; id++)
    {
        world.Enter(1, id);
    }

    world.BeginFrame(2);
    world.Leave(2);
    world.Leave(4);
    std::vector<std::uint32_t> live;
    for (const std::uint32_t slot : store.Live())
    {
        CHECK(store.IsLive(slot));
        live.push_back(store.NetIds()[slot]);
    }

    std::sort(live.begin(), live.end());
    CHECK((live == std::vector<std::uint32_t>{1, 3, 5}));
}

TEST(Store_GrowsWithoutLosingDataOrPendingReleases)
{
    WorldStore world(Schema());
    ArchetypeStore& store = world.Archetype(1);
    const int hp = store.FieldIndex("hp");
    world.BeginFrame(1);
    for (std::uint32_t id = 1; id <= 100; id++)
    {
        world.Enter(1, id);
    }

    world.BeginFrame(2);
    for (std::uint32_t id = 1; id <= 50; id++)
    {
        world.Leave(id);
    }

    const std::uint32_t version = store.Version();
    for (std::uint32_t id = 101; id <= 1100; id++)
    {
        const std::uint32_t slot = world.Enter(1, id);
        const double value = id / 2000.0;
        store.SetNumbers(hp, slot, &value);
        const double p[2] = {static_cast<double>(id), -static_cast<double>(id)};
        const double v[2] = {0, 0};
        store.ResetMotion(slot, p, v, 2, 0);
    }

    CHECK(store.Version() > version);
    CHECK_EQ(store.Entered().size(), 1000u);
    CHECK_EQ(store.Left().size(), 50u);
    double out[MaxMotionStride];
    for (std::uint32_t id = 51; id <= 1100; id++)
    {
        const std::uint32_t slot = SlotOf(world.Locate(id));
        CHECK_EQ(store.NetIds()[slot], id);
        if (id > 100)
        {
            CHECK_EQ(store.Column<float>(hp)[slot], static_cast<float>(id / 2000.0));
            EvaluateSlot(store, slot, 2, 0, out);
            CHECK(out[0] == id && out[1] == -static_cast<double>(id));
        }
    }

    // Slots released before the growth are still held back for this frame, then reusable.
    world.BeginFrame(3);
    const std::uint32_t capacity = store.Capacity();
    for (std::uint32_t id = 2000; id < 2050; id++)
    {
        world.Enter(1, id);
    }

    CHECK_EQ(store.Capacity(), capacity);
}

TEST(Store_RecordsMergedUpdateMasksWithMotionAboveTheGroupBits)
{
    WorldStore world(Schema());
    ArchetypeStore& store = world.Archetype(1);
    world.BeginFrame(1);
    const std::uint32_t slot = world.Enter(1, 7);
    CHECK_EQ(store.Entered().size(), 1u);
    world.BeginFrame(2);
    CHECK_EQ(store.Entered().size(), 0u);
    store.MarkUpdated(slot, 0b1);
    const double p[2] = {1, 2};
    const double v[2] = {0.1, 0.2};
    store.PushSegment(slot, p, v, 2, 0);
    CHECK_EQ(store.Updated().size(), 1u);
    CHECK_EQ(store.UpdateMask(slot), 0b1u | MotionChangeBit);
    CHECK_EQ(MotionChangeBit, 0x100u);
    world.BeginFrame(3);
    CHECK_EQ(store.Updated().size(), 0u);
    CHECK_EQ(store.UpdateMask(slot), 0u);
}

TEST(Store_ClearsTheMaskOfAReleasedSlotAndIgnoresUpdatesToDeadSlots)
{
    WorldStore world(Schema());
    ArchetypeStore& store = world.Archetype(1);
    world.BeginFrame(1);
    const std::uint32_t slot = world.Enter(1, 7);
    world.BeginFrame(2);
    store.MarkUpdated(slot, 0b1);
    world.Leave(7);
    CHECK_EQ(store.UpdateMask(slot), 0u);
    store.MarkUpdated(slot, 0b1);
    const double zero[2] = {0, 0};
    store.PushSegment(slot, zero, zero, 2, 0);
    CHECK_EQ(store.UpdateMask(slot), 0u);
    CHECK_THROWS(std::logic_error, store.Release(slot));
    CHECK_THROWS(std::logic_error, store.Release(store.Capacity() + 5));
}

TEST(Store_ZeroesTheFieldsOfAReusedSlot)
{
    WorldStore world(Schema());
    ArchetypeStore& probe = world.Archetype(2);
    world.BeginFrame(1);
    const std::uint32_t slot = world.Enter(2, 1);
    const double rotation[4] = {0.1, 0.2, 0.3, 0.9};
    probe.SetNumbers(0, slot, rotation);
    probe.SetText(1, slot, "alpha");
    const std::uint8_t serial[2] = {1, 2};
    probe.SetBytes(2, slot, serial);
    const double p0[3] = {1, 2, 3};
    const double v0[3] = {4, 5, 6};
    probe.ResetMotion(slot, p0, v0, 1, 3);
    const double p1[3] = {7, 8, 9};
    const double v1[3] = {1, 1, 1};
    probe.PushSegment(slot, p1, v1, 2, 4);
    world.BeginFrame(2);
    world.Leave(1);
    world.BeginFrame(3);
    const std::uint32_t reused = world.Enter(2, 2);
    CHECK_EQ(reused, slot);
    for (int c = 0; c < 4; c++)
    {
        CHECK_EQ(probe.NumberAt(0, slot, c), 0.0);
    }

    CHECK(probe.TextAt(1, reused).empty());
    CHECK(probe.BytesAt(2, reused).empty());
    // Motion: head, count and entry 0 are zeroed; entries past the count are never read.
    double motion[MaxMotionStride] = {9, 9, 9, 9, 9, 9};
    EvaluateSlot(probe, reused, 10, 0.5, motion);
    CHECK(std::all_of(std::begin(motion), std::end(motion), [](double x) { return x == 0; }));
    CHECK_EQ(static_cast<int>(probe.HeadEntry(reused)), 0);
    CHECK_EQ(probe.FieldComponents(0), 4);
    CHECK_EQ(probe.FieldComponents(1), 0);
    CHECK_THROWS(std::logic_error, probe.Column<double>(1));
    CHECK_THROWS(std::bad_variant_access, probe.Column<float>(0));
}

TEST(Store_KeepsMultiComponentAndTextValuesThroughGrowth)
{
    WorldStore world(Schema());
    ArchetypeStore& probe = world.Archetype(2);
    world.BeginFrame(1);
    for (std::uint32_t id = 1; id <= 600; id++)
    {
        const std::uint32_t slot = world.Enter(2, id);
        const double rotation[4] = {0, 0, 0, static_cast<double>(id)};
        probe.SetNumbers(0, slot, rotation);
        probe.SetText(1, slot, "p" + std::to_string(id));
    }

    for (std::uint32_t id = 1; id <= 600; id++)
    {
        const std::uint32_t slot = SlotOf(world.Locate(id));
        CHECK_EQ(probe.NumberAt(0, slot, 3), static_cast<double>(id));
        CHECK_EQ(std::string(probe.TextAt(1, slot)), "p" + std::to_string(id));
    }
}

TEST(Store_CountsAnUnknownLeaveAndAStuckTickAsAnomalies)
{
    WorldStore world(Schema());
    world.BeginFrame(1);
    CHECK(!world.Leave(99));
    CHECK_EQ(world.anomalies, 1u);
    world.BeginFrame(1);
    CHECK_EQ(world.anomalies, 2u);
    // A RESET frame may go back: a restarted server begins again at a low tick.
    world.BeginFrame(10);
    world.BeginFrame(3, true);
    CHECK_EQ(world.anomalies, 2u);
    world.BeginFrame(3);
    CHECK_EQ(world.anomalies, 3u);
}

TEST(Store_DropsEverythingOnResetAndRefillsWithoutGrowing)
{
    WorldStore world(Schema());
    ArchetypeStore& store = world.Archetype(1);
    world.BeginFrame(1);
    const std::uint32_t capacity = store.Capacity();
    for (std::uint32_t id = 1; id <= capacity; id++)
    {
        world.Enter(1, id);
    }

    world.BeginFrame(2);
    world.Reset();
    CHECK(world.resetThisFrame);
    CHECK_EQ(world.EntityCount(), 0u);
    CHECK_EQ(store.Left().size(), 0u);
    CHECK_EQ(world.Locate(1), NotFound);
    for (std::uint32_t id = 1000; id < 1000 + capacity; id++)
    {
        world.Enter(1, id);
    }

    CHECK_EQ(store.Capacity(), capacity);
    CHECK_EQ(store.Entered().size(), capacity);
    world.BeginFrame(3);
    CHECK(!world.resetThisFrame);
}

TEST(Store_RefusesABadMaxNetIdOrRenderDelayAndOutOfRangeNetIds)
{
    CHECK_THROWS(std::invalid_argument, WorldStore(Schema(), {0, 300}));
    CHECK_THROWS(std::invalid_argument, WorldStore(Schema(), {NotFound, 300}));
    for (const double delay : {0.0, -5.0, std::numeric_limits<double>::infinity(), std::numeric_limits<double>::quiet_NaN()})
    {
        CHECK_THROWS(std::invalid_argument, WorldStore(Schema(), {1000, delay}));
    }

    WorldStore world(Schema(), {1000, 300});
    world.BeginFrame(1);
    CHECK_THROWS(std::out_of_range, world.Enter(1, 0));
    CHECK_THROWS(std::out_of_range, world.Enter(1, 1001));
    CHECK_EQ(world.Enter(1, 1000), 0u);
    CHECK_EQ(world.Locate(5000000), NotFound);
}

TEST(Store_StartsSmallAndDoubles)
{
    WorldStore world(Schema());
    ArchetypeStore& store = world.Archetype(2);
    CHECK_EQ(store.Capacity(), 16u);
    world.BeginFrame(1);
    for (std::uint32_t id = 1; id <= 17; id++)
    {
        world.Enter(2, id);
    }

    CHECK_EQ(store.Capacity(), 32u);
}

TEST(Store_SizesTheMotionRingFromTheTickPeriod)
{
    CHECK_EQ(SegmentHistoryFor(50000), 8);
    CHECK_EQ(SegmentHistoryFor(100000), 5);
    CHECK_EQ(SegmentHistoryFor(1000000), 4);
    CHECK_EQ(SegmentHistoryFor(1000), 255);
    CHECK_EQ(MotionSegmentsOffset(8), 48);
    CHECK_EQ(MotionRecordBytes(3, 8) % 8, 0);
}

TEST(Store_ValidateSchemaRefusesInconsistentShapes)
{
    auto bad = Schema();
    bad.tickPeriodUs = 0;
    CHECK_THROWS(std::invalid_argument, ValidateSchema(bad));
    bad = Schema();
    bad.archetypes[1].index = 5;
    CHECK_THROWS(std::invalid_argument, ValidateSchema(bad));
    bad = Schema();
    bad.archetypes[1].groups.assign(9, "g");
    CHECK_THROWS(std::invalid_argument, ValidateSchema(bad));
    bad = Schema();
    bad.archetypes[1].position->dims = 4;
    CHECK_THROWS(std::invalid_argument, ValidateSchema(bad));
    bad = Schema();
    bad.archetypes[1].fields[1].group = 3;
    CHECK_THROWS(std::invalid_argument, ValidateSchema(bad));
    bad = Schema();
    bad.archetypes[2].fields[0].components = 5;
    CHECK_THROWS(std::invalid_argument, ValidateSchema(bad));
    bad = Schema();
    bad.archetypes[1].fields[1].name = "template";
    CHECK_THROWS(std::invalid_argument, ValidateSchema(bad));
}

TEST(Arena_RewritesInPlaceWhileAValueFitsAndAppendsOtherwise)
{
    ByteArena arena;
    arena.Grow(4);
    arena.Set(0, AsBytes("hello"));
    arena.Set(1, AsBytes("x"));
    CHECK_EQ(arena.ArenaBytes(), 6u);
    // Shorter or equal: in place, nothing appended.
    arena.Set(0, AsBytes("hey"));
    arena.Set(0, AsBytes("world"));
    CHECK_EQ(arena.ArenaBytes(), 6u);
    CHECK(std::ranges::equal(arena.At(0), AsBytes("world")));
    // Longer: appended, the old reservation becomes dead space.
    arena.Set(1, AsBytes("longer"));
    CHECK_EQ(arena.ArenaBytes(), 12u);
    CHECK(std::ranges::equal(arena.At(1), AsBytes("longer")));
    arena.Clear(0);
    CHECK(arena.At(0).empty());
    CHECK(std::ranges::equal(arena.At(1), AsBytes("longer")));
}

TEST(Arena_CompactsOnceHalfIsDeadAndKeepsEveryValue)
{
    ByteArena arena;
    arena.Grow(64);
    // Every slot grows its value repeatedly: without compaction the arena would hold every past reservation.
    std::size_t largest = 0;
    for (int round = 1; round <= 200; round++)
    {
        for (std::uint32_t slot = 0; slot < 64; slot++)
        {
            const std::string value(static_cast<std::size_t>(round) % 40 + slot % 3, static_cast<char>('a' + slot % 26));
            arena.Set(slot, AsBytes(value));
        }

        largest = std::max(largest, arena.ArenaBytes());
    }

    for (std::uint32_t slot = 0; slot < 64; slot++)
    {
        const std::string expected(200 % 40 + slot % 3, static_cast<char>('a' + slot % 26));
        CHECK(std::ranges::equal(arena.At(slot), AsBytes(expected)));
    }

    // Bounded by a small multiple of the live bytes (64 slots x at most 41 B), not by 200 rounds of growth.
    CHECK_MSG(largest < 64 * 41 * 4, "the arena reached " << largest << " bytes");
}
