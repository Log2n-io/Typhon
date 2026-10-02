#include "clock/clock.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>

namespace typhon::client {

namespace {

double Clamp(double value, double min, double max) { return value < min ? min : value > max ? max : value; }

// The largest local time accepted, about 31 700 years: far past any monotonic clock, and far inside what render ticks convert to exactly.
constexpr double MaxTimeMs = 1e15;

}  // namespace

Clock::Clock(const ClockOptions& options)
    : initialPeriodMs_(options.tickPeriodMs),
      initialDelayMs_(Clamp(options.initialDelayMs, options.minDelayMs, options.maxDelayMs)),
      minDelayMs_(options.minDelayMs),
      delayCeilingMs_(options.maxDelayMs),
      windowMs_(options.windowMs),
      maxRateAdjust_(options.maxRateAdjust),
      snapMs_(options.snapMs),
      maxExtrapolationMs_(options.maxExtrapolationMs),
      delayMs_(initialDelayMs_),
      pendingPeriodMs_(options.tickPeriodMs)
{
    if (!(options.tickPeriodMs > 0))
    {
        throw std::invalid_argument("tickPeriodMs must be positive");
    }
}

void Clock::OnFrame(std::int64_t tick, double recvMs)
{
    if (!(std::abs(recvMs) < MaxTimeMs))
    {
        throw std::invalid_argument("recvMs must be finite and within ±10^15 ms");
    }

    if (pieceCount_ == 0)
    {
        pieceTick_[0] = static_cast<double>(tick);
        pieceMs_[0] = 0;
        piecePeriod_[0] = pendingPeriodMs_;
        pieceCount_ = 1;
    }

    newestTick_ = std::max(newestTick_, tick);
    PushSample(recvMs, recvMs - ServerMsOf(tick));
}

void Clock::OnPeriodChange(std::int64_t tick, double periodMs)
{
    if (!(periodMs > 0) || !std::isfinite(periodMs))
    {
        throw std::invalid_argument("periodMs must be positive");
    }

    if (pieceCount_ == 0)
    {
        pendingPeriodMs_ = periodMs;
        return;
    }

    const double ms = ServerMsOf(tick);
    if (pieceCount_ == PeriodHistory)
    {
        std::copy(pieceTick_.begin() + 1, pieceTick_.end(), pieceTick_.begin());
        std::copy(pieceMs_.begin() + 1, pieceMs_.end(), pieceMs_.begin());
        std::copy(piecePeriod_.begin() + 1, piecePeriod_.end(), piecePeriod_.begin());
        pieceCount_--;
    }

    pieceTick_[pieceCount_] = static_cast<double>(tick);
    pieceMs_[pieceCount_] = ms;
    piecePeriod_[pieceCount_] = periodMs;
    pieceCount_++;
}

void Clock::Reset()
{
    newestTick_ = -1;
    started_ = false;
    sampleCount_ = 0;
    sampleStart_ = 0;
    minOffsetMs_ = 0;
    delayMs_ = initialDelayMs_;
    pendingPeriodMs_ = initialPeriodMs_;
    pieceCount_ = 0;
    renderMs_ = 0;
    renderTick_ = 0;
    renderFrac_ = 0;
}

void Clock::Update(double nowMs)
{
    if (!(std::abs(nowMs) < MaxTimeMs))
    {
        throw std::invalid_argument("nowMs must be finite and within ±10^15 ms");
    }

    if (pieceCount_ == 0 || sampleCount_ == 0)
    {
        return;
    }

    const double target = nowMs - minOffsetMs_ - delayMs_;
    if (!started_)
    {
        renderMs_ = target;
        started_ = true;
    }
    else
    {
        const double elapsed = std::max(0.0, nowMs - lastUpdateMs_);
        const double predicted = renderMs_ + elapsed;
        const double error = target - predicted;
        if (error >= snapMs_)
        {
            renderMs_ = target;
        }
        else if (error > -snapMs_)
        {
            const double correction = Clamp((error / TickPeriodMs()) * 0.5, -maxRateAdjust_, maxRateAdjust_);
            renderMs_ = predicted + elapsed * correction;
        }
        // Otherwise render time is far ahead of the target: hold.
    }

    lastUpdateMs_ = nowMs;
    const double limit = ServerMsOf(newestTick_) + maxExtrapolationMs_;
    renderMs_ = std::min(renderMs_, limit);
    ToTick(renderMs_);
}

double Clock::ServerMsOf(std::int64_t tick) const
{
    int p = pieceCount_ - 1;
    while (p > 0 && static_cast<double>(tick) < pieceTick_[p])
    {
        p--;
    }

    return pieceMs_[p] + (static_cast<double>(tick) - pieceTick_[p]) * piecePeriod_[p];
}

void Clock::ToTick(double ms)
{
    int p = pieceCount_ - 1;
    while (p > 0 && ms < pieceMs_[p])
    {
        p--;
    }

    const double rel = (ms - pieceMs_[p]) / piecePeriod_[p];
    const double whole = std::floor(rel);
    renderTick_ = static_cast<std::int64_t>(pieceTick_[p] + whole);
    renderFrac_ = rel - whole;
}

void Clock::PushSample(double recvMs, double offset)
{
    const int index = (sampleStart_ + sampleCount_) % SampleCapacity;
    sampleRecvMs_[index] = recvMs;
    sampleOffset_[index] = offset;
    if (sampleCount_ < SampleCapacity)
    {
        sampleCount_++;
    }
    else
    {
        sampleStart_ = (sampleStart_ + 1) % SampleCapacity;
    }

    while (sampleCount_ > 1 && recvMs - sampleRecvMs_[sampleStart_] > windowMs_)
    {
        sampleStart_ = (sampleStart_ + 1) % SampleCapacity;
        sampleCount_--;
    }

    double min = std::numeric_limits<double>::infinity();
    for (int i = 0; i < sampleCount_; i++)
    {
        min = std::min(min, sampleOffset_[(sampleStart_ + i) % SampleCapacity]);
    }

    minOffsetMs_ = min;
    const int n = sampleCount_;
    if (n < MinSamplesForJitter)
    {
        return;
    }

    // Insertion sort of the jitter samples: n <= 128, no allocation.
    for (int i = 0; i < n; i++)
    {
        const double v = sampleOffset_[(sampleStart_ + i) % SampleCapacity] - min;
        int j = i;
        while (j > 0 && scratch_[j - 1] > v)
        {
            scratch_[j] = scratch_[j - 1];
            j--;
        }

        scratch_[j] = v;
    }

    // Nearest-rank 95th percentile.
    const double p95 = scratch_[static_cast<std::size_t>(std::ceil(n * 0.95)) - 1];
    delayMs_ = Clamp(TickPeriodMs() + p95, minDelayMs_, delayCeilingMs_);
}

}  // namespace typhon::client
