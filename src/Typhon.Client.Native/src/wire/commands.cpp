#include "wire/commands.hpp"

#include <memory>
#include <stdexcept>

#include "wire/constants.hpp"
#include "wire/errors.hpp"
#include "wire/realm_frame.hpp"
#include "wire/reader.hpp"
#include "wire/writer.hpp"

namespace typhon::client {

namespace {

// Consumes a decode without keeping anything: the validation pass.
class Discard final : public CommandSink {
public:
    void Command(const MessagePlan&, std::uint32_t, std::uint32_t) override {}
    void Number(const FieldPlan&, const double*) override {}
    void Text(const FieldPlan&, std::string_view) override {}
    void Bytes(const FieldPlan&, std::span<const std::uint8_t>) override {}
    void List(const FieldPlan&, int, const double*) override {}
};

void Decode(std::span<const std::uint8_t> message, const CatalogPlan& plan, CommandSink& sink, const RealmFrame* frame)
{
    WireReader r(message);
    if (r.U8() != MessageType::Commands)
    {
        throw ProtocolError("not a COMMANDS message");
    }

    const std::uint32_t clientTick = r.U32();
    std::uint32_t count = r.Varu();
    if (count == 0)
    {
        throw Malformed("a COMMANDS message carries at least one command");
    }

    for (; count > 0; count--)
    {
        const MessagePlan& type = plan.Command(r.Varu());
        const std::uint32_t seq = r.U16();
        sink.Command(type, seq, clientTick);
        // A tickLo command field rebuilds against the client's claimed tick, the only frame a command has.
        ReadSection(r, *type.body, clientTick, sink, true, frame);
    }

    r.ExpectEnd("COMMANDS");
}

}  // namespace

void WriteCommands(WireWriter& w, std::uint32_t clientTick, std::span<const CommandInput> commands, const RealmFrame* frame)
{
    const std::shared_ptr<const RealmFrame> commandFrame = frame == nullptr ? nullptr : frame->ForCommands();
    if (commands.empty())
    {
        throw std::out_of_range("a COMMANDS message carries at least one command");
    }

    w.U8(MessageType::Commands);
    w.U32(clientTick);
    w.Varu(static_cast<std::uint32_t>(commands.size()));
    for (const CommandInput& c : commands)
    {
        if (c.seq > 0xFFFF)
        {
            throw std::out_of_range("seq " + std::to_string(c.seq) + " is not a u16");
        }

        w.Varu(static_cast<std::uint32_t>(c.type->idx));
        w.U16(c.seq);
        WriteSection(w, *c.type->body, c.values, true, commandFrame.get());
    }
}

void ReadCommands(std::span<const std::uint8_t> message, const CatalogPlan& plan, CommandSink& sink, const RealmFrame* frame)
{
    const std::shared_ptr<const RealmFrame> commandFrame = frame == nullptr ? nullptr : frame->ForCommands();
    Discard discard;
    Decode(message, plan, discard, commandFrame.get());
    Decode(message, plan, sink, commandFrame.get());
}

}  // namespace typhon::client
