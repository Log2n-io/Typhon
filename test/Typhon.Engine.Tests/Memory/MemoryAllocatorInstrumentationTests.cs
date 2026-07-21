using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Memory;

/// <summary>
/// Tests the Phase 1 profiler instrumentation of <see cref="MemoryAllocator"/> and <see cref="MemoryBlockBase"/>:
/// pinned-byte tracking, live-block counting, source-tag propagation, and managed-vs-pinned accounting separation.
/// </summary>
/// <remarks>
/// These exercise the <i>counter</i> half of the instrumentation — the state the scheduler reads at tick boundary to build
/// gauge snapshots. The <i>event</i> half (<see cref="TyphonEvent.EmitMemoryAlloc"/> emitting records into the ring) is gated
/// on <see cref="TelemetryConfig.ProfilerMemoryAllocationsActive"/> which is a <c>static readonly</c> set at type load — flipping
/// it at test-time isn't possible, so we cover the emit path via codec round-trip tests
/// (<c>MemoryAllocEventCodecTests</c>) plus a smoke test here that confirms the allocator path doesn't throw when the flag is off.
/// </remarks>
[TestFixture]
public class MemoryAllocatorInstrumentationTests
{
    private MemoryAllocator Allocator => (MemoryAllocator)AllocatorTestServices.MemoryAllocator;

    [Test]
    public void AllocatePinned_increments_PinnedBytes_and_LiveBlocks()
    {
        var baselineBytes = Allocator.PinnedBytes;
        var baselineBlocks = Allocator.PinnedLiveBlocks;

        using var block = Allocator.AllocatePinned("test.pinned.increment", AllocatorTestServices.AllocationResource, size: 4096, alignment: 64);

        Assert.Multiple(() =>
        {
            Assert.That(Allocator.PinnedBytes, Is.EqualTo(baselineBytes + 4096));
            Assert.That(Allocator.PinnedLiveBlocks, Is.EqualTo(baselineBlocks + 1));
        });
    }

    [Test]
    public void PinnedMemoryBlock_Dispose_decrements_counters()
    {
        var baselineBytes = Allocator.PinnedBytes;
        var baselineBlocks = Allocator.PinnedLiveBlocks;

        var block = Allocator.AllocatePinned("test.pinned.dispose", AllocatorTestServices.AllocationResource, size: 8192, alignment: 64);
        Assert.That(Allocator.PinnedBytes, Is.EqualTo(baselineBytes + 8192));
        Assert.That(Allocator.PinnedLiveBlocks, Is.EqualTo(baselineBlocks + 1));

        block.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(Allocator.PinnedBytes, Is.EqualTo(baselineBytes));
            Assert.That(Allocator.PinnedLiveBlocks, Is.EqualTo(baselineBlocks));
        });
    }

    [Test]
    public void PeakPinnedBytes_never_regresses()
    {
        // PeakPinnedBytes is a high-water mark — it must satisfy two properties:
        //  (1) peak >= current live bytes at all times,
        //  (2) peak never decreases across any sequence of alloc/free.
        // Note: AllocatorTestServices is a singleton across the suite, so absolute values depend on test-ordering. We check properties, not deltas.

        var peakBefore = Allocator.PeakPinnedBytes;
        var block = Allocator.AllocatePinned("test.peak.block", AllocatorTestServices.AllocationResource, size: 1_048_576, alignment: 64);
        var peakDuring = Allocator.PeakPinnedBytes;

        Assert.Multiple(() =>
        {
            Assert.That(peakDuring, Is.GreaterThanOrEqualTo(peakBefore), "peak must not regress during alloc");
            Assert.That(peakDuring, Is.GreaterThanOrEqualTo(Allocator.PinnedBytes), "peak >= live at all times");
        });

        block.Dispose();
        var peakAfter = Allocator.PeakPinnedBytes;

        Assert.That(peakAfter, Is.GreaterThanOrEqualTo(peakDuring), "peak must not regress after free");
    }

    [Test]
    public void AllocateArray_does_not_affect_pinned_counters()
    {
        var baselineBytes = Allocator.PinnedBytes;
        var baselineBlocks = Allocator.PinnedLiveBlocks;

        using var arr = Allocator.AllocateArray("test.array", AllocatorTestServices.AllocationResource, size: 4096);

        // Managed array allocation must not move the unmanaged gauge — these back separate visual series in the viewer.
        Assert.Multiple(() =>
        {
            Assert.That(Allocator.PinnedBytes, Is.EqualTo(baselineBytes));
            Assert.That(Allocator.PinnedLiveBlocks, Is.EqualTo(baselineBlocks));
        });
    }

    [Test]
    public void SourceTag_propagates_to_block()
    {
        using var pinned = Allocator.AllocatePinned(
            "test.tag.pinned", AllocatorTestServices.AllocationResource,
            size: 512, alignment: 64, sourceTag: MemoryAllocSource.WalStaging);
        using var array = Allocator.AllocateArray(
            "test.tag.array", AllocatorTestServices.AllocationResource,
            size: 512, sourceTag: MemoryAllocSource.MemoryBlockArray);

        Assert.Multiple(() =>
        {
            Assert.That(pinned.SourceTag, Is.EqualTo((ushort)MemoryAllocSource.WalStaging));
            Assert.That(array.SourceTag, Is.EqualTo((ushort)MemoryAllocSource.MemoryBlockArray));
        });
    }

    [Test]
    public void Default_SourceTag_is_unattributed()
    {
        // Allocations that omit the tag parameter should fall back to MemoryAllocSource.Unattributed (0).
        using var block = Allocator.AllocatePinned("test.tag.default", AllocatorTestServices.AllocationResource, size: 256, alignment: 64);
        Assert.That(block.SourceTag, Is.EqualTo((ushort)MemoryAllocSource.Unattributed));
    }

    [Test]
    public void Emit_path_is_safe_when_profiler_gate_is_off()
    {
        // Smoke test: with ProfilerMemoryAllocationsActive = false (default in tests), AllocatePinned / Dispose must not throw
        // even though they call through TyphonEvent.EmitMemoryAlloc. The call should short-circuit at the Active gate.
        Assert.DoesNotThrow(() =>
        {
            using var a = Allocator.AllocatePinned("test.emit.noop.a", AllocatorTestServices.AllocationResource, size: 1024, alignment: 64);
            using var b = Allocator.AllocatePinned("test.emit.noop.b", AllocatorTestServices.AllocationResource, size: 2048, alignment: 64);
        });
    }
}
