#pragma once

#include <cstddef>
#include <new>
#include <vector>

// The allocator hooks of the client config (13 § 7): every buffer the store, the applier and the arenas grow goes through them, so an
// embedding engine can route the SDK's memory to its own heap. The catalog and its plan are built once per WELCOME and use the default
// heap. The hooks are process-wide: set them before the first client is created, never while one is alive.
namespace typhon::client {

using AllocFn = void* (*)(std::size_t size, std::size_t alignment, void* user);
using FreeFn = void (*)(void* block, void* user);

struct AllocatorHooks {
    AllocFn alloc = nullptr;
    FreeFn free = nullptr;
    void* user = nullptr;
};

// Installs `hooks`, or restores the default heap when both functions are null.
void SetAllocatorHooks(const AllocatorHooks& hooks);

void* HookAlloc(std::size_t size, std::size_t alignment);
void HookFree(void* block) noexcept;

// A stateless allocator over the hooks: every instance is interchangeable, so containers move and swap freely.
template <class T>
struct HookAllocator {
    using value_type = T;

    HookAllocator() noexcept = default;
    template <class U>
    HookAllocator(const HookAllocator<U>&) noexcept
    {
    }

    T* allocate(std::size_t n) { return static_cast<T*>(HookAlloc(n * sizeof(T), alignof(T))); }
    void deallocate(T* p, std::size_t) noexcept { HookFree(p); }

    template <class U>
    bool operator==(const HookAllocator<U>&) const noexcept
    {
        return true;
    }
};

template <class T>
using Vec = std::vector<T, HookAllocator<T>>;

}  // namespace typhon::client
