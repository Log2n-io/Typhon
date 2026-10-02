#include "json/json.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdio>

#include "wire/utf8.hpp"

namespace typhon::client::json {

namespace {

constexpr int MaxDepth = 64;

class Parser {
public:
    explicit Parser(std::string_view text) : text_(text) {}

    Value ParseDocument()
    {
        SkipWhitespace();
        Value value = ParseValue(0);
        SkipWhitespace();
        if (pos_ != text_.size())
        {
            Fail("unexpected content after the value");
        }

        return value;
    }

private:
    [[noreturn]] void Fail(const std::string& what) const { throw ParseError(what + " at offset " + std::to_string(pos_)); }

    void SkipWhitespace()
    {
        while (pos_ < text_.size())
        {
            const char c = text_[pos_];
            if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
            {
                break;
            }

            pos_++;
        }
    }

    bool Consume(std::string_view literal)
    {
        if (text_.substr(pos_, literal.size()) == literal)
        {
            pos_ += literal.size();
            return true;
        }

        return false;
    }

    Value ParseValue(int depth)
    {
        if (depth > MaxDepth)
        {
            Fail("nesting deeper than " + std::to_string(MaxDepth));
        }

        if (pos_ >= text_.size())
        {
            Fail("unexpected end of input");
        }

        switch (text_[pos_])
        {
            case '{':
                return ParseObject(depth);
            case '[':
                return ParseArray(depth);
            case '"':
                return Value::MakeString(ParseString());
            case 't':
                if (Consume("true"))
                {
                    return Value::MakeBool(true);
                }

                break;
            case 'f':
                if (Consume("false"))
                {
                    return Value::MakeBool(false);
                }

                break;
            case 'n':
                if (Consume("null"))
                {
                    return {};
                }

                break;
            default:
                return Value::MakeNumber(ParseNumber());
        }

        Fail("invalid literal");
    }

    Value ParseObject(int depth)
    {
        pos_++;
        std::vector<Value::Member> members;
        SkipWhitespace();
        if (pos_ < text_.size() && text_[pos_] == '}')
        {
            pos_++;
            return Value::MakeObject(std::move(members));
        }

        for (;;)
        {
            SkipWhitespace();
            if (pos_ >= text_.size() || text_[pos_] != '"')
            {
                Fail("expected a member name");
            }

            std::string key = ParseString();
            SkipWhitespace();
            if (pos_ >= text_.size() || text_[pos_] != ':')
            {
                Fail("expected ':'");
            }

            pos_++;
            SkipWhitespace();
            Value value = ParseValue(depth + 1);
            members.emplace_back(std::move(key), std::move(value));
            SkipWhitespace();
            if (pos_ < text_.size() && text_[pos_] == ',')
            {
                pos_++;
                continue;
            }

            if (pos_ < text_.size() && text_[pos_] == '}')
            {
                pos_++;
                return Value::MakeObject(std::move(members));
            }

            Fail("expected ',' or '}'");
        }
    }

    Value ParseArray(int depth)
    {
        pos_++;
        std::vector<Value> items;
        SkipWhitespace();
        if (pos_ < text_.size() && text_[pos_] == ']')
        {
            pos_++;
            return Value::MakeArray(std::move(items));
        }

        for (;;)
        {
            SkipWhitespace();
            items.push_back(ParseValue(depth + 1));
            SkipWhitespace();
            if (pos_ < text_.size() && text_[pos_] == ',')
            {
                pos_++;
                continue;
            }

            if (pos_ < text_.size() && text_[pos_] == ']')
            {
                pos_++;
                return Value::MakeArray(std::move(items));
            }

            Fail("expected ',' or ']'");
        }
    }

    std::uint32_t Hex4()
    {
        if (pos_ + 4 > text_.size())
        {
            Fail("truncated \\u escape");
        }

        std::uint32_t v = 0;
        for (int i = 0; i < 4; i++)
        {
            const char c = text_[pos_++];
            v <<= 4;
            if (c >= '0' && c <= '9')
            {
                v |= static_cast<std::uint32_t>(c - '0');
            }
            else if (c >= 'a' && c <= 'f')
            {
                v |= static_cast<std::uint32_t>(c - 'a' + 10);
            }
            else if (c >= 'A' && c <= 'F')
            {
                v |= static_cast<std::uint32_t>(c - 'A' + 10);
            }
            else
            {
                Fail("invalid \\u escape");
            }
        }

        return v;
    }

