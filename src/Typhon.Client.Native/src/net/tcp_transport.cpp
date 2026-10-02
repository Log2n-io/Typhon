#include <algorithm>
#include <cstring>
#include <mutex>
#include <vector>

#include "net/transport.hpp"
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
#include <cerrno>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <sys/socket.h>
#include <unistd.h>
#endif

namespace typhon::client {

namespace {

#ifdef _WIN32
using SocketHandle = SOCKET;
constexpr SocketHandle InvalidSocket = INVALID_SOCKET;

void CloseSocket(SocketHandle s) { closesocket(s); }
int LastError() { return WSAGetLastError(); }
bool WouldBlock(int error) { return error == WSAEWOULDBLOCK || error == WSAEINPROGRESS; }
int PollOne(WSAPOLLFD* fd, int timeoutMs) { return WSAPoll(fd, 1, timeoutMs); }
using PollFd = WSAPOLLFD;
constexpr int ShutdownWrite = SD_SEND;

void DisableSigpipe(SocketHandle) {}

void EnsureWinsock()
{
    // Started once per process and never cleaned up: a client may be created and destroyed many times, and WSACleanup while another
    // client's socket is open would break it.
    static std::once_flag once;
    std::call_once(once,
                   []
                   {
                       WSADATA data;
                       WSAStartup(MAKEWORD(2, 2), &data);
                   });
}

bool SetNonBlocking(SocketHandle s)
{
    u_long on = 1;
    return ioctlsocket(s, FIONBIO, &on) == 0;
}

int SendSome(SocketHandle s, const std::uint8_t* data, std::size_t length)
{
    return send(s, reinterpret_cast<const char*>(data), static_cast<int>(std::min<std::size_t>(length, 1 << 30)), 0);
}

int ReceiveSome(SocketHandle s, std::uint8_t* data, std::size_t length)
{
    return recv(s, reinterpret_cast<char*>(data), static_cast<int>(length), 0);
}
#else
using SocketHandle = int;
constexpr SocketHandle InvalidSocket = -1;

void CloseSocket(SocketHandle s) { ::close(s); }
int LastError() { return errno; }
bool WouldBlock(int error) { return error == EWOULDBLOCK || error == EAGAIN || error == EINPROGRESS || error == EINTR; }
int PollOne(pollfd* fd, int timeoutMs) { return ::poll(fd, 1, timeoutMs); }
using PollFd = pollfd;
constexpr int ShutdownWrite = SHUT_WR;

// macOS and the BSDs have no MSG_NOSIGNAL: a write to a closed peer would raise SIGPIPE and kill the process.
void DisableSigpipe([[maybe_unused]] SocketHandle s)
{
#ifdef SO_NOSIGPIPE
    int on = 1;
    setsockopt(s, SOL_SOCKET, SO_NOSIGPIPE, &on, sizeof on);
#endif
}

void EnsureWinsock() {}

bool SetNonBlocking(SocketHandle s)
{
    const int flags = fcntl(s, F_GETFL, 0);
    return flags >= 0 && fcntl(s, F_SETFL, flags | O_NONBLOCK) == 0;
}

int SendSome(SocketHandle s, const std::uint8_t* data, std::size_t length)
{
#ifdef MSG_NOSIGNAL
    constexpr int Flags = MSG_NOSIGNAL;
#else
    constexpr int Flags = 0;
#endif
    return static_cast<int>(::send(s, data, length, Flags));
}

int ReceiveSome(SocketHandle s, std::uint8_t* data, std::size_t length) { return static_cast<int>(::recv(s, data, length, 0)); }
#endif

namespace Codes = ::typhon::client::CloseCode;

constexpr std::size_t HeaderBytes = 4;
constexpr std::size_t ReadChunk = 64 * 1024;

std::uint32_t ReadLength(const std::uint8_t* h)
{
    return static_cast<std::uint32_t>(h[0]) | static_cast<std::uint32_t>(h[1]) << 8 | static_cast<std::uint32_t>(h[2]) << 16 |
           static_cast<std::uint32_t>(h[3]) << 24;
}

// One resolved address, copied out of getaddrinfo's list so the list can be freed at once.
struct Address {
    int family = 0;
    int type = 0;
    int protocol = 0;
    sockaddr_storage address{};
    socklen_t length = 0;
};

class TcpTransport final : public Transport {
public:
    TcpTransport(std::string host, std::uint16_t port) : host_(std::move(host)), port_(port) {}

