using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Shared hash utility functions for in-memory and page-backed hash map implementations.
/// Hash functions extracted from HashMap; meta/bucket helpers extracted from PagedHashMapBase.
/// </summary>
internal static unsafe class HashUtils
{
    // ═══════════════════════════════════════════════════════════════════════
    // Hash functions — JIT-specialized by sizeof(TKey)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute the hash of a key. JIT eliminates dead branches based on <c>sizeof(TKey)</c>:
    /// 4 bytes → Fibonacci multiply, 8 bytes → XOR-fold + Fibonacci, 16 bytes → XOR-fold-4 + Fibonacci,
    /// other → xxHash32 (generic bytes).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ComputeHash<TKey>(TKey key) where TKey : unmanaged
    {
        if (sizeof(TKey) == 4)
        {
            return FastHash32(Unsafe.As<TKey, uint>(ref key));
        }

        if (sizeof(TKey) == 8)
        {
            return FastHash64(Unsafe.As<TKey, ulong>(ref key));
        }

        if (sizeof(TKey) == 16)
        {
            return FastHash128((byte*)Unsafe.AsPointer(ref key));
        }

        return XxHash32_Bytes((byte*)Unsafe.AsPointer(ref key), sizeof(TKey));
    }

    /// <summary>Wang/Jenkins integer hash — deterministic, excellent distribution, ~3-4 cycles.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint WangJenkins32(uint h)
    {
        h = (h ^ 61) ^ (h >> 16);
        h *= 0x85EBCA6B;
        h ^= h >> 13;
        h *= 0xC2B2AE35;
        h ^= h >> 16;
        return h;
    }

    /// <summary>
    /// Fast 2-operation hash for 4-byte keys: Fibonacci multiplicative hash + mix.
    /// ~2 cycles — 3x faster than WangJenkins, good distribution for open addressing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint FastHash32(uint h)
    {
        h *= 0x9E3779B9u; // Fibonacci / golden ratio — 1 multiply, ~1 cycle
        return h == 0 ? 1u : h; // sentinel: 0 means empty slot in open addressing
    }

    /// <summary>
    /// Fast hash for 8-byte keys: XOR-fold to 32 bits, then single Fibonacci multiply.
    /// ~2 cycles. Optimal distribution for sequential keys with power-of-2 masking:
    /// consecutive golden-ratio multiples are maximally spaced (avg probe 1.0 at 75% load).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint FastHash64(ulong key)
    {
        uint h = (uint)key ^ (uint)(key >> 32);
        h *= 0x9E3779B9u; // Fibonacci / golden ratio
        return h == 0 ? 1u : h;
    }

    /// <summary>
    /// Fast hash for 16-byte keys (Guid): XOR-fold 4 uints, then Fibonacci multiply with extra mixing.
    /// ~4 cycles — 5x faster than xxHash32_Bytes for 16 bytes, good distribution for open addressing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint FastHash128(byte* key)
    {
        uint* p = (uint*)key;
        uint h = p[0] ^ p[1] ^ p[2] ^ p[3];
        h *= 0x85EBCA6Bu;
        h ^= h >> 16;
        h *= 0x9E3779B9u;
        return h == 0 ? 1u : h;
    }

    /// <summary>Inlined xxHash32 over 8 bytes — deterministic, excellent distribution, ~8-10 cycles.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint XxHash32_8Bytes(long key)
    {
        const uint Prime2 = 2246822519u;
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;

        uint lo = (uint)key;
        uint hi = (uint)(key >> 32);

        uint h = Prime5 + 8u;
        h += lo * Prime3;
        h = ((h << 17) | (h >> 15)) * Prime4;
        h += hi * Prime3;
        h = ((h << 17) | (h >> 15)) * Prime4;

        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    /// <summary>xxHash32 over arbitrary byte length — fallback for key sizes other than 4 or 8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint XxHash32_Bytes(byte* input, int len)
    {
        const uint Prime1 = 2654435761u;
        const uint Prime2 = 2246822519u;
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;

        uint h = Prime5 + (uint)len;
        byte* p = input;
        byte* end = input + len;

        // Process 4-byte blocks
        while (p + 4 <= end)
        {
            h += *(uint*)p * Prime3;
            h = ((h << 17) | (h >> 15)) * Prime4;
            p += 4;
        }

        // Process remaining bytes
        while (p < end)
        {
            h += *p * Prime5;
            h = ((h << 11) | (h >> 21)) * Prime1;
            p++;
        }

        // Avalanche
        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Meta packing / Bucket resolution
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pack level, next, and bucketCount into a single 64-bit value.
    /// Layout: Level(bits 56-63) | Next(bits 32-55) | BucketCount(bits 0-31).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long PackMeta(int level, int next, int bucketCount) =>
        ((long)(level & 0xFF) << 56) | ((long)(next & 0x00FFFFFF) << 32) | (uint)bucketCount;

    /// <summary>
    /// Unpack a 64-bit packed meta into (Level, Next, BucketCount).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int Level, int Next, int BucketCount) UnpackMeta(long packed)
    {
        int level = (int)((packed >> 56) & 0xFF);
        int next = (int)((packed >> 32) & 0x00FFFFFF);
        int bucketCount = (int)(packed & 0xFFFFFFFF);
        return (level, next, bucketCount);
    }

    /// <summary>
    /// Resolve a hash to a bucket index using bitmask arithmetic (no modulo).
    /// If the bucket has already been split this round (bucket &lt; next), the finer modulus is used.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ResolveBucket(uint hash, int level, int next, int n0)
    {
        int mod = n0 << level;                        // N0 × 2^Level (always power of 2)
        int bucket = (int)(hash & (uint)(mod - 1));   // bitmask: 1 AND instruction

        if (bucket < next)
        {
            // This bucket already split this round — use finer modulus
            bucket = (int)(hash & (uint)((mod << 1) - 1));
        }

        return bucket;
    }
}
