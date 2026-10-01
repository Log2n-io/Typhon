// stream-*, the engine streams and motion-eval: the store after each frame, rendered in the implementation-neutral shape of the .NET
// SDK's StreamSnapshot (every collection sorted by its key, numbers as IEEE bits, text and bytes as hex); the per-frame call log of the
// engine's own encoder; and § 6's motion evaluation, bit for bit.

#include <algorithm>
#include <cstring>
#include <sstream>

#include "apply/frame_applier.hpp"
#include "golden_support.hpp"
#include "motion/motion.hpp"
#include "snapshot.hpp"
#include "test_framework.hpp"
#include "wire/messages.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

// Two kinds of vector share the stream- prefix: a SNAPSHOT vector ({catalog, snapshots}) is read here, a CALL-LOG vector
// ({catalogHash, frames}) by the engine-stream test. The partition is stated, and the leftovers asserted.
const std::vector<std::string> CallLogVectors = {"stream-engine", "stream-engine-3d"};

// Records each frame's decode calls, spelled exactly as the .NET fixture spells them.
class CallLogSink final : public TickSink {
public:
    std::vector<std::vector<std::string>> frames;
    std::vector<std::uint32_t> ticks;
    std::vector<std::uint8_t> flags;

    void BeginTick(std::uint32_t tick, std::uint8_t f, std::uint32_t) override
    {
        calls_ = {"beginTick " + std::to_string(tick)};
        ticks.push_back(tick);
        flags.push_back(f);
    }

    void Realm(const RealmFrame* frame) override { calls_.push_back(frame == nullptr ? "realm none" : "realm " + std::to_string(frame->realmId)); }
    void BeginEntities(const ArchetypePlan& archetype) override { calls_.push_back("beginEntities " + archetype.name); }
    void Enter(std::uint32_t netId, std::span<const double>, std::span<const double>, std::uint32_t, std::uint8_t) override
    {
        calls_.push_back("enter " + std::to_string(netId));
    }

    void Segment(std::uint32_t netId, std::span<const double>, std::span<const double>, std::uint32_t, std::uint8_t) override
    {
        calls_.push_back("segment " + std::to_string(netId));
    }

    void State(std::uint32_t netId, std::uint8_t groupMask) override
    {
        char mask[8];
        std::snprintf(mask, sizeof mask, "0x%02x", groupMask);
        calls_.push_back("state " + std::to_string(netId) + " " + mask);
    }

    void Leave(std::uint32_t netId) override { calls_.push_back("leave " + std::to_string(netId)); }
    void Event(const MessagePlan& type) override { calls_.push_back("event " + type.name); }
    void Self(const ArchetypePlan*, std::uint32_t netId, std::uint32_t, std::uint8_t) override { calls_.push_back("self " + std::to_string(netId)); }
    void Ack(std::uint32_t seq, std::uint8_t) override { calls_.push_back("ack " + std::to_string(seq)); }
    void Source(std::uint32_t requestId, std::uint8_t, std::uint32_t) override { calls_.push_back("source " + std::to_string(requestId)); }
    void BeginAggregate(const GridPlan& grid, bool) override { calls_.push_back("beginAggregate " + std::to_string(grid.idx)); }
    void AggregateCell(std::uint32_t cell, std::span<const std::uint32_t>) override { calls_.push_back("aggregateCell " + std::to_string(cell)); }
    void Metric(const MetricPlan& metric, int, double) override { calls_.push_back("metric " + metric.name); }
    void Debug(std::uint8_t subType, std::span<const std::uint8_t>) override { calls_.push_back("debug " + std::to_string(subType)); }
    void Ext(std::uint32_t appTypeId, std::span<const std::uint8_t>) override { calls_.push_back("ext " + std::to_string(appTypeId)); }
    void UnknownBlock(std::uint8_t blockType) override { calls_.push_back("unknownBlock " + std::to_string(blockType)); }

    void EndTick() override
    {
        calls_.push_back("endTick");
        frames.push_back(calls_);
    }

    void Number(const FieldPlan&, const double*) override {}
    void Integer64(const FieldPlan&, const std::uint64_t*) override {}
    void Text(const FieldPlan&, std::string_view) override {}
    void Bytes(const FieldPlan&, std::span<const std::uint8_t>) override {}
    void List(const FieldPlan&, int, const double*) override {}

private:
    std::vector<std::string> calls_;
};

std::size_t LiveCount(const FrameApplier& applier, std::string_view archetype)
{
    return applier.World().Archetype(static_cast<std::size_t>(applier.Plan().ArchetypeByName(archetype)->idx)).LiveCount();
}