    ~TcpTransport() override { Drop(); }

    void Open() override
    {
        if (state_ != State::Idle)
        {
            Fail(Codes::ProtocolError, "a transport opens once");
            return;
        }

        EnsureWinsock();
        addrinfo hints{};
        hints.ai_family = AF_UNSPEC;
        hints.ai_socktype = SOCK_STREAM;
        hints.ai_protocol = IPPROTO_TCP;
        addrinfo* found = nullptr;
        // Name resolution is the one blocking step: getaddrinfo has no portable non-blocking form. A numeric host resolves at once.
        const std::string port = std::to_string(port_);
        if (getaddrinfo(host_.c_str(), port.c_str(), &hints, &found) != 0 || found == nullptr)
        {
            Fail(Codes::GoingAway, "cannot resolve " + host_);
            return;
        }

        // Every address, in resolver order: "localhost" resolves to ::1 first on most systems, and a server bound to 127.0.0.1 refuses that one.
        for (const addrinfo* a = found; a != nullptr; a = a->ai_next)
        {
            Address entry;
            entry.family = a->ai_family;
            entry.type = a->ai_socktype;
            entry.protocol = a->ai_protocol;
            entry.length = static_cast<socklen_t>(std::min<std::size_t>(a->ai_addrlen, sizeof entry.address));
            std::memcpy(&entry.address, a->ai_addr, static_cast<std::size_t>(entry.length));
            addresses_.push_back(entry);
        }

        freeaddrinfo(found);
        state_ = State::Connecting;
        ConnectNext();
    }

    TransportEvent Poll(int timeoutMs) override
    {
        // The previous message is released first: its view was valid until this call.
        Consume(delivered_);
        delivered_ = 0;
        if (state_ == State::Closed)
        {
            if (closeReported_)
            {
                return TransportEvent::None;
            }

            return Report();
        }

        if (state_ == State::Idle)
        {
            return TransportEvent::None;
        }

        // A message already buffered is delivered without touching the socket.
        if (state_ == State::Open)
        {
            const TransportEvent ready = Extract();
            if (ready != TransportEvent::None)
            {
                return ready;
            }
        }

        PollFd fd{};
        fd.fd = socket_;
        fd.events = POLLIN;
        if (state_ == State::Connecting || outHead_ < out_.size())
        {
            fd.events |= POLLOUT;
        }

        const int ready = PollOne(&fd, timeoutMs);
        if (ready < 0)
        {
            if (!WouldBlock(LastError()))
            {
                return FailNow(Codes::GoingAway, "poll failed");
            }

            return TransportEvent::None;
        }

        if (ready > 0 && state_ == State::Connecting && (fd.revents & (POLLOUT | POLLERR | POLLHUP)) != 0)
        {
            int error = 0;
            socklen_t length = sizeof error;
            getsockopt(socket_, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &length);
            if (error != 0 || (fd.revents & (POLLERR | POLLHUP)) != 0)
            {
                // This address refused; the next one may not.
                ConnectNext();
                return state_ == State::Closed ? Report() : TransportEvent::None;
            }

            state_ = State::Preamble;
            out_.insert(out_.end(), std::begin(protocol::TcpPreamble), std::end(protocol::TcpPreamble));
        }

        if (state_ != State::Connecting)
        {
            if (!Flush())
            {
                return FailNow(Codes::GoingAway, "the connection was lost while sending");
            }

            if (ready > 0 && (fd.revents & (POLLIN | POLLHUP | POLLERR)) != 0 && !ReadAvailable())
            {
                return FailNow(Codes::GoingAway, "the connection was lost while receiving");
            }
        }

        if (state_ == State::Preamble && Buffered() >= HeaderBytes)
        {
            if (std::memcmp(in_.data() + inHead_, protocol::TcpPreamble, HeaderBytes) != 0)
            {
                return FailNow(Codes::ProtocolError, "the peer did not answer the TYP3 preamble: not a Typhon TCP endpoint");
            }

            Consume(HeaderBytes);
            state_ = State::Open;
            return TransportEvent::Open;
        }

        if (state_ == State::Open)
        {
            const TransportEvent next = Extract();
            if (next != TransportEvent::None)
            {
                return next;
            }
        }

        if (peerClosed_)
        {
            // An end before the preamble, or mid-message, is still only a vanished peer: TCP carries no reason.
            return FailNow(Codes::GoingAway, "the server closed the connection");
        }

        return TransportEvent::None;
    }