    std::string ParseString()
    {
        pos_++;
        std::string out;
        for (;;)
        {
            if (pos_ >= text_.size())
            {
                Fail("unterminated string");
            }

            const char c = text_[pos_++];
            if (c == '"')
            {
                return out;
            }

            if (static_cast<unsigned char>(c) < 0x20)
            {
                Fail("unescaped control character in a string");
            }

            if (c != '\\')
            {
                out.push_back(c);
                continue;
            }

            if (pos_ >= text_.size())
            {
                Fail("truncated escape");
            }

            const char e = text_[pos_++];
            switch (e)
            {
                case '"':
                    out.push_back('"');
                    break;
                case '\\':
                    out.push_back('\\');
                    break;
                case '/':
                    out.push_back('/');
                    break;
                case 'b':
                    out.push_back('\b');
                    break;
                case 'f':
                    out.push_back('\f');
                    break;
                case 'n':
                    out.push_back('\n');
                    break;
                case 'r':
                    out.push_back('\r');
                    break;
                case 't':
                    out.push_back('\t');
                    break;
                case 'u':
                {
                    std::uint32_t cp = Hex4();
                    if (cp >= 0xD800 && cp <= 0xDBFF && text_.substr(pos_, 2) == "\\u")
                    {
                        const std::size_t save = pos_;
                        pos_ += 2;
                        const std::uint32_t low = Hex4();
                        if (low >= 0xDC00 && low <= 0xDFFF)
                        {
                            cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                        }
                        else
                        {
                            pos_ = save;
                        }
                    }

                    // A lone surrogate has no UTF-8 form: it reads as U+FFFD, as a lenient decoder would.
                    if (cp >= 0xD800 && cp <= 0xDFFF)
                    {
                        cp = 0xFFFD;
                    }

                    utf8::AppendCodePoint(out, cp);
                    break;
                }
                default:
                    Fail("invalid escape");
            }
        }
    }

    double ParseNumber()
    {
        const std::size_t start = pos_;
        if (pos_ < text_.size() && text_[pos_] == '-')
        {
            pos_++;
        }

        if (pos_ >= text_.size() || !IsDigit(text_[pos_]))
        {
            Fail("invalid number");
        }

        if (text_[pos_] == '0')
        {
            pos_++;
        }
        else
        {
            while (pos_ < text_.size() && IsDigit(text_[pos_]))
            {
                pos_++;
            }
        }

        if (pos_ < text_.size() && text_[pos_] == '.')
        {
            pos_++;
            if (pos_ >= text_.size() || !IsDigit(text_[pos_]))
            {
                Fail("invalid number fraction");
            }

            while (pos_ < text_.size() && IsDigit(text_[pos_]))
            {
                pos_++;
            }
        }

        if (pos_ < text_.size() && (text_[pos_] == 'e' || text_[pos_] == 'E'))
        {
            pos_++;
            if (pos_ < text_.size() && (text_[pos_] == '+' || text_[pos_] == '-'))
            {
                pos_++;
            }

            if (pos_ >= text_.size() || !IsDigit(text_[pos_]))
            {
                Fail("invalid number exponent");
            }

            while (pos_ < text_.size() && IsDigit(text_[pos_]))
            {
                pos_++;
            }
        }

        double value = 0;
        const char* first = text_.data() + start;
        const char* last = text_.data() + pos_;
        const auto [ptr, ec] = std::from_chars(first, last, value);
        if (ptr != last || (ec != std::errc() && ec != std::errc::result_out_of_range))
        {
            Fail("invalid number");
        }

        if (ec == std::errc::result_out_of_range)
        {
            // JSON.parse reads an overflow as ±Infinity and an underflow as ±0; from_chars reports both as out of range.
            // Which one is decided by the decimal exponent of the leading significant digit, not by the exponent's sign alone: 0.000…1
            // written without an exponent underflows, and 1000…0e-5 overflows.
            const bool negative = text_[start] == '-';
            const std::string_view token(first, static_cast<std::size_t>(last - first));
            const std::size_t e = token.find_first_of("eE");
            const std::string_view mantissa = token.substr(negative ? 1 : 0, e == std::string_view::npos ? std::string_view::npos : e - (negative ? 1 : 0));
            long long exponent = 0;
            if (e != std::string_view::npos)
            {
                // Clamped while read: the exponent only has to say "far beyond the range", never its exact size.
                const bool down = e + 1 < token.size() && token[e + 1] == '-';
                for (std::size_t i = e + 1; i < token.size(); i++)
                {
                    if (IsDigit(token[i]))
                    {
                        exponent = std::min(exponent * 10 + (token[i] - '0'), 1000000LL);
                    }
                }

                exponent = down ? -exponent : exponent;
            }

            const std::size_t dot = mantissa.find('.');
            const std::string_view whole = mantissa.substr(0, dot);
            const std::size_t lead = whole.find_first_not_of('0');
            long long leading = 0;
            if (lead != std::string_view::npos)
            {
                leading = static_cast<long long>(whole.size() - lead) - 1;
            }
            else if (dot != std::string_view::npos)
            {
                const std::string_view fraction = mantissa.substr(dot + 1);
                const std::size_t nonzero = fraction.find_first_not_of('0');
                leading = nonzero == std::string_view::npos ? -1000000LL : -static_cast<long long>(nonzero) - 1;
            }

            const bool tiny = leading + exponent < 0;
            value = tiny ? (negative ? -0.0 : 0.0) : (negative ? -HUGE_VAL : HUGE_VAL);
        }

        return value;
    }

    static bool IsDigit(char c) { return c >= '0' && c <= '9'; }

