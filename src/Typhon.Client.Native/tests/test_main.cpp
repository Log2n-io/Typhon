#include <cstdio>
#include <cstring>
#include <exception>

#include "test_framework.hpp"

namespace typhon::test {

std::vector<TestCase>& Registry()
{
    static std::vector<TestCase> registry;
    return registry;
}

void Fail(const char* file, int line, const std::string& what)
{
    throw TestFailure(std::string(file) + ":" + std::to_string(line) + ": " + what);
}

}  // namespace typhon::test

int main(int argc, char** argv)
{
    const char* filter = argc > 1 ? argv[1] : nullptr;
    int run = 0;
    int failed = 0;
    for (const auto& test : typhon::test::Registry())
    {
        if (filter != nullptr && std::strstr(test.name, filter) == nullptr)
        {
            continue;
        }

        run++;
        try
        {
            test.body();
        }
        catch (const typhon::test::TestFailure& e)
        {
            failed++;
            std::printf("FAIL %s\n  %s\n", test.name, e.what());
            continue;
        }
        catch (const std::exception& e)
        {
            failed++;
            std::printf("FAIL %s\n  unexpected exception: %s\n", test.name, e.what());
            continue;
        }

        std::printf("pass %s\n", test.name);
    }

    std::printf("\n%d test(s), %d failed\n", run, failed);
    return failed == 0 && run > 0 ? 0 : 1;
}
