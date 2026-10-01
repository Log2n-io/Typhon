#include "commands/command_queue.hpp"

#include <algorithm>
#include <stdexcept>

#include "wire/commands.hpp"
#include "wire/constants.hpp"
#include "wire/realm_frame.hpp"

namespace typhon::client {

namespace {

// Whether a field travels over the session's realm frame (SUB-30): a position, or a list of them.
bool IsRealmFramed(const FieldPlan& field)
{
    const auto framed = [](CodecKind kind) { return kind == CodecKind::Pos2 || kind == CodecKind::Pos3; };
    return framed(field.kind) || (field.element != nullptr && framed(field.element->kind));
}

// Empties the queue however a flush ends: a send that throws midway must not leave the batch to be sent a second time, with seqs the
// server already executed.
class ClearOnExit {
public:
    explicit ClearOnExit(std::size_t& count) : count_(count) {}
    ~ClearOnExit() { count_ = 0; }
    ClearOnExit(const ClearOnExit&) = delete;
    ClearOnExit& operator=(const ClearOnExit&) = delete;

private:
    std::size_t& count_;
};

}  // namespace

CommandQueue::CommandQueue(std::shared_ptr<const CatalogPlan> plan, std::function<double()> now, std::uint32_t maxMessageBytes,
                           std::uint32_t firstSeq)
    : plan_(std::move(plan)),
      now_(std::move(now)),
      maxMessageBytes_(maxMessageBytes != 0 ? maxMessageBytes : static_cast<std::uint32_t>(plan_->GetCatalog().clientMessageBytes)),
      seq_(firstSeq & 0xFFFF)
{
    // Any frame at the command width: a command's size depends on its values' widths, never on the realm's bounds.
    const double min[3] = {-1, -1, -1};
    const double max[3] = {1, 1, 1};
    measuringFrame_ = std::make_shared<const RealmFrame>(0, 0, 0, 0, protocol::CommandPositionBits, 1.0, true, min, max);
    for (const CatalogCommand& c : plan_->GetCatalog().commands)
    {
        const auto idx = static_cast<std::size_t>(c.idx);
        if (commands_.size() <= idx)
        {
            commands_.resize(idx + 1, nullptr);
            buckets_.resize(idx + 1);
        }

        commands_[idx] = &c;
    }

    framed_.assign(commands_.size(), 0);
    for (std::size_t idx = 0; idx < commands_.size(); idx++)
    {
        if (commands_[idx] == nullptr)
        {
            continue;
        }

        const MessagePlan& type = plan_->Command(static_cast<std::uint32_t>(idx));
        for (const auto& field : type.fields)
        {
            framed_[idx] = framed_[idx] != 0 || IsRealmFramed(*field) ? 1 : 0;
        }
    }
}

std::int32_t CommandQueue::Enqueue(const MessagePlan& type, std::span<const NamedValue> values)
{
    if (plan_->CommandByName(type.name) != &type)
    {
        throw std::invalid_argument("command '" + type.name + "' does not belong to this catalog");
    }

    if (!Take(type))
    {
        dropped_++;
        return CommandRateLimited;
    }

    const CatalogCommand* command = commands_[static_cast<std::size_t>(type.idx)];
    const bool latest = command != nullptr && command->delivery == "latest";

    // Built in the scratch entry first: a value the codec refuses leaves the queue and the seq untouched.
    scratch_.type = &type;
    Copy(scratch_, values);
    scratch_.bytes = Measure(scratch_);
    if (scratch_.bytes > maxMessageBytes_ - HeaderBytes(1))
    {
        throw std::length_error("command '" + type.name + "' needs " + std::to_string(scratch_.bytes) + " B, above the " +
                                std::to_string(maxMessageBytes_) + " B limit");
    }

    scratch_.seq = seq_;
    seq_ = (seq_ + 1) & 0xFFFF;
    // Swapped in, never copied: the views in `values` point into the entry's own buffers, which a swap moves along with them, and the
    // displaced entry's buffers become the next scratch.
    if (latest)
    {
        for (std::size_t i = 0; i < count_; i++)
        {
            if (pending_[i].type == &type)
            {
                scratch_.Swap(pending_[i]);
                coalesced_++;
                return static_cast<std::int32_t>(pending_[i].seq);
            }
        }
    }

    if (count_ == pending_.size())
    {
        pending_.emplace_back();
    }

    scratch_.Swap(pending_[count_]);
    return static_cast<std::int32_t>(pending_[count_++].seq);
}

int CommandQueue::Flush(std::uint32_t clientTick, const std::function<void(std::span<const std::uint8_t>)>& send, const RealmFrame* frame)
{
    if (frame == nullptr)
    {
        // With no realm, a command carrying a realm-framed field cannot be encoded: it is dropped (counted) and the others go.
        std::size_t kept = 0;
        for (std::size_t i = 0; i < count_; i++)
        {
            if (framed_[static_cast<std::size_t>(pending_[i].type->idx)] == 0)
            {
                pending_[kept++].Swap(pending_[i]);
            }
            else
            {
                unframed_++;
            }
        }

        count_ = kept;
    }

    const ClearOnExit clear(count_);
    int messages = 0;
    std::size_t from = 0;
    while (from < count_)
    {
        std::uint32_t bytes = HeaderBytes(1);
        std::size_t to = from;
        // One more command may also widen the count's varint (1 B to 2 B at 128): the widening is part of what must fit.
        while (to < count_ && bytes + pending_[to].bytes + (HeaderBytes(to - from + 1) - HeaderBytes(to - from)) <= maxMessageBytes_)
        {
            bytes += pending_[to].bytes + (HeaderBytes(to - from + 1) - HeaderBytes(to - from));
            to++;
        }

        batch_.clear();
        for (std::size_t i = from; i < to; i++)
        {
            batch_.push_back({pending_[i].type, pending_[i].seq, pending_[i].values});
        }

        writer_.Reset();
        WriteCommands(writer_, clientTick, batch_, frame);
        send(writer_.Written());
        messages++;
        from = to;
    }

    return messages;
}

bool CommandQueue::Take(const MessagePlan& type)
{
    const CatalogCommand* command = commands_[static_cast<std::size_t>(type.idx)];
    if (command == nullptr || !command->rate.has_value())
    {
        return true;
    }

    Bucket& bucket = buckets_[static_cast<std::size_t>(type.idx)];
    const double nowMs = now_();
    if (!bucket.started)
    {
        bucket = {static_cast<double>(command->rate->burst), nowMs, true};
    }

    const double elapsed = std::max(0.0, nowMs - bucket.lastMs);
    bucket.lastMs = nowMs;
    bucket.tokens = std::min(static_cast<double>(command->rate->burst), bucket.tokens + elapsed / 1000 * command->rate->perSec);
    if (bucket.tokens < 1)
    {
        return false;
    }

    bucket.tokens -= 1;
    return true;
}

void CommandQueue::Copy(Pending& entry, std::span<const NamedValue> values)
{
    // Sized first, then filled: the NamedValue views point into these buffers, which must not move once a view is taken.
    std::size_t numbers = 0;
    std::size_t integers = 0;
    std::size_t text = 0;
    for (const NamedValue& v : values)
    {
        numbers += v.value.numbers.size();
        integers += v.value.integers.size();
        text += v.value.text.size() + v.value.bytes.size();
    }

    entry.numbers.resize(numbers);
    entry.integers.resize(integers);
    entry.text.resize(text);
    entry.values.clear();
    std::size_t n = 0;
    std::size_t k = 0;
    std::size_t t = 0;
    for (const NamedValue& v : values)
    {
        // The name is re-pointed at the plan's own string: the caller's may not outlive the call.
        const FieldPlan* field = nullptr;
        for (const auto& f : entry.type->fields)
        {
            if (f->name == v.name)
            {
                field = f.get();
                break;
            }
        }

        if (field == nullptr)
        {
            throw std::invalid_argument("command '" + entry.type->name + "' has no field '" + std::string(v.name) + "'");
        }

        FieldValue copy;
        copy.kind = v.value.kind;
        std::copy(v.value.numbers.begin(), v.value.numbers.end(), entry.numbers.begin() + static_cast<std::ptrdiff_t>(n));
        copy.numbers = {entry.numbers.data() + n, v.value.numbers.size()};
        n += v.value.numbers.size();
        std::copy(v.value.integers.begin(), v.value.integers.end(), entry.integers.begin() + static_cast<std::ptrdiff_t>(k));
        copy.integers = {entry.integers.data() + k, v.value.integers.size()};
        k += v.value.integers.size();
        std::copy(v.value.text.begin(), v.value.text.end(), entry.text.begin() + static_cast<std::ptrdiff_t>(t));
        copy.text = {entry.text.data() + t, v.value.text.size()};
        t += v.value.text.size();
        std::copy(v.value.bytes.begin(), v.value.bytes.end(), entry.text.begin() + static_cast<std::ptrdiff_t>(t));
        copy.bytes = {reinterpret_cast<const std::uint8_t*>(entry.text.data() + t), v.value.bytes.size()};
        t += v.value.bytes.size();
        entry.values.push_back({field->name, copy});
    }
}

std::uint32_t CommandQueue::Measure(const Pending& entry)
{
    measuring_.Reset();
    // A realm-framed field's width is the command width whatever the realm (typhon.3), so any frame measures it.
    WriteSection(measuring_, *entry.type->body, entry.values, true, measuringFrame_.get());
    return static_cast<std::uint32_t>(WireWriter::VaruSize(static_cast<std::uint32_t>(entry.type->idx)) + 2 + measuring_.Position());
}

std::uint32_t CommandQueue::HeaderBytes(std::size_t count)
{
    // u8 type | u32 clientTick | varu count
    return 5 + static_cast<std::uint32_t>(WireWriter::VaruSize(static_cast<std::uint32_t>(count)));
}

}  // namespace typhon::client
