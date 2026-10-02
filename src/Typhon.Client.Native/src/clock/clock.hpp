#pragma once

#include <array>
#include <cstdint>

// Maps server ticks to local time and produces the render time motion is evaluated at — the TypeScript SDK's clock/clock.ts:
// - Server timeline: tick -> server milliseconds is piecewise linear, one piece per tick period (PERIOD); render time runs on it and
//   is converted to a tick through the piece it falls in.
// - Offset: each frame yields recvMs - serverMs(tick); the smallest over a window is the best estimate of the true offset.
// - Render delay: period + p95(offset - minOffset), clamped to [min, max].
// - Convergence: render time advances with local time, corrected by at most +/- maxRateAdjust; it jumps forward only past snapMs, and
//   never backward.
// - Output: RenderTick (an integer) plus RenderFrac in [0, 1).
namespace typhon::client {

struct ClockOptions {
    // Server tick period in milliseconds, from WELCOME.
    double tickPeriodMs = 0;
    double initialDelayMs = 200;
    double minDelayMs = 150;
    double maxDelayMs = 300;
    double windowMs = 2000;
    double maxRateAdjust = 0.05;
    double snapMs = 1000;
    // How far render time may run past the newest frame before it holds: more than the 500 ms keepalive.
    double maxExtrapolationMs = 1000;
};

class Clock {
public:
    explicit Clock(const ClockOptions& options);

    std::int64_t RenderTick() const { return renderTick_; }
    double RenderFrac() const { return renderFrac_; }
    double MaxDelayMs() const { return delayCeilingMs_; }
    double RenderDelayMs() const { return delayMs_; }
    double TickPeriodMs() const { return pieceCount_ > 0 ? piecePeriod_[pieceCount_ - 1] : pendingPeriodMs_; }
    // Newest tick received, or -1.
    std::int64_t LatestTick() const { return newestTick_; }
    double OffsetMs() const { return minOffsetMs_; }

    // Records a frame for `tick`, received at local time `recvMs`, in arrival order. Throws std::invalid_argument for a time that is not
    // finite, here and in Update: one NaN would otherwise hold render time at NaN for good.
    void OnFrame(std::int64_t tick, double recvMs);

    // The server changed its tick period from `tick` on (the PERIOD flag).
    void OnPeriodChange(std::int64_t tick, double periodMs);

    // Forgets every frame, sample and period change (a reconnection).
    void Reset();

    // Advances render time to local time `nowMs`. Call once per rendered frame.
    void Update(double nowMs);

private:
    static constexpr int SampleCapacity = 128;
    static constexpr int MinSamplesForJitter = 4;
    static constexpr int PeriodHistory = 8;

    double ServerMsOf(std::int64_t tick) const;
    void ToTick(double ms);
    void PushSample(double recvMs, double offset);

    std::int64_t renderTick_ = 0;
    double renderFrac_ = 0;

    double initialPeriodMs_;
    double initialDelayMs_;
    double minDelayMs_;
    double delayCeilingMs_;
    double windowMs_;
    double maxRateAdjust_;
    double snapMs_;
    double maxExtrapolationMs_;

    double delayMs_;
    double renderMs_ = 0;
    bool started_ = false;
    double lastUpdateMs_ = 0;
    std::int64_t newestTick_ = -1;

    std::array<double, PeriodHistory> pieceTick_{};
    std::array<double, PeriodHistory> pieceMs_{};
    std::array<double, PeriodHistory> piecePeriod_{};
    int pieceCount_ = 0;
    double pendingPeriodMs_;

    std::array<double, SampleCapacity> sampleRecvMs_{};
    std::array<double, SampleCapacity> sampleOffset_{};
    int sampleStart_ = 0;
    int sampleCount_ = 0;
    double minOffsetMs_ = 0;
    std::array<double, SampleCapacity> scratch_{};
};

}  // namespace typhon::client
