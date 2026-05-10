using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

[PublicAPI]
public interface IMemoryAllocator
{
    MemoryBlockArray AllocateArray(string id, IResource parent, int size, bool zeroed = false, ushort sourceTag = 0);
    PinnedMemoryBlock AllocatePinned(string id, IResource parent, int size, bool zeroed = false, int alignment = 0, ushort sourceTag = 0);
}

[PublicAPI]
public class MemoryAllocatorOptions
{
    public string Name { get; set; } = "Default Memory Allocator";
}

[PublicAPI]
public class MemoryAllocator : ResourceNode, IMemoryAllocator, IMetricSource, IDebugPropertiesProvider
{
    private ConcurrentCollection<MemoryBlockBase> _blocks;

    // Allocation tracking — grand totals (includes both pinned/unmanaged and managed arrays)
    private long _totalAllocatedBytes;
    private long _peakAllocatedBytes;
    private long _cumulativeAllocations;
    private long _cumulativeDeallocations;

    // Pinned-only (unmanaged) tracking — backs the MemoryUnmanagedTotalBytes / PeakBytes / LiveBlocks gauges.
    // Separate from the grand totals because the unmanaged gauge must not be inflated by managed array allocs.
    private long _pinnedBytes;
    private long _peakPinnedBytes;
    private int _pinnedLiveBlocks;

    /// <summary>Running total of bytes currently held by live <see cref="PinnedMemoryBlock"/> instances (unmanaged via <c>NativeMemory</c>).</summary>
    public long PinnedBytes => _pinnedBytes;

    /// <summary>All-time peak of <see cref="PinnedBytes"/> since this allocator was created.</summary>
    public long PeakPinnedBytes => _peakPinnedBytes;

    /// <summary>Count of live (not-yet-disposed) <see cref="PinnedMemoryBlock"/> instances from this allocator.</summary>
    public int PinnedLiveBlocks => _pinnedLiveBlocks;

    public MemoryAllocator(IResourceRegistry resourceRegistry, MemoryAllocatorOptions options) :
        base(options?.Name ?? "Default Memory Allocator", ResourceType.Service, resourceRegistry.Allocation)
    {
        _blocks = new ConcurrentCollection<MemoryBlockBase>();
    }

