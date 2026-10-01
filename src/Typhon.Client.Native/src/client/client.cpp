#include "client/client.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <thread>

namespace typhon::client {

namespace {

int WaitMs(double untilMs, double nowMs) { return static_cast<int>(std::max(0.0, std::ceil(untilMs - nowMs))); }

// Events handled per Poll after the first: enough to drain a burst, bounded so one call cannot run unbounded.
constexpr int DrainBudget = 64;

void Sleep(int ms)
{
    if (ms > 0)
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(ms));
    }
}

// Counts Poll's nesting for the duration of one call.
class Depth {
public:
    explicit Depth(int& depth) : depth_(depth) { depth_++; }
    ~Depth() { depth_--; }
    Depth(const Depth&) = delete;
    Depth& operator=(const Depth&) = delete;

private:
    int& depth_;
};

}  // namespace

Client::Client(ClientOptions options)
    : options_(std::move(options)), now_(options_.now ? options_.now : NowFn(MonotonicNowMs)), backoff_(options_.backoff, options_.random)
{
    if (!options_.transport)
    {
        if (options_.host.empty() || options_.port == 0)
        {
            throw std::invalid_argument("a client needs a host and a port, or a transport factory");
        }

        options_.transport = [host = options_.host, port = options_.port] { return MakeTcpTransport(host, port); };
    }
}

Client::~Client() = default;

void Client::Start()
{
    if (status_ != ClientStatus::Stopped && status_ != ClientStatus::GaveUp)
    {
        return;
    }

    backoff_.Reset();
    Open();
}

void Client::Stop(int code, const std::string& reason)
{
    status_ = ClientStatus::Stopped;
    if (connection_ != nullptr)
    {
        // The close reports through OnClose, which sees Stopped and does not reconnect. Retired, not destroyed: Stop may be called from
        // a callback running inside this connection's own Poll.
        retired_.push_back(std::move(connection_));
        retired_.back()->Close(code, reason);
    }

    if (ping_ != nullptr)
    {
        ping_->Stop();
    }
}

bool Client::Poll(int timeoutMs)
{
    if (depth_ > 0)
    {
        throw std::logic_error("Poll was called from inside one of its own callbacks");
    }

    retired_.clear();
    const Depth depth(depth_);
    const double now = now_();
    if (status_ == ClientStatus::WaitingToReconnect)
    {
        if (now >= reconnectAtMs_)
        {
            Open();
        }
        else
        {
            // Nothing to wait on but the deadline: sleep through it, no longer than asked.
            Sleep(std::min(timeoutMs, WaitMs(reconnectAtMs_, now)));
            return false;
        }
    }

    if (connection_ == nullptr)
    {
        Sleep(timeoutMs);
        return false;
    }

    if (ping_ != nullptr && ping_->Running())
    {
        timeoutMs = std::min(timeoutMs, WaitMs(ping_->DueAtMs(), now));
    }

    // The first event may wait; the rest only drain what is already there. A callback may close or replace the connection between two.
    bool handled = connection_->Poll(timeoutMs);
    for (int i = 0; handled && i < DrainBudget && connection_ != nullptr && connection_->State() != ConnectionState::Closed; i++)
    {
        if (!connection_->Poll(0))
        {
            break;
        }
    }

    if (connection_ != nullptr && connection_->State() == ConnectionState::Open && ping_ != nullptr)
    {
        PingMessage ping;
        const auto lastApplied = applier_ != nullptr && applier_->Tick() >= 0 ? static_cast<std::uint32_t>(applier_->Tick()) : 0u;
        if (ping_->Due(now_(), lastApplied, ping))
        {
            try
            {
                connection_->SendPing(ping);
            }
            catch (const std::exception&)
            {
                // A ping racing a close is a ping skipped, never the end of pinging.
                pingsFailed_++;
            }
        }
    }

    return handled;
}

const SessionInfo* Client::Session() const { return connection_ != nullptr ? connection_->Session() : nullptr; }

int Client::FlushCommands()
{
    if (commands_ == nullptr)
    {
        return 0;
    }

    if (connection_ == nullptr || connection_->State() != ConnectionState::Open)
    {
        commands_->Clear();
        return 0;
    }

    const auto tick = applier_->Tick() >= 0 ? static_cast<std::uint32_t>(applier_->Tick()) : 0u;
    Connection& connection = *connection_;
    return commands_->Flush(tick, [&connection](std::span<const std::uint8_t> message) { connection.Send(message); }, applier_->Realm());
}

