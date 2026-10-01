// tick-*: the decoder's call log equals the C# RecordingSink's, call for call.

#include "golden_support.hpp"
#include "test_framework.hpp"
#include "wire/tick_reader.hpp"

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
