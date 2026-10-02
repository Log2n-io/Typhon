#include "store/memory.hpp"

#include <stdexcept>

namespace typhon::client {

namespace {

// The default heap, cache-line aligned: a column is walked per frame, and no type the SDK stores needs more. One fixed alignment, so
// the free needs no size or alignment from its caller.
constexpr std::size_t DefaultAlignment = 64;

void* DefaultAlloc(std::size_t size, std::size_t, void*) { return ::operator new(size == 0 ? 1 : size, std::align_val_t{DefaultAlignment}); }

void DefaultFree(void* block, void*) { ::operator delete(block, std::align_val_t{DefaultAlignment}); }

// The hooks are set before any client exists (memory.hpp), so one block of memory is never freed by a different hook than allocated it.
AllocatorHooks g_hooks{DefaultAlloc, DefaultFree, nullptr};

}  // namespace

void SetAllocatorHooks(const AllocatorHooks& hooks)
{
    if (hooks.alloc == nullptr && hooks.free == nullptr)
    {
        g_hooks = {DefaultAlloc, DefaultFree, nullptr};
        return;
    }

    if (hooks.alloc == nullptr || hooks.free == nullptr)
    {
        throw std::invalid_argument("allocator hooks need both an alloc and a free function");
    }

    g_hooks = hooks;
}

void* HookAlloc(std::size_t size, std::size_t alignment)
{
    void* block = g_hooks.alloc(size, alignment, g_hooks.user);
    if (block == nullptr)
    {
        throw std::bad_alloc();
    }

    return block;
}

void HookFree(void* block) noexcept
{
    if (block != nullptr)
    {
        g_hooks.free(block, g_hooks.user);
    }
}

}  // namespace typhon::client