    std::span<const std::uint8_t> Message() const override { return message_; }

    void Send(std::span<const std::uint8_t> message) override
    {
        if (state_ != State::Open && state_ != State::Preamble)
        {
            return;
        }

        const auto length = static_cast<std::uint32_t>(message.size());
        const std::uint8_t header[HeaderBytes] = {static_cast<std::uint8_t>(length), static_cast<std::uint8_t>(length >> 8),
                                                  static_cast<std::uint8_t>(length >> 16), static_cast<std::uint8_t>(length >> 24)};
        out_.insert(out_.end(), std::begin(header), std::end(header));
        out_.insert(out_.end(), message.begin(), message.end());
        // Sent at once when the socket takes it: a command batch should not wait for the next poll.
        if (!Flush())
        {
            Fail(Codes::GoingAway, "the connection was lost while sending");
        }
    }

    void SetInboundCap(std::uint32_t bytes) override { inboundCap_ = bytes; }

    void Close() override
    {
        if (state_ == State::Closed)
        {
            return;
        }

        if ((state_ == State::Open || state_ == State::Preamble) && Flush())
        {
            // A graceful end: FIN after what is queued, then whatever the server already sent is read and dropped. Closing over unread data
            // makes the kernel answer with RST, and a peer that receives RST may discard the BYE still in its own receive buffer.
            ::shutdown(socket_, ShutdownWrite);
            std::uint8_t sink[4096];
            for (int i = 0; i < 64 && ReceiveSome(socket_, sink, sizeof sink) > 0; i++)
            {
            }
        }

        Fail(Codes::Normal, "closed by this side");
    }

    int CloseCode() const override { return closeCode_; }
    const std::string& CloseReason() const override { return closeReason_; }

private:
    enum class State : std::uint8_t
    {
        Idle,
        Connecting,
        Preamble,
        Open,
        Closed,
    };

    // Starts a non-blocking connect to the next resolved address, or fails once none is left.
    void ConnectNext()
    {
        Drop();
        while (nextAddress_ < addresses_.size())
        {
            const Address& a = addresses_[nextAddress_++];
            socket_ = ::socket(a.family, a.type, a.protocol);
            if (socket_ == InvalidSocket || !SetNonBlocking(socket_))
            {
                Drop();
                continue;
            }

            // Frames are small and latency-bound: no Nagle delay. And no SIGPIPE where send cannot suppress it per call.
            int on = 1;
            setsockopt(socket_, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&on), sizeof on);
            DisableSigpipe(socket_);
            const int result = ::connect(socket_, reinterpret_cast<const sockaddr*>(&a.address), a.length);
            if (result == 0 || WouldBlock(LastError()))
            {
                return;
            }

            Drop();
        }

        Fail(Codes::GoingAway, "cannot connect to " + host_ + ":" + std::to_string(port_));
    }

    TransportEvent Report()
    {
        closeReported_ = true;
        return TransportEvent::Closed;
    }

    std::size_t Buffered() const { return inTail_ - inHead_; }

    void Consume(std::size_t bytes)
    {
        inHead_ += bytes;
        if (inHead_ == inTail_)
        {
            inHead_ = 0;
            inTail_ = 0;
        }
    }