// Replays an engine stream (a WELCOME, then TICKs) through an applier and a call-log reader, and checks the log against the vector.
std::unique_ptr<FrameApplier> ReplayEngineStream(const std::string& name)
{
    const Value vector = GoldenJson(name);
    const auto messages = Unframe(GoldenBin(name));
    CHECK_MSG(messages.size() > 1, name << ": the stream is a WELCOME and at least one TICK");
    const WelcomeMessage welcome = ParseWelcome(messages[0]);
    CHECK_MSG(!welcome.catalogJson.empty(), name << ": the vector carries its own catalog");
    // The .bin and the .json are two files that can drift apart; the digest stops them doing it silently.
    CHECK_EQ(CatalogHashToHex(welcome.catalogHash), vector.Find("catalogHash")->AsString());

    auto catalog = std::make_shared<const Catalog>(ParseCatalog(std::span<const std::uint8_t>(welcome.catalogJson)));
    const auto plan = CatalogPlan::Compile(std::move(catalog));
    auto applier = std::make_unique<FrameApplier>(plan);
    TickReader reader(plan);
    CallLogSink sink;
    for (std::size_t i = 1; i < messages.size(); i++)
    {
        applier->Apply(messages[i]);
        reader.Read(messages[i], sink);
    }

    const auto& expected = vector.Find("frames")->Items();
    CHECK_EQ(sink.frames.size(), expected.size());
    for (std::size_t i = 0; i < expected.size(); i++)
    {
        std::vector<std::string> calls;
        for (const Value& call : expected[i].Find("calls")->Items())
        {
            calls.push_back(call.AsString());
        }

        CHECK_MSG(sink.frames[i] == calls, name << ": frame " << i << " calls differ");
        CHECK_EQ(static_cast<double>(sink.ticks[i]), expected[i].Find("tick")->AsNumber());
        CHECK_EQ(static_cast<double>(sink.flags[i]), expected[i].Find("flags")->AsNumber());
    }

    CHECK_MSG(applier->World().anomalies == 0, name << ": every update names an entity the store holds");
    CHECK_EQ(applier->World().frames, static_cast<std::uint64_t>(messages.size() - 1));
    CHECK_MSG((applier->Flags() & TickFlags::Reset) != 0, name << ": the last frame is the profile switch");
    return applier;
}

}  // namespace

TEST(GoldenStreams_EverySnapshotVectorRendersTheReferenceReplica)
{
    std::vector<std::string> names;
    for (const std::string& name : GoldenNames("stream-"))
    {
        if (std::find(CallLogVectors.begin(), CallLogVectors.end(), name) == CallLogVectors.end())
        {
            names.push_back(name);
        }
    }

    CHECK(std::find(names.begin(), names.end(), "stream-kitchen-sink") != names.end());
    for (const std::string& name : names)
    {
        const Value vector = GoldenJson(name);
        CHECK_MSG(vector.Find("snapshots") != nullptr, name << " is not a snapshot vector; classify it");
        std::vector<Value> events;
        FrameApplierOptions options;
        FrameApplier* self = nullptr;
        options.onEvent = [&](const EventRecord& event) { events.push_back(RenderEvent(event, *self)); };
        FrameApplier applier(PlanOf(vector.Find("catalog")->AsString()), std::move(options));
        self = &applier;

        const auto messages = Unframe(GoldenBin(name));
        const auto& snapshots = vector.Find("snapshots")->Items();
        CHECK_EQ(messages.size(), snapshots.size());
        for (std::size_t i = 0; i < messages.size(); i++)
        {
            events.clear();
            applier.Apply(messages[i]);
            const Value actual = Render(applier, events);
            CHECK_MSG(actual.Equals(snapshots[i]), name << ": snapshot after frame " << i << "\n  got      " << actual.Dump() << "\n  expected "
                                                        << snapshots[i].Dump());
        }
    }
}

TEST(GoldenStreams_TheEngineStreamAppliesToWhatTheServerHeld)
{
    const auto applier = ReplayEngineStream("stream-engine");
    // The last frame is the RESET refill, so the counts are that frame's, not the churn's.
    CHECK_EQ(LiveCount(*applier, "ProjCreature"), 4u);
    CHECK_EQ(LiveCount(*applier, "ProjRock"), 3u);
}

TEST(GoldenStreams_TheDeepEngineStreamAppliesToWhatTheServerHeld)
{
    const auto applier = ReplayEngineStream("stream-engine-3d");
    CHECK_EQ(applier->Plan().ArchetypeByName("ProjFlyer")->position->dims, 3);
    CHECK_EQ(LiveCount(*applier, "ProjFlyer"), 4u);
    CHECK_EQ(LiveCount(*applier, "ProjCreature"), 3u);
    CHECK_EQ(LiveCount(*applier, "ProjRock"), 2u);
}

namespace {

Value MotionEntity(const std::string& archetype, std::uint32_t netId, std::uint8_t epoch, const double* out, int dims)
{
    const auto d = static_cast<std::size_t>(dims);
    return Value::MakeObject({{"archetype", Str(archetype)},
                              {"netId", Num(netId)},
                              {"epoch", Num(epoch)},
                              {"position", BitsOf({out, d})},
                              {"velocity", BitsOf({out + d, d})}});
}

void CheckEntities(const std::vector<Value>& actual, const Value& expected, const std::string& context)
{
    const auto& want = expected.Items();
    CHECK_MSG(actual.size() == want.size(), context << ": " << actual.size() << " entities, expected " << want.size());
    for (std::size_t i = 0; i < want.size(); i++)
    {
        CHECK_MSG(actual[i].Equals(want[i]), context << ": entity " << i << " is " << actual[i].Dump() << ", expected " << want[i].Dump());
    }
}

}  // namespace

