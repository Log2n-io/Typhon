using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

public partial class ManagedPagedMMF
{
    public class BitmapL3
    {
        private readonly LogicalSegment<PersistentStore> _segment;
        private Memory<long> _l1All;
        private Memory<long> _l2All;
        private Memory<long> _l1Any;

        public BitmapL3(LogicalSegment<PersistentStore> segment)
        {
            _segment = segment;
            Capacity = LogicalSegment<PersistentStore>.GetItemCount<long>(segment.Length) * 64;

            var length = Math.Max(1, (Capacity + 4095) / 4096);
            _l1All = new long[length];
            _l1Any = new long[length];
            length = Math.Max(1, (length + 63) / 64);

            _l2All = new long[length];
        }

        public void Grow()
        {
            Capacity = LogicalSegment<PersistentStore>.GetItemCount<long>(_segment.Length) * 64;
            
            var length = Math.Max(1, (Capacity + 4095) / 4096);
            var l1All = new long[length];
            var l1Any = new long[length];
            length = Math.Max(1, (length + 63) / 64);

            var l2All = new long[length];
            
            _l1All.CopyTo(l1All);
            _l1Any.CopyTo(l1Any);
            _l2All.CopyTo(l2All);

            _l1All = l1All;
            _l1Any = l1Any;
            _l2All = l2All;
        }
        

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool SetL0(int bitIndex, ChangeSet changeSet = null)
        {
            var l0Offset = bitIndex >> 6;
            var l0Mask = 1L << (bitIndex & 0x3F);

            var (pageIndex, pageOffset) = LogicalSegment<PersistentStore>.GetItemLocation<long>(l0Offset);
            var epoch = _segment.Store.EpochManager.GlobalEpoch;
            var page = _segment.GetPageExclusive(pageIndex, epoch, out var memPageIdx);
            var dataOffset = page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0;
            var data = page.RawData<long>(dataOffset, (PageRawDataSize - dataOffset) / sizeof(long));
            {
                var prevL0 = Interlocked.Or(ref data[pageOffset], l0Mask);
                if ((prevL0 & l0Mask) != 0)
                {
                    _segment.Store.UnlatchPageExclusive(memPageIdx);
                    // The bit was concurrently set by someone else
                    return false;
                }

                if (data[pageOffset] != prevL0)
                {
                    changeSet?.AddByMemPageIndex(memPageIdx);
                }

                if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = _l1All.Span[l1Offset];
                    _l1All.Span[l1Offset] |= l1Mask;

                    if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        _l2All.Span[l2Offset] |= l2Mask;
                    }
                }

