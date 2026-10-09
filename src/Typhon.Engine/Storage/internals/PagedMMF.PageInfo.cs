using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

public partial class PagedMMF
{
    /// <summary>
    /// The state of one page-cache slot: 64 bytes, one cache line, in native memory (<see cref="PageSlotTable"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// No managed references (#1127): a cache of 67 M slots must not give the GC 67 M objects to mark. A slot's in-flight read lives in a side table
    /// sized by the reads in flight (<see cref="PagedMMF"/>'s <c>_readTasks</c>), and <see cref="ReadPending"/> says whether to look there.
    /// </para>
    /// <para>
    /// All-zero bytes are a free slot, so a freshly allocated table needs no initialisation: <see cref="EncodedFilePageIndex"/> holds the
    /// complement of the file page index, which makes zero read as -1 (no page). Every other field is free at zero.
    /// </para>
    /// <para>
    /// One slot per cache line also ends the false sharing neighbouring slots had as heap objects allocated in one loop.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct PageInfoData
    {
        internal const int ClockSweepMaxValue = 5;

        /// <inheritdoc cref="PageInfo.WritebackGen"/>
        [FieldOffset(0)] public long WritebackGen;

        /// <inheritdoc cref="PageInfo.CapturedGen"/>
        [FieldOffset(8)] public long CapturedGen;

        /// <inheritdoc cref="PageInfo.AccessEpoch"/>
        [FieldOffset(16)] public long AccessEpoch;

        /// <summary><c>~FilePageIndex</c>, so that zero bytes decode as -1. Read and written only through <see cref="PageInfo"/>.</summary>
        [FieldOffset(24)] public int EncodedFilePageIndex;

        /// <inheritdoc cref="PageInfo.DirtyCounter"/>
        [FieldOffset(28)] public int DirtyCounter;

        [FieldOffset(32)] public AccessControlSmall StateSyncRoot;
        [FieldOffset(36)] public AccessControlSmall PageExclusiveLatch;

        /// <inheritdoc cref="PageInfo.ActiveChunkWriters"/>
        [FieldOffset(40)] public int ActiveChunkWriters;

        /// <inheritdoc cref="PageInfo.SlotRefCount"/>
        [FieldOffset(44)] public int SlotRefCount;

        [FieldOffset(48)] public int ClockSweepCounter;
        [FieldOffset(52)] public PageState PageState;
        [FieldOffset(54)] public short ExclusiveLatchDepth;

        /// <inheritdoc cref="PageInfo.CrcVerified"/>
        [FieldOffset(56)] public bool CrcVerified;

        /// <inheritdoc cref="PageInfo.SlotReady"/>
        [FieldOffset(57)] public bool SlotReady;

        /// <inheritdoc cref="PageInfo.ReadPending"/>
        [FieldOffset(58)] public bool ReadPending;

        /// <summary>
        /// The page directory's chain link (#1136): the next slot in this slot's bucket, plus one; 0 ends the chain. Owned by
        /// <see cref="PageDirectory"/>, which alone reads and writes it.
        /// </summary>
        [FieldOffset(60)] public int DirectoryNext;
    }

