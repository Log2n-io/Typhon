#pragma once

#include <functional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

// A minimal test runner: no dependency, one executable, `ctest` runs it. A check that fails throws, ending its test; the runner reports
// every failed test and exits non-zero. A test name filter is the first command-line argument (a substring).
namespace typhon::test {

struct TestFailure : std::runtime_error {
    using std::runtime_error::runtime_error;
};

struct TestCase {
    const char* name;
    void (*body)();
};

std::vector<TestCase>& Registry();

struct Registrar {
    Registrar(const char* name, void (*body)()) { Registry().push_back({name, body}); }
};

[[noreturn]] void Fail(const char* file, int line, const std::string& what);

}  // namespace typhon::test

#define TYPHON_CONCAT_INNER(a, b) a##b
#define TYPHON_CONCAT(a, b) TYPHON_CONCAT_INNER(a, b)

#define TEST(name)                                                                       \
    static void name();                                                                  \
    static const ::typhon::test::Registrar TYPHON_CONCAT(name, _registrar)(#name, name); \
    static void name()

#define CHECK(condition)                                                  \
    do                                                                    \
    {                                                                     \
        if (!(condition))                                                 \
        {                                                                 \
            ::typhon::test::Fail(__FILE__, __LINE__, "CHECK(" #condition ")"); \
        }                                                                 \
    } while (0)

#define CHECK_MSG(condition, message)                                                                   \
    do                                                                                                  \
    {                                                                                                   \
        if (!(condition))                                                                               \
        {                                                                                               \
            std::ostringstream typhon_stream;                                                           \
            typhon_stream << "CHECK(" #condition "): " << message;                                      \
            ::typhon::test::Fail(__FILE__, __LINE__, typhon_stream.str());                              \
        }                                                                                               \
    } while (0)

#define CHECK_EQ(actual, expected)                                                                                       \
    do                                                                                                                   \
    {                                                                                                                    \
        const auto& typhon_a = (actual);                                                                                 \
        const auto& typhon_e = (expected);                                                                               \
        if (!(typhon_a == typhon_e))                                                                                     \
        {                                                                                                                \
            std::ostringstream typhon_stream;                                                                            \
            typhon_stream << "CHECK_EQ(" #actual ", " #expected "): got " << typhon_a << ", expected " << typhon_e;      \
            ::typhon::test::Fail(__FILE__, __LINE__, typhon_stream.str());                                               \
        }                                                                                                                \
    } while (0)

// Runs `action` and checks it throws an exception of type E.
#define CHECK_THROWS(E, action)                                                                     \
    do                                                                                              \
    {                                                                                               \
        bool typhon_threw = false;                                                                  \
        try                                                                                         \
        {                                                                                           \
            action;                                                                                 \
        }                                                                                           \
        catch (const E&)                                                                            \
        {                                                                                           \
            typhon_threw = true;                                                                    \
        }                                                                                           \
        if (!typhon_threw)                                                                          \
        {                                                                                           \
            ::typhon::test::Fail(__FILE__, __LINE__, "CHECK_THROWS(" #E ", " #action "): no throw"); \
        }                                                                                           \
    } while (0)
