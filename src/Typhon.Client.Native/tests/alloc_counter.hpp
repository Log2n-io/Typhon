#pragma once

#include <cstddef>
#include <utility>

// Counts allocations through both doors: the SDK's allocator hooks and a replaced global operator new (which sees whatever does not go through
// the hooks). Linking alloc_counter.cpp replaces the program's global allocation functions; they count only while a measurement runs.
namespace typhon::test {

struct AllocationCount {
    std::size_t news = 0;
    std::size_t hookAllocs = 0;
};

// Installs counting allocator hooks for the object's lifetime; everything allocated under them is freed under them.
struct CountingHooks {
    CountingHooks();
    ~CountingHooks();
};

void StartCounting();
// Suspends counting (a callback whose own allocations are not the SDK's) and resumes it.
void PauseCounting();
void ResumeCounting();
AllocationCount StopCounting();

// Counts the allocations `body` makes.
template <class Body>
AllocationCount CountAllocations(Body&& body)
{
    StartCounting();
    body();
    return StopCounting();
}

}  // namespace typhon::test
