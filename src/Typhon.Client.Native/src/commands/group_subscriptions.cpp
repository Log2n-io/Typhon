#include "commands/group_subscriptions.hpp"

#include <algorithm>
#include <stdexcept>
#include <string>
#include <vector>

#include "wire/constants.hpp"

namespace typhon::client {

GroupSubscriptions::GroupSubscriptions(std::shared_ptr<const CatalogPlan> plan)
    : plan_(std::move(plan)),
      command_(plan_->CommandByName(BuiltIn::SubscribeRequest))
{
}

std::uint16_t GroupSubscriptions::Request(std::uint32_t set, std::int64_t key, std::uint8_t op)
{
    if (command_ == nullptr)
    {
        throw std::logic_error("the catalog lists no SubscribeRequest: no explicit group set takes a client's requests");
    }

    const auto& sets = plan_->GroupSets();
    if (set >= sets.size() || sets[set].selector != "explicit")
    {
        throw std::invalid_argument("group set " + std::to_string(set) + " is not an explicit set of this catalog");
    }

    if (key == 0)
    {
        throw std::invalid_argument("key 0 names no group");
    }

    if (op != SubscribeOp::Subscribe && op != SubscribeOp::Unsubscribe)
    {
        throw std::invalid_argument("op " + std::to_string(op) + " is neither Subscribe nor Unsubscribe");
    }

    Entry entry;
    entry.requestId = TakeId();
    entry.set = set;
    entry.key = key;
    entry.op = op;
    queued_.push_back(entry);
    return entry.requestId;
}

int GroupSubscriptions::Pump(const TrySend& send, int max)
{
    int count = 0;
    while (command_ != nullptr && count < max && !queued_.empty())
    {
        Entry entry = queued_.front();
        const double requestId = entry.requestId;
        const double set = entry.set;
        const double op = entry.op;
        const std::uint64_t key = static_cast<std::uint64_t>(entry.key);
        const NamedValue values[] = {
            {"requestId", FieldValue::OfNumbers({&requestId, 1})},
            {"set", FieldValue::OfNumbers({&set, 1})},
            {"key", FieldValue::OfIntegers({&key, 1})},
            {"op", FieldValue::OfNumbers({&op, 1})},
        };

        const std::int32_t seq = send(*command_, values);
        if (seq < 0)
        {
            break;
        }

        queued_.pop_front();
        entry.seq = static_cast<std::uint16_t>(seq & 0xFFFF);
        sent_[entry.requestId] = entry;
        requestBySeq_[entry.seq] = entry.requestId;
        count++;
    }

    return count;
}

void GroupSubscriptions::Observe(std::span<const AckEntry> acks, std::span<const SourceEntry> sources)
{
    std::size_t requeued = 0;
    for (const AckEntry& ack : acks)
    {
        const auto bySeq = requestBySeq_.find(static_cast<std::uint16_t>(ack.seq));
        if (bySeq == requestBySeq_.end())
        {
            continue;
        }

        const auto found = sent_.find(bySeq->second);
        requestBySeq_.erase(bySeq);
        if (found == sent_.end())
        {
            continue;
        }

        const Entry entry = found->second;
        sent_.erase(found);
        if (ack.reason == AckReason::RateLimited)
        {
            // Back at the head, ahead of what was never sent, in the order the refused ones went.
            queued_.insert(queued_.begin() + static_cast<std::ptrdiff_t>(requeued++), entry);
            continue;
        }

        Answer(entry, SubscribeOutcome::Rejected, 0, ack.reason);
    }

    for (const SourceEntry& source : sources)
    {
        const auto found = sent_.find(static_cast<std::uint16_t>(source.requestId));
        if (found == sent_.end())
        {
            continue;
        }

        const Entry entry = found->second;
        sent_.erase(found);
        requestBySeq_.erase(entry.seq);
        if (source.status == SourceStatus::Applied)
        {
            Answer(entry, SubscribeOutcome::Applied, 0, 0);
        }
        else
        {
            Answer(entry, SubscribeOutcome::Refused, static_cast<std::uint16_t>(source.code), 0);
        }
    }
}

void GroupSubscriptions::Reset()
{
    std::vector<Entry> sent;
    sent.reserve(sent_.size());
    for (const auto& [id, entry] : sent_)
    {
        sent.push_back(entry);
    }

    std::sort(sent.begin(), sent.end(), [](const Entry& a, const Entry& b) { return a.requestId < b.requestId; });
    std::deque<Entry> queued;
    queued.swap(queued_);
    sent_.clear();
    requestBySeq_.clear();
    for (const Entry& entry : sent)
    {
        Answer(entry, SubscribeOutcome::Dropped, 0, 0);
    }

    for (const Entry& entry : queued)
    {
        Answer(entry, SubscribeOutcome::Dropped, 0, 0);
    }
}

std::uint16_t GroupSubscriptions::TakeId()
{
    if (queued_.size() + sent_.size() >= 0xFFFF)
    {
        throw std::length_error("65 535 requests are waiting for their answers");
    }

    // 1 to 65 535, never one still waiting for its answer.
    for (;;)
    {
        nextId_ = nextId_ == 0xFFFF ? 1 : static_cast<std::uint16_t>(nextId_ + 1);
        const std::uint16_t id = nextId_;
        if (sent_.find(id) == sent_.end()
            && std::none_of(queued_.begin(), queued_.end(), [id](const Entry& e) { return e.requestId == id; }))
        {
            return id;
        }
    }
}

void GroupSubscriptions::Answer(const Entry& entry, SubscribeOutcome outcome, std::uint16_t code, std::uint8_t ackReason)
{
    if (!onAnswer)
    {
        return;
    }

    SubscribeResult result;
    result.requestId = entry.requestId;
    result.set = entry.set;
    result.key = entry.key;
    result.op = entry.op;
    result.outcome = outcome;
    result.code = code;
    result.ackReason = ackReason;
    onAnswer(result);
}

}  // namespace typhon::client