    /// <summary>
    /// A handle on one slot's <see cref="PageInfoData"/>. Copying it copies a pointer, so code that takes a handle in a local and mutates through
    /// it cannot lose a write to a struct copy: every field is exposed by <c>ref</c>, the two locks included — a lock returned by value would
    /// compile and lock a copy.
    /// </summary>
    /// <remarks>
    /// Two exceptions. <see cref="FilePageIndex"/> is stored encoded: it has a plain getter and setter, and volatile helpers for the code that
    /// needs ordering on it (PS-15). <see cref="ClockSweepCounter"/> is read-only; it changes only through the methods below.
    /// </remarks>
    internal readonly unsafe struct PageInfo
    {
        private readonly PageInfoData* _p;

        internal PageInfo(PageInfoData* p) => _p = p;

        /// <summary>The file page this slot holds, or -1.</summary>
        public int FilePageIndex
        {
            get => ~_p->EncodedFilePageIndex;
            set => _p->EncodedFilePageIndex = ~value;
        }

        /// <summary><see cref="FilePageIndex"/> read with acquire semantics.</summary>
        public int ReadFilePageIndexVolatile() => ~Volatile.Read(ref _p->EncodedFilePageIndex);

        /// <summary><see cref="FilePageIndex"/> written with release semantics.</summary>
        public void WriteFilePageIndexVolatile(int filePageIndex) => Volatile.Write(ref _p->EncodedFilePageIndex, ~filePageIndex);

        /// <summary>
        /// Number of live mutator marks on this page — one per <see cref="ChangeSet.AddByMemPageIndex"/> /
        /// <see cref="ChangeSet.RegisterReDirty"/> that has not yet been released by the ChangeSet that took it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// STRICTLY CONSERVED: the only code that may raise this is a ChangeSet registering a mark, and the only code that
        /// may lower it is that same ChangeSet releasing its own marks. No other subsystem — checkpoint included — touches
        /// it. That is the whole point of the field: an owner-scoped count is balanced by construction, so it cannot drift.
        /// </para>
        /// <para>
        /// This is <b>not</b> "the page needs writing" — that is <see cref="WritebackGen"/>. Conflating the two is what
        /// made this counter leak: mutator marks arrive K times per checkpoint cycle (once per unit of work) while the
        /// checkpoint acks once per cycle, so any scheme where the writer decrements the mutator's count leaves K-1
        /// behind for ever (#824), and any scheme where it decrements ALL of them destroys marks taken after the capture
        /// (#385). Neither is fixable while one integer carries both meanings.
        /// </para>
        /// </remarks>
        public ref int DirtyCounter => ref _p->DirtyCounter;

        /// <summary>
        /// Monotonic stamp bumped by every path that modifies this page's bytes. Compared against
        /// <see cref="CapturedGen"/> to answer "are the current bytes on disk?".
        /// </summary>
        /// <remarks>
        /// <c>WritebackGen != CapturedGen</c> means the page carries unwritten bytes: it must be collected by the next
        /// checkpoint and must not be evicted. The writer captures the value it snapshotted and, after fsync, publishes it
        /// to <see cref="CapturedGen"/>. A modification racing the capture bumps <see cref="WritebackGen"/> past the
        /// captured value, so the page stays owed — CP-04's re-dirty defence falls out of the comparison instead of
        /// needing a count to survive a decrement. The pair carries over when the slot is reused (PS-10), so a capture
        /// published late for a previous occupant can never exceed it.
        /// </remarks>
        public ref long WritebackGen => ref _p->WritebackGen;

        /// <summary>
        /// The <see cref="WritebackGen"/> value whose bytes are known durable on the data file. Only ever advanced, and
        /// only by a writer that has fsynced the snapshot it took at that generation.
        /// </summary>
        public ref long CapturedGen => ref _p->CapturedGen;

        public ref AccessControlSmall StateSyncRoot => ref _p->StateSyncRoot;

        /// <summary>Must always be changed under <see cref="StateSyncRoot"/>.</summary>
        public ref PageState PageState => ref _p->PageState;

        /// <summary>Re-entrance depth of the exclusive latch (several chunks on one page).</summary>
        public ref short ExclusiveLatchDepth => ref _p->ExclusiveLatchDepth;

        /// <summary>Thread ownership of the exclusive latch.</summary>
        public ref AccessControlSmall PageExclusiveLatch => ref _p->PageExclusiveLatch;

        /// <summary>
        /// The epoch at which this page was last accessed via epoch-based protection.
        /// Pages with AccessEpoch >= MinActiveEpoch cannot be evicted.
        /// Value 0 means "not epoch-tagged" (legacy access only).
        /// </summary>
        public ref long AccessEpoch => ref _p->AccessEpoch;

        /// <summary>
        /// Whether the page CRC has been verified since it was loaded from disk.
        /// Reset to false during page allocation (Allocating state), set to true after verification.
        /// No need for volatile — reset by the slot's owner before <see cref="SlotReady"/> publishes it, and checked after I/O completion.
        /// </summary>
        public ref bool CrcVerified => ref _p->CrcVerified;

        /// <summary>
        /// PS-15: <c>false</c> from the moment the slot is claimed for a file page until its owner has prepared it — <see cref="CrcVerified"/>
        /// reset, and the page either cleared (not on disk, PS-14) or its read started and recorded in the read table. The slot is visible in the
        /// page directory before that, so a concurrent requester that finds it waits on this flag instead of using it. Written with
        /// <c>Volatile.Write</c> (a release, so the preparation is visible to whoever reads <c>true</c>) and read with
        /// <c>Volatile.Read</c>.
        /// </summary>
        public ref bool SlotReady => ref _p->SlotReady;

        /// <summary>
        /// Whether the read table may hold a read task for this slot. Set by the slot's owner after inserting the task and before
        /// <see cref="SlotReady"/> publishes both; cleared only by a thread that may not race a new read into the slot — a requester holding
        /// the slot by its epoch tag, <see cref="TryAcquire"/> under the slot's lock, or the slot's owner. Lets a cache hit skip the table.
        /// </summary>
        public ref bool ReadPending => ref _p->ReadPending;

        /// <summary>
        /// Number of <see cref="ChunkAccessor{TStore}"/> instances that have marked this page dirty in their local
        /// <c>_dirtyFlags</c> bitmask but have not yet flushed via <see cref="ChunkAccessor{TStore}.CommitChanges"/>.
        /// <para>
        /// While &gt; 0, the page may contain partially-written B+Tree data (e.g., a node with odd OLC version).
        /// <see cref="WritePagesForCheckpoint"/> skips such pages to avoid writing inconsistent snapshots to disk.
        /// The page stays dirty and will be captured in the next checkpoint cycle after the writers commit.
        /// </para>
        /// <para>
        /// Accessed via <see cref="Interlocked"/> from multiple threads (writer threads increment/decrement,
        /// checkpoint thread reads). Plain reads are safe on x64 TSO after Interlocked barriers on writer side.
        /// </para>
        /// </summary>
        public ref int ActiveChunkWriters => ref _p->ActiveChunkWriters;

        /// <summary>
        /// Number of <see cref="ChunkAccessor{TStore}"/> slots currently referencing this memory page.
        /// While &gt; 0, the page memory must not be reused — callers may hold raw <c>byte*</c> or
        /// <c>ref T</c> pointers derived from the slot's cached base address.
        /// <para>
        /// Unlike <see cref="ActiveChunkWriters"/> (which prevents checkpoint from writing inconsistent data),
        /// this counter only prevents page eviction in <see cref="TryAcquire"/>. Checkpoint can safely
        /// snapshot a page with SlotRefCount &gt; 0 as long as ACW == 0.
        /// </para>
        /// <para>
        /// Incremented in <see cref="ChunkAccessor{TStore}.LoadIntoSlot"/>, decremented (deferred) in
        /// <see cref="ChunkAccessor{TStore}.EvictSlot"/> and (immediate) in <see cref="ChunkAccessor{TStore}.Dispose"/>.
        /// </para>
        /// </summary>
        public ref int SlotRefCount => ref _p->SlotRefCount;

        public int ClockSweepCounter => _p->ClockSweepCounter;

        public void IncrementClockSweepCounter()
        {
            ref var counter = ref _p->ClockSweepCounter;
            var curValue = counter;
            if (curValue == PageInfoData.ClockSweepMaxValue)
            {
                return;
            }

            SpinWait sw = new();
            while (Interlocked.CompareExchange(ref counter, curValue + 1, curValue) != curValue)
            {
                curValue = counter;
                if (curValue == PageInfoData.ClockSweepMaxValue)
                {
                    return;
                }
                sw.SpinOnce();
            }
        }

        public void DecrementClockSweepCounter()
        {
            ref var counter = ref _p->ClockSweepCounter;
            var curValue = counter;
            if (curValue == 0)
            {
                return;
            }

            SpinWait sw = new();
            while (Interlocked.CompareExchange(ref counter, curValue - 1, curValue) != curValue)
            {
                curValue = counter;
                if (curValue == 0)
                {
                    return;
                }
                sw.SpinOnce();
            }
        }

        public void ResetClockSweepCounter() => _p->ClockSweepCounter = 0;
    }

