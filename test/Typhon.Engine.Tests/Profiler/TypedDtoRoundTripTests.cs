using NUnit.Framework;
using System;
using Typhon.Engine.Internals;
using Typhon.Profiler;
using Typhon.Profiler.Events;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Phase 1B/1C exemplar — round-trips a real <see cref="BTreeInsertEvent"/> through the generator-emitted
/// encoder and the new typed <see cref="BTreeInsertEventDto.Decode"/>. Validates that the hand-authored
/// shape matches the wire format the encoder produces. Once the generator generalizes (1D), this fixture
/// becomes a model for one-test-per-family.
/// </summary>
[TestFixture]
public sealed class TypedDtoRoundTripTests
{
    private const long TicksPerUs = 10; // arbitrary — the test only cares about the conversion math.
    private const int CurrentTick = 42;

    private const byte ThreadSlot = 7;
    private const long StartTs = 1_234_567_890L;
    private const long EndTs = 1_234_568_890L;
    private const ulong SpanId = 0xABCDEF0123456789UL;
    private const ulong ParentSpanId = 0x1122334455667788UL;

    [Test]
    public void BTreeInsert_RoundTrip_NoTraceContext_NoSourceLocation()
    {
        var ev = new BTreeInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                // TraceIdHi/Lo = 0 → no trace context bit in spanFlags.
                // SourceLocationId = 0 → no source-location bit in spanFlags.
            },
        };

        var size = ev.ComputeSize();
        Span<byte> buf = stackalloc byte[size];
        ev.EncodeTo(buf, EndTs, out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(size));

        var dto = BTreeInsertEventDto.Decode(buf, CurrentTick, TicksPerUs);

        Assert.That(dto.ThreadSlot, Is.EqualTo(ThreadSlot));
        Assert.That(dto.TickNumber, Is.EqualTo(CurrentTick));
        Assert.That(dto.TimestampUs, Is.EqualTo(StartTs / (double)TicksPerUs));
        Assert.That(dto.DurationUs, Is.EqualTo((EndTs - StartTs) / (double)TicksPerUs));
        Assert.That(dto.SpanId, Is.EqualTo(SpanId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(dto.ParentSpanId, Is.EqualTo(ParentSpanId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(dto.TraceIdHi, Is.Null);
        Assert.That(dto.TraceIdLo, Is.Null);
        Assert.That(dto.SourceLocationId, Is.EqualTo((ushort)0));
    }

    [Test]
    public void BTreeInsert_RoundTrip_WithTraceContext()
    {
        const ulong traceHi = 0xAAAA_BBBB_CCCC_DDDDUL;
        const ulong traceLo = 0x1111_2222_3333_4444UL;
        var ev = new BTreeInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                TraceIdHi = traceHi,
                TraceIdLo = traceLo,
            },
        };

        var size = ev.ComputeSize();
        Span<byte> buf = stackalloc byte[size];
        ev.EncodeTo(buf, EndTs, out _);

        var dto = BTreeInsertEventDto.Decode(buf, CurrentTick, TicksPerUs);

        Assert.That(dto.TraceIdHi, Is.EqualTo(traceHi.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(dto.TraceIdLo, Is.EqualTo(traceLo.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Test]
    public void BTreeInsert_RoundTrip_WithSourceLocation()
    {
        const ushort siteId = 0xABCD;
        var ev = new BTreeInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
                SourceLocationId = siteId,
            },
        };

        var size = ev.ComputeSize();
        Span<byte> buf = stackalloc byte[size];
        ev.EncodeTo(buf, EndTs, out _);

        var dto = BTreeInsertEventDto.Decode(buf, CurrentTick, TicksPerUs);

        Assert.That(dto.SourceLocationId, Is.EqualTo(siteId));
    }

    [Test]
    public void SpatialQueryAabb_RoundTrip_WithPayload()
    {
        // Span event with 5 payload fields — exercises the generator's payload-write/read path end-to-end.
        var ev = new SpatialQueryAabbEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
            },
            NodesVisited = 42,
            LeavesEntered = 8,
            ResultCount = 5,
            RestartCount = 1,
            CategoryMask = 0xFF00FF00,
        };

        var size = ev.ComputeSize();
        Span<byte> buf = stackalloc byte[size];
        ev.EncodeTo(buf, EndTs, out _);

        var dto = SpatialQueryAabbEventDto.Decode(buf, CurrentTick, TicksPerUs);

        Assert.That(dto.NodesVisited, Is.EqualTo((ushort)42));
        Assert.That(dto.LeavesEntered, Is.EqualTo((ushort)8));
        Assert.That(dto.ResultCount, Is.EqualTo((ushort)5));
        Assert.That(dto.RestartCount, Is.EqualTo((byte)1));
        Assert.That(dto.CategoryMask, Is.EqualTo(0xFF00FF00u));
        Assert.That(dto.SpanId, Is.EqualTo(SpanId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Test]
    public void CheckpointCycle_RoundTrip_OptionalsPresent()
    {
        // Optional payload field present (DirtyPageCount). Validates the generator's optMask read + conditional decode.
        const long targetLsn = 0x1111_2222_3333_4444L;
        const byte reason = 7;
        const int dirtyPages = 12345;

        var ev = new CheckpointCycleEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
            },
            TargetLsn = targetLsn,
            Reason = reason,
            DirtyPageCount = dirtyPages, // setter flips OptDirtyPageCount in _optMask
        };

        var size = ev.ComputeSize();
        Span<byte> buf = stackalloc byte[size];
        ev.EncodeTo(buf, EndTs, out _);

        var dto = CheckpointCycleEventDto.Decode(buf, CurrentTick, TicksPerUs);

        Assert.That(dto.TargetLsn, Is.EqualTo(targetLsn));
        Assert.That(dto.Reason, Is.EqualTo(reason));
        Assert.That(dto.DirtyPageCount, Is.EqualTo(dirtyPages));
    }

    [Test]
    public void CheckpointCycle_RoundTrip_OptionalsAbsent()
    {
        // Optional NOT set — DirtyPageCount stays absent (mask bit 0). DTO surfaces null.
        const long targetLsn = 0x9999L;
        const byte reason = 3;

        var ev = new CheckpointCycleEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
            },
            TargetLsn = targetLsn,
            Reason = reason,
            // DirtyPageCount intentionally not set — mask bit stays clear.
        };

        var size = ev.ComputeSize();
        Span<byte> buf = stackalloc byte[size];
        ev.EncodeTo(buf, EndTs, out _);

        var dto = CheckpointCycleEventDto.Decode(buf, CurrentTick, TicksPerUs);

        Assert.That(dto.TargetLsn, Is.EqualTo(targetLsn));
        Assert.That(dto.Reason, Is.EqualTo(reason));
        Assert.That(dto.DirtyPageCount, Is.Null);
    }

    [Test]
    public void TransactionCommit_RoundTrips_ViaTopLevelDispatch()
    {
        // TransactionCommit is now generator-owned (Shape=Span, EmitEncoder=true). Round-trip via the producer
        // ref struct + TraceEventDecoder.Decode top-level dispatch validates the typed wire path end-to-end.
        const long tsn = 0x1234567890ABCDEFL;
        var ev = new TransactionCommitEvent
        {
            Header = new TraceSpanHeader { ThreadSlot = ThreadSlot, StartTimestamp = StartTs, SpanId = SpanId, ParentSpanId = ParentSpanId },
            Tsn = tsn,
            ComponentCount = 7,
        };

        var size = ev.ComputeSize();
        Span<byte> buf = stackalloc byte[size];
        ev.EncodeTo(buf, EndTs, out _);

        var dto = TraceEventDecoder.Decode(buf, CurrentTick, TicksPerUs);

        Assert.That(dto, Is.InstanceOf<TransactionCommitEventDto>());
        var tc = (TransactionCommitEventDto)dto;
        Assert.That(tc.Tsn, Is.EqualTo(tsn));
        Assert.That(tc.ComponentCount, Is.EqualTo(7));
        Assert.That(tc.SpanId, Is.EqualTo(SpanId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Test]
    public void PageCacheFetch_RoundTrip_NoTrailingOptMask()
    {
        // PageCacheFetch declares no [Optional] fields, so the generator emits no trailing optMask byte.
        const int filePageIndex = 4096;
        var ev = new PageCacheFetchEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
            },
            FilePageIndex = filePageIndex,
        };

        var size = ev.ComputeSize();
        // Wire size for no-trace-context PageCacheFetch: span header (37) + i32 FilePageIndex (4) = 41 bytes.
        Assert.That(size, Is.EqualTo(41));

        Span<byte> buf = stackalloc byte[size];
        ev.EncodeTo(buf, EndTs, out var written);
        Assert.That(written, Is.EqualTo(41));

        var dto = TraceEventDecoder.Decode(buf, CurrentTick, TicksPerUs);
        Assert.That(dto, Is.InstanceOf<PageCacheFetchEventDto>());
        var pcf = (PageCacheFetchEventDto)dto;
        Assert.That(pcf.FilePageIndex, Is.EqualTo(filePageIndex));
    }

    [Test]
    public void TopLevelDispatch_RoutesGeneratedAndHandGlueKinds()
    {
        // Generated path: BTreeInsert (EmitEncoder=true).
        var bt = new BTreeInsertEvent
        {
            Header = new TraceSpanHeader { ThreadSlot = 1, StartTimestamp = StartTs, SpanId = SpanId, ParentSpanId = 0 },
        };
        Span<byte> buf1 = stackalloc byte[bt.ComputeSize()];
        bt.EncodeTo(buf1, EndTs, out _);
        var dto1 = TraceEventDecoder.Decode(buf1, CurrentTick, TicksPerUs);
        Assert.That(dto1, Is.InstanceOf<BTreeInsertEventDto>(), "generated-path dispatch failed");

        // Hand-glue path: PageCacheFetch (EmitEncoder=false).
        var size2 = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheFetch, hasTraceContext: false, optMask: 0);
        Span<byte> buf2 = stackalloc byte[size2];
        PageCacheEventCodec.Encode(buf2, EndTs, TraceEventKind.PageCacheFetch, ThreadSlot, StartTs,
            SpanId, ParentSpanId, traceIdHi: 0, traceIdLo: 0, filePageIndex: 1234, pageCount: 0, optMask: 0, out _);
        var dto2 = TraceEventDecoder.Decode(buf2, CurrentTick, TicksPerUs);
        Assert.That(dto2, Is.InstanceOf<PageCacheFetchEventDto>(), "hand-glue dispatch failed");
        var pcf = (PageCacheFetchEventDto)dto2;
        Assert.That(pcf.FilePageIndex, Is.EqualTo(1234));
    }

    [Test]
    public void DurabilityRecoveryTickFence_RoundTrip_PayloadFieldRenamedOnCollision()
    {
        // DurabilityRecoveryTickFenceEvent has a payload field named `TickNumber` which collides with the
        // consumer-observed TraceEventDto.TickNumber base property. The generator must rename the DTO property
        // to TickNumberPayload to preserve the base semantic. This test guards that rename.
        var ev = new DurabilityRecoveryTickFenceEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = ThreadSlot,
                StartTimestamp = StartTs,
                SpanId = SpanId,
                ParentSpanId = ParentSpanId,
            },
            TickFenceCount = 3,
            Entries = 17,
            TickNumber = 999,
        };

        var size = ev.ComputeSize();
        Span<byte> buf = stackalloc byte[size];
        ev.EncodeTo(buf, EndTs, out _);

        var dto = DurabilityRecoveryTickFenceEventDto.Decode(buf, CurrentTick, TicksPerUs);

        Assert.That(dto.TickFenceCount, Is.EqualTo(3));
        Assert.That(dto.Entries, Is.EqualTo(17));
        // Base property keeps consumer-observed value.
        Assert.That(dto.TickNumber, Is.EqualTo(CurrentTick));
        // Payload value lives under the renamed property.
        Assert.That(dto.TickNumberPayload, Is.EqualTo(999L));
    }
}
