// tick-*: the decoder's call log equals the C# RecordingSink's, call for call.

#include "golden_support.hpp"
#include "test_framework.hpp"
#include "wire/tick_reader.hpp"

#include <climits>
#include <cmath>
#include <cstdint>
#include <stdexcept>

#include "apply/frame_applier.hpp"

using namespace typhon::client;
using namespace typhon::test;

TEST(GoldenTicks_EveryVectorDecodesToTheReferenceLog)
{
    const auto names = GoldenNames("tick-");
    CHECK(names.size() >= 3);
    for (const std::string& name : names)
    {
        const Value vector = GoldenJson(name);
        TickReader reader(PlanOf(vector.Find("catalog")->AsString()));
        // A vector whose message does not carry its own REALM names the frame the session held before it.
        reader.realm = FrameFromJson(vector.Find("frame"));
        RecordingSink sink;
        reader.Read(GoldenBin(name), sink);
        CheckLog(sink.log, *vector.Find("log"), name);
    }
}

TEST(GoldenTicks_SkipsUnselectedBlocksByTheirLength)
{
    const Value vector = GoldenJson("tick-blocks");
    TickReader reader(PlanOf(vector.Find("catalog")->AsString()));
    RecordingSink sink;
    reader.Read(GoldenBin("tick-blocks"), sink, BlockMask::Acks);
    std::vector<std::string> calls;
    for (const Value& entry : sink.log)
    {
        calls.push_back(entry.Find("call")->AsString());
    }

    CHECK(calls == (std::vector<std::string>{"beginTick", "ack", "ack", "endTick"}));
}

// W32 / W33 end to end in the store: tick-exact's 64-bit values land in u64 / i64 columns exactly, a count field is count values per
// slot, and an event and SELF hand their 64-bit fields over as bit patterns.
TEST(GoldenTicks_ExactValuesLandInTypedColumns)
{
    std::vector<std::int64_t> amounts;
    std::vector<double> corners;
    FrameApplierOptions options;
    options.onEvent = [&](const EventRecord& event)
    {
        amounts.push_back(static_cast<std::int64_t>(event.Integers(event.FieldIndex("amount"))[0]));
        const auto corner = event.Numbers(event.FieldIndex("corner"));
        corners.assign(corner.begin(), corner.end());
    };

    const auto plan = PlanOf("catalog-exact");
    FrameApplier applier(plan, std::move(options));
    applier.Apply(GoldenBin("tick-exact"));

    const ArchetypePlan& vaultPlan = *plan->ArchetypeByName("Vault");
    const ArchetypeStore& vault = applier.World().Archetype(static_cast<std::size_t>(vaultPlan.idx));
    const auto slotOf = [&](std::uint32_t netId) { return SlotOf(applier.World().Locate(netId)); };
    const int balance = vault.FieldIndex("balance");
    const int seen = vault.FieldIndex("seen");
    const int delta = vault.FieldIndex("delta");
    const int id = vault.FieldIndex("id");
    const int box = vault.FieldIndex("box");
    CHECK(vault.Schema().fields[static_cast<std::size_t>(balance)].kind == FieldKind::U64);
    CHECK(vault.Schema().fields[static_cast<std::size_t>(id)].kind == FieldKind::I64);
    // The state records swapped the money groups: netId 1 holds the second value set, netId 2 kept its own.
    CHECK_EQ(vault.IntegerAt(balance, slotOf(1)), UINT64_MAX);
    CHECK_EQ(vault.IntegerAt(balance, slotOf(2)), UINT64_MAX);
    // Each 64-bit field has its own column: seen is the second set's 0 where balance is all ones, so a column mix-up cannot pass.
    CHECK_EQ(vault.IntegerAt(seen, slotOf(1)), 0u);
    CHECK_EQ(vault.IntegerAt(seen, slotOf(2)), 0u);
    CHECK_EQ(static_cast<std::int64_t>(vault.IntegerAt(delta, slotOf(1))), -(std::int64_t{1} << 53) - 1);
    CHECK_EQ(vault.Column<std::int64_t>(id)[slotOf(1)], INT64_MIN);
    CHECK_EQ(vault.Column<std::int64_t>(id)[slotOf(2)], -1);
    const auto boxes = vault.Column<float>(box);
    const std::vector<float> box2(boxes.begin() + 6 * slotOf(2), boxes.begin() + 6 * slotOf(2) + 6);
    CHECK(box2 == (std::vector<float>{-1, -2, -3, 1, 2, 3.5f}));

    CHECK(amounts == (std::vector<std::int64_t>{-(std::int64_t{1} << 53) - 1}));
    CHECK(corners.size() == 3 && corners[0] == 1 && std::isinf(corners[1]) && corners[2] == -2.5);
    const SelfState& self = applier.Self();
    const ArchetypePlan& owner = *self.archetype;
    int pin = -1;
    for (const auto& f : owner.ownerFields)
    {
        if (f->name == "pin")
        {
            pin = f->index;
        }
    }

    CHECK_EQ(self.Integers(pin)[0], 0x8000000000000001ull);
    CHECK_EQ(applier.World().anomalies, 0u);
}

