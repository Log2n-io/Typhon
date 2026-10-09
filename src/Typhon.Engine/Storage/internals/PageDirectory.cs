using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The page directory: which cache slot holds a file page (#1136, rule PS-17). A chained hash whose chains run through the slot records
/// themselves (<see cref="PagedMMF.PageInfoData.DirectoryNext"/>), keyed by each slot's own file page index: no node object, no tombstone, no
/// rehash, and no managed reference per entry. It replaces a <c>ConcurrentDictionary</c>, which held one GC object per resident page — 67 M at
/// #945's 512 GiB target.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buckets.</b> One <c>int</c> each, a power of two at least the slot count, in a zeroed native block (0 = empty): bit 31 is the bucket's
/// lock, the rest is the head slot plus one. A slot's key changes only while it is in no chain: its owner writes it just before
/// <see cref="GetOrAdd"/>, and it is reset only after <see cref="TryRemove"/> — so under a bucket's lock every key in its chain is stable.
/// </para>
/// <para>
/// <b>Lookups are hints.</b> <see cref="TryGet"/> returns a slot whose key it read equal to the page; the caller validates the slot after tagging
/// it (PS-15), as it did with the dictionary. An absent answer is exact: an empty bucket is read with acquire, and an absent reached by
/// following links is confirmed under the bucket's lock. Without that confirmation, a reader paused on a node that is reclaimed and relinked
/// into another chain would follow the other chain to its end and report a resident page absent — and its caller would evict a page for nothing.
/// </para>
/// <para>
/// <b>Mutations are exact</b>, under the bucket's lock: <see cref="GetOrAdd"/> converges concurrent misses on one slot (PS-15), and
/// <see cref="TryRemove"/> unlinks one slot, never another slot's mapping. An unlinked slot keeps its link, so a reader paused on it carries on
/// down the rest of its old chain. Nothing under the lock can throw or wait (raw pointers, no allocation), the lock is never held across
/// another bucket's, and its only outer lock is a slot's <c>StateSyncRoot</c>; waiters spin without sleeping.
/// </para>
/// <para>
/// Freed with the store (a child block). <see cref="PagedMMF"/> nulls its reference at dispose, and the diagnostics that can run later check it.
/// </para>
/// </remarks>
internal sealed unsafe class PageDirectory
{
    private const int LockBit = int.MinValue;
    private const int HeadMask = int.MaxValue;

    /// <summary>Links followed without the lock before a lookup confirms under it: a reader chasing reused nodes cannot loop.</summary>
    internal const int MaxLockFreeSteps = 64;

    private readonly LargePinnedMemoryBlock _block;
    private readonly int* _buckets;
    private readonly PagedMMF.PageInfoData* _slots;
    private readonly int _shift;

    /// <summary>Number of buckets, a power of two.</summary>
    internal long BucketCount { get; }

    /// <summary>Bytes held by the buckets.</summary>
    internal long Bytes => BucketCount * sizeof(int);

    /// <summary>Test seam: called in a lock-free walk after a node's key was read and before its link is, with the node's slot.</summary>
    internal Action<int> WalkProbe;

    /// <param name="allocator">The engine allocator the buckets come from.</param>
    /// <param name="owner">The store: the buckets are its child block, freed with it.</param>
    /// <param name="slots">The slot records the chains run through.</param>
    /// <param name="bucketCountForTests">0 for the default (a power of two ≥ the slot count, at least 64); a power of two otherwise.</param>
    internal PageDirectory(IMemoryAllocator allocator, IResource owner, PagedMMF.PageSlotTable slots, long bucketCountForTests = 0)
    {
        BucketCount = bucketCountForTests > 0 ? bucketCountForTests : BucketCountFor(slots.Count);
        Debug.Assert(BitOperations.IsPow2(BucketCount));
        _shift = 32 - BitOperations.Log2((ulong)BucketCount);
        _block = allocator.AllocateLargePinned("PageDirectory", owner, Bytes, 64, LargeBlockContents.Zeroed);
        _buckets = (int*)_block.DataAsPointer;
        _slots = slots.Base;
    }

    /// <summary>The default bucket count for <paramref name="slotCount"/> slots: a power of two at least the slot count, and at least 64.</summary>
    internal static long BucketCountFor(long slotCount) => Math.Max(64L, (long)BitOperations.RoundUpToPowerOf2((ulong)Math.Max(1L, slotCount)));

    /// <summary>Fibonacci hashing: the top bits of the product. A 64-bit shift, so one bucket (a shift of 32) is well defined.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Bucket(int filePageIndex) => (long)((ulong)((uint)filePageIndex * 0x9E3779B9u) >> _shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int KeyOf(int slot) => ~Volatile.Read(ref _slots[slot].EncodedFilePageIndex);

    /// <summary>
    /// The slot holding <paramref name="filePageIndex"/>, as a hint the caller validates against the slot (PS-15). <c>false</c> means the page
    /// was not published at some instant during the call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGet(int filePageIndex, out int memPageIndex)
    {
        var head = Volatile.Read(ref _buckets[Bucket(filePageIndex)]) & HeadMask;
        if (head != 0)
        {
            memPageIndex = head - 1;
            return KeyOf(memPageIndex) == filePageIndex || TryGetPastHead(filePageIndex, ref memPageIndex);
        }

        memPageIndex = -1;
        return false;
    }

    /// <summary>The walk past a bucket's head, out of line so the hit path stays small. <paramref name="slot"/> is the node whose key missed.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryGetPastHead(int filePageIndex, ref int slot)
    {
        for (var steps = 0; steps < MaxLockFreeSteps; steps++)
        {
            WalkProbe?.Invoke(slot);
            var next = Volatile.Read(ref _slots[slot].DirectoryNext);
            if (next == 0)
            {
                break;
            }

            slot = next - 1;
            if (KeyOf(slot) == filePageIndex)
            {
                return true;
            }
        }

        // Absent by the lock-free walk, or a walk too long to trust: decide under the lock, where the chain cannot change.
        return TryGetLocked(filePageIndex, out slot);
    }

    private bool TryGetLocked(int filePageIndex, out int memPageIndex)
    {
        ref var bucket = ref _buckets[Bucket(filePageIndex)];
        var head = Lock(ref bucket);
        memPageIndex = Find(head, filePageIndex);
        Volatile.Write(ref bucket, head);
        return memPageIndex >= 0;
    }

    /// <summary>
    /// Publishes <paramref name="memPageIndex"/> for <paramref name="filePageIndex"/> unless another slot already holds the page, and returns the
    /// slot that does (PS-15: concurrent misses converge). The slot's key must already be <paramref name="filePageIndex"/>, and it must be in no
    /// chain.
    /// </summary>
    internal int GetOrAdd(int filePageIndex, int memPageIndex)
    {
        Debug.Assert(KeyOf(memPageIndex) == filePageIndex, "A slot is published under its own key.");
        ref var bucket = ref _buckets[Bucket(filePageIndex)];
        var head = Lock(ref bucket);
        var existing = Find(head, filePageIndex);
        if (existing >= 0)
        {
            Volatile.Write(ref bucket, head);
            return existing;
        }

        // Linked at the head. The release store that unlocks the bucket publishes the slot, its link and its key together.
        _slots[memPageIndex].DirectoryNext = head;
        Volatile.Write(ref bucket, memPageIndex + 1);
        return memPageIndex;
    }

    /// <summary>
    /// Removes the mapping of <paramref name="filePageIndex"/> to <paramref name="memPageIndex"/>, and only that one. The slot keeps its link, so
    /// a reader paused on it finishes the walk it started.
    /// </summary>
    internal bool TryRemove(int filePageIndex, int memPageIndex)
    {
        ref var bucket = ref _buckets[Bucket(filePageIndex)];
        var head = Lock(ref bucket);
        var target = memPageIndex + 1;
        var removed = false;
        if (head == target)
        {
            head = _slots[memPageIndex].DirectoryNext;
            removed = true;
        }
        else
        {
            for (var prev = head; prev != 0;)
            {
                var next = _slots[prev - 1].DirectoryNext;
                if (next == target)
                {
                    Volatile.Write(ref _slots[prev - 1].DirectoryNext, _slots[memPageIndex].DirectoryNext);
                    removed = true;
                    break;
                }

                prev = next;
            }
        }

        Volatile.Write(ref bucket, head);
        Debug.Assert(!removed || ~_slots[memPageIndex].EncodedFilePageIndex == filePageIndex, "A slot is unpublished under its own key.");
        return removed;
    }

    /// <summary>The slot in the chain from <paramref name="head"/> whose key is <paramref name="filePageIndex"/>, or -1. Under the bucket's lock.</summary>
    private int Find(int head, int filePageIndex)
    {
        for (var next = head; next != 0; next = _slots[next - 1].DirectoryNext)
        {
            if (~_slots[next - 1].EncodedFilePageIndex == filePageIndex)
            {
                return next - 1;
            }
        }

        return -1;
    }

    /// <summary>Takes the bucket's lock and returns its head word. A full fence (the CAS), so the chain and its keys are seen as last unlocked.</summary>
    private static int Lock(ref int bucket)
    {
        var sw = new SpinWait();
        while (true)
        {
            var word = Volatile.Read(ref bucket);
            if ((word & LockBit) == 0 && Interlocked.CompareExchange(ref bucket, word | LockBit, word) == word)
            {
                return word;
            }

            // Never Sleep(1): the holder runs a few loads and stores, and a TryAcquire waiting here holds a slot's lock.
            sw.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>Test seam: the number of slots in the chain of <paramref name="filePageIndex"/>'s bucket.</summary>
    internal int ChainLengthForTests(int filePageIndex)
    {
        var n = 0;
        for (var next = Volatile.Read(ref _buckets[Bucket(filePageIndex)]) & HeadMask; next != 0; next = _slots[next - 1].DirectoryNext)
        {
            n++;
        }

        return n;
    }

    /// <summary>Test seam: the bucket of <paramref name="filePageIndex"/>.</summary>
    internal long BucketForTests(int filePageIndex) => Bucket(filePageIndex);
}
