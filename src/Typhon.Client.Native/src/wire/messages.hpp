#pragma once

#include <array>
#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

// The control messages of 03 § 3. Writers include the type byte; Parse* take the whole message and check its type byte (1002) and
// that its body ends exactly where the message does (1007). Tokens and hashes are opaque bytes.
namespace typhon::client {

class WireReader;
class WireWriter;

struct HelloMessage {
    std::uint32_t major = 0;
    std::uint32_t minor = 0;
    std::uint32_t caps = 0;
    std::string kind;
    std::string token;
    std::array<std::uint8_t, 16> resumeToken{};
    std::array<std::uint8_t, 8> clientCatalogHash{};
    std::vector<std::uint8_t> helloPayload;
};

struct WelcomeMessage {
    std::uint32_t major = 0;
    std::uint32_t minor = 0;
    std::uint32_t capsGranted = 0;
    std::uint32_t sessionId = 0;
    std::array<std::uint8_t, 16> resumeToken{};
    std::uint32_t tick = 0;
    std::uint32_t tickPeriodUs = 0;
    std::array<std::uint8_t, 8> catalogHash{};
    // The canonical catalog JSON, or empty when the client's hash matched and the catalog was skipped.
    std::vector<std::uint8_t> catalogJson;
};

struct PingMessage {
    std::uint32_t clientMs = 0;
    std::uint32_t lastAppliedTick = 0;
};

struct PongMessage {
    std::uint32_t clientMs = 0;
    std::uint32_t tick = 0;
    std::uint32_t usIntoTick = 0;
};

struct KickMessage {
    std::uint32_t code = 0;
    std::string reason;
};

void WriteHello(WireWriter& w, const HelloMessage& m);
HelloMessage ParseHello(std::span<const std::uint8_t> message);

void WriteWelcome(WireWriter& w, const WelcomeMessage& m);
WelcomeMessage ParseWelcome(std::span<const std::uint8_t> message);

// Throws 1002 when the server granted a capability the client did not request (W23).
void CheckCapsGranted(std::uint32_t requested, std::uint32_t granted);

void WritePing(WireWriter& w, const PingMessage& m);
PingMessage ParsePing(std::span<const std::uint8_t> message);

void WritePong(WireWriter& w, const PongMessage& m);
PongMessage ParsePong(std::span<const std::uint8_t> message);

// Writes KICK, truncating the reason at a code-point boundary to fit a WebSocket close frame (W24).
void WriteKick(WireWriter& w, const KickMessage& m);
KickMessage ParseKick(std::span<const std::uint8_t> message);

// Writes BYE: 1000, or a code in 4000-4999.
void WriteBye(WireWriter& w, std::uint32_t code);
std::uint32_t ParseBye(std::span<const std::uint8_t> message);

// A catalog digest's display form: 16 lower-case hex digits, most significant first, from its 8 wire bytes (W20).
std::string CatalogHashToHex(std::span<const std::uint8_t> hash);

}  // namespace typhon::client