    std::string_view text_;
    std::size_t pos_ = 0;
};

void Escape(std::string& out, const std::string& s)
{
    out.push_back('"');
    for (const char c : s)
    {
        switch (c)
        {
            case '"':
                out += "\\\"";
                break;
            case '\\':
                out += "\\\\";
                break;
            case '\n':
                out += "\\n";
                break;
            case '\r':
                out += "\\r";
                break;
            case '\t':
                out += "\\t";
                break;
            default:
                if (static_cast<unsigned char>(c) < 0x20)
                {
                    char buffer[8];
                    std::snprintf(buffer, sizeof buffer, "\\u%04x", static_cast<unsigned>(static_cast<unsigned char>(c)));
                    out += buffer;
                }
                else
                {
                    out.push_back(c);
                }
        }
    }

    out.push_back('"');
}

void DumpInto(std::string& out, const Value& v)
{
    switch (v.GetType())
    {
        case Type::Null:
            out += "null";
            break;
        case Type::Bool:
            out += v.AsBool() ? "true" : "false";
            break;
        case Type::Number:
        {
            char buffer[32];
            const auto [ptr, ec] = std::to_chars(buffer, buffer + sizeof buffer, v.AsNumber());
            out.append(buffer, ec == std::errc() ? ptr : buffer);
            break;
        }
        case Type::String:
            Escape(out, v.AsString());
            break;
        case Type::Array:
        {
            out.push_back('[');
            bool first = true;
            for (const Value& item : v.Items())
            {
                if (!first)
                {
                    out.push_back(',');
                }

                first = false;
                DumpInto(out, item);
            }

            out.push_back(']');
            break;
        }
        case Type::Object:
        {
            out.push_back('{');
            bool first = true;
            for (const auto& [key, value] : v.Members())
            {
                if (!first)
                {
                    out.push_back(',');
                }

                first = false;
                Escape(out, key);
                out.push_back(':');
                DumpInto(out, value);
            }

            out.push_back('}');
            break;
        }
    }
}

}  // namespace

Value Value::MakeBool(bool b)
{
    Value v;
    v.type_ = Type::Bool;
    v.bool_ = b;
    return v;
}

Value Value::MakeNumber(double n)
{
    Value v;
    v.type_ = Type::Number;
    v.number_ = n;
    return v;
}

Value Value::MakeString(std::string s)
{
    Value v;
    v.type_ = Type::String;
    v.string_ = std::move(s);
    return v;
}

Value Value::MakeArray(std::vector<Value> items)
{
    Value v;
    v.type_ = Type::Array;
    v.items_ = std::move(items);
    return v;
}

Value Value::MakeObject(std::vector<Member> members)
{
    Value v;
    v.type_ = Type::Object;
    v.members_ = std::move(members);
    return v;
}

const Value* Value::Find(std::string_view key) const
{
    const Value* found = nullptr;
    for (const auto& [name, value] : members_)
    {
        if (name == key)
        {
            found = &value;
        }
    }

    return found;
}

Value* Value::FindMutable(std::string_view key)
{
    Value* found = nullptr;
    for (auto& [name, value] : members_)
    {
        if (name == key)
        {
            found = &value;
        }
    }

    return found;
}

void Value::Set(std::string key, Value value)
{
    for (auto& member : members_)
    {
        if (member.first == key)
        {
            member.second = std::move(value);
            return;
        }
    }

    members_.emplace_back(std::move(key), std::move(value));
}

bool Value::Equals(const Value& other) const
{
    if (type_ != other.type_)
    {
        return false;
    }

    switch (type_)
    {
        case Type::Null:
            return true;
        case Type::Bool:
            return bool_ == other.bool_;
        case Type::Number:
            return number_ == other.number_ || (number_ != number_ && other.number_ != other.number_);
        case Type::String:
            return string_ == other.string_;
        case Type::Array:
            if (items_.size() != other.items_.size())
            {
                return false;
            }

            for (std::size_t i = 0; i < items_.size(); i++)
            {
                if (!items_[i].Equals(other.items_[i]))
                {
                    return false;
                }
            }

            return true;
        case Type::Object:
        {
            // Distinct keys only: last-wins lookups on both sides.
            std::size_t distinct = 0;
            for (std::size_t i = 0; i < members_.size(); i++)
            {
                bool later = false;
                for (std::size_t j = i + 1; j < members_.size(); j++)
                {
                    later = later || members_[j].first == members_[i].first;
                }

                if (later)
                {
                    continue;
                }

                distinct++;
                const Value* theirs = other.Find(members_[i].first);
                if (theirs == nullptr || !members_[i].second.Equals(*theirs))
                {
                    return false;
                }
            }

            std::size_t otherDistinct = 0;
            for (std::size_t i = 0; i < other.members_.size(); i++)
            {
                bool later = false;
                for (std::size_t j = i + 1; j < other.members_.size(); j++)
                {
                    later = later || other.members_[j].first == other.members_[i].first;
                }

                otherDistinct += later ? 0 : 1;
            }

            return distinct == otherDistinct;
        }
    }

    return false;
}

std::string Value::Dump() const
{
    std::string out;
    DumpInto(out, *this);
    return out;
}

Value Parse(std::string_view text) { return Parser(text).ParseDocument(); }

}  // namespace typhon::client::json