                if (prevL0 == 0 && (prevL0 | l0Mask) != 0)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    _l1Any.Span[l1Offset] |= l1Mask;
                }
            }
            _segment.Store.UnlatchPageExclusive(memPageIdx);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool SetL1(int index, ChangeSet changeSet = null)
        {
            var l0Offset = index;
            var l0Mask = -1L;

            var (pageIndex, pageOffset) = LogicalSegment<PersistentStore>.GetItemLocation<long>(l0Offset);
            var epoch = _segment.Store.EpochManager.GlobalEpoch;
            var page = _segment.GetPageExclusive(pageIndex, epoch, out var memPageIdx);
            var dataOffset = page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0;
            var data = page.RawData<long>(dataOffset, (PageRawDataSize - dataOffset) / sizeof(long));
            {
                // CAS, not OR: bulk-allocate the entire L1 word IFF every bit is currently zero. The previous implementation used `Interlocked.Or(...)`
                // which can't be undone — when the L1 word turned out to be partially occupied, the function returned false but had already set every
                // previously-unset bit, leaking those pages into the bitmap with no segment claiming them. The orphan-page bug surfaced as power-of-2
                // contiguous Unknown ranges in the Workbench File Map. With CAS, partial collision means the underlying word is untouched — caller cleanly
                // falls back to page-by-page allocation.
                var prevL0 = Interlocked.CompareExchange(ref data[pageOffset], l0Mask, 0L);

                if (prevL0 != 0)
                {
                    _segment.Store.UnlatchPageExclusive(memPageIdx);
                    // Can't allocate the whole L1 — at least one bit already set. Bitmap untouched.
                    return false;
                }

                if (data[pageOffset] != prevL0)
                {
                    changeSet?.AddByMemPageIndex(memPageIdx);
                }

                if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = _l1All.Span[l1Offset];
                    _l1All.Span[l1Offset] |= l1Mask;

                    if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        _l2All.Span[l2Offset] |= l2Mask;
                    }
                }

                if (prevL0 == 0 && (prevL0 | l0Mask) != 0)
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    _l1Any.Span[l1Offset] |= l1Mask;
                }
            }
            _segment.Store.UnlatchPageExclusive(memPageIdx);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void ClearL0(int index, ChangeSet changeSet = null)
        {
            var l0Offset = index >> 6;
            var l0SetMask = 1L << (index & 0x3F);
            var l0ClearMask = ~l0SetMask;

            var (pageIndex, pageOffset) = LogicalSegment<PersistentStore>.GetItemLocation<long>(l0Offset);
            var epoch = _segment.Store.EpochManager.GlobalEpoch;
            var page = _segment.GetPageExclusive(pageIndex, epoch, out var memPageIdx);
            var dataOff = page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0;
            var data = page.RawData<long>(dataOff, (PageRawDataSize - dataOff) / sizeof(long));
            {
                var prevL0 = Interlocked.And(ref data[pageOffset], l0ClearMask);
                if ((prevL0 & l0SetMask) != 0)
                {
                    changeSet?.AddByMemPageIndex(memPageIdx);
                }

                if ((prevL0 == -1) && ((prevL0 & l0ClearMask) != -1))
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    var prevL1 = _l1All.Span[l1Offset];
                    _l1All.Span[l1Offset] &= ~l1Mask;

                    if (prevL1 == -1)
                    {
                        var l2Offset = l1Offset >> 6;
                        var l2Mask = 1L << (l1Offset & 0x3F);
                        _l2All.Span[l2Offset] &= ~l2Mask;
                    }
                }

                if ((prevL0 != 0) && ((prevL0 & l0ClearMask) == 0))
                {
                    var l1Offset = l0Offset >> 6;
                    var l1Mask = 1L << (l0Offset & 0x3F);

                    _l1Any.Span[l1Offset] &= ~l1Mask;
                }
            }
            _segment.Store.UnlatchPageExclusive(memPageIdx);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool IsSet(int index)
        {
            var offset = index >> 6;
            var mask = 1L << (index & 0x3F);

            var (pageIndex, pageOffset) = LogicalSegment<PersistentStore>.GetItemLocation<long>(offset);
            var epoch = _segment.Store.EpochManager.GlobalEpoch;
            var page = _segment.GetPage(pageIndex, epoch, out _);
            var dataOff = page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0;
            var data = page.RawDataReadOnly<long>(dataOff, (PageRawDataSize - dataOff) / sizeof(long));
            return (data[pageOffset] & mask) != 0L;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool FindNextUnsetL0(ref int index, ref long mask)
        {
            var capacity = Capacity;

            var c0 = ++index;
            long v0 = mask;
            long t0;

            var ll0 = (capacity + 63) / 64;
            var ll1 = _l1All.Length;
            var ll2 = _l2All.Length;

            var epoch = _segment.Store.EpochManager.GlobalEpoch;
            var curPageId = -1;
            var i0 = 0;
            ReadOnlySpan<long> curDataSpan = default;

            while (c0 < capacity)
            {
                // Do we have to fetch a new L0?
                if (((c0 & 0x3F) == 0) || (v0 == -1))
                {
                    // Check if we can skip the rest of the level 0
                    for (i0 = c0 >> 6; i0 < ll0; i0 = c0 >> 6)
                    {
                        var (pageId, offset) = LogicalSegment<PersistentStore>.GetItemLocation<long>(i0);
                        if (pageId != curPageId)
                        {
                            var page = _segment.GetPage(pageId, epoch, out _);
                            var dataOff = page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0;
                            curDataSpan = page.RawDataReadOnly<long>(dataOff, (PageRawDataSize - dataOff) / sizeof(long));
                            curPageId = pageId;
                        }
                        var data = curDataSpan;
                        t0 = 1L << (c0 & 0x3F);
                        v0 = data[offset] | (t0 - 1);

                        if (v0 != -1)
                        {
                            break;
                        }
                        c0 = ++i0 << 6;

                        // Check if we can skip the rest of the level 1
                        for (int i1 = c0 >> 12; i1 < ll1; i1 = c0 >> 12)
                        {
                            var v1 = _l1All.Span[i1] >> (i0 & 0x3F);
                            if (v1 != -1)
                            {
                                break;
                            }

                            i0 = 0;
                            c0 = ++i1 << 12;

                            // Check if we can skip the rest of the level 2
                            for (int i2 = c0 >> 18; i2 < ll2; i2 = c0 >> 18)
                            {
                                var v2 = _l2All.Span[i2] >> (i1 & 0x3F);
                                if (v2 != -1)
                                {
                                    break;
                                }
                                i1 = 0;
                                c0 = ++i2 << 18;
                            }
                        }
                    }
                }

                if (c0 >= capacity)
                {
                    return false;
                }
                var bitPos = BitOperations.TrailingZeroCount(~v0);
                v0 |= (1L << bitPos);
                index = (c0 & ~0x3F) + bitPos;
                mask = v0;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool FindNextUnsetL1(ref int index, ref long mask)
        {
            var c1 = ++index;
            long v1 = mask;
            int i1 = 0;
            var ll1 = _l1All.Length;
            var ll2 = _l2All.Length;

            var max = Capacity >> 6;
            while (c1 < max)
            {
                if (((c1 & 0x3F) == 0) || (v1 == -1))
                {
                    // Check if we can skip the rest of the level 1
                    for (i1 = c1 >> 6; i1 < ll1; i1 = c1 >> 6)
                    {
                        var t1 = 1L << (c1 & 0x3F);
                        v1 = _l1All.Span[i1] | (t1 - 1);
                        if (v1 != -1)
                        {
                            break;
                        }

                        c1 = ++i1 << 6;

                        // Check if we can skip the rest of the level 2
                        for (int i2 = c1 >> 12; i2 < ll2; i2 = c1 >> 12)
                        {
                            var v2 = _l2All.Span[i2] >> (i1 & 0x3F);
                            if (v2 != -1)
                            {
                                break;
                            }

                            i1 = 0;
                            c1 = ++i2 << 12;
                        }
                    }

                }

                // Re-check capacity after the skip-L1 inner loop. When L1All is nearly fully saturated the `c1 = ++i1 << 6` step advances `c1` past `max`
                // (and `c1 >> 6` past `_l1Any.Length`); without this guard the fall-through to `_l1Any.Span[c1 >> 6]` raises `IndexOutOfRangeException`
                // instead of cleanly returning false. The sibling `FindNextUnsetL0` already has the equivalent `if (c0 >= capacity) return false;` guard —
                // this restores symmetry.
                if (c1 >= max)
                {
                    return false;
                }

                var t = 1L << (c1 & 0x3F);
                v1 = _l1Any.Span[c1 >> 6] | (t - 1);
                var bitPos = BitOperations.TrailingZeroCount(~v1);
                v1 |= (1L << bitPos);
                var foundIndex = (c1 & ~0x3F) + bitPos;
                // Padding-bit guard: `_l1All` / `_l1Any` are rounded up to 64-bit longs, so the top entry can have padding bits (positions >= Capacity/64)
                // that are zero — they look "unset" but don't correspond to any real L1 group. Without this guard, `SetL1(foundIndex)` resolves a `pageIndex`
                // past the segment's `_pages.Length` and raises IOOR inside `GetPageExclusive`. Hit at 8.5 M scale when the bitmap saturates: L1 padding bits
                // 2750/2751 in a 2750-capacity bitmap got returned and crashed segment-grow.
                if (foundIndex >= max)
                {
                    return false;
                }
                index = foundIndex;
                mask = v1;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Allocate(ref Span<int> result, int startFrom, ChangeSet changeSet = null)
        {
            Debug.Assert(result.IsEmpty==false && result.Length > 0, "A valid span with a length > 0 must be passed");
            Debug.Assert(startFrom>= 0 && startFrom < result.Length, "Start index must be within the valid range of the given result span");
            
            var length = result.Length - startFrom;
            var hasL1 = true;
            var destI = startFrom;

            // Allocate per bulk of 64 pages as long as we can
            while (hasL1 && (length >= 64))
            {
                int i = -1;
                long mask = 0;
                while (FindNextUnsetL1(ref i, ref mask) && (length >= 64))
                {
                    if (SetL1(i, changeSet))
                    {
                        for (int j = 0; j < 64; j++)
                        {
                            result[destI++] = (i<<6) + j;
                        }
                        length -= 64;
                    }
                }

                hasL1 = length < 64;
            }

            // Allocate page by page
            {
                int i = -1;
                long mask = 0;
                while (FindNextUnsetL0(ref i, ref mask) && (length > 0))
                {
                    if (SetL0(i, changeSet))
                    {
                        result[destI++] = i;
                        --length;
                    }
                }
            }

            // Error during allocation, rollback
            if (length > 0)
            {
                for (int i = startFrom; i < destI; i++)
                {
                    ClearL0(result[i], changeSet);
                }
                result[startFrom..].Clear();
                return false;
            }

            return true;
        }

        public void Free(ReadOnlySpan<int> pages, int startFrom, ChangeSet changeSet = null)
        {
            Debug.Assert(pages.IsEmpty==false && pages.Length > 0, "A valid span with a length > 0 must be passed");
            Debug.Assert(startFrom >= 0 && startFrom < pages.Length, "Start index must be within the valid range of the given pages span");

            var length = pages.Length;
            for (int i = startFrom; i < length; i++)
            {
                ClearL0(pages[i], changeSet);
            }
        }

        /// <summary>
        /// Frees a contiguous range of pages by clearing their L0 bits. Symmetric pair to the indices used by <see cref="Allocate"/> when allocations come
        /// out contiguously. Saves the intermediate int[] allocation the <see cref="Free(ReadOnlySpan{int},int,ChangeSet)"/> overload would require for
        /// synthesizing a range.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Used by the BulkLoad recovery path (Phase 3b in <see cref="WalRecovery"/>): when an orphan <see cref="WalChunkType.BulkBegin"/> is detected, every
        /// contiguous range in the manifest's <see cref="BulkPageRange"/> list is freed via this method.
        /// </para>
        /// <para>
        /// <b>Idempotency:</b> clearing an already-clear bit is a no-op (the underlying <see cref="ClearL0"/> AND-mask is monotonic). Calling
        /// <c>FreeRange</c> twice on the same range produces the same end state — required by BL-04 (recovery idempotency).
        /// </para>
        /// </remarks>
        /// <param name="firstIndex">Lowest page id in the range (inclusive).</param>
        /// <param name="count">Number of pages to free. Range covers <c>[firstIndex, firstIndex + count)</c>.</param>
        /// <param name="changeSet">Optional change set for tracking dirty bitmap pages.</param>
        public void FreeRange(int firstIndex, int count, ChangeSet changeSet = null)
        {
            Debug.Assert(firstIndex >= 0, "firstIndex must be non-negative");
            Debug.Assert(count > 0, "count must be positive");
            Debug.Assert(firstIndex + count <= Capacity, "range exceeds bitmap capacity");

            var end = firstIndex + count;
            for (int i = firstIndex; i < end; i++)
            {
                ClearL0(i, changeSet);
            }
        }

        /// <summary>
        /// Copies the level-0 occupancy words into <paramref name="dest"/> — one <c>long</c> per 64 pages, a set bit meaning the page is allocated. Read-only;
        /// walks only the resident occupancy-segment pages, so it incurs no data-page I/O. <paramref name="dest"/> must hold at
        /// least <c>(Capacity + 63) / 64</c> words.
        /// </summary>
        public void ReadOccupancyBits(Span<long> dest)
        {
            var wordCount = (Capacity + 63) / 64;
            if (dest.Length < wordCount)
            {
                throw new ArgumentException($"Destination span too small: need {wordCount} words, got {dest.Length}.", nameof(dest));
            }

            var epoch = _segment.Store.EpochManager.GlobalEpoch;
            var curPageId = -1;
            ReadOnlySpan<long> curData = default;
            for (var i = 0; i < wordCount; i++)
            {
                var (pageId, offset) = LogicalSegment<PersistentStore>.GetItemLocation<long>(i);
                if (pageId != curPageId)
                {
                    var page = _segment.GetPage(pageId, epoch, out _);
                    var dataOff = page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0;
                    curData = page.RawDataReadOnly<long>(dataOff, (PageRawDataSize - dataOff) / sizeof(long));
                    curPageId = pageId;
                }
                dest[i] = curData[offset];
            }
        }

        public int Capacity { get; private set; }
    }
}