    // One whole message out of the buffer, a refusal of an oversized one, or None while it is incomplete.
    TransportEvent Extract()
    {
        if (Buffered() < HeaderBytes)
        {
            return TransportEvent::None;
        }

        const std::uint8_t* h = in_.data() + inHead_;
        const std::uint32_t length = ReadLength(h);
        // Checked before the body is read (03 § 10).
        if (length > inboundCap_)
        {
            return FailNow(Codes::MessageTooBig,
                           "the server announced a " + std::to_string(length) + " B message, above the " + std::to_string(inboundCap_) + " B cap");
        }

        if (Buffered() < HeaderBytes + length)
        {
            return TransportEvent::None;
        }

        message_ = {h + HeaderBytes, length};
        delivered_ = HeaderBytes + length;
        return TransportEvent::Message;
    }

    // Whether enough is buffered to stop reading: one whole message at the cap is the most a step can need, and a first header above the cap
    // ends the stream without its body being read (03 § 10). What stays in the socket is read by a later poll, once the buffer has been
    // consumed — so neither a fast nor a hostile peer can grow the buffer past one message, nor keep one poll reading forever.
    bool HasEnough() const
    {
        if (state_ == State::Open && Buffered() >= HeaderBytes && ReadLength(in_.data() + inHead_) > inboundCap_)
        {
            return true;
        }

        return Buffered() >= HeaderBytes + static_cast<std::size_t>(inboundCap_);
    }

    bool ReadAvailable()
    {
        while (!HasEnough())
        {
            // Into spare capacity, not a fresh zero-filled chunk per read: what is unread moves to the front first, and the buffer grows only
            // when that is not room enough.
            if (in_.size() - inTail_ < ReadChunk)
            {
                if (inHead_ > 0)
                {
                    std::memmove(in_.data(), in_.data() + inHead_, Buffered());
                    inTail_ -= inHead_;
                    inHead_ = 0;
                }

                if (in_.size() - inTail_ < ReadChunk)
                {
                    in_.resize(std::max(in_.size() * 2, inTail_ + ReadChunk));
                }
            }

            const int got = ReceiveSome(socket_, in_.data() + inTail_, ReadChunk);
            if (got > 0)
            {
                inTail_ += static_cast<std::size_t>(got);
                continue;
            }

            if (got == 0)
            {
                peerClosed_ = true;
                return true;
            }

            return WouldBlock(LastError());
        }

        return true;
    }

    bool Flush()
    {
        while (outHead_ < out_.size())
        {
            const int sent = SendSome(socket_, out_.data() + outHead_, out_.size() - outHead_);
            if (sent <= 0)
            {
                if (sent < 0 && WouldBlock(LastError()))
                {
                    return true;
                }

                return false;
            }

            outHead_ += static_cast<std::size_t>(sent);
        }

        out_.clear();
        outHead_ = 0;
        return true;
    }

    void Fail(int code, std::string reason)
    {
        if (state_ == State::Closed)
        {
            return;
        }

        closeCode_ = code;
        closeReason_ = std::move(reason);
        state_ = State::Closed;
        message_ = {};
        Drop();
    }

    TransportEvent FailNow(int code, std::string reason)
    {
        Fail(code, std::move(reason));
        return Report();
    }

    void Drop()
    {
        if (socket_ != InvalidSocket)
        {
            CloseSocket(socket_);
            socket_ = InvalidSocket;
        }
    }

    std::string host_;
    std::uint16_t port_;
    std::vector<Address> addresses_;
    std::size_t nextAddress_ = 0;
    SocketHandle socket_ = InvalidSocket;
    State state_ = State::Idle;
    bool peerClosed_ = false;
    bool closeReported_ = false;
    int closeCode_ = 0;
    std::string closeReason_;
    std::uint32_t inboundCap_ = protocol::WelcomeMaxBytes;
    // Received bytes in [inHead_, inTail_); the vector's size is the buffer's capacity.
    Vec<std::uint8_t> in_;
    std::size_t inHead_ = 0;
    std::size_t inTail_ = 0;
    std::size_t delivered_ = 0;
    std::span<const std::uint8_t> message_;
    Vec<std::uint8_t> out_;
    std::size_t outHead_ = 0;
};

}  // namespace

std::unique_ptr<Transport> MakeTcpTransport(std::string host, std::uint16_t port) { return std::make_unique<TcpTransport>(std::move(host), port); }

}  // namespace typhon::client
