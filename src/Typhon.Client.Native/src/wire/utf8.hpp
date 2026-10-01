#pragma once

#include <cstdint>
#include <span>
#include <string>
#include <string_view>

namespace typhon::client::utf8 {

// Whether `bytes` is valid UTF-8, strictly: no overlong forms, no surrogates, nothing above U+10FFFF — what .NET's strict decoder and
// a fatal TextDecoder accept. A leading U+FEFF is kept, not stripped.
bool IsValid(std::span<const std::uint8_t> bytes);

// Compares two valid UTF-8 strings in UTF-16 code-unit order — the order C#'s ordinal comparison and JavaScript's `<` use, which is
// the catalog's canonical order. It differs from byte order only between U+E000..U+FFFF and the supplementary planes.
int CompareUtf16(std::string_view a, std::string_view b);

// Appends the UTF-8 encoding of a code point (assumed valid: not a surrogate, at most U+10FFFF).
void AppendCodePoint(std::string& out, std::uint32_t codePoint);

// The longest prefix of `text` (valid UTF-8) whose length fits `maxBytes`, cut between code points.
std::string_view Truncate(std::string_view text, std::size_t maxBytes);

}  // namespace typhon::client::utf8