TEST(GoldenMotion_EvaluationMatchesTheReferenceBitForBit)
{
    // The vectors, and the cases each must carry: a vector that loses a case fails rather than quietly shrinking.
    const std::vector<std::pair<std::string, std::vector<std::string>>> vectors = {
        {"motion-eval",
         {"a-static-archetype", "exactly-at-a-segment-start", "between-two-none-samples", "before-the-oldest-segment-held",
          "past-the-newest-segment"}},
        {"motion-eval-wrap",
         {"before-the-wrap", "inside-the-wrap", "across-the-ring-index-wrap", "after-the-wrap", "older-than-the-oldest-held",
          "just-before-the-epoch-change", "between-the-two-epochs-samples", "just-after-the-epoch-change"}},
    };

    for (const auto& [name, caseNames] : vectors)
    {
        const Value vector = GoldenJson(name);
        const auto plan = PlanOf(vector.Find("catalog")->AsString());
        const auto frames = Unframe(GoldenBin(vector.Find("stream")->AsString()));
        const auto& cases = vector.Find("cases")->Items();

        // The ring depth the vector was generated with: a store that sizes its ring differently evaluates differently.
        {
            FrameApplier applier(plan);
            for (const auto& a : plan->Archetypes())
            {
                CHECK_EQ(static_cast<double>(applier.World().Archetype(static_cast<std::size_t>(a->idx)).SegmentHistory()),
                         vector.Find("segmentHistory")->AsNumber());
            }
        }

        std::vector<std::string> carried;
        for (const Value& c : cases)
        {
            carried.push_back(c.Find("name")->AsString());
        }

        CHECK_MSG(carried == caseNames, name << ": the cases differ from the expected list");

        for (const Value& testCase : cases)
        {
            const std::string context = name + "/" + testCase.Find("name")->AsString();
            const auto afterFrame = static_cast<std::size_t>(testCase.Find("afterFrame")->AsNumber());
            CHECK(afterFrame < frames.size());
            FrameApplier applier(plan);
            for (std::size_t i = 0; i <= afterFrame; i++)
            {
                applier.Apply(frames[i], 1000.0 + static_cast<double>(i) * 50);
            }

            const auto renderTick = static_cast<std::int64_t>(testCase.Find("renderTick")->AsNumber());
            const double frac = FromBits(testCase.Find("renderFrac")->AsString());
            double out[MaxMotionStride];

            // Per slot: every entity the case names, found by netId — slots are this implementation's business.
            std::vector<Value> perSlot;
            for (const Value& entity : testCase.Find("entities")->Items())
            {
                const std::string& archetypeName = entity.Find("archetype")->AsString();
                const ArchetypePlan* archetype = plan->ArchetypeByName(archetypeName);
                const auto netId = static_cast<std::uint32_t>(entity.Find("netId")->AsNumber());
                const std::uint32_t location = applier.World().Locate(netId);
                CHECK_MSG(location != NotFound, context << ": " << archetypeName << " " << netId << " is held");
                CHECK_EQ(ArchetypeOf(location), static_cast<std::uint32_t>(archetype->idx));
                const ArchetypeStore& store = applier.World().Archetype(static_cast<std::size_t>(archetype->idx));
                const std::uint32_t slot = SlotOf(location);
                EvaluateSlot(store, slot, renderTick, frac, out);
                perSlot.push_back(MotionEntity(archetypeName, netId, EpochAt(store, slot, renderTick), out, store.Dims()));
            }

            CheckEntities(perSlot, *testCase.Find("entities"), context + " per slot");

            // In batch, over exactly the live entities of the archetypes the case names: a missed enter or a stray leave shows up as a
            // different list, not as a value that happens to match.
            std::vector<std::pair<std::pair<int, std::uint32_t>, Value>> perLive;
            for (const Value& archetypeName : testCase.Find("archetypes")->Items())
            {
                const ArchetypePlan* archetype = plan->ArchetypeByName(archetypeName.AsString());
                const ArchetypeStore& store = applier.World().Archetype(static_cast<std::size_t>(archetype->idx));
                const auto stride = static_cast<std::size_t>(store.MotionStride());
                std::vector<double> all(store.LiveCount() * stride);
                EvaluateLive(store, renderTick, frac, all);
                for (std::size_t i = 0; i < store.LiveCount(); i++)
                {
                    const std::uint32_t slot = store.Live()[i];
                    const std::uint32_t netId = store.NetIds()[slot];
                    perLive.push_back({{archetype->idx, netId},
                                       MotionEntity(archetypeName.AsString(), netId, EpochAt(store, slot, renderTick), all.data() + i * stride,
                                                    store.Dims())});
                }
            }

            std::sort(perLive.begin(), perLive.end(), [](const auto& x, const auto& y) { return x.first < y.first; });
            std::vector<Value> batch;
            for (auto& e : perLive)
            {
                batch.push_back(std::move(e.second));
            }

            CheckEntities(batch, *testCase.Find("entities"), context + " in batch");
        }
    }
}
