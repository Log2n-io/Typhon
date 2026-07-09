using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Internals;

public partial class PagedMMF
{
    internal class PageInfo
    {
        private const int ClockSweepMaxValue = 5;
        
        public readonly int MemPageIndex;
        public int FilePageIndex;
        public int ClockSweepCounter => _clockSweepCounter;
        public int DirtyCounter;

        public AccessControlSmall StateSyncRoot;
        public PageState PageState;                     // Must always be changed under StateSyncRoot lock
        public short ExclusiveLatchDepth;               // Re-entrance depth (multiple chunks on same page)
        public AccessControlSmall PageExclusiveLatch;   // Thread ownership for exclusive latch

        /// <summary>
        /// The epoch at which this page was last accessed via epoch-based protection.
        /// Pages with AccessEpoch >= MinActiveEpoch cannot be evicted.
        /// Value 0 means "not epoch-tagged" (legacy access only).
        /// </summary>
        public long AccessEpoch;

        /// <summary>
        /// Whether the page CRC has been verified since it was loaded from disk.
        /// Reset to false during page allocation (Allocating state), set to true after verification.
        /// No need for volatile — set during single-owner Allocating state and checked after I/O completion.
        /// </summary>
        public bool CrcVerified;

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
        public int ActiveChunkWriters;

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
        public int SlotRefCount;

        private int _clockSweepCounter;
        private Lazy<Task<int>> _ioReadTask;

        public void SetIOReadTask(ValueTask<int> task) => _ioReadTask = new Lazy<Task<int>>(task.AsTask);

        public Task<int> IOReadTask => _ioReadTask?.Value;

        public void ResetIOCompletionTask() => _ioReadTask = null;

        public PageInfo(int memPageIndex)
        {
            MemPageIndex = memPageIndex;
            FilePageIndex = -1;
            _clockSweepCounter = 0;
            StateSyncRoot = new AccessControlSmall();
            PageExclusiveLatch = new AccessControlSmall();
        }

        public void IncrementClockSweepCounter()
        {
            var curValue = _clockSweepCounter;
            if (curValue == ClockSweepMaxValue)
            {
                return;
            }

            SpinWait sw = new();
            while (Interlocked.CompareExchange(ref _clockSweepCounter, curValue + 1, curValue) != curValue)
            {
                curValue = _clockSweepCounter;
                if (curValue == ClockSweepMaxValue)
                {
                    return;
                }
                sw.SpinOnce();
            }
        }

        public void DecrementClockSweepCounter()
        {
            var curValue = _clockSweepCounter;
            if (curValue == 0)
            {
                return;
            }

            SpinWait sw = new();
            while (Interlocked.CompareExchange(ref _clockSweepCounter, curValue - 1, curValue) != curValue)
            {
                curValue = _clockSweepCounter;
                if (curValue == 0)
                {
                    return;
                }
                sw.SpinOnce();
            }
        }

        public void ResetClockSweepCounter() => _clockSweepCounter = 0;
    }
}