#pragma once

#include <cstdint>

// The codec arithmetic of 03-wire-protocol.md § 12.1 (W1-W10), bit-identical with the C# reference (Typhon.Protocol/Wire/WireMath.cs)
// and the TypeScript SDK (protocol/math.ts).
//
// The rules that make that true (W1): everything is binary64; ties round half away from zero; values are clamped in double before any
// integer conversion; only + - * /, sqrt, floor, abs and comparisons; powers of two from a table built by doubling. Each formula is
// written left to right, one IEEE operation per step: reordering a product changes the last bit. The build disables FP contraction
// (/fp:contract-, -ffp-contract=off) so the compiler cannot fuse a multiply and an add behind our back.
namespace typhon::client::math {

inline constexpr double Tau = 6.283185307179586;           // C# Math.Tau, TS 2 * Math.PI
inline constexpr double Sqrt1_2 = 0.7071067811865476;      // C# Math.Sqrt(0.5), TS Math.SQRT1_2 (W8)

// 2^e as an exact double, for e in [-64, 64].
double Pow2(int e);

// Rounds half away from zero (rha).
double RoundHalfAway(double x);

// 2^(b-1) - 1: the largest magnitude of a symmetric signed code.
double SymmetricLimit(int bits);

// 2^b - 1: the top code of an unsigned quantizer.
double UnsignedTop(int bits);

// quant, pos (W2, W3)
double QuantStep(double min, double max, int bits);
double EncodeQuant(double v, double min, double step, double top);
double DecodeQuant(double q, double min, double step);

// vec (W4)
double EncodeVec(double v, double scale, double limit);
double DecodeVec(double q, double scale, double limit);

// vel (W5)
double EncodeVel(double d, double unit, double limit);
double DecodeVel(double q, double unit, double limit);

// unorm, snorm (W6)
double EncodeUnorm(double v, double top);
double DecodeUnorm(double q, double top);
double EncodeSnorm(double v, double limit);
double DecodeSnorm(double q, double limit);

// angle (W7)
double EncodeAngle(double theta, int bits);
double DecodeAngle(double q, int bits);

// quat3 (W8)
std::uint32_t EncodeQuat3(double x, double y, double z, double w);
void DecodeQuat3(std::uint32_t bits, double* out);

// f16 (W10)
std::uint16_t EncodeF16(double x);
double DecodeF16(std::uint16_t bits);

// tickLo (W9)
std::uint32_t DecodeTickLo(std::uint32_t low, std::uint32_t frameTick);

}  // namespace typhon::client::math
