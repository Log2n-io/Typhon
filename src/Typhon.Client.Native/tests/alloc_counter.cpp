#include "alloc_counter.hpp"

#include <atomic>
#include <cstdlib>
#include <new>

#include "store/memory.hpp"

namespace {

std::atomic<bool> g_counting{false};
std::atomic<std::size_t> g_news{0};
std::atomic<std::size_t> g_hookAllocs{0};

void* Raw(std::size_t size, std::size_t alignment)
{
    if (size == 0)
    {
        size = 1;
    }

    alignment = alignment < alignof(std::max_align_t) ? alignof(std::max_align_t) : alignment;
#ifdef _WIN32
    void* p = _aligned_malloc(size, alignment);
#else
    void* p = std::aligned_alloc(alignment, (size + alignment - 1) / alignment * alignment);
#endif
    if (p == nullptr)
    {
        throw std::bad_alloc();
    }

    return p;
}

void RawFree(void* p) noexcept
{
#ifdef _WIN32
    _aligned_free(p);
#else
    std::free(p);
#endif
}

void* Counted(std::size_t size, std::size_t alignment)
{
    if (g_counting.load(std::memory_order_relaxed))
    {
        g_news.fetch_add(1, std::memory_order_relaxed);
    }

    return Raw(size, alignment);
}

void* HookAllocCounting(std::size_t size, std::size_t alignment, void*)
{
    if (g_counting.load(std::memory_order_relaxed))
    {
        g_hookAllocs.fetch_add(1, std::memory_order_relaxed);
    }

    return Raw(size, alignment < 64 ? 64 : alignment);
}

void HookFreeCounting(void* p, void*) { RawFree(p); }

}  // namespace

namespace typhon::test {

CountingHooks::CountingHooks() { client::SetAllocatorHooks({HookAllocCounting, HookFreeCounting, nullptr}); }

CountingHooks::~CountingHooks() { client::SetAllocatorHooks({}); }

void StartCounting()
{
    g_news = 0;
    g_hookAllocs = 0;
    g_counting = true;
}

void PauseCounting() { g_counting = false; }

void ResumeCounting() { g_counting = true; }

AllocationCount StopCounting()
{
    g_counting = false;
    return {g_news.load(), g_hookAllocs.load()};
}

}  // namespace typhon::test

// The replaced global allocation functions: counted while a measurement runs, otherwise plain.
void* operator new(std::size_t size) { return Counted(size, alignof(std::max_align_t)); }
void* operator new[](std::size_t size) { return Counted(size, alignof(std::max_align_t)); }
void* operator new(std::size_t size, std::align_val_t alignment) { return Counted(size, static_cast<std::size_t>(alignment)); }
void* operator new[](std::size_t size, std::align_val_t alignment) { return Counted(size, static_cast<std::size_t>(alignment)); }
void operator delete(void* p) noexcept { RawFree(p); }
void operator delete[](void* p) noexcept { RawFree(p); }
void operator delete(void* p, std::size_t) noexcept { RawFree(p); }
void operator delete[](void* p, std::size_t) noexcept { RawFree(p); }
void operator delete(void* p, std::align_val_t) noexcept { RawFree(p); }
void operator delete[](void* p, std::align_val_t) noexcept { RawFree(p); }
void operator delete(void* p, std::size_t, std::align_val_t) noexcept { RawFree(p); }
void operator delete[](void* p, std::size_t, std::align_val_t) noexcept { RawFree(p); }
