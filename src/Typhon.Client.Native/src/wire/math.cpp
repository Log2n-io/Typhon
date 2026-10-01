#include "wire/math.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <limits>

namespace typhon::client::math {

namespace {

constexpr int Pow2Bias = 64;

std::array<double, 2 * Pow2Bias + 1> BuildPow2()
{
    std::array<double, 2 * Pow2Bias + 1> table{};
    table[Pow2Bias] = 1;
    for (int i = 1; i <= Pow2Bias; i++)
    {
        table[Pow2Bias + i] = table[Pow2Bias + i - 1] * 2;
        table[Pow2Bias - i] = table[Pow2Bias - i + 1] / 2;
    }

    return table;
}

const std::array<double, 2 * Pow2Bias + 1> Pow2Table = BuildPow2();

constexpr double Infinity = std::numeric_limits<double>::infinity();

// Ties to even over m < 2^11, where every step is exact.
double RoundHalfEven(double m)
{
    const double f = std::floor(m);
    const double d = m - f;
    return d > 0.5 ? f + 1 : d < 0.5 ? f : f + std::fmod(f, 2.0);
}

// Math.round in JavaScript for x > 0 — the only place the formulas use it — which is round half away from zero there.
double RoundHalfUp(double x) { return std::round(x); }

}  // namespace

double Pow2(int e) { return Pow2Table[static_cast<std::size_t>(e + Pow2Bias)]; }

double RoundHalfAway(double x) { return std::round(x); }

double SymmetricLimit(int bits) { return Pow2(bits - 1) - 1; }

double UnsignedTop(int bits) { return Pow2(bits) - 1; }

double QuantStep(double min, double max, int bits) { return (max - min) / Pow2(bits); }

double EncodeQuant(double v, double min, double step, double top)
{
    const double x = (v - min) / step;
    if (!(x > 0))
    {
        return 0;
    }

    const double r = RoundHalfUp(x);
    return r > top ? top : r;
}

double DecodeQuant(double q, double min, double step) { return min + q * step; }

double EncodeVec(double v, double scale, double limit)
{
    if (v != v)
    {
        return 0;
    }

    const double r = RoundHalfAway(v / scale);
    return r > limit ? limit : r < -limit ? -limit : r;
}

double DecodeVec(double q, double scale, double limit) { return (q < -limit ? -limit : q) * scale; }

double EncodeVel(double d, double unit, double limit)
{
    const double x = d / unit;
    if (x != x)
    {
        return 0;
    }

    const double r = RoundHalfAway(x);
    return r > limit ? limit : r < -limit ? -limit : r;
}

double DecodeVel(double q, double unit, double limit) { return (q < -limit ? -limit : q) * unit; }

double EncodeUnorm(double v, double top)
{
    const double x = v * top;
    if (!(x > 0))
    {
        return 0;
    }

    const double r = RoundHalfUp(x);
    return r > top ? top : r;
}

// A division, not a multiplication by the reciprocal, which differs in the last bit.
double DecodeUnorm(double q, double top) { return q / top; }

double EncodeSnorm(double v, double limit)
{
    if (v != v)
    {
        return 0;
    }

    const double c = v < -1 ? -1 : v > 1 ? 1 : v;
    return RoundHalfAway(c * limit);
}

double DecodeSnorm(double q, double limit)
{
    const double x = q / limit;
    return x < -1 ? -1 : x;
}

double EncodeAngle(double theta, int bits)
{
    if (!std::isfinite(theta))
    {
        return 0;
    }

    const double p = Pow2(bits);
    const double k = RoundHalfAway((theta * p) / Tau);
    if (!(std::fabs(k) <= 9007199254740992.0))
    {
        return 0;
    }

    return k - p * std::floor((k + Pow2(bits - 1)) / p);
}

double DecodeAngle(double q, int bits) { return (q * Tau) / Pow2(bits); }

std::uint32_t EncodeQuat3(double x, double y, double z, double w)
{
    const double n = std::sqrt(x * x + y * y + z * z + w * w);
    double c[4];
    if (!(n > 0) || n == Infinity)
    {
        c[0] = 0;
        c[1] = 0;
        c[2] = 0;
        c[3] = 1;
    }
    else
    {
        c[0] = x / n;
        c[1] = y / n;
        c[2] = z / n;
        c[3] = w / n;
    }

    int largest = 0;
    for (int k = 1; k < 4; k++)
    {
        if (std::fabs(c[k]) > std::fabs(c[largest]))
        {
            largest = k;
        }
    }

    if (c[largest] < 0)
    {
        for (double& v : c)
        {
            v = -v;
        }
    }

    std::uint32_t bits = static_cast<std::uint32_t>(largest);
    int shift = 2;
    for (int k = 0; k < 4; k++)
    {
        if (k == largest)
        {
            continue;
        }

        const double r = RoundHalfAway((c[k] / Sqrt1_2) * 511);
        const double e = r > 511 ? 511 : r < -511 ? -511 : r;
        bits |= (static_cast<std::uint32_t>(static_cast<std::int32_t>(e)) & 0x3FFu) << shift;
        shift += 10;
    }

    return bits;
}

void DecodeQuat3(std::uint32_t bits, double* out)
{
    const int largest = static_cast<int>(bits & 3u);
    double sum = 0;
    int shift = 2;
    for (int k = 0; k < 4; k++)
    {
        if (k == largest)
        {
            continue;
        }

        // Sign-extend the 10-bit component.
        int e = static_cast<int>((bits >> shift) & 0x3FFu);
        if (e >= 512)
        {
            e -= 1024;
        }

        if (e < -511)
        {
            e = -511;
        }

        const double v = (static_cast<double>(e) / 511) * Sqrt1_2;
        out[k] = v;
        sum += v * v;
        shift += 10;
    }

    const double rest = 1 - sum;
    out[largest] = std::sqrt(rest > 0 ? rest : 0);
}

std::uint16_t EncodeF16(double x)
{
    if (x != x)
    {
        return 0x7E00;
    }

    const std::uint16_t s = (x < 0 || (x == 0 && std::signbit(x))) ? 0x8000 : 0;
    const double a = std::fabs(x);
    if (a == Infinity)
    {
        return static_cast<std::uint16_t>(s | 0x7C00);
    }

    if (a < Pow2(-14))
    {
        // Subnormal; r = 1024 lands on the smallest normal.
        return static_cast<std::uint16_t>(s | static_cast<std::uint16_t>(RoundHalfEven(a * Pow2(24))));
    }

    int e = static_cast<int>((std::bit_cast<std::uint64_t>(a) >> 52) & 0x7FF) - 1023;
    if (e > 15)
    {
        return static_cast<std::uint16_t>(s | 0x7C00);
    }

    double r = RoundHalfEven(a * Pow2(10 - e));
    if (r == 2048)
    {
        r = 1024;
        e += 1;
    }

    if (e > 15)
    {
        return static_cast<std::uint16_t>(s | 0x7C00);
    }

    return static_cast<std::uint16_t>(s | ((e + 15) << 10) | (static_cast<int>(r) - 1024));
}

double DecodeF16(std::uint16_t bits)
{
    const int e = (bits >> 10) & 31;
    const int f = bits & 0x3FF;
    double v;
    if (e == 0)
    {
        v = f / 16777216.0;
    }
    else if (e == 31)
    {
        if (f != 0)
        {
            // The canonical NaN, whatever the pattern's sign.
            return std::numeric_limits<double>::quiet_NaN();
        }

        v = Infinity;
    }
    else
    {
        v = (1024 + f) * Pow2(e - 25);
    }

    return (bits & 0x8000) != 0 ? -v : v;
}

std::uint32_t DecodeTickLo(std::uint32_t low, std::uint32_t frameTick) { return frameTick - ((frameTick - low) & 0xFFFFu); }

}  // namespace typhon::client::math
