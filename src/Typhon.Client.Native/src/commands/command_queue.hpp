#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <vector>

#include "catalog/catalog_plan.hpp"
#include "store/memory.hpp"
#include "wire/commands.hpp"
#include "wire/field_codec.hpp"
#include "wire/writer.hpp"

namespace typhon::client {

class RealmFrame;

// What CommandQueue::Enqueue returns instead of a sequence number.
inline constexpr std::int32_t CommandRateLimited = -1;

// Batches a rendered frame's commands into COMMANDS messages (§ 3) — the TypeScript SDK's commands/queue.ts:
// - Seq: u16, wrapping, one space across every command type; SELF.lastSeq reports the highest drained (W31).
// - Latest wins: a `latest` command replaces the pending one of its type, with a fresh seq.
// - The cap: a batch is split into as many messages as limits.clientMessageBytes needs, all with the same clientTick; a single command
//   above the cap is a caller error.
// - The rate: a token bucket per type from the catalog's rate, a pre-check only — the server's bucket decides.
// Values are COPIED at Enqueue (a C caller's buffers are not the queue's to hold), into storage reused across flushes.
class CommandQueue {
public:
    // `maxMessageBytes` 0: the catalog's limits.clientMessageBytes.
    CommandQueue(std::shared_ptr<const CatalogPlan> plan, std::function<double()> now, std::uint32_t maxMessageBytes = 0,
                 std::uint32_t firstSeq = 1);

    const CatalogPlan& Plan() const { return *plan_; }
    std::size_t PendingCount() const { return count_; }
    std::uint64_t RateLimitedCount() const { return dropped_; }
    std::uint64_t CoalescedCount() const { return coalesced_; }
    // Pending commands a flush dropped because they carry a realm-framed field and the session held no realm (SUB-30).
    std::uint64_t DroppedWithoutRealm() const { return unframed_; }
    std::uint32_t NextSeq() const { return seq_; }

    // Queues one command and returns its seq, or CommandRateLimited. Throws std::invalid_argument for a field the command does not have,
    // std::out_of_range for a value the codec cannot represent (an enum outside its names, an over-cap string) and std::length_error for a
    // command that alone exceeds the message cap.
    std::int32_t Enqueue(const MessagePlan& type, std::span<const NamedValue> values);

    // Writes the pending commands as COMMANDS messages for `clientTick` — the newest server tick this client applied, never a predicted
    // one — over `frame` (the session's realm), hands each to `send`, then clears them. Returns the messages sent.
    int Flush(std::uint32_t clientTick, const std::function<void(std::span<const std::uint8_t>)>& send, const RealmFrame* frame);

    // Drops the pending commands unsent (a disconnect: their seqs are never acknowledged), counted in DiscardedCount.
    void Clear()
    {
        discarded_ += count_;
        count_ = 0;
    }

    // Commands Clear dropped unsent.
    std::uint64_t DiscardedCount() const { return discarded_; }

private:
    struct Pending {
        const MessagePlan* type = nullptr;
        std::uint32_t seq = 0;
        std::uint32_t bytes = 0;
        Vec<double> numbers;
        // 64-bit integer components (W32), as bit patterns.
        Vec<std::uint64_t> integers;
        Vec<char> text;
        Vec<NamedValue> values;

        // Member by member: std::swap would move through a temporary, and a container moved into one allocates in a checked (Debug)
        // standard library. The views in `values` follow their buffers, which a vector swap exchanges without moving.
        void Swap(Pending& other) noexcept
        {
            std::swap(type, other.type);
            std::swap(seq, other.seq);
            std::swap(bytes, other.bytes);
            numbers.swap(other.numbers);
            integers.swap(other.integers);
            text.swap(other.text);
            values.swap(other.values);
        }
    };

    struct Bucket {
        double tokens = 0;
        double lastMs = 0;
        bool started = false;
    };

    bool Take(const MessagePlan& type);
    void Copy(Pending& entry, std::span<const NamedValue> values);
    std::uint32_t Measure(const Pending& entry);
    static std::uint32_t HeaderBytes(std::size_t count);

    std::shared_ptr<const CatalogPlan> plan_;
    std::function<double()> now_;
    std::uint32_t maxMessageBytes_;
    std::uint32_t seq_;
    std::shared_ptr<const RealmFrame> measuringFrame_;
    WireWriter measuring_{512};
    WireWriter writer_{1024};
    // Per command wire index: the catalog command, for its delivery and rate.
    std::vector<const CatalogCommand*> commands_;
    std::vector<Bucket> buckets_;
    std::vector<Pending> pending_;
    Pending scratch_;
    std::size_t count_ = 0;
    std::vector<CommandInput> batch_;
    std::uint64_t dropped_ = 0;
    std::uint64_t coalesced_ = 0;
    std::uint64_t unframed_ = 0;
    std::uint64_t discarded_ = 0;
    // Per command wire index: 1 when the type carries a realm-framed field.
    std::vector<std::uint8_t> framed_;
};

}  // namespace typhon::client
