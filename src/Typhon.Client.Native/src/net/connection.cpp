#include "net/connection.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <stdexcept>

#include "wire/constants.hpp"
#include "wire/errors.hpp"
#include "wire/utf8.hpp"

namespace typhon::client {

namespace {

std::string HexByte(std::uint8_t b)
{
    static constexpr char Digits[] = "0123456789abcdef";
    return {'0', 'x', Digits[b >> 4], Digits[b & 15]};
}

template <std::size_t N>
bool IsZero(const std::array<std::uint8_t, N>& bytes)
{
    return std::all_of(bytes.begin(), bytes.end(), [](std::uint8_t b) { return b == 0; });
}

}  // namespace

double MonotonicNowMs()
{
    using namespace std::chrono;
    return duration<double, std::milli>(steady_clock::now().time_since_epoch()).count();
}

Connection::Connection(std::unique_ptr<Transport> transport, ConnectionOptions options, ConnectionListener& listener, NowFn now)
    : transport_(std::move(transport)), options_(std::move(options)), listener_(listener), now_(std::move(now)), cache_(options_.catalogCache)
{
}

void Connection::Connect()
{
    if (state_ != ConnectionState::Idle)
    {
        throw std::logic_error("a connection connects once; build another one to reconnect");
    }

    state_ = ConnectionState::Connecting;
    // The hello timeout covers the connect and the preamble too: a black-holed SYN or a peer that accepts and never answers TYP3 would
    // otherwise hold the client in Connecting forever. OnOpen restarts it for WELCOME.
    helloDeadlineMs_ = now_() + options_.helloTimeoutMs;
    transport_->Open();
}

bool Connection::Poll(int timeoutMs)
{
    if (state_ == ConnectionState::Idle || state_ == ConnectionState::Closed)
    {
        return false;
    }

    if (state_ == ConnectionState::Connecting || state_ == ConnectionState::AwaitingWelcome)
    {
        const double left = helloDeadlineMs_ - now_();
        if (left <= 0)
        {
            const std::string within = std::to_string(options_.helloTimeoutMs) + " ms";
            if (state_ == ConnectionState::Connecting)
            {
                // Nothing reached a Typhon server: a transient failure, reported as the transport's own would be.
                Fail(CloseCode::GoingAway, "no Typhon server answered within " + within);
            }
            else
            {
                Fail(CloseCode::HelloTimeout, "WELCOME did not arrive within " + within);
            }

            return true;
        }

        timeoutMs = std::min(timeoutMs, static_cast<int>(std::ceil(left)));
    }

    switch (transport_->Poll(timeoutMs))
    {
        case TransportEvent::Open:
            OnOpen();
            return true;
        case TransportEvent::Message:
            OnMessage(transport_->Message());
            return true;
        case TransportEvent::Closed:
            if (state_ != ConnectionState::Closed)
            {
                state_ = ConnectionState::Closed;
                const int code = kick_.has_value() ? kick_->first : transport_->CloseCode();
                const std::string reason = kick_.has_value() ? kick_->second : transport_->CloseReason();
                // An oversized frame or a peer that is not Typhon is this side's verdict, even though the transport detected it.
                const bool local = !kick_.has_value() && (code == CloseCode::MessageTooBig || code == CloseCode::ProtocolError);
                Report(code, reason, local, code == CloseCode::Normal || code == CloseCode::GoingAway);
            }

            return true;
        default:
            return false;
    }
}

void Connection::Send(std::span<const std::uint8_t> message)
{
    if (state_ != ConnectionState::Open)
    {
        throw std::logic_error("the connection is not open");
    }

    if (message.size() > outboundCap_)
    {
        throw std::length_error("a client message of " + std::to_string(message.size()) + " B is above the " + std::to_string(outboundCap_) +
                                " B limit");
    }

    transport_->Send(message);
}

void Connection::SendPing(const PingMessage& ping)
{
    writer_.Reset();
    WritePing(writer_, ping);
    Send(writer_.Written());
}

void Connection::Close(int code, const std::string& reason)
{
    if (state_ == ConnectionState::Closed || state_ == ConnectionState::Idle)
    {
        return;
    }

    if (state_ == ConnectionState::Open && IsValidClientCloseCode(code))
    {
        writer_.Reset();
        WriteBye(writer_, static_cast<std::uint32_t>(code));
        transport_->Send(writer_.Written());
    }

    Finish(code, reason, true);
}

void Connection::OnOpen()
{
    HelloMessage hello;
    hello.major = protocol::Major;
    hello.minor = protocol::Minor;
    hello.caps = options_.caps;
    hello.kind = options_.kind;
    hello.token = options_.token;
    hello.resumeToken = options_.resumeToken.value_or(ResumeToken{});
    hello.clientCatalogHash = cache_ != nullptr ? cache_->hash : CatalogHash{};
    hello.helloPayload = options_.helloPayload;
    writer_.Reset();
    try
    {
        WriteHello(writer_, hello);
    }
    catch (const std::exception& e)
    {
        // A HELLO this side cannot encode (a kind or token over its limit) is the application's error, and nothing was sent.
        Fail(CloseCode::ClientRefusedTheStream, std::string("HELLO cannot be encoded: ") + e.what());
        return;
    }

    if (writer_.Position() > static_cast<std::size_t>(protocol::HelloMaxBytes))
    {
        Fail(CloseCode::ClientRefusedTheStream, "HELLO is above the " + std::to_string(protocol::HelloMaxBytes) + " B limit");
        return;
    }

    state_ = ConnectionState::AwaitingWelcome;
    // Until WELCOME brings limits.frameBytes, the only bound a client has is the protocol's (§ 10).
    transport_->SetInboundCap(protocol::WelcomeMaxBytes);
    transport_->Send(writer_.Written());
    helloDeadlineMs_ = now_() + options_.helloTimeoutMs;
}

void Connection::OnMessage(std::span<const std::uint8_t> message)
{
    const double recvMs = now_();
    listener_.OnMessage(message, recvMs);
    // The recorder hook may have stopped the client: a message that arrives on a closed connection is not applied.
    if (state_ == ConnectionState::Closed)
    {
        return;
    }

    try
    {
        Dispatch(message, recvMs);
    }
    catch (const WireFormatError& e)
    {
        Fail(e.CloseCode(), e.what());
    }
    catch (const CatalogError& e)
    {
        Fail(CloseCode::MalformedPayload, e.what());
    }
    catch (const std::bad_alloc&)
    {
        throw;
    }
    catch (const std::exception& e)
    {
        // Any other decode failure is a malformed payload. A listener's own exception lands here too and closes the session: the store
        // may hold part of a frame, the same contract as a WireFormatError.
        Fail(CloseCode::MalformedPayload, e.what());
    }
}

void Connection::Dispatch(std::span<const std::uint8_t> message, double recvMs)
{
    if (message.empty())
    {
        Fail(CloseCode::ProtocolError, "an empty message");
        return;
    }

    const std::uint8_t type = message[0];
    if (state_ == ConnectionState::AwaitingWelcome)
    {
        if (type != MessageType::Welcome)
        {
            Fail(CloseCode::ProtocolError, "message type " + HexByte(type) + " before WELCOME");
            return;
        }

        OnWelcome(message);
        return;
    }

    switch (type)
    {
        case MessageType::Tick:
            listener_.OnTick(message, recvMs);
            break;
        case MessageType::Pong:
            listener_.OnPong(ParsePong(message), recvMs);
            break;
        case MessageType::Kick:
        {
            KickMessage kick = ParseKick(message);
            listener_.OnKick(static_cast<int>(kick.code), kick.reason);
            // The server closes next; its code travels only in the KICK.
            kick_.emplace(static_cast<int>(kick.code), std::move(kick.reason));
            break;
        }
        default:
            Fail(CloseCode::ProtocolError, "message type " + HexByte(type) + " is unknown or out of state");
            break;
    }
}

void Connection::OnWelcome(std::span<const std::uint8_t> message)
{
    const WelcomeMessage welcome = ParseWelcome(message);
    if (welcome.major != static_cast<std::uint32_t>(protocol::Major))
    {
        Fail(CloseCode::ProtocolError, "the server speaks major " + std::to_string(welcome.major) + ", this client major " +
                                           std::to_string(protocol::Major));
        return;
    }

    CheckCapsGranted(options_.caps, welcome.capsGranted);
    const bool skipped = welcome.catalogJson.empty();
    if (skipped && cache_ == nullptr)
    {
        Fail(CloseCode::ProtocolError, "WELCOME skipped the catalog, and this client holds none");
        return;
    }

    // A skip is only a skip of the catalog this client offered: any other hash names a catalog it does not hold.
    if (skipped && cache_->hash != welcome.catalogHash)
    {
        Fail(CloseCode::ProtocolError, "WELCOME skipped catalog " + CatalogHashToHex(welcome.catalogHash) + ", but this client offered " +
                                           CatalogHashToHex(cache_->hash));
        return;
    }

    auto cache = std::make_shared<CatalogCache>();
    cache->hash = welcome.catalogHash;
    if (skipped)
    {
        cache->json = cache_->json;
        cache->plan = cache_->plan;
    }
    else
    {
        cache->json = welcome.catalogJson;
        auto catalog = std::make_shared<const Catalog>(ParseCatalog(std::span<const std::uint8_t>(cache->json)));
        cache->plan = CatalogPlan::Compile(std::move(catalog));
    }

    // The hash is the server's, echoed back on the next connect; the client never computes one.
    cache_ = std::move(cache);
    SessionInfo session;
    session.sessionId = welcome.sessionId;
    session.tick = welcome.tick;
    session.tickPeriodUs = welcome.tickPeriodUs;
    session.capsGranted = welcome.capsGranted;
    if (!IsZero(welcome.resumeToken))
    {
        session.resumeToken = welcome.resumeToken;
    }

    session.catalogHash = welcome.catalogHash;
    session.plan = cache_->plan;
    session.catalogSkipped = skipped;
    session.resumed = options_.resumeToken.has_value() && !IsZero(*options_.resumeToken);

    const Catalog& catalog = cache_->plan->GetCatalog();
    outboundCap_ = static_cast<std::uint32_t>(catalog.clientMessageBytes);
    resumeGraceMs_ = catalog.resumeGraceMs;
    transport_->SetInboundCap(static_cast<std::uint32_t>(catalog.frameBytes));
    session_ = std::move(session);
    state_ = ConnectionState::Open;
    listener_.OnWelcome(*session_);
}

void Connection::Fail(int code, const std::string& reason)
{
    if (state_ == ConnectionState::Closed)
    {
        return;
    }

    Finish(code, reason, true);
}

void Connection::Finish(int code, const std::string& reason, bool local)
{
    // 1002, 1007 and 1009 are not codes a client may send (W24): a BYE 4004 says the stream was refused, while the transport carries one.
    if ((state_ == ConnectionState::Open || state_ == ConnectionState::AwaitingWelcome) && !IsValidClientCloseCode(code))
    {
        writer_.Reset();
        WriteBye(writer_, CloseCode::ClientRefusedTheStream);
        transport_->Send(writer_.Written());
    }

    state_ = ConnectionState::Closed;
    transport_->Close();
    // Whether the session ENDED cleanly, which a local close does not by itself make it: a protocol abort comes through here too.
    Report(code, reason, local, code == CloseCode::Normal || code == CloseCode::GoingAway);
}

void Connection::Report(int code, const std::string& reason, bool local, bool wasClean)
{
    ConnectionClose close;
    close.code = code;
    close.reason = reason;
    close.wasClean = wasClean;
    close.local = local;
    if (session_.has_value() && session_->resumeToken.has_value())
    {
        close.resumeToken = session_->resumeToken;
        close.resumeDeadlineMs = now_() + resumeGraceMs_;
    }

    session_.reset();
    listener_.OnClose(close);
}

}  // namespace typhon::client
