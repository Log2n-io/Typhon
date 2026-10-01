// tick-*: the decoder's call log equals the C# RecordingSink's, call for call.

#include "golden_support.hpp"
#include "test_framework.hpp"
#include "wire/tick_reader.hpp"

#include <climits>
#include <cmath>
#include <cstdint>

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
