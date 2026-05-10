using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Concurrent bitmap tracking which page cache slots have had their Full-Page Image (FPI) written
/// in the current checkpoint cycle. Indexed by <c>memPageIndex</c> (cache slot, not file page).
/// </summary>
/// <remarks>
/// <para>
/// One bit per slot, 64 slots per <c>ulong</c> word. All operations are lock-free using
/// <see cref="Interlocked"/> atomics on individual words.
/// </para>
/// <para>
/// <see cref="ClearAll"/> is only safe at checkpoint start when no concurrent <see cref="TestAndSet"/>
/// calls are in progress.
/// </para>
/// </remarks>
internal sealed class FpiBitmap
{
    private readonly ulong[] _words;
    private readonly int _bitCount;

    /// <summary>
    /// Creates a new FPI tracking bitmap with the specified number of bits.
    /// </summary>
    /// <param name="bitCount">Number of page cache slots to track. Must be positive.</param>
    public FpiBitmap(int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitCount);

        _bitCount = bitCount;
        _words = new ulong[(bitCount + 63) >> 6];
    }

    /// <summary>
    /// Atomically tests and sets the bit for the given cache slot.
    /// </summary>
    /// <param name="memPageIndex">Cache slot index (0-based).</param>
    /// <returns>
    /// <c>true</c> if the bit was already set (FPI already written this cycle);
    /// <c>false</c> if the bit was clear and is now set (caller must write the FPI).
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TestAndSet(int memPageIndex)
    {
        var wordIndex = memPageIndex >> 6;
        var mask = 1UL << (memPageIndex & 0x3F);

        var previous = Interlocked.Or(ref _words[wordIndex], mask);
        return (previous & mask) != 0;
    }

    /// <summary>
    /// Atomically clears the bit for the given cache slot.
    /// </summary>
    /// <param name="memPageIndex">Cache slot index (0-based).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear(int memPageIndex)
    {
        var wordIndex = memPageIndex >> 6;
        var mask = ~(1UL << (memPageIndex & 0x3F));

        Interlocked.And(ref _words[wordIndex], mask);
    }

    /// <summary>
    /// Reads the current state of the bit for the given cache slot.
    /// </summary>
    /// <param name="memPageIndex">Cache slot index (0-based).</param>
    /// <returns><c>true</c> if the bit is set.</returns>
    /// <remarks>Plain read — atomic on x64 for ≤64-bit types (no Volatile.Read needed).</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSet(int memPageIndex)
    {
        var wordIndex = memPageIndex >> 6;
        var mask = 1UL << (memPageIndex & 0x3F);
        return (_words[wordIndex] & mask) != 0;
    }

    /// <summary>
    /// Clears all bits. Only safe at checkpoint boundaries when no concurrent <see cref="TestAndSet"/> calls
    /// are in progress.
    /// </summary>
    public void ClearAll() => Array.Clear(_words);

    /// <summary>Total number of bits in this bitmap.</summary>
    public int BitCount => _bitCount;
}
