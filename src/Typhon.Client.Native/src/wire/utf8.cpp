#include "wire/utf8.hpp"

namespace typhon::client::utf8 {

namespace {

// Decodes the code point at `at`, advancing it; the input is valid UTF-8.
std::uint32_t Next(std::string_view s, std::size_t& at)
{
    const auto b0 = static_cast<std::uint8_t>(s[at]);
    if (b0 < 0x80)
    {
        at += 1;
        return b0;
    }

    if (b0 < 0xE0)
    {
        const std::uint32_t cp = ((b0 & 0x1Fu) << 6) | (static_cast<std::uint8_t>(s[at + 1]) & 0x3Fu);
        at += 2;
        return cp;
    }

    if (b0 < 0xF0)
    {
        const std::uint32_t cp = ((b0 & 0x0Fu) << 12) | ((static_cast<std::uint8_t>(s[at + 1]) & 0x3Fu) << 6)
                                 | (static_cast<std::uint8_t>(s[at + 2]) & 0x3Fu);
        at += 3;
        return cp;
    }

    const std::uint32_t cp = ((b0 & 0x07u) << 18) | ((static_cast<std::uint8_t>(s[at + 1]) & 0x3Fu) << 12)
                             | ((static_cast<std::uint8_t>(s[at + 2]) & 0x3Fu) << 6) | (static_cast<std::uint8_t>(s[at + 3]) & 0x3Fu);
    at += 4;
    return cp;
}

// The code point's first UTF-16 code unit, and whether a second follows.
std::uint32_t FirstUnit(std::uint32_t cp) { return cp < 0x10000 ? cp : 0xD800 + ((cp - 0x10000) >> 10); }

std::uint32_t SecondUnit(std::uint32_t cp) { return 0xDC00 + ((cp - 0x10000) & 0x3FF); }

}  // namespace

bool IsValid(std::span<const std::uint8_t> bytes)
{
    std::size_t i = 0;
    const std::size_t n = bytes.size();
    while (i < n)
    {
        const std::uint8_t b0 = bytes[i];
        if (b0 < 0x80)
        {
            i++;
            continue;
        }

        int length;
        std::uint32_t cp;
        std::uint32_t min;
        if ((b0 & 0xE0) == 0xC0)
        {
            length = 2;
            cp = b0 & 0x1Fu;
            min = 0x80;
        }
        else if ((b0 & 0xF0) == 0xE0)
        {
            length = 3;
            cp = b0 & 0x0Fu;
            min = 0x800;
        }
        else if ((b0 & 0xF8) == 0xF0)
        {
            length = 4;
            cp = b0 & 0x07u;
            min = 0x10000;
        }
        else
        {
            return false;
        }

        if (i + static_cast<std::size_t>(length) > n)
        {
            return false;
        }

        for (int k = 1; k < length; k++)
        {
            const std::uint8_t b = bytes[i + static_cast<std::size_t>(k)];
            if ((b & 0xC0) != 0x80)
            {
                return false;
            }

            cp = (cp << 6) | (b & 0x3Fu);
        }

        if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
        {
            return false;
        }

        i += static_cast<std::size_t>(length);
    }

    return true;
}

int CompareUtf16(std::string_view a, std::string_view b)
{
    std::size_t i = 0;
    std::size_t j = 0;
    while (i < a.size() && j < b.size())
    {
        const std::uint32_t ca = Next(a, i);
        const std::uint32_t cb = Next(b, j);
        if (ca == cb)
        {
            continue;
        }

        const std::uint32_t ua = FirstUnit(ca);
        const std::uint32_t ub = FirstUnit(cb);
        if (ua != ub)
        {
            return ua < ub ? -1 : 1;
        }

        // Same high surrogate: the low ones decide.
        return SecondUnit(ca) < SecondUnit(cb) ? -1 : 1;
    }

    if (i < a.size())
    {
        return 1;
    }

    return j < b.size() ? -1 : 0;
}

void AppendCodePoint(std::string& out, std::uint32_t cp)
{
    if (cp < 0x80)
    {
        out.push_back(static_cast<char>(cp));
    }
    else if (cp < 0x800)
    {
        out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
    else if (cp < 0x10000)
    {
        out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
    else
    {
        out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
}

std::string_view Truncate(std::string_view text, std::size_t maxBytes)
{
    if (text.size() <= maxBytes)
    {
        return text;
    }

    std::size_t cut = maxBytes;
    // Back up over continuation bytes, so the cut never splits a code point.
    while (cut > 0 && (static_cast<std::uint8_t>(text[cut]) & 0xC0) == 0x80)
    {
        cut--;
    }

    return text.substr(0, cut);
}

}  // namespace typhon::client::utf8
