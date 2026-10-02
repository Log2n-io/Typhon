#pragma once

// An in-memory transport the net and C ABI tests drive by hand: the test is the peer, and every event the transport raises is raised
// by the test.

#include <deque>
#include <memory>
#include <string>
#include <vector>

#include "golden_support.hpp"
#include "net/connection.hpp"
#include "net/transport.hpp"
#include "wire/constants.hpp"
#include "wire/messages.hpp"
#include "wire/writer.hpp"

namespace typhon::test {

using namespace typhon::client;

using Bytes = std::vector<std::uint8_t>;

// The far end of a fake transport: what the client sent, how it closed, and the events the test queues for it.
struct Link {
    std::deque<std::pair<TransportEvent, Bytes>> incoming;
    int closeCode = CloseCode::GoingAway;
    std::vector<Bytes> sent;
    bool opened = false;
    bool closed = false;
    std::uint32_t inboundCap = 0;

    void Open() { incoming.emplace_back(TransportEvent::Open, Bytes{}); }
    void Deliver(Bytes message) { incoming.emplace_back(TransportEvent::Message, std::move(message)); }
    void ServerClose(int code = CloseCode::GoingAway)
    {
        closeCode = code;
        incoming.emplace_back(TransportEvent::Closed, Bytes{});
    }
};

class FakeTransport final : public Transport {
public:
    explicit FakeTransport(std::shared_ptr<Link> link) : link_(std::move(link)) {}

    void Open() override { link_->opened = true; }

    TransportEvent Poll(int) override
    {
        if (link_->closed || link_->incoming.empty())
        {
            return TransportEvent::None;
        }

        auto [event, bytes] = std::move(link_->incoming.front());
        link_->incoming.pop_front();
        if (event == TransportEvent::Message && bytes.size() > link_->inboundCap)
        {
            // The TCP transport's own check, before the body is read.
            link_->closeCode = CloseCode::MessageTooBig;
            return TransportEvent::Closed;
        }

        message_ = std::move(bytes);
        return event;
    }

    std::span<const std::uint8_t> Message() const override { return message_; }
    void Send(std::span<const std::uint8_t> message) override { link_->sent.emplace_back(message.begin(), message.end()); }
    void SetInboundCap(std::uint32_t bytes) override { link_->inboundCap = bytes; }
    void Close() override { link_->closed = true; }
    int CloseCode() const override { return link_->closeCode; }
    const std::string& CloseReason() const override { return reason_; }

private:
    std::shared_ptr<Link> link_;
    Bytes message_;
    std::string reason_;
};

inline const CatalogHash Hash = {1, 2, 3, 4, 5, 6, 7, 8};

inline ResumeToken TokenOf(std::uint8_t seed)
{
    ResumeToken token{};
    for (std::size_t i = 0; i < token.size(); i++)
    {
        token[i] = static_cast<std::uint8_t>(seed + i);
    }

    return token;
}

// A WELCOME's variable parts, set fluently: WelcomeParts().Major(4).
struct WelcomeParts {
    std::uint32_t major = 3;
    std::uint32_t capsGranted = 0;
    ResumeToken resumeToken{};
    bool skipCatalog = false;
    Bytes catalogJson;
    CatalogHash hash = Hash;

    WelcomeParts& Major(std::uint32_t v) { major = v; return *this; }
    WelcomeParts& Caps(std::uint32_t v) { capsGranted = v; return *this; }
    WelcomeParts& Resume(const ResumeToken& v) { resumeToken = v; return *this; }
    WelcomeParts& SkipCatalog() { skipCatalog = true; return *this; }
    WelcomeParts& CatalogJson(Bytes v) { catalogJson = std::move(v); return *this; }
    WelcomeParts& WithHash(const CatalogHash& v) { hash = v; return *this; }
};

inline Bytes Welcome(const WelcomeParts& parts = WelcomeParts())
{
    WelcomeMessage m;
    m.major = parts.major;
    m.capsGranted = parts.capsGranted;
    m.sessionId = 7;
    m.resumeToken = parts.resumeToken;
    m.tick = 100;
    m.tickPeriodUs = 50000;
    m.catalogHash = parts.hash;
    if (!parts.skipCatalog)
    {
        m.catalogJson = parts.catalogJson.empty() ? GoldenBin("catalog-kitchen-sink") : parts.catalogJson;
    }

    WireWriter w(1 << 16);
    WriteWelcome(w, m);
    return {w.Written().begin(), w.Written().end()};
}

inline Bytes Tick(std::uint32_t tick, std::uint8_t flags = 0)
{
    WireWriter w(16);
    w.U8(MessageType::Tick);
    w.U32(tick);
    w.U8(flags);
    return {w.Written().begin(), w.Written().end()};
}

}  // namespace typhon::test
