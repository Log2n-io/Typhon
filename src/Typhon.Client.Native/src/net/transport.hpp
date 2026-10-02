#pragma once

#include <cstdint>
#include <memory>
#include <span>
#include <string>

#include "store/memory.hpp"

// The message transport under a connection: whole messages in and out, nothing about their meaning. TCP is the one built in (13 § 7):
// the TYP3 preamble both ways, then `u32 len` little-endian framing excluding itself (03 § 10). The interface is the test seam — a
// connection test drives an in-memory transport instead of a socket.
namespace typhon::client {

enum class TransportEvent : std::uint8_t
{
    // Nothing happened within the poll's timeout.
    None,
    // The transport is ready for the first message (TCP: connected, and the server answered the preamble).
    Open,
    // One whole message: Message() views it until the next Poll.
    Message,
    // The transport ended; CloseCode() says how. Terminal.
    Closed,
};

class Transport {
public:
    virtual ~Transport() = default;

    // Starts opening. Never blocks; the outcome arrives through Poll.
    virtual void Open() = 0;

    // Waits up to `timeoutMs` (0: do not wait) for the next event.
    virtual TransportEvent Poll(int timeoutMs) = 0;

    // The message of the latest Message event, valid until the next Poll.
    virtual std::span<const std::uint8_t> Message() const = 0;

    // Queues one message; it leaves during the next Polls. Never blocks.
    virtual void Send(std::span<const std::uint8_t> message) = 0;

    // The largest inbound message accepted: one above it ends the transport with 1009 before its body is read (03 § 10).
    virtual void SetInboundCap(std::uint32_t bytes) = 0;

    // Ends the transport after flushing what Send queued, as far as the socket takes it without blocking.
    virtual void Close() = 0;

    // How the transport ended: 1001 for an orderly end or a vanished peer (TCP carries no code), 1009 for an oversized message, 1002
    // for a peer that did not answer the preamble.
    virtual int CloseCode() const = 0;

    // Why, for diagnostics.
    virtual const std::string& CloseReason() const = 0;
};

// The TCP transport over POSIX sockets or Winsock, non-blocking: a connect, the preamble exchange, framing, and an output buffer drained
// as the socket accepts it.
std::unique_ptr<Transport> MakeTcpTransport(std::string host, std::uint16_t port);

}  // namespace typhon::client