// W34: the tick-coll vector in the store — an entity's and the controlled entity's lists overwrite whole, keep their total when
// truncated, and keep their columns between sends (the .NET CollectionStoreTests and the TypeScript collections test).
TEST(GoldenTicks_TheCollectionVectorLandsInTheStore)
{
    const auto plan = PlanOf("catalog-coll");
    FrameApplier applier(plan);
    applier.Apply(GoldenBin("tick-coll"));

    const std::uint32_t one = applier.World().Locate(1);
    const std::uint32_t two = applier.World().Locate(2);
    const ArchetypeStore& store = applier.World().Archetype(ArchetypeOf(one));
    const int items = store.FieldIndex("items");
    const int tags = store.FieldIndex("tags");
    CHECK(store.Schema().fields[static_cast<std::size_t>(items)].kind == FieldKind::Collection);

    // netId 1 entered with no item and one tag, then a state sent one item: the list is that one.
    const CollectionValue* first = store.CollectionAt(items, SlotOf(one));
    CHECK(first != nullptr);
    CHECK(first->Total() == 1 && first->Count() == 1 && !first->Truncated());
    CHECK_EQ(first->NumberAt(0, "id"), 65535.0);
    CHECK_EQ(first->NumberAt(0, "owner"), 4000000000.0);
    CHECK(first->TextAt(0, "name").empty());
    CHECK_EQ(store.CollectionAt(tags, SlotOf(one))->NumberAt(0, "tag"), 9.0);

    // netId 2: four of nine sent, in the state's order — the enter's list is overwritten whole.
    const CollectionValue* second = store.CollectionAt(items, SlotOf(two));
    CHECK(second->Total() == 9 && second->Count() == 4 && second->Truncated());
    CHECK(second->TextAt(0, "name") == "bouclier \xC3\xB8" && second->TextAt(1, "name") == "sword");
    CHECK(second->TextAt(2, "name") == "bouclier \xC3\xB8" && second->TextAt(3, "name").empty());
    CHECK(second->NumberAt(0, "stack") == 31 && second->NumberAt(1, "stack") == 3);
    CHECK(second->NumberAt(0, "lit") == 0 && second->NumberAt(1, "lit") == 1);
    CHECK_EQ(store.CollectionAt(tags, SlotOf(two))->Count(), 3u);
    // Past Count() nothing is readable by name.
    CHECK_THROWS(std::out_of_range, (void)second->NumberAt(4, "stack"));

    // SELF: the owner collection of the controlled entity.
    const SelfState& self = applier.Self();
    int keys = -1;
    for (const auto& f : self.archetype->ownerFields)
    {
        if (f->name == "keys")
        {
            keys = f->index;
        }
    }

    const CollectionValue* held = self.Collection(keys);
    CHECK(held != nullptr && held->Total() == 2 && held->Count() == 2);
    CHECK_EQ(held->IntegerAt(0, "code"), UINT64_MAX);
    CHECK_EQ(held->NumberAt(0, "where"), 1.5);
    CHECK_EQ(applier.World().anomalies, 0u);
}

// A change of controlled entity drops the previous one's collection, even where the new archetype's owner field at its place is a number.
TEST(Collections_AControlChangeDropsTheOwnerCollection)
{
    const auto coll = PlanOf("catalog-coll");
    const auto kitchen = PlanOf("catalog-kitchen-sink");
    const ArchetypePlan& locker = *coll->ArchetypeByName("Locker");
    const ArchetypePlan* numeric = nullptr;
    for (const auto& a : kitchen->Archetypes())
    {
        if (!a->ownerFields.empty() && a->ownerFields[0]->valueKind == ValueKind::Number)
        {
            numeric = a.get();
        }
    }

    CHECK(numeric != nullptr && locker.ownerFields[0]->valueKind == ValueKind::Collection);
    SelfState self;
    self.Receive(&locker, 1, 0, 1);
    self.CollectionFor(*locker.ownerFields[0]).Begin(2, 2);
    CHECK(self.Collection(0) != nullptr);

    self.Receive(numeric, 2, 0, 1);
    const double one = 1;
    self.SetNumber(*numeric->ownerFields[0], &one);
    CHECK(self.Collection(0) == nullptr);
    self.Receive(&locker, 3, 0, 1);
    CHECK(self.Collection(0) == nullptr);
}

// The capacity grows to the largest list and is kept; a shorter list reuses it, and the cut is visible.
TEST(Collections_AListGrowsShrinksAndReusesItsColumns)
{
    const auto coll = PlanOf("catalog-coll");
    const FieldPlan& keys = *coll->ArchetypeByName("Locker")->ownerFields[0];
    CollectionValue value;
    value.Bind(keys);
    value.Begin(2, 2);
    const std::uint32_t grown = value.Capacity();
    CHECK(grown >= 2);
    value.Begin(9, 1);
    CHECK(value.Capacity() == grown && value.Count() == 1 && value.Total() == 9 && value.Truncated());
    CHECK_THROWS(std::out_of_range, (void)value.NumberAt(1, "where"));
    CHECK_THROWS(std::out_of_range, (void)value.Numbers(99));
    value.Reset();
    CHECK(value.Count() == 0 && value.Capacity() == grown && !value.Truncated());
}
