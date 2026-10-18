#pragma once

#include <climits>
#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <span>
#include <unordered_map>

#include "apply/frame_state.hpp"
#include "catalog/catalog_plan.hpp"
#include "wire/field_codec.hpp"

namespace typhon::client {

// A SubscribeRequest's op (14 § 4, P13).
namespace SubscribeOp {
inline constexpr std::uint8_t Subscribe = 1;
inline constexpr std::uint8_t Unsubscribe = 2;
}  // namespace SubscribeOp

// How a request ended: SOURCE_APPLIED (the key is held and complete, or released), SOURCE_ERROR (with its code), an ACK other than
// RateLimited (with its reason), or the session ended first — whether the server took it is unknown, and a new session holds none of it.
enum class SubscribeOutcome : std::uint8_t
{
    Applied = 1,
    Refused = 2,
    Rejected = 3,
    Dropped = 4,
};

struct SubscribeResult {
    std::uint16_t requestId = 0;
    // The explicit set's index in the catalog's groupSets.
    std::uint32_t set = 0;
    std::int64_t key = 0;
    std::uint8_t op = 0;
    SubscribeOutcome outcome = SubscribeOutcome::Dropped;
    // The SOURCE_ERROR code when Refused, else 0.
    std::uint16_t code = 0;
    // The ACK reason when Rejected, else 0.
    std::uint8_t ackReason = 0;
};

// Sends one command and returns its seq, or a negative value when it was not sent (no token, no session) — CommandQueue::Enqueue.
using TrySend = std::function<std::int32_t(const MessagePlan&, std::span<const NamedValue>)>;

// A session's SubscribeRequests, from the call to the answer (14 § 4, P13): the .NET and TypeScript SDKs' GroupSubscriptions, pinned across
// them by the subscribe-requests vector. It sends nothing and keeps no clock: Pump it into the session's CommandQueue before each flush,
// Observe each applied frame's ACKS then SOURCES, Reset it when the session ends.
// - Pump sends the queued requests oldest first and stops at the first the queue refuses (its token bucket is the command's catalog rate).
// - An ACK RateLimited puts its request back at the head of the queue, to go again under a new seq; any other ACK ends it Rejected.
// - An ACK can be lost (a session further behind than the server's ACK history); that request stays sent with no answer. Asking again is
//   safe: a subscribe of a key held, or an unsubscribe of one not held, is answered like the first.
// Single-threaded, like the Client it serves.
class GroupSubscriptions {
public:
    explicit GroupSubscriptions(std::shared_ptr<const CatalogPlan> plan);

    // Whether the catalog lists SubscribeRequest: some explicit set takes a client's requests.
    bool Available() const { return command_ != nullptr; }
    std::size_t QueuedCount() const { return queued_.size(); }
    std::size_t SentCount() const { return sent_.size(); }

    // Queues a request and returns its id. Throws std::logic_error when the catalog lists no SubscribeRequest, std::invalid_argument for a
    // set that is not explicit, key 0 or another op.
    std::uint16_t Request(std::uint32_t set, std::int64_t key, std::uint8_t op);

    // Sends the queued requests, oldest first, until `send` refuses one; returns how many went.
    int Pump(const TrySend& send, int max = INT_MAX);

    // Matches one applied frame's ACKS, then its SOURCES.
    void Observe(std::span<const AckEntry> acks, std::span<const SourceEntry> sources);

    // The session is over: every request still sent (by id), then queued (in order), ends Dropped.
    void Reset();

    // Called for each request that ends, in the order they end.
    std::function<void(const SubscribeResult&)> onAnswer;

private:
    struct Entry {
        std::uint16_t requestId = 0;
        std::uint32_t set = 0;
        std::int64_t key = 0;
        std::uint8_t op = 0;
        std::uint16_t seq = 0;
    };

    std::uint16_t TakeId();
    void Answer(const Entry& entry, SubscribeOutcome outcome, std::uint16_t code, std::uint8_t ackReason);

    std::shared_ptr<const CatalogPlan> plan_;
    const MessagePlan* command_;
    std::deque<Entry> queued_;
    std::unordered_map<std::uint16_t, Entry> sent_;
    std::unordered_map<std::uint16_t, std::uint16_t> requestBySeq_;
    std::uint16_t nextId_ = 0;
};

}  // namespace typhon::client
