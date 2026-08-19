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
        public int DirtyCounter;

        /// <summary>
        /// Monotonic stamp bumped by every path that modifies this page's bytes. Compared against
        /// <see cref="CapturedGen"/> to answer "are the current bytes on disk?".
        /// </summary>
        /// <remarks>
        /// <c>WritebackGen != CapturedGen</c> means the page carries unwritten bytes: it must be collected by the next
        /// checkpoint and must not be evicted. The writer captures the value it snapshotted and, after fsync, publishes it
        /// to <see cref="CapturedGen"/>. A modification racing the capture bumps <see cref="WritebackGen"/> past the
        /// captured value, so the page stays owed — CP-04's re-dirty defence falls out of the comparison instead of
        /// needing a count to survive a decrement.
        /// </remarks>
        public long WritebackGen;

        /// <summary>
        /// The <see cref="WritebackGen"/> value whose bytes are known durable on the data file. Only ever advanced, and
        /// only by a writer that has fsynced the snapshot it took at that generation.
        /// </summary>
        public long CapturedGen;

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