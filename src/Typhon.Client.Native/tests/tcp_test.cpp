// The TCP transport against a real socket: a loopback listener in this process plays the server, one step at a time. Single-threaded: the
// client connects without blocking and the kernel completes the handshake, so the listener can accept, write and read between two polls.

#include <chrono>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "net/transport.hpp"
#include "test_framework.hpp"
#include "wire/constants.hpp"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>
#endif

using namespace typhon::client;

namespace {

#ifdef _WIN32
using Socket = SOCKET;
constexpr Socket NoSocket = INVALID_SOCKET;
void CloseOne(Socket s) { closesocket(s); }
#else
using Socket = int;
constexpr Socket NoSocket = -1;
void CloseOne(Socket s) { ::close(s); }
#endif

using Bytes = std::vector<std::uint8_t>;

Bytes Framed(const Bytes& message)
{
    const auto n = static_cast<std::uint32_t>(message.size());
    Bytes out = {static_cast<std::uint8_t>(n), static_cast<std::uint8_t>(n >> 8), static_cast<std::uint8_t>(n >> 16),
                 static_cast<std::uint8_t>(n >> 24)};
    out.insert(out.end(), message.begin(), message.end());
    return out;
}

const Bytes Preamble = {0x54, 0x59, 0x50, 0x33};

// A blocking loopback listener on an ephemeral port, driven step by step by the test.
class Listener {
public:
    Listener()
    {
        // The transport starts Winsock on first use; a listener created first needs it too.
        auto warm = MakeTcpTransport("127.0.0.1", 1);
        warm->Open();
        listen_ = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0;
        CHECK(::bind(listen_, reinterpret_cast<const sockaddr*>(&address), sizeof address) == 0);
        CHECK(::listen(listen_, 4) == 0);
        socklen_t length = sizeof address;
        CHECK(::getsockname(listen_, reinterpret_cast<sockaddr*>(&address), &length) == 0);
        port_ = ntohs(address.sin_port);
    }

    ~Listener()
    {
        if (peer_ != NoSocket)
        {
            CloseOne(peer_);
        }

        CloseOne(listen_);
    }

    std::uint16_t Port() const { return port_; }

    // Accepts the client's connection, polling the client meanwhile: a client may need a poll to move on to the address that answers.
    void Accept(Transport& client)
    {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(8);
        while (std::chrono::steady_clock::now() < deadline)
        {
            fd_set readable;
            FD_ZERO(&readable);
            FD_SET(listen_, &readable);
            timeval wait{0, 10000};
            if (::select(static_cast<int>(listen_) + 1, &readable, nullptr, nullptr, &wait) > 0)
            {
                peer_ = ::accept(listen_, nullptr, nullptr);
                CHECK(peer_ != NoSocket);
                return;
            }

            CHECK(client.Poll(0) == TransportEvent::None);
        }

        CHECK_MSG(false, "the client never connected");
    }

    void Send(const Bytes& bytes)
    {
        std::size_t at = 0;
        while (at < bytes.size())
        {
            const int sent = ::send(peer_, reinterpret_cast<const char*>(bytes.data() + at), static_cast<int>(bytes.size() - at), 0);
            CHECK(sent > 0);
            at += static_cast<std::size_t>(sent);
        }
    }

    // Sends from another thread: a payload larger than the socket buffers would block a single-threaded test whose client is not reading
    // yet. Tolerant of a client that hangs up midway, which is what some tests are about.
    std::thread SendInBackground(Bytes bytes)
    {
        const Socket peer = peer_;
        return std::thread(
            [peer, bytes = std::move(bytes)]
            {
                std::size_t at = 0;
                while (at < bytes.size())
                {
                    const int sent = ::send(peer, reinterpret_cast<const char*>(bytes.data() + at), static_cast<int>(bytes.size() - at), 0);
                    if (sent <= 0)
                    {
                        return;
                    }

                    at += static_cast<std::size_t>(sent);
                }
            });
    }

    Bytes Receive(std::size_t count)
    {
        Bytes out(count);
        std::size_t at = 0;
        while (at < count)
        {
            const int got = ::recv(peer_, reinterpret_cast<char*>(out.data() + at), static_cast<int>(count - at), 0);
            CHECK(got > 0);
            at += static_cast<std::size_t>(got);
        }

        return out;
    }

    // An orderly end from the server's side.
    void Hangup()
    {
        CloseOne(peer_);
        peer_ = NoSocket;
    }

private:
    Socket listen_ = NoSocket;
    Socket peer_ = NoSocket;
    std::uint16_t port_ = 0;
};

// Polls until an event other than None arrives, or the wait passes. A refused connect takes about 2 s to surface on Windows, which retries
// the SYN after an RST from loopback; the tests that wait for one allow for it.
TransportEvent Next(Transport& t, int seconds = 2)
{
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
    while (std::chrono::steady_clock::now() < deadline)
    {
        const TransportEvent e = t.Poll(20);
        if (e != TransportEvent::None)
        {
            return e;
        }
    }

    return TransportEvent::None;
}

// A transport connected to the listener, through the preamble exchange.
std::unique_ptr<Transport> Opened(Listener& server, const char* host = "127.0.0.1")
{
    auto t = MakeTcpTransport(host, server.Port());
    t->Open();
    server.Accept(*t);
    server.Send(Preamble);
    CHECK(Next(*t) == TransportEvent::Open);
    CHECK(server.Receive(4) == Preamble);
    return t;
}

}  // namespace