    /// <summary>
    /// The block holding every slot's <see cref="PageInfoData"/>, 64-byte aligned, allocated from the engine's allocator as a child of the
    /// store, so it is counted with the engine's native memory and freed with the store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allocated zeroed, by contract and lazily (#945: 4 GiB of records at 512 GiB of cache, never written at open): every slot starts free,
    /// with no initialisation loop over the records.
    /// </para>
    /// <para>
    /// Freed deterministically when <see cref="PagedMMF"/> is disposed. Nothing may touch it after that, and nothing does: the checkpoint
    /// thread is joined and the shutdown flush has run before the engine disposes its store. What may still arrive afterwards is a caller
    /// the store's teardown cannot order — a transaction outliving the store releasing its dirty marks, a diagnostic — and those find
    /// <see cref="PagedMMF"/>'s reference already null and touch nothing. Using the store concurrently with its disposal is not supported,
    /// for the records as for the page memory.
    /// </para>
    /// </remarks>
    internal sealed unsafe class PageSlotTable
    {
        private readonly LargePinnedMemoryBlock _block;

        /// <summary>First slot, 64-byte aligned.</summary>
        public readonly PageInfoData* Base;

        /// <summary>Number of slots.</summary>
        public readonly int Count;

        public PageSlotTable(IMemoryAllocator allocator, IResource owner, int count)
        {
            Count = count;
            _block = allocator.AllocateLargePinned("PageSlots", owner, (long)count * sizeof(PageInfoData), 64, LargeBlockContents.Zeroed);
            Base = (PageInfoData*)_block.DataAsPointer;
        }

        /// <summary>Bytes held.</summary>
        public long Bytes => _block.Size;

        /// <summary>Whether the block has been freed. Test seam.</summary>
        internal bool IsFreed => _block.IsDisposed;

        public PageInfo this[int memPageIndex]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)memPageIndex >= (uint)Count)
                {
                    ThrowSlotOutOfRange(memPageIndex, Count);
                }
                return new PageInfo(Base + memPageIndex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowSlotOutOfRange(int memPageIndex, int count) =>
            throw new IndexOutOfRangeException($"Page-cache slot {memPageIndex} is outside the cache's {count} slots.");
    }
}
