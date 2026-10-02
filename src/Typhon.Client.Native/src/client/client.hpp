#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <vector>

#include "apply/frame_applier.hpp"
#include "clock/clock.hpp"
#include "commands/command_queue.hpp"
#include "net/connection.hpp"
#include "net/reconnect.hpp"

// The whole client behind the C ABI (13 § 7): one session kept up across reconnects, its replica, its render clock, its command queue
// and its ping loop, all driven by the caller's Poll — no thread inside. Single-threaded: use a Client from one thread at a time.
namespace typhon::client {

enum class ClientStatus : std::uint8_t
{
    // Not started, or stopped.
    Stopped,
    // A connection is opening or awaiting WELCOME.
    Connecting,
    Open,
    // Between a close and the next attempt.
    WaitingToReconnect,
    // The rule or the attempt budget ended it; LastClose() says why.
    GaveUp,
};

struct ClientOptions {
    std::string host;
    std::uint16_t port = 0;
    std::string kind;
    std::string token;
    std::uint32_t caps = 0;
    std::vector<std::uint8_t> helloPayload;
    int helloTimeoutMs = protocol::HelloTimeoutMs;
    BackoffOptions backoff;
    // Attempts before giving up, counted from the last open session; negative: unlimited.
    int maxAttempts = -1;
    // Decides for a clean close and application codes (4100-4999). Default: stay closed.
    std::function<bool(const ConnectionClose&)> shouldReconnect;
    // The render clock's bounds; tickPeriodMs comes from the catalog.
    ClockOptions clock;
    std::uint32_t maxNetId = 1u << 22;

    // Callbacks, synchronous from inside Poll.
    std::function<void(const SessionInfo&)> onWelcome;
    std::function<void(const ConnectionClose&)> onClose;
    std::function<void(const ConnectionClose&, ReconnectRule)> onGiveUp;
    std::function<void(const EventRecord&)> onEvent;
    std::function<void(const RealmFrame*, const RealmFrame*)> onRealmChanged;
    std::function<void()> onReset;
    // After each TICK applied: the replica is consistent here.
    std::function<void()> onFrame;
    // Every inbound message, before it is decoded, with its receive time: the recorder's hook.
    std::function<void(std::span<const std::uint8_t>, double)> onMessage;

    // Test seams: the transport (default TCP to host:port), the clock and the jitter source.
    std::function<std::unique_ptr<Transport>()> transport;
    NowFn now;
    std::function<double()> random;
};

class Client final : private ConnectionListener {
public:
    explicit Client(ClientOptions options);
    ~Client() override;

    Client(const Client&) = delete;
    Client& operator=(const Client&) = delete;

    // Opens the first connection; every later one follows a close the rule says is transient.
    void Start();

    // Stops reconnecting and closes the connection in flight with BYE.
    void Stop(int code = CloseCode::Normal, const std::string& reason = {});

    // One step: opens a due reconnect, waits up to `timeoutMs` for the first transport event, then handles what is already there - up to
    // a budget of events, so a caller polling once per rendered frame, slower than the tick rate, never falls behind - applying each TICK
    // before returning, and sends a due PING. Returns whether anything happened. The wait is cut short by the next ping or reconnect
    // deadline; with nothing to do at all (stopped, given up) it sleeps the timeout, so a loop paced by it does not spin.
    //
    // Not re-entrant: called from one of its own callbacks it throws std::logic_error. Stop and Start may be called from a callback.
    bool Poll(int timeoutMs);

    // Whether a Poll is on the stack: a callback is running. The C ABI defers a destroy requested from one.
    bool InPoll() const { return depth_ > 0; }

    ClientStatus Status() const { return status_; }
    // The open session, or null.
    const SessionInfo* Session() const;
    // The replica, the clock and the queue exist from the first WELCOME on, and survive reconnects to the same catalog.
    FrameApplier* Applier() { return applier_.get(); }
    Clock* RenderClock() { return clock_.get(); }
    CommandQueue* Commands() { return commands_.get(); }
    const PingScheduler* Ping() const { return ping_.get(); }
    const ConnectionClose* LastClose() const { return lastClose_.has_value() ? &*lastClose_ : nullptr; }
    std::uint64_t SessionsOpened() const { return opened_; }
    int Attempt() const { return backoff_.Attempt(); }
    std::uint64_t PingsFailed() const { return pingsFailed_; }

    // Local monotonic milliseconds, the time base of the render clock.
    double NowMs() const { return now_(); }

    // Sends the queued commands for the newest applied tick over the session's realm; dropped when the session is not open (counted by
    // CommandQueue::DiscardedCount). Returns the messages sent.
    int FlushCommands();

private:
    void Open();
    void OnWelcome(const SessionInfo& session) override;
    void OnTick(std::span<const std::uint8_t> message, double recvMs) override;
    void OnPong(const PongMessage& pong, double recvMs) override;
    void OnMessage(std::span<const std::uint8_t> message, double recvMs) override;
    void OnClose(const ConnectionClose& close) override;

    ClientOptions options_;
    NowFn now_;
    Backoff backoff_;
    std::unique_ptr<Connection> connection_;
    // Connections that closed while a Poll was on the stack - a callback may close one, then start and stop another - destroyed once the
    // outermost Poll has returned.
    std::vector<std::unique_ptr<Connection>> retired_;
    // Poll's nesting depth: 1 while its callbacks run.
    int depth_ = 0;
    ClientStatus status_ = ClientStatus::Stopped;
    double reconnectAtMs_ = 0;
    std::shared_ptr<const CatalogCache> cache_;
    std::optional<ResumeToken> resumeToken_;
    double resumeDeadlineMs_ = 0;
    std::optional<ConnectionClose> lastClose_;
    std::uint64_t opened_ = 0;
    std::uint64_t pingsFailed_ = 0;

    std::unique_ptr<Clock> clock_;
    std::unique_ptr<FrameApplier> applier_;
    std::unique_ptr<CommandQueue> commands_;
    std::unique_ptr<PingScheduler> ping_;
    CatalogHash planHash_{};
};

}  // namespace typhon::client
