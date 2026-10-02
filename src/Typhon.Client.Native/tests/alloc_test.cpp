// E-14: no allocation per steady-state frame. Counted twice over: through the SDK's allocator hooks (every buffer the replica, the arenas and the
// queue grow) AND through a replaced global operator new, which sees whatever does not go through the hooks — a std::string or std::vector a
// decoder path forgot about. A frame applied after warm-up must cost zero of either.

#include <cstring>
#include <string>

#include "alloc_counter.hpp"
#include "apply/frame_applier.hpp"
#include "commands/command_queue.hpp"
#include "golden_support.hpp"
#include "snapshot.hpp"
#include "store/memory.hpp"
#include "test_framework.hpp"
#include "wire/constants.hpp"
#include "wire/realm_frame.hpp"

using namespace typhon::client;
using namespace typhon::test;

TEST(Allocation_TheCounterSeesWhatItMustSee)
{
    // A measurement that cannot fail is not one: an allocation through each door has to register.
    CountingHooks hooks;
    const auto [news, hookAllocs] = CountAllocations(
        []
        {
            auto* p = new int(3);
            delete p;
            Vec<int> v(10);
        });
    // At least: a Debug standard library adds its own bookkeeping allocations (MSVC's iterator proxies), never fewer.
    CHECK(news >= 1);
    CHECK(hookAllocs >= 1);
}

TEST(Allocation_ASteadyStateFrameAllocatesNothing)
{
    CountingHooks hooks;
    for (const char* name : {"stream-kitchen-sink", "stream-motion"})
    {
        const Value vector = GoldenJson(name);
        const auto frames = Unframe(GoldenBin(name));
        std::size_t events = 0;
        FrameApplierOptions options;
        options.onEvent = [&events](const EventRecord&) { events++; };
        FrameApplier applier(PlanOf(vector.Find("catalog")->AsString()), std::move(options));

        // Applied once whole, then its frames without RESET again and again. A RESET is not a steady-state frame — it re-lays the grids over
        // the realm and reshapes SELF, by design — so the measured passes skip them; everything else, each pass is the same work over a replica
        // whose buffers the warm-up grew to their working size.
        for (const auto& frame : frames)
        {
            applier.Apply(frame);
        }

        const auto pass = [&]
        {
            for (const auto& frame : frames)
            {
                if ((frame[5] & TickFlags::Reset) == 0)
                {
                    applier.Apply(frame);
                }
            }
        };

        for (int warm = 0; warm < 3; warm++)
        {
            pass();
        }

        const std::size_t before = events;
        const auto [news, hookAllocs] = CountAllocations(
            [&]
            {
                for (int i = 0; i < 20; i++)
                {
                    pass();
                }
            });
        CHECK_MSG(news == 0 && hookAllocs == 0, name << ": " << news << " operator new and " << hookAllocs << " hook allocations over 20 passes");
        // The kitchen sink is the stream with events: its passes must have dispatched some, or the event path went unmeasured.
        CHECK_MSG(std::string(name) != "stream-kitchen-sink" || events > before, name << ": the measured passes dispatched no event");
        CHECK(applier.World().frames > frames.size());
    }
}

// E-14 with collections (W34): the tick-coll frame, applied once as it is — a RESET, which shapes the store and SELF by design — then again and again
// with the RESET bit cleared, re-sends both entities' lists, their text and the controlled entity's owner list over the same netIds. After the warm-up
// every column, arena and slot has its size, so a frame of collections allocates nothing.
TEST(Allocation_ASteadyCollectionFrameAllocatesNothing)
{
    CountingHooks hooks;
    FrameApplier applier(PlanOf("catalog-coll"));
    const auto reset = GoldenBin("tick-coll");
    CHECK((reset[5] & TickFlags::Reset) != 0);
    applier.Apply(reset);
    auto frame = reset;
    frame[5] = static_cast<std::uint8_t>(frame[5] & ~TickFlags::Reset);
    for (int warm = 0; warm < 4; warm++)
    {
        applier.Apply(frame);
    }

    const auto [news, hookAllocs] = CountAllocations(
        [&]
        {
            for (int i = 0; i < 50; i++)
            {
                applier.Apply(frame);
            }
        });
    CHECK_MSG(news == 0 && hookAllocs == 0, news << " operator new and " << hookAllocs << " hook allocations over 50 collection frames");

    const ArchetypePlan& locker = *applier.Plan().ArchetypeByName("Locker");
    const ArchetypeStore& store = applier.World().Archetype(static_cast<std::size_t>(locker.idx));
    const CollectionValue* items = store.CollectionAt(store.FieldIndex("items"), SlotOf(applier.World().Locate(2)));
    CHECK(items != nullptr && items->Count() == 4 && items->Total() == 9);
}

TEST(Allocation_ASteadyStateCommandBatchAllocatesNothing)
{
    CountingHooks hooks;
    const auto plan = PlanOf("catalog-kitchen-sink");
    CommandQueue queue(plan, [] { return 0.0; });
    const MessagePlan& steer = *plan->CommandByName("Steer");
    const double min[3] = {-8192, -8192, 0};
    const double max[3] = {8192, 8192, 256};
    const RealmFrame frame(0, 0, 0, 0, 24, 256.0, false, min, max);
    double heading = 0.25;
    const double one = 1;
    const double half = 0.5;
    const NamedValue values[] = {{"heading", FieldValue::OfNumbers({&heading, 1})},
                                 {"boost", FieldValue::OfNumbers({&one, 1})},
                                 {"speed", FieldValue::OfNumbers({&half, 1})},
                                 {"stance", FieldValue::OfNumbers({&one, 1})},
                                 {"note", FieldValue::OfText("go")}};
    std::size_t bytes = 0;
    const std::function<void(std::span<const std::uint8_t>)> send = [&bytes](std::span<const std::uint8_t> m) { bytes += m.size(); };
    const auto batch = [&]
    {
        for (int i = 0; i < 8; i++)
        {
            queue.Enqueue(steer, values);
        }

        queue.Flush(1, send, &frame);
    };

    for (int warm = 0; warm < 3; warm++)
    {
        batch();
    }

    const auto [news, hookAllocs] = CountAllocations(
        [&]
        {
            for (int i = 0; i < 50; i++)
            {
                batch();
            }
        });
    CHECK_MSG(news == 0 && hookAllocs == 0, news << " operator new and " << hookAllocs << " hook allocations over 50 batches");
    CHECK(bytes > 0);
}
