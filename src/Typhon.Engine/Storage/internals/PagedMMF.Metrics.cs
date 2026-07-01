using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

public partial class PagedMMF
{
    /// <summary>
    /// Some real-time metrics.
    /// </summary>
    /// <remarks>
    /// Some fields are documented as "approximately" because they are not updated atomically, so in rare case of read/modify/write by concurrent threads
    ///  some updates will be missed.
    /// </remarks>
    [PublicAPI]
    internal class Metrics
    {
        /// Approximately the number of page requests that successfully hit the cache. Counted only while the profiler is
        /// active (the increment is gated by <see cref="TelemetryConfig.ProfilerActive"/> at the call site) — it sits on
        /// the hottest path in the engine (3-4× per point read, every reader thread), so an always-on increment bounced
        /// one shared cache line across every core and crippled concurrent-read scaling. A stat must never tax the path
        /// it measures; with the profiler off the JIT folds the increment away entirely.
        public int MemPageCacheHit;

        /// Approximately the number of page requests that missed the cache and had to be allocated again. Gated by
        /// <see cref="TelemetryConfig.ProfilerActive"/> too, so the hit/miss pair (and the derived hit-rate) is either
        /// both-counted or both-zero — never a misleading 0%-hit reading when profiling is off.
        public int MemPageCacheMiss;
        
        /// Approximately the number of pages that were read from disk
        public int ReadFromDiskCount;
        
        /// Approximately the number of pages that were written to disk
        public int PageWrittenToDiskCount;

        /// Approximately the number of IO write operations executed
        public int WrittenOperationCount;
        
        /// The exact number of Memory Pages that are currently free (and can be used to allocate new file pages).
        public int FreeMemPageCount;
        
        ///
        public int TotalMemPageAllocatedCount;

        /// Approximately the number of times allocation entered backpressure wait
        public int BackpressureWaitCount;

        private readonly PagedMMF _owner;

        [PublicAPI]
        public struct MemPageExtraInfo
        {
            public int FreeMemPageCount       { get; internal set; }
            public int AllocatingMemPageCount { get; internal set; }
            public int IdleMemPageCount       { get; internal set; }
            public int ExclusiveMemPageCount  { get; internal set; }
            public int DirtyPageCount         { get; internal set; }
            public int LockedByThreadCount    { get; internal set; }
            public int PendingIOReadCount     { get; internal set; }
            public int MinClockSweepCounter      { get; internal set; }
            public int MaxClockSweepCounter      { get; internal set; }
            public int BackpressureWaitCount     { get; internal set; }
            public int EpochProtectedPageCount   { get; internal set; }
            public int SlotRefPageCount          { get; internal set; }

            public override string ToString() =>
                $"Free: {FreeMemPageCount}, Allocating: {AllocatingMemPageCount}, Idle: {IdleMemPageCount}, " +
                $"Exclusive: {ExclusiveMemPageCount}, Dirty: {DirtyPageCount}, " +
                $"LockedByThread: {LockedByThreadCount}, PendingIORead: {PendingIOReadCount}, " +
                $"MinClockSweepCounter: {MinClockSweepCounter}, MaxClockSweepCounter: {MaxClockSweepCounter}, " +
                $"BackpressureWaitCount: {BackpressureWaitCount}, " +
                $"EpochProtectedPageCount: {EpochProtectedPageCount}, SlotRefPageCount: {SlotRefPageCount}";
        }
        
        public Metrics(PagedMMF owner, int freePageCount)
        {
            _owner = owner;
            FreeMemPageCount = freePageCount;
        }

        public void GetMemPageExtraInfo(out MemPageExtraInfo res) => _owner.GetMemPageExtraInfo(out res);
    }
}
