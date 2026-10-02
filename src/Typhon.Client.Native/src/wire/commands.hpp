#pragma once

#include <cstdint>
#include <span>

#include "catalog/catalog_plan.hpp"
#include "wire/field_codec.hpp"

// COMMANDS (0x83): u32 clientTick | varu count >= 1 | (varu cmdTypeIdx | u16 seq | fields)* — the client's batch for one rendered
// frame (03 § 3, § 8). Fields travel in the catalog's canonical order, decoded field by field (W11).
namespace typhon::client {

class RealmFrame;
class WireWriter;

struct CommandInput {
    const MessagePlan* type = nullptr;
    // The session's sequence number: wraps at 2^16, compared with serial arithmetic (RFC 1982).
    std::uint32_t seq = 0;
    std::span<const NamedValue> values;
};

// Receives a decoded COMMANDS message: each command's header, then its fields through the field members.
class CommandSink : public virtual FieldSink {
public:
    virtual void Command(const MessagePlan& type, std::uint32_t seq, std::uint32_t clientTick) = 0;
};

// Writes a COMMANDS message, type byte included. An enum value outside its names is refused here (W13). A realm-framed field travels
// over `frame`'s bounds at the command width, 32 bits; a command with one needs the session's frame.
void WriteCommands(WireWriter& w, std::uint32_t clientTick, std::span<const CommandInput> commands, const RealmFrame* frame = nullptr);

// Validates whole, then decodes, a COMMANDS message — the server's direction, for tests and tools.
void ReadCommands(std::span<const std::uint8_t> message, const CatalogPlan& plan, CommandSink& sink, const RealmFrame* frame = nullptr);

}  // namespace typhon::client
