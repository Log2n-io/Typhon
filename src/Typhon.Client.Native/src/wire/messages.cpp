#include "wire/messages.hpp"

#include <cstdio>
#include <stdexcept>

#include "wire/constants.hpp"
#include "wire/errors.hpp"
#include "wire/reader.hpp"
#include "wire/utf8.hpp"
#include "wire/writer.hpp"

namespace typhon::client {

namespace {

template <typename T, typename Read>
T Parse(std::span<const std::uint8_t> message, std::uint8_t type, Read read)
{
    WireReader r(message);
    const std::uint8_t actual = r.U8();
    if (actual != type)
    {
        char text[64];
        std::snprintf(text, sizeof text, "expected message type 0x%x, got 0x%x", type, actual);
        throw ProtocolError(text);
    }

    T result = read(r);
    r.ExpectEnd("message");
    return result;
}

std::uint32_t InRange(std::uint64_t value, std::uint64_t max, const char* what)
{
    if (value > max)
    {
        throw std::out_of_range(std::string(what) + " " + std::to_string(value) + " is not in [0, " + std::to_string(max) + "]");
    }

    return static_cast<std::uint32_t>(value);
}

template <std::size_t N>
void Copy(WireReader& r, std::array<std::uint8_t, N>& into)
{
    const std::size_t at = r.Take(N);
    for (std::size_t i = 0; i < N; i++)
    {
        into[i] = r.Bytes()[at + i];
    }
}

std::vector<std::uint8_t> CopyBytes(WireReader& r, std::size_t count)
{
    const std::size_t at = r.Take(count);
    const auto bytes = r.Bytes().subspan(at, count);
    return {bytes.begin(), bytes.end()};
}

}  // namespace

void WriteHello(WireWriter& w, const HelloMessage& m)
{
    w.U8(MessageType::Hello);
    w.U16(InRange(m.major, 0xFFFF, "major"));
    w.U16(InRange(m.minor, 0xFFFF, "minor"));
    w.U32(m.caps);
    w.Str(m.kind, protocol::SessionKindMaxBytes);
    w.Str(m.token, protocol::TokenMaxBytes);
    w.Raw(m.resumeToken);
    w.Raw(m.clientCatalogHash);
    w.Blob(m.helloPayload, protocol::HelloPayloadMaxBytes);
}

HelloMessage ParseHello(std::span<const std::uint8_t> message)
{
    return Parse<HelloMessage>(message, MessageType::Hello,
                               [](WireReader& r)
                               {
                                   HelloMessage m;
                                   m.major = r.U16();
                                   m.minor = r.U16();
                                   m.caps = r.U32();
                                   m.kind = std::string(r.Str(protocol::SessionKindMaxBytes));
                                   m.token = std::string(r.Str(protocol::TokenMaxBytes));
                                   Copy(r, m.resumeToken);
                                   Copy(r, m.clientCatalogHash);
                                   m.helloPayload = CopyBytes(r, r.BlobLength(protocol::HelloPayloadMaxBytes));
                                   return m;
                               });
}

void WriteWelcome(WireWriter& w, const WelcomeMessage& m)
{
    w.U8(MessageType::Welcome);
    w.U16(InRange(m.major, 0xFFFF, "major"));
    w.U16(InRange(m.minor, 0xFFFF, "minor"));
    w.U32(m.capsGranted);
    w.U32(m.sessionId);
    w.Raw(m.resumeToken);
    w.U32(m.tick);
    w.U32(m.tickPeriodUs);
    w.Raw(m.catalogHash);
    w.Varu(static_cast<std::uint32_t>(m.catalogJson.size()));
    w.Raw(m.catalogJson);
}

WelcomeMessage ParseWelcome(std::span<const std::uint8_t> message)
{
    return Parse<WelcomeMessage>(message, MessageType::Welcome,
                                 [](WireReader& r)
                                 {
                                     WelcomeMessage m;
                                     m.major = r.U16();
                                     m.minor = r.U16();
                                     m.capsGranted = r.U32();
                                     m.sessionId = r.U32();
                                     Copy(r, m.resumeToken);
                                     m.tick = r.U32();
                                     m.tickPeriodUs = r.U32();
                                     Copy(r, m.catalogHash);
                                     m.catalogJson = CopyBytes(r, r.BlobLength(r.Remaining()));
                                     return m;
                                 });
}

void CheckCapsGranted(std::uint32_t requested, std::uint32_t granted)
{
    if ((granted & ~requested) != 0)
    {
        char text[96];
        std::snprintf(text, sizeof text, "the server granted caps 0x%x beyond the 0x%x requested", granted, requested);
        throw ProtocolError(text);
    }
}

void WritePing(WireWriter& w, const PingMessage& m)
{
    w.U8(MessageType::Ping);
    w.U32(m.clientMs);
    w.U32(m.lastAppliedTick);
}

PingMessage ParsePing(std::span<const std::uint8_t> message)
{
    return Parse<PingMessage>(message, MessageType::Ping, [](WireReader& r) { return PingMessage{r.U32(), r.U32()}; });
}

void WritePong(WireWriter& w, const PongMessage& m)
{
    w.U8(MessageType::Pong);
    w.U32(m.clientMs);
    w.U32(m.tick);
    w.U32(m.usIntoTick);
}

PongMessage ParsePong(std::span<const std::uint8_t> message)
{
    return Parse<PongMessage>(message, MessageType::Pong,
                              [](WireReader& r)
                              {
                                  PongMessage m;
                                  m.clientMs = r.U32();
                                  m.tick = r.U32();
                                  m.usIntoTick = r.U32();
                                  return m;
                              });
}

void WriteKick(WireWriter& w, const KickMessage& m)
{
    w.U8(MessageType::Kick);
    w.U16(InRange(m.code, 0xFFFF, "close code"));
    w.Str(utf8::Truncate(m.reason, protocol::KickReasonMaxBytes), protocol::KickReasonMaxBytes);
}

KickMessage ParseKick(std::span<const std::uint8_t> message)
{
    return Parse<KickMessage>(message, MessageType::Kick,
                              [](WireReader& r)
                              {
                                  KickMessage m;
                                  m.code = r.U16();
                                  m.reason = std::string(r.Str(protocol::KickReasonMaxBytes));
                                  return m;
                              });
}

void WriteBye(WireWriter& w, std::uint32_t code)
{
    if (!IsValidClientCloseCode(static_cast<int>(InRange(code, 0xFFFF, "BYE code"))))
    {
        throw std::out_of_range("BYE code " + std::to_string(code) + " is neither 1000 nor in 4000-4999");
    }

    w.U8(MessageType::Bye);
    w.U16(code);
}

std::uint32_t ParseBye(std::span<const std::uint8_t> message)
{
    return Parse<std::uint32_t>(message, MessageType::Bye,
                                [](WireReader& r)
                                {
                                    const std::uint32_t code = r.U16();
                                    if (!IsValidClientCloseCode(static_cast<int>(code)))
                                    {
                                        throw Malformed("BYE code " + std::to_string(code) + " is not a client code");
                                    }

                                    return code;
                                });
}

std::string CatalogHashToHex(std::span<const std::uint8_t> hash)
{
    std::string hex;
    for (std::size_t i = hash.size(); i-- > 0;)
    {
        char two[3];
        std::snprintf(two, sizeof two, "%02x", hash[i]);
        hex += two;
    }

    return hex;
}

}  // namespace typhon::client
