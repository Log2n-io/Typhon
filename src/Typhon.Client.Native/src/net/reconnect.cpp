#include "net/reconnect.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <stdexcept>

#include "wire/constants.hpp"

namespace typhon::client {

ReconnectRule ReconnectRuleOf(int code)
{
    switch (code)
    {
        case CloseCode::GoingAway:
        case CloseCode::InternalError:
        case CloseCode::TryAgainLater:
        case CloseCode::PolicyViolation:
        case CloseCode::NoAcknowledgement:
        case CloseCode::HelloTimeout:
            return ReconnectRule::Reconnect;
        case CloseCode::ProtocolError:
        case CloseCode::MalformedPayload:
        case CloseCode::MessageTooBig:
        case CloseCode::AuthenticationRejected:
        // This client's own code, which no server sends: the stream was refused, and it would be refused again.
        case CloseCode::ClientRefusedTheStream:
            return ReconnectRule::Never;
        case CloseCode::Normal:
            return ReconnectRule::Application;
        default:
            if (code >= CloseCode::FirstApplicationCode && code <= CloseCode::LastApplicationCode)
            {
                return ReconnectRule::Application;
            }

            return code > CloseCode::ClientRefusedTheStream && code < CloseCode::FirstApplicationCode ? ReconnectRule::Never : ReconnectRule::Reconnect;
    }
}

Backoff::Backoff(BackoffOptions options, std::function<double()> random)
    : options_(options),
      random_(std::move(random)),
      state_(static_cast<std::uint64_t>(std::chrono::steady_clock::now().time_since_epoch().count()) | 1)
{
}

double Backoff::Next()
{
    const double full = std::min(options_.maxMs, options_.initialMs * std::pow(options_.factor, attempts_));
    attempts_++;
    double r;
    if (random_)
    {
        r = random_();
    }
    else
    {
        // xorshift64*: jitter needs spread across clients, not cryptographic quality, and no shared generator state.
        state_ ^= state_ >> 12;
        state_ ^= state_ << 25;
        state_ ^= state_ >> 27;
        r = static_cast<double>((state_ * 0x2545F4914F6CDD1Dull) >> 11) / 9007199254740992.0;
    }

    return full * (1 - options_.jitter * r);
}

PingScheduler::PingScheduler(double pingHz, double rttWeight) : weight_(rttWeight)
{
    if (!(pingHz > 0))
    {
        throw std::invalid_argument("pingHz must be positive");
    }

    intervalMs_ = 1000 / pingHz;
}

void PingScheduler::Start(double nowMs)
{
    running_ = true;
    dueMs_ = nowMs;
}

bool PingScheduler::Due(double nowMs, std::uint32_t lastAppliedTick, PingMessage& ping)
{
    if (!running_ || nowMs < dueMs_)
    {
        return false;
    }

    // Re-armed from the due time, not from now, so the rate holds; a poll that fell a whole interval behind owes one ping, not a burst.
    dueMs_ += intervalMs_;
    if (dueMs_ <= nowMs)
    {
        dueMs_ = nowMs + intervalMs_;
    }

    ping.clientMs = static_cast<std::uint32_t>(static_cast<std::uint64_t>(nowMs));
    ping.lastAppliedTick = lastAppliedTick;
    sent_++;
    return true;
}

void PingScheduler::OnPong(const PongMessage& pong, double recvMs)
{
    received_++;
    lastPongTick_ = pong.tick;
    const std::uint32_t now = static_cast<std::uint32_t>(static_cast<std::uint64_t>(recvMs));
    const auto rtt = static_cast<double>(static_cast<std::uint32_t>(now - pong.clientMs));
    latestRtt_ = rtt;
    smoothedRtt_ = smoothedRtt_ == 0 ? rtt : smoothedRtt_ + weight_ * (rtt - smoothedRtt_);
}

}  // namespace typhon::client
