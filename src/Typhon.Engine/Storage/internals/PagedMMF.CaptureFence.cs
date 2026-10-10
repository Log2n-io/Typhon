#pragma warning disable 1591   // as PagedMMF.cs: a public type of the internal namespace (Phase 2b entanglement)

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

public partial class PagedMMF
{
    /// <summary>
    /// A lock word inside a cached page goes to disk whenever the page is captured while someone holds it — the checkpoint's copy, an immediate save's
    /// write — and the release then changes the page in memory only. Left at that, the clean page is evicted, read back, and the lock is held by no one,
    /// for good (#1253). The capture fence lets a holder notice: one word per slot, its high half the captures of the slot's page started, its low half
    /// those in flight. A holder takes a <see cref="CaptureTicket"/> (the word) before it locks; at its release, if a capture was in flight when it
    /// took the ticket or one has started since, it marks the page modified, and the next checkpoint writes it with the lock free (PS-20).
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is enough.</b> Say a capture read the word while held. The capture began (<see cref="BeginCapture"/>) before that read, which came
    /// before the release; the holder reads the fence after its release, so it sees the capture started — unless the capture began before the holder's
    /// ticket. Then either the capture was still in flight at the ticket, which says so, or it had ended — before the ticket, so before the lock was
    /// taken, so it never saw it held. Every step is a full fence: the holder's acquire and release are CASes between its two volatile reads, and the
    /// capture's begin and end are interlocked around its reads of the page.</para>
    /// <para><b>Cost.</b> Two reads of a word written only by captures, per hold; a page rewritten once per capture it overlapped. No lock word moves, so
    /// neighbouring chains keep their own cache lines. A crash can leave a captured hold on disk with no rewrite: a crash open frees every chain's lock
    /// as its table loads, before anything takes one (<see cref="ComponentRevisionManager.ResetChainLocksAfterCrash"/>).</para>
    /// <para>The fence word is per slot, not per page: a ticket is compared with a later read of the same slot, and the holder's epoch keeps the page in
    /// that slot for the whole hold. The started count wraps off the top of the word, harmlessly: a hold would have to overlap exactly 2^32 captures to
    /// miss them; the in-flight count never carries into it.</para>
    /// </remarks>
    internal readonly struct CaptureTicket
    {
        private readonly int _slotPlusOne;   // 0 in a default ticket, which no hold may end with
        internal readonly long Fence;

        internal CaptureTicket(int memPageIndex, long fence)
        {
            _slotPlusOne = memPageIndex + 1;
            Fence = fence;
        }

        internal int MemPageIndex => _slotPlusOne - 1;

        internal bool IsTaken => _slotPlusOne != 0;
    }

    private const long CaptureStarted = 1L << 32;

    private LargePinnedMemoryBlock _captureFenceBlock;
    private unsafe long* _captureFence;

    // On a cache line of its own, past the fence words: a count bumped by every marking hold, kept off the line of the fields every hold reads.
    private unsafe long* _locksHeldThroughCapture;

    /// <summary>Lock holds that overlapped a capture of their page, each followed by marking the page modified. Diagnostic.</summary>
    internal unsafe long LocksHeldThroughCapture => Volatile.Read(ref *_locksHeldThroughCapture);

    private unsafe void AllocateCaptureFence(IMemoryAllocator memoryAllocator, int pageCount)
    {
        var fenceBytes = ((long)pageCount * sizeof(long) + 63) & ~63L;
        _captureFenceBlock = memoryAllocator.AllocateLargePinned("CaptureFence", this, fenceBytes + 64, 64, LargeBlockContents.Zeroed);
        _captureFence = (long*)_captureFenceBlock.DataAsPointer;
        _locksHeldThroughCapture = (long*)(_captureFenceBlock.DataAsPointer + fenceBytes);
    }

    /// <summary>A capture of the slot's page begins: before its first read of the page.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe void BeginCapture(int memPageIndex) => Interlocked.Add(ref _captureFence[memPageIndex], CaptureStarted + 1);

    /// <summary>The capture ends: after its last read of the page.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe void EndCapture(int memPageIndex) => Interlocked.Decrement(ref _captureFence[memPageIndex]);

    /// <summary>A ticket for a hold of the lock <paramref name="lockWord"/> names, inside a cached page. Taken BEFORE the lock is.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe CaptureTicket TakeCaptureTicket(ref byte lockWord)
    {
        var memPageIndex = (int)(Unsafe.ByteOffset(ref *_memPagesAddr, ref lockWord) >> PageSizePow2);
        Debug.Assert((uint)memPageIndex < (uint)MemPagesCount, "the lock is not in the page cache");
        return new CaptureTicket(memPageIndex, Volatile.Read(ref _captureFence[memPageIndex]));
    }

    /// <summary>Ends the hold <paramref name="ticket"/> was taken for. Called AFTER the lock is released.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe void EndLockHold(in CaptureTicket ticket)
    {
        Debug.Assert(ticket.IsTaken, "a lock hold ended without the ticket taken for it");

        // A capture in flight at the ticket, or one started since — which changed the word, as a word no capture touched is unchanged.
        if ((int)ticket.Fence != 0 || Volatile.Read(ref _captureFence[ticket.MemPageIndex]) != ticket.Fence)
        {
            LockHeldThroughCapture(ticket.MemPageIndex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe void LockHeldThroughCapture(int memPageIndex)
    {
        Interlocked.Increment(ref *_locksHeldThroughCapture);
        MarkPageModified(memPageIndex);
    }
}
