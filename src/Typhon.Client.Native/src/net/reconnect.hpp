#pragma once

#include <cstdint>
#include <functional>

#include "wire/messages.hpp"

// The reconnect policy (05 § 1) and the ping loop's arithmetic — the TypeScript SDK's net/reconnect.ts and net/ping.ts, with the timers
// replaced by deadlines the caller's poll checks.
namespace typhon::client {

// What a close code says about reconnecting (§ 3).
enum class ReconnectRule : std::uint8_t
{
    // The failure is the client's or the session's own: reconnecting would repeat it.
    Never,
    // Transient: reconnect after a backoff.
    Reconnect,
    // A clean close or an application code (4100-4999): the application decides.
    Application,
};

// The rule for a close code. The engine-reserved range 4005-4099 is fatal until a rule gives it a meaning; a transport that died without
// a code is transient.
ReconnectRule ReconnectRuleOf(int code);

struct BackoffOptions {
    double initialMs = 500;
    double maxMs = 30000;
    double factor = 2;
    // Fraction of the wait that is random, so a restarted server is not hit by every client at once.
    double jitter = 0.5;
};

// Exponential backoff with jitter: delay = min(max, initial * factor^attempt) * (1 - jitter * random).
class Backoff {
public:
    explicit Backoff(BackoffOptions options = {}, std::function<double()> random = {});

    // Attempts taken since the last Reset.
    int Attempt() const { return attempts_; }

    // The next wait in milliseconds; counts the attempt.
    double Next();

    void Reset() { attempts_ = 0; }

private:
    BackoffOptions options_;
    std::function<double()> random_;
    std::uint64_t state_;
    int attempts_ = 0;
};

// The mandatory PING loop's state (§ 3): one every 1 / pingHz seconds once the session is open, carrying the newest applied tick, and a
// round-trip estimate from the PONG that answers. clientMs is the local clock truncated to 32 bits; the round trip is the difference in
// the same arithmetic, so a wrap every 49 days costs nothing.
class PingScheduler {
public:
    // `rttWeight`: weight of a new sample in the smoothed round trip, 1/8 — TCP's estimator (RFC 6298).
    explicit PingScheduler(double pingHz, double rttWeight = 0.125);

    // Arms the loop at `nowMs`: the first ping is due at once, so the server hears from the client before the first frame.
    void Start(double nowMs);
    void Stop() { running_ = false; }
    bool Running() const { return running_; }

    // When the next ping is due; meaningful while running.
    double DueAtMs() const { return dueMs_; }

    // The ping to send at `nowMs` when one is due, and re-arms the loop; false otherwise. A ping that could not go out is a ping skipped,
    // never the end of pinging: the loop stays armed whatever the send does.
    bool Due(double nowMs, std::uint32_t lastAppliedTick, PingMessage& ping);

    // Records the answer: its clientMs is the one this client sent, so the round trip needs no state per ping.
    void OnPong(const PongMessage& pong, double recvMs);

    double RttMs() const { return smoothedRtt_; }
    double LastRttMs() const { return latestRtt_; }
    std::uint64_t PingsSent() const { return sent_; }
    std::uint64_t PongsReceived() const { return received_; }
    // The server tick of the latest PONG, or -1.
    std::int64_t ServerTick() const { return lastPongTick_; }

private:
    double intervalMs_;
    double weight_;
    bool running_ = false;
    double dueMs_ = 0;
    double smoothedRtt_ = 0;
    double latestRtt_ = 0;
    std::uint64_t sent_ = 0;
    std::uint64_t received_ = 0;
    std::int64_t lastPongTick_ = -1;
};

}  // namespace typhon::client
