#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <vector>

#include "catalog/catalog_plan.hpp"
#include "net/transport.hpp"
#include "wire/constants.hpp"
#include "wire/messages.hpp"
#include "wire/writer.hpp"

// One typhon.3 session over a transport (the TypeScript SDK's net/connection.ts): the handshake, the message caps, the close codes, and
// frames delivered in arrival order for the consumer to apply synchronously. Single-threaded and poll-driven: nothing happens between
// two Poll calls.
namespace typhon::client {

enum class ConnectionState : std::uint8_t
{
    Idle,
    // The transport is opening; nothing has been sent.
    Connecting,
    // HELLO is out and WELCOME is due within the hello timeout.
    AwaitingWelcome,
    // TICK, PONG and KICK arrive; COMMANDS, PING and BYE may be sent.
    Open,
    Closed,
};

using ResumeToken = std::array<std::uint8_t, 16>;
using CatalogHash = std::array<std::uint8_t, 8>;

// A catalog held from an earlier session, offered by its hash so WELCOME may skip it (§ 10). The client echoes the hash it was given
// and never computes one.
struct CatalogCache {
    CatalogHash hash{};
    std::vector<std::uint8_t> json;
    std::shared_ptr<const CatalogPlan> plan;
};

// What WELCOME opened.
struct SessionInfo {
    std::uint32_t sessionId = 0;
    // The server's tick when the session opened.
    std::uint32_t tick = 0;
    std::uint32_t tickPeriodUs = 0;
    // A subset of the capabilities requested (W23).
    std::uint32_t capsGranted = 0;
    // The token a reconnect may present within limits.resumeGraceMs; none when resume is disabled.
    std::optional<ResumeToken> resumeToken;
    CatalogHash catalogHash{};
    std::shared_ptr<const CatalogPlan> plan;
    // Whether WELCOME skipped the catalog because the offered hash matched.
    bool catalogSkipped = false;
    // Whether this HELLO presented a resume token; the session's first frame then carries RESET.
    bool resumed = false;
};

// Why a connection ended.
struct ConnectionClose {
    // The code the reconnect policy reads: the KICK's when one arrived, this side's verdict on a protocol failure, or the transport's.
    int code = 0;
    std::string reason;
    // Whether the session ended cleanly: only 1000 and 1001 are.
    bool wasClean = false;
    // Whether this side ended it.
    bool local = false;
    // The resume token of the session that ended, when it may still be resumed, and the local time it goes stale at.
    std::optional<ResumeToken> resumeToken;
    double resumeDeadlineMs = 0;
};

// What a connection reports, synchronously from inside Poll.
class ConnectionListener {
public:
    virtual ~ConnectionListener() = default;
    // The session is open: the catalog is compiled and frames follow.
    virtual void OnWelcome(const SessionInfo&) {}
    // One TICK, in arrival order: apply it before returning — frames do not commute (05 § 1). The view is valid during the call.
    virtual void OnTick(std::span<const std::uint8_t>, double /*recvMs*/) {}
    virtual void OnPong(const PongMessage&, double /*recvMs*/) {}
    // Every inbound message, before it is decoded: the recorder's hook.
    virtual void OnMessage(std::span<const std::uint8_t>, double /*recvMs*/) {}
    // A KICK; its close follows.
    virtual void OnKick(int /*code*/, const std::string& /*reason*/) {}
    virtual void OnClose(const ConnectionClose&) {}
};

struct ConnectionOptions {
    // The application-declared session kind, <= 32 UTF-8 bytes (W21).
    std::string kind;
    // The opaque admission token, <= 8 KiB (W22).
    std::string token;
    // The capabilities asked for (W23).
    std::uint32_t caps = 0;
    // Up to 256 bytes for the application's admission hook (W19).
    std::vector<std::uint8_t> helloPayload;
    // A catalog held from an earlier session: HELLO offers its hash.
    std::shared_ptr<const CatalogCache> catalogCache;
    // A resume token from an earlier session, presented within its grace (01 § 3).
    std::optional<ResumeToken> resumeToken;
    // How long WELCOME may take; past it the connection closes with 4002 (W22).
    int helloTimeoutMs = protocol::HelloTimeoutMs;
};

// The local clock every timeout is measured on: monotonic milliseconds.
using NowFn = std::function<double()>;

// Monotonic milliseconds since an arbitrary origin (steady_clock).
double MonotonicNowMs();

class Connection {
public:
    Connection(std::unique_ptr<Transport> transport, ConnectionOptions options, ConnectionListener& listener, NowFn now = MonotonicNowMs);

    Connection(const Connection&) = delete;
    Connection& operator=(const Connection&) = delete;

    ConnectionState State() const { return state_; }
    // The open session, or null before WELCOME and after the close.
    const SessionInfo* Session() const { return session_.has_value() ? &*session_ : nullptr; }
    // The catalog to offer on the next connect: the one this session used.
    const std::shared_ptr<const CatalogCache>& Cache() const { return cache_; }

    // Opens the transport; HELLO follows on its own once it is open. A connection connects once.
    void Connect();

    // Waits up to `timeoutMs` for one transport event and handles it; enforces the hello timeout. Returns whether anything happened.
    bool Poll(int timeoutMs);

    // Sends COMMANDS, PING or BYE. Throws std::logic_error when not open, std::length_error above the cap in force — which the server
    // would answer with 1009.
    void Send(std::span<const std::uint8_t> message);

    // Sends a PING: mandatory at the catalog's pingHz (02 § 6).
    void SendPing(const PingMessage& ping);

    // A clean leave: BYE, then the close. `code` is 1000 or an application code in 4000-4999 (W24).
    void Close(int code = CloseCode::Normal, const std::string& reason = {});

private:
    void OnOpen();
    void OnMessage(std::span<const std::uint8_t> message);
    void Dispatch(std::span<const std::uint8_t> message, double recvMs);
    void OnWelcome(std::span<const std::uint8_t> message);
    void Fail(int code, const std::string& reason);
    void Finish(int code, const std::string& reason, bool local);
    void Report(int code, const std::string& reason, bool local, bool wasClean);

    std::unique_ptr<Transport> transport_;
    ConnectionOptions options_;
    ConnectionListener& listener_;
    NowFn now_;
    WireWriter writer_{protocol::HelloMaxBytes};
    ConnectionState state_ = ConnectionState::Idle;
    std::optional<SessionInfo> session_;
    std::shared_ptr<const CatalogCache> cache_;
    double helloDeadlineMs_ = 0;
    // A KICK's code and reason, reported when the close that follows it arrives.
    std::optional<std::pair<int, std::string>> kick_;
    std::uint32_t outboundCap_ = protocol::HelloMaxBytes;
    int resumeGraceMs_ = 0;
};

}  // namespace typhon::client