TEST(Tcp_ExchangesThePreambleAndFramesBothWays)
{
    Listener server;
    auto t = Opened(server);
    // Two messages in one write, the second split across two: framing, not packets, delimits them.
    Bytes burst = Framed({1, 2, 3});
    const Bytes second = Framed({9, 8, 7, 6, 5});
    burst.insert(burst.end(), second.begin(), second.begin() + 5);
    server.Send(burst);
    CHECK(Next(*t) == TransportEvent::Message);
    CHECK((Bytes(t->Message().begin(), t->Message().end()) == Bytes{1, 2, 3}));
    server.Send(Bytes(second.begin() + 5, second.end()));
    CHECK(Next(*t) == TransportEvent::Message);
    CHECK((Bytes(t->Message().begin(), t->Message().end()) == Bytes{9, 8, 7, 6, 5}));

    t->Send(Bytes{0x84, 1});
    CHECK(server.Receive(6) == Framed({0x84, 1}));
}

TEST(Tcp_ResolvesLocalhostToTheAddressThatAnswers)
{
    // The listener is on 127.0.0.1 only; "localhost" may resolve to ::1 first, which refuses.
    Listener server;
    auto t = MakeTcpTransport("localhost", server.Port());
    t->Open();
    server.Accept(*t);
    server.Send(Preamble);
    CHECK(Next(*t) == TransportEvent::Open);
}

TEST(Tcp_RefusesAPeerThatIsNotTyphon)
{
    Listener server;
    auto t = MakeTcpTransport("127.0.0.1", server.Port());
    t->Open();
    server.Accept(*t);
    server.Send({'H', 'T', 'T', 'P'});
    CHECK(Next(*t) == TransportEvent::Closed);
    CHECK_EQ(t->CloseCode(), CloseCode::ProtocolError);
}

TEST(Tcp_ClosesWith1009OnAnOversizedFrameWithoutBufferingItsBody)
{
    Listener server;
    auto t = Opened(server);
    t->SetInboundCap(64);
    // A header announcing 1 MiB, then 256 KiB of body: the stream ends at the header.
    Bytes oversized = {0, 0, 0x10, 0};
    oversized.resize(4 + 256 * 1024, 0xAB);
    std::thread sender = server.SendInBackground(std::move(oversized));
    const TransportEvent event = Next(*t);
    const int code = t->CloseCode();
    server.Hangup();
    sender.join();
    CHECK(event == TransportEvent::Closed);
    CHECK_EQ(code, CloseCode::MessageTooBig);
}

TEST(Tcp_ReportsAPeerThatVanishesMidMessage)
{
    Listener server;
    auto t = Opened(server);
    const Bytes half = Framed({1, 2, 3, 4, 5, 6, 7, 8});
    server.Send(Bytes(half.begin(), half.begin() + 6));
    server.Hangup();
    CHECK(Next(*t) == TransportEvent::Closed);
    CHECK_EQ(t->CloseCode(), CloseCode::GoingAway);
    CHECK(t->Poll(0) == TransportEvent::None);
}

TEST(Tcp_ReportsARefusedConnect)
{
    std::uint16_t port = 0;
    {
        Listener gone;
        port = gone.Port();
    }

    auto t = MakeTcpTransport("127.0.0.1", port);
    t->Open();
    CHECK(Next(*t, 8) == TransportEvent::Closed);
    CHECK_EQ(t->CloseCode(), CloseCode::GoingAway);
}

TEST(Tcp_DeliversALargeBurstOneMessageAtATime)
{
    // Many frames in one write: each poll delivers one, the buffer never holds more than a capped message's worth beyond what is consumed.
    Listener server;
    auto t = Opened(server);
    t->SetInboundCap(1024);
    Bytes burst;
    for (int i = 0; i < 2000; i++)
    {
        const Bytes frame = Framed(Bytes(100, static_cast<std::uint8_t>(i)));
        burst.insert(burst.end(), frame.begin(), frame.end());
    }

    std::thread sender = server.SendInBackground(std::move(burst));
    int received = 0;
    bool ordered = true;
    for (int i = 0; i < 2000 && Next(*t) == TransportEvent::Message; i++)
    {
        ordered = ordered && t->Message().size() == 100u && t->Message()[0] == static_cast<std::uint8_t>(i & 0xFF);
        received++;
    }

    sender.join();
    CHECK_EQ(received, 2000);
    CHECK(ordered);
}
