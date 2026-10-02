// The JSON reader's numbers at the edges of binary64, read the way JSON.parse reads them: an overflow is ±Infinity, an underflow ±0 — decided
// by the magnitude the digits spell, not by the sign of the exponent alone.

#include <cmath>
#include <string>

#include "json/json.hpp"
#include "test_framework.hpp"

using typhon::client::json::Parse;

namespace {

double Number(const std::string& text) { return Parse(text).AsNumber(); }

}  // namespace

TEST(Json_OverflowIsInfinityAndUnderflowIsZero)
{
    CHECK(Number("1e400") == HUGE_VAL);
    CHECK(Number("-1e400") == -HUGE_VAL);
    CHECK(Number("1e-400") == 0.0);
    CHECK(std::signbit(Number("-1e-400")));
    CHECK(Number("123.4e-400") == 0.0);
}

TEST(Json_TheDigitsDecideWithoutAnExponent)
{
    // 0.(400 zeros)1 has no exponent at all, and underflows.
    CHECK(Number("0." + std::string(400, '0') + "1") == 0.0);
    CHECK(Number("1" + std::string(400, '0')) == HUGE_VAL);
    // Digits against the exponent: 10^400 x 10^-5 still overflows; 10^-400 x 10^5 still underflows.
    CHECK(Number("1" + std::string(400, '0') + "e-5") == HUGE_VAL);
    CHECK(Number("0." + std::string(399, '0') + "1e5") == 0.0);
    // In range: untouched.
    CHECK(Number("1.5e300") == 1.5e300);
    CHECK(Number("2.5e-300") == 2.5e-300);
}
