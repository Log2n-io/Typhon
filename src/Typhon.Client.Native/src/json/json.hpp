#pragma once

#include <cstdint>
#include <memory>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// A strict, dependency-free JSON reader (RFC 8259) for the catalog a server sends in WELCOME and for test expectations. Not a
// general-purpose library: no writer beyond what the tests need, numbers as binary64 (what JSON.parse and the C# model read), objects
// as ordered member lists with JSON.parse's last-wins rule for a repeated key, and a depth bound so a hostile catalog cannot exhaust
// the stack.
namespace typhon::client::json {

class ParseError : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

enum class Type : std::uint8_t
{
    Null,
    Bool,
    Number,
    String,
    Array,
    Object,
};

class Value {
public:
    using Member = std::pair<std::string, Value>;

    Value() = default;
    static Value MakeBool(bool b);
    static Value MakeNumber(double n);
    static Value MakeString(std::string s);
    static Value MakeArray(std::vector<Value> items = {});
    static Value MakeObject(std::vector<Member> members = {});

    Type GetType() const { return type_; }
    bool IsNull() const { return type_ == Type::Null; }
    bool IsBool() const { return type_ == Type::Bool; }
    bool IsNumber() const { return type_ == Type::Number; }
    bool IsString() const { return type_ == Type::String; }
    bool IsArray() const { return type_ == Type::Array; }
    bool IsObject() const { return type_ == Type::Object; }

    bool AsBool() const { return bool_; }
    double AsNumber() const { return number_; }
    const std::string& AsString() const { return string_; }
    const std::vector<Value>& Items() const { return items_; }
    std::vector<Value>& Items() { return items_; }
    const std::vector<Member>& Members() const { return members_; }
    std::vector<Member>& Members() { return members_; }

    // The member named `key` for editing (the last one when repeated), or nullptr.
    Value* FindMutable(std::string_view key);

    // The member named `key` (the last one when repeated), or nullptr.
    const Value* Find(std::string_view key) const;

    // Sets a member, replacing an existing one. For building expectations in tests.
    void Set(std::string key, Value value);

    // Structural equality: numbers by value, objects by member set regardless of order.
    bool Equals(const Value& other) const;

    // Compact JSON text, for diagnostics.
    std::string Dump() const;

private:
    Type type_ = Type::Null;
    bool bool_ = false;
    double number_ = 0;
    std::string string_;
    std::vector<Value> items_;
    std::vector<Member> members_;
};

// Parses one JSON value spanning the whole text, which must be valid UTF-8. Throws ParseError.
Value Parse(std::string_view text);

}  // namespace typhon::client::json
