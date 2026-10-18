// subscribe-requests: the .NET core's script of requests, pumps, frames and a session's end, replayed through this core over a real
// CommandQueue — whose token bucket is the command's catalog rate — at the times the steps name.

#include <string>
#include <vector>

#include "commands/command_queue.hpp"
#include "commands/group_subscriptions.hpp"
#include "golden_support.hpp"
#include "test_framework.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

const char* OutcomeName(SubscribeOutcome outcome)
{
    switch (outcome)
    {
        case SubscribeOutcome::Applied:
            return "applied";
        case SubscribeOutcome::Refused:
            return "refused";
        case SubscribeOutcome::Rejected:
            return "rejected";
        case SubscribeOutcome::Dropped:
            return "dropped";
    }

    return "?";
}

int Int(const Value& object, const char* name) { return static_cast<int>(object.Find(name)->AsNumber()); }

std::int64_t Key(const Value& object) { return std::stoll(object.Find("key")->AsString()); }

void CheckAnswers(const std::vector<SubscribeResult>& actual, const Value& expected, const std::string& where)
{
    const auto& want = expected.Items();
    CHECK_MSG(actual.size() == want.size(), where + ": " + std::to_string(actual.size()) + " answers, expected " + std::to_string(want.size()));
    for (std::size_t i = 0; i < std::min(actual.size(), want.size()); i++)
    {
        const SubscribeResult& a = actual[i];
        const Value& w = want[i];
        CHECK_EQ(a.requestId, Int(w, "requestId"));
        CHECK_EQ(static_cast<int>(a.set), Int(w, "set"));
        CHECK_EQ(a.key, Key(w));
        CHECK_EQ(static_cast<int>(a.op), Int(w, "op"));
        CHECK_EQ(std::string(OutcomeName(a.outcome)), w.Find("outcome")->AsString());
        CHECK_EQ(static_cast<int>(a.code), Int(w, "code"));
        CHECK_EQ(static_cast<int>(a.ackReason), Int(w, "ackReason"));
    }
}

}  // namespace

TEST(GoldenSubscribeRequests_TheScriptReplaysToTheCommittedOutputs)
{
    const auto plan = PlanOf("subscribe-requests");
    const Value vector = GoldenJson("subscribe-requests");
    double nowMs = 0;
    CommandQueue queue(plan, [&nowMs] { return nowMs; }, 0, 1);
    GroupSubscriptions core(plan);
    std::vector<SubscribeResult> answers;
    core.onAnswer = [&answers](const SubscribeResult& r) { answers.push_back(r); };

    int index = 0;
    for (const Value& step : vector.Find("steps")->Items())
    {
        const std::string what = step.Find("do")->AsString();
        const std::string where = "step " + std::to_string(index++) + " (" + what + ")";
        if (what == "request")
        {
            const auto set = static_cast<std::uint32_t>(Int(step, "set"));
            const auto op = static_cast<std::uint8_t>(Int(step, "op"));
            if (step.Find("throws") != nullptr)
            {
                CHECK_THROWS(std::invalid_argument, (void)core.Request(set, Key(step), op));
            }
            else
            {
                CHECK_EQ(static_cast<int>(core.Request(set, Key(step), op)), Int(step, "requestId"));
            }
        }
        else if (what == "pump")
        {
            nowMs = step.Find("atMs")->AsNumber();
            struct Sent {
                int seq;
                int requestId;
                int set;
                std::int64_t key;
                int op;
            };
            std::vector<Sent> sent;
            core.Pump([&](const MessagePlan& command, std::span<const NamedValue> values) -> std::int32_t {
                const std::int32_t seq = queue.Enqueue(command, values);
                if (seq < 0)
                {
                    return -1;
                }

                sent.push_back({seq, static_cast<int>(values[0].value.numbers[0]), static_cast<int>(values[1].value.numbers[0]),
                                static_cast<std::int64_t>(values[2].value.integers[0]), static_cast<int>(values[3].value.numbers[0])});
                return seq;
            });
            queue.Clear();
            const auto& want = step.Find("sent")->Items();
            CHECK_MSG(sent.size() == want.size(), where + ": " + std::to_string(sent.size()) + " sent, expected " + std::to_string(want.size()));
            for (std::size_t i = 0; i < std::min(sent.size(), want.size()); i++)
            {
                CHECK_EQ(sent[i].seq, Int(want[i], "seq"));
                CHECK_EQ(sent[i].requestId, Int(want[i], "requestId"));
                CHECK_EQ(sent[i].set, Int(want[i], "set"));
                CHECK_EQ(sent[i].key, Key(want[i]));
                CHECK_EQ(sent[i].op, Int(want[i], "op"));
            }
        }
        else if (what == "observe")
        {
            std::vector<AckEntry> acks;
            for (const Value& a : step.Find("acks")->Items())
            {
                acks.push_back({static_cast<std::uint32_t>(Int(a, "seq")), static_cast<std::uint8_t>(Int(a, "reason"))});
            }

            std::vector<SourceEntry> sources;
            for (const Value& s : step.Find("sources")->Items())
            {
                sources.push_back({static_cast<std::uint32_t>(Int(s, "requestId")), static_cast<std::uint8_t>(Int(s, "status")),
                                   static_cast<std::uint32_t>(Int(s, "code"))});
            }

            core.Observe(acks, sources);
            CheckAnswers(answers, *step.Find("answers"), where);
            answers.clear();
        }
        else if (what == "reset")
        {
            core.Reset();
            CheckAnswers(answers, *step.Find("answers"), where);
            answers.clear();
        }
    }
}