    public MemoryBlockArray AllocateArray(string id, IResource parent, int size, bool zeroed = false, ushort sourceTag = 0)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive");
        }
        var block = zeroed ? GC.AllocateArray<byte>(size) : GC.AllocateUninitializedArray<byte>(size);

        var mb = new MemoryBlockArray(this, block, id, parent, sourceTag);
        _blocks.Add(mb);

        // Update allocation tracking
        var newTotal = Interlocked.Add(ref _totalAllocatedBytes, size);
        if (newTotal > _peakAllocatedBytes)
        {
            _peakAllocatedBytes = newTotal;
        }
        Interlocked.Increment(ref _cumulativeAllocations);

        // Profiler: emit a MemoryAllocEvent (zero cost when ProfilerMemoryAllocationsActive is false — JIT folds the gate away).
        TyphonEvent.EmitMemoryAlloc(MemoryAllocDirection.Alloc, sourceTag, (ulong)size, (ulong)newTotal);

        return mb;
    }

    public PinnedMemoryBlock AllocatePinned(string id, IResource parent, int size, bool zeroed = false, int alignment = 0, ushort sourceTag = 0)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive");
        }

        if (alignment < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment cannot be negative");
        }

        if (!BitOperations.IsPow2(alignment))
        {
            throw new ArgumentException("Alignment must be a power of 2", nameof(alignment));
        }

        var mb = new PinnedMemoryBlock(this, size, alignment, id, parent, sourceTag);
        if (zeroed)
        {
            mb.DataAsSpan.Clear();
        }
        _blocks.Add(mb);

        // Phase 5: Memory:AlignmentWaste instant — emitted only when the requested size isn't a multiple of the alignment,
        // i.e. when the underlying allocator must round up to the next aligned chunk. The leaf-gate check is FIRST so when
        // tracing is off the JIT folds the entire computation block away.
        if (TelemetryConfig.MemoryAlignmentWasteActive && alignment > 0)
        {
            var rem = size % alignment;
            if (rem != 0)
            {
                var wasted = alignment - rem;
                var padded = size + wasted;
                var wastePctHundredths = (ushort)Math.Min((wasted * 10000L) / padded, 10000L);
                TyphonEvent.EmitMemoryAlignmentWaste(size, alignment, wastePctHundredths);
            }
        }

        // Update allocation tracking — grand totals
        var newTotal = Interlocked.Add(ref _totalAllocatedBytes, size);
        if (newTotal > _peakAllocatedBytes)
        {
            _peakAllocatedBytes = newTotal;
        }
        Interlocked.Increment(ref _cumulativeAllocations);

        // Pinned-only tracking for the unmanaged gauge family
        var newPinnedTotal = Interlocked.Add(ref _pinnedBytes, size);
        if (newPinnedTotal > _peakPinnedBytes)
        {
            _peakPinnedBytes = newPinnedTotal;
        }
        Interlocked.Increment(ref _pinnedLiveBlocks);

        // Profiler: emit a MemoryAllocEvent (gated). TotalAfterBytes is the unmanaged running total — matches what the
        // MemoryUnmanagedTotalBytes gauge reports, so viewer markers align with the area chart.
        TyphonEvent.EmitMemoryAlloc(MemoryAllocDirection.Alloc, sourceTag, (ulong)size, (ulong)newPinnedTotal);

        return mb;
    }

    internal void Remove(MemoryBlockBase block)
    {
        var size = block.MemoryBlockSize;
        var newTotal = Interlocked.Add(ref _totalAllocatedBytes, -size);
        Interlocked.Increment(ref _cumulativeDeallocations);

        // Pinned-only counters only decrement for PinnedMemoryBlock — MemoryBlockArray is managed, not tracked in the unmanaged gauge.
        long newPinnedTotal = newTotal;
        if (block is PinnedMemoryBlock)
        {
            newPinnedTotal = Interlocked.Add(ref _pinnedBytes, -size);
            Interlocked.Decrement(ref _pinnedLiveBlocks);
        }

        _blocks.Remove(block);

        // Profiler emits the symmetric free event — direction=Free, carries the block's SourceTag so viewer can pair
        // it with its alloc. TotalAfterBytes uses the pinned-only total when it's a pinned block (to stay aligned with
        // the gauge), otherwise the grand total.
        TyphonEvent.EmitMemoryAlloc(MemoryAllocDirection.Free, block.SourceTag, (ulong)size, (ulong)newPinnedTotal);
    }

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        // Memory: total bytes across all blocks
        writer.WriteMemory(_totalAllocatedBytes, _peakAllocatedBytes);

        // Capacity: active block count (no hard limit)
        long blockCount = _blocks.Count;
        writer.WriteCapacity(blockCount, long.MaxValue);

        // Throughput: allocation lifecycle
        writer.WriteThroughput("Allocations", _cumulativeAllocations);
        writer.WriteThroughput("Deallocations", _cumulativeDeallocations);
    }

    /// <inheritdoc />
    public void ResetPeaks() => _peakAllocatedBytes = _totalAllocatedBytes;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties()
    {
        var blocks = _blocks.ToArray(); // Snapshot for consistency

        long arrayBlocks = 0, pinnedBlocks = 0;
        long arrayBytes = 0, pinnedBytes = 0;

        foreach (var block in blocks)
        {
            if (block is MemoryBlockArray)
            {
                arrayBlocks++;
                arrayBytes += block.EstimatedMemorySize;
            }
            else if (block is PinnedMemoryBlock)
            {
                pinnedBlocks++;
                pinnedBytes += block.EstimatedMemorySize;
            }
        }

        return new Dictionary<string, object>
        {
            // Overall stats
            ["Blocks.Total"] = blocks.Length,
            ["Bytes.Total"] = _totalAllocatedBytes,
            ["Bytes.Peak"] = _peakAllocatedBytes,

            // By type breakdown
            ["ArrayBlocks.Count"] = arrayBlocks,
            ["ArrayBlocks.Bytes"] = arrayBytes,
            ["PinnedBlocks.Count"] = pinnedBlocks,
            ["PinnedBlocks.Bytes"] = pinnedBytes,

            // Lifecycle counters
            ["Cumulative.Allocations"] = _cumulativeAllocations,
            ["Cumulative.Deallocations"] = _cumulativeDeallocations,
        };
    }
}