void Client::Open()
{
    ConnectionOptions options;
    options.kind = options_.kind;
    options.token = options_.token;
    options.caps = options_.caps;
    options.helloPayload = options_.helloPayload;
    options.helloTimeoutMs = options_.helloTimeoutMs;
    options.catalogCache = cache_;
    if (resumeToken_.has_value() && now_() < resumeDeadlineMs_)
    {
        options.resumeToken = resumeToken_;
    }

    status_ = ClientStatus::Connecting;
    connection_ = std::make_unique<Connection>(options_.transport(), std::move(options), static_cast<ConnectionListener&>(*this), now_);
    connection_->Connect();
}

void Client::OnWelcome(const SessionInfo& session)
{
    opened_++;
    backoff_.Reset();
    // A new session: the previous one's resume token names a session this server has replaced. This one's arrives with its close.
    resumeToken_.reset();
    resumeDeadlineMs_ = 0;
    status_ = ClientStatus::Open;
    cache_ = connection_->Cache();
    // The replica survives a reconnect to the same catalog (the resumed session's first frame RESETs it); another catalog rebuilds it.
    if (applier_ == nullptr || planHash_ != session.catalogHash)
    {
        const Catalog& catalog = session.plan->GetCatalog();
        ClockOptions clock = options_.clock;
        clock.tickPeriodMs = catalog.tickPeriodUs / 1000.0;
        clock_ = std::make_unique<Clock>(clock);
        FrameApplierOptions applier;
        applier.maxNetId = options_.maxNetId;
        applier.clock = clock_.get();
        applier.onEvent = options_.onEvent;
        applier.onRealmChanged = options_.onRealmChanged;
        applier.onReset = options_.onReset;
        applier_ = std::make_unique<FrameApplier>(session.plan, std::move(applier));
        commands_ = std::make_unique<CommandQueue>(session.plan, now_);
        ping_ = std::make_unique<PingScheduler>(catalog.pingHz > 0 ? catalog.pingHz : 4);
        planHash_ = session.catalogHash;
    }

    ping_->Start(now_());
    if (options_.onWelcome)
    {
        options_.onWelcome(session);
    }
}

void Client::OnTick(std::span<const std::uint8_t> message, double recvMs)
{
    applier_->Apply(message, recvMs);
    if (options_.onFrame)
    {
        options_.onFrame();
    }
}

void Client::OnPong(const PongMessage& pong, double recvMs) { ping_->OnPong(pong, recvMs); }

void Client::OnMessage(std::span<const std::uint8_t> message, double recvMs)
{
    if (options_.onMessage)
    {
        options_.onMessage(message, recvMs);
    }
}

void Client::OnClose(const ConnectionClose& close)
{
    // Called from inside the connection's own Poll or Close: the object is retired, not destroyed, until that call has returned.
    if (connection_ != nullptr)
    {
        cache_ = connection_->Cache() != nullptr ? connection_->Cache() : cache_;
        retired_.push_back(std::move(connection_));
    }

    if (ping_ != nullptr)
    {
        ping_->Stop();
    }

    if (commands_ != nullptr)
    {
        commands_->Clear();
    }

    // Only a close that carries a token replaces it: an attempt that failed before WELCOME had no session, and the token of the one that
    // did is still good within its grace - the network blip resume exists for.
    if (close.resumeToken.has_value())
    {
        resumeToken_ = close.resumeToken;
        resumeDeadlineMs_ = close.resumeDeadlineMs;
    }

    lastClose_ = close;
    // Decided before the callback: one that stops the client, or stops and starts it again, has already said what happens next.
    const ClientStatus before = status_;
    if (options_.onClose)
    {
        options_.onClose(close);
    }

    if (before == ClientStatus::Stopped || status_ != before || connection_ != nullptr)
    {
        return;
    }

    const ReconnectRule rule = ReconnectRuleOf(close.code);
    const bool again =
        rule == ReconnectRule::Reconnect || (rule == ReconnectRule::Application && options_.shouldReconnect && options_.shouldReconnect(close));
    if (!again || (options_.maxAttempts >= 0 && backoff_.Attempt() >= options_.maxAttempts))
    {
        status_ = ClientStatus::GaveUp;
        if (options_.onGiveUp)
        {
            options_.onGiveUp(close, rule);
        }

        return;
    }

    status_ = ClientStatus::WaitingToReconnect;
    reconnectAtMs_ = now_() + backoff_.Next();
}

}  // namespace typhon::client
