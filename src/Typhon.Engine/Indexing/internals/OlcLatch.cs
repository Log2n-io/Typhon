using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Optimistic Lock Coupling latch operating on a B+Tree node's OlcVersion field.
/// Layout (32 bits):
///   Bit 0:      Locked (exclusive writer active)
///   Bit 1:      Obsolete (node replaced by SMO)
///   Bits 2-31:  Version counter (30 bits, ~1.07B versions)
/// </summary>
internal readonly ref struct OlcLatch
{
    private readonly ref int _version;  // ref to chunk's OlcVersion field

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OlcLatch(ref int olcVersion) => _version = ref olcVersion;

    // --- Reader API (zero writes to shared state) ---

    /// <summary>
    /// Read version. Returns 0 if locked or obsolete (caller must restart).
    /// On x64 (TSO), loads are never reordered with other loads — no acquire barrier needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadVersion()
    {
        int v = _version;
        return (v & 0b11) == 0 ? v : 0;  // locked (bit 0) or obsolete (bit 1) -> restart
    }

    /// <summary>
    /// Validate version unchanged since snapshot. On mismatch, emit a Concurrency:OlcLatch:ValidationFail trace event (Tier-2 gated).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ValidateVersion(int expected)
    {
        var actual = _version;
        if (actual == expected)
        {
            return true;
        }
        TyphonEvent.EmitConcurrencyOlcLatchValidationFail((uint)expected, (uint)actual);
        return false;
    }

    /// <summary>
    /// After acquiring the write lock, validates that no other writer modified the node between our version snapshot and our lock acquisition.
    /// Must be called while holding the write lock.
    /// After TryWriteLock succeeds, _version = (v | 1) where v was read inside TryWriteLock.
    /// If v == expectedUnlockedVersion, nobody modified the node between our ReadVersion and our lock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ValidateVersionLocked(int expectedUnlockedVersion)
    {
        var actual = _version;
        var expectedLocked = expectedUnlockedVersion | 1;
        if (actual == expectedLocked)
        {
            return true;
        }
        TyphonEvent.EmitConcurrencyOlcLatchValidationFail((uint)expectedLocked, (uint)actual);
        return false;
    }

    // --- Writer API ---

    /// <summary>
    /// Acquire exclusive write lock. Returns false on contention; emits a Concurrency:OlcLatch:WriteLockAttempt trace event on failure.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteLock()
    {
        int v = _version;
        if ((v & 0b1) != 0)
        {
            TyphonEvent.EmitConcurrencyOlcLatchWriteLockAttempt((uint)v, false);
            return false;
        }
        if (Interlocked.CompareExchange(ref _version, v | 0b1, v) == v)
        {
            return true;
        }
        TyphonEvent.EmitConcurrencyOlcLatchWriteLockAttempt((uint)v, false);
        return false;
    }

    /// <summary>
    /// Release write lock and increment version.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUnlock()
    {
        // Increment version (bits 2-31), clear locked (bit 0), preserve obsolete (bit 1)
        // On x64 (TSO), stores are never reordered with other stores — no release barrier needed.
        int v = _version;
        var newV = ((v >> 2) + 1) << 2 | (v & 0b10);  // version++, keep obsolete, clear lock
        _version = newV;
        TyphonEvent.EmitConcurrencyOlcLatchWriteUnlock((uint)v, (uint)newV);
    }

    /// <summary>
    /// Mark node as obsolete. Must hold write lock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkObsolete()
    {
        _version |= 0b10;
        TyphonEvent.EmitConcurrencyOlcLatchMarkObsolete((uint)_version);
    }

    /// <summary>
    /// Release write lock WITHOUT incrementing version. Used when a writer acquires the lock but decides to restart without modifying the
    /// node (e.g., version validation failure).
    /// This avoids unnecessary version bumps that would cause cascading restarts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AbortWriteLock() => _version = _version & ~0b1;  // Clear locked bit (bit 0) without changing version counter or obsolete bit

    /// <summary>Check if locked (for diagnostics only).</summary>
    public bool IsLocked => (_version & 0b1) != 0;

    /// <summary>Check if obsolete.</summary>
    public bool IsObsolete => (_version & 0b10) != 0;
}
