using System;
using NUnit.Framework;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Phase 1 wire-format coverage for the post-#294 source-attribution feature (#302). Each test builds an event ref struct with a non-zero
/// <c>Header.SourceLocationId</c>, encodes it, then decodes the wire bytes via the codec's <c>Decode</c> and asserts the siteId round-trips.
/// Covers both the generator-emitted <c>EncodeTo</c> path (events with <c>[TraceEvent(..., EmitEncoder = true)]</c>) and the hand-written codec
/// path (the 9 events keeping their hand-written <c>EncodeTo</c>: 5× PageCache, 4× Transaction, EcsQueryAny).
/// </summary>
/// <remarks>
/// <para>
/// The legacy-bytes test (<see cref="ZeroSiteId_ProducesByteIdenticalLegacyOutput"/>) is the backward-compatibility guarantor — it asserts that pre-#302
/// traces remain byte-identical when source attribution is disabled (<c>Header.SourceLocationId == 0</c>).
/// </para>
/// <para>
/// See <c>claude/design/observability/10-profiler-source-attribution.md</c> §4 for the wire-format design
/// and <c>TraceRecordHeader.SpanFlagsHasSourceLocation</c> for the flag bit definition.
/// </para>
/// </remarks>
[TestFixture]
public class SourceLocationCodecRoundTripTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // Generator-emitted EncodeTo path — events with [TraceEvent(EmitEncoder = true)]
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void BTreeInsert_WithSiteId_NoTraceContext_RoundTripsViaCodec()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new BTreeInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = 1,
                StartTimestamp = 1000,
                SpanId = 0xAABBUL,
                ParentSpanId = 0xCCDDUL,
                SourceLocationId = 0x012F,
            },
        };

        evt.EncodeTo(buffer, endTimestamp: 1500, out var written);
        Assert.That(written, Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize + 2),
            "37-byte span header + 2-byte siteId = 39 bytes");

        var decoded = BTreeEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0x012F));
        Assert.That(decoded.HasSourceLocation, Is.True);
        Assert.That(decoded.HasTraceContext, Is.False);
        Assert.That(decoded.SpanId, Is.EqualTo(0xAABBUL));
        Assert.That(decoded.ParentSpanId, Is.EqualTo(0xCCDDUL));
    }

    [Test]
    public void BTreeInsert_WithSiteIdAndTraceContext_RoundTripsViaCodec()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new BTreeInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = 1,
                StartTimestamp = 1000,
                SpanId = 1,
                TraceIdHi = 0x1122334455667788UL,
                TraceIdLo = 0x99AABBCCDDEEFF00UL,
                SourceLocationId = 0xBEEF,
            },
        };

        evt.EncodeTo(buffer, endTimestamp: 1500, out var written);
        Assert.That(written, Is.EqualTo(TraceRecordHeader.MaxSpanHeaderSize + 2),
            "53-byte span header (with trace ctx) + 2-byte siteId = 55 bytes");

        var decoded = BTreeEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0xBEEF));
        Assert.That(decoded.HasSourceLocation, Is.True);
        Assert.That(decoded.HasTraceContext, Is.True);
        Assert.That(decoded.TraceIdHi, Is.EqualTo(0x1122334455667788UL));
        Assert.That(decoded.TraceIdLo, Is.EqualTo(0x99AABBCCDDEEFF00UL));
    }

    [Test]
    public void ZeroSiteId_ProducesByteIdenticalLegacyOutput()
    {
        // Two events: one with SourceLocationId explicitly = 0, one with the field uninitialized.
        // Both must produce identical wire bytes — no flag bit, no extra payload, byte-identical to pre-#302.
        Span<byte> withZero = stackalloc byte[128];
        Span<byte> baseline = stackalloc byte[128];

        var evtZero = new BTreeInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = 7,
                StartTimestamp = 1000,
                SpanId = 0xAABBUL,
                ParentSpanId = 0xCCDDUL,
                SourceLocationId = 0,
            },
        };
        var evtNoField = new BTreeInsertEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = 7,
                StartTimestamp = 1000,
                SpanId = 0xAABBUL,
                ParentSpanId = 0xCCDDUL,
            },
        };

        evtZero.EncodeTo(withZero, endTimestamp: 2000, out var w1);
        evtNoField.EncodeTo(baseline, endTimestamp: 2000, out var w2);

        Assert.That(w1, Is.EqualTo(w2));
        Assert.That(withZero[..w1].ToArray(), Is.EqualTo(baseline[..w2].ToArray()));

        // Round-trip via codec — siteId should decode as 0, no flag bits.
        var decoded = BTreeEventCodec.Decode(withZero[..w1]);
        Assert.That(decoded.HasSourceLocation, Is.False);
        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hand-written PageCacheEventCodec path — 5 events
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void PageCacheFetch_WithSiteId_RoundTripsViaCodec()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new PageCacheFetchEvent
        {
            Header = new TraceSpanHeader { ThreadSlot = 2, StartTimestamp = 100, SpanId = 5, SourceLocationId = 0x1234 },
            FilePageIndex = 42,
        };

        evt.EncodeTo(buffer, endTimestamp: 200, out var written);
        var decoded = PageCacheEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0x1234));
        Assert.That(decoded.HasSourceLocation, Is.True);
        Assert.That(decoded.FilePageIndex, Is.EqualTo(42));
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.PageCacheFetch));
    }

    [Test]
    public void PageCacheDiskWrite_WithOptionalsAndSiteIdAndTraceContext_RoundTripsViaCodec()
    {
        // Stress test: optional payload field + trace context + source-location all coexisting.
        Span<byte> buffer = stackalloc byte[128];
        var evt = new PageCacheDiskWriteEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = 3, StartTimestamp = 100, SpanId = 7,
                TraceIdHi = 0xABCD0000UL, TraceIdLo = 0xEF010203UL,
                SourceLocationId = 0xCAFE,
            },
            FilePageIndex = 99,
        };
        evt.PageCount = 17; // sets optMask bit

        evt.EncodeTo(buffer, endTimestamp: 200, out var written);
        var decoded = PageCacheEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0xCAFE));
        Assert.That(decoded.HasSourceLocation, Is.True);
        Assert.That(decoded.HasTraceContext, Is.True);
        Assert.That(decoded.HasPageCount, Is.True);
        Assert.That(decoded.PageCount, Is.EqualTo(17));
        Assert.That(decoded.FilePageIndex, Is.EqualTo(99));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hand-written TransactionEventCodec path — 4 events
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TransactionCommit_WithSiteId_RoundTripsViaCodec()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new TransactionCommitEvent
        {
            Header = new TraceSpanHeader { ThreadSlot = 4, StartTimestamp = 100, SpanId = 11, SourceLocationId = 0x9999 },
            Tsn = 0x1234567890ABCDEFL,
        };

        evt.EncodeTo(buffer, endTimestamp: 200, out var written);
        var decoded = TransactionEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0x9999));
        Assert.That(decoded.HasSourceLocation, Is.True);
        Assert.That(decoded.Tsn, Is.EqualTo(0x1234567890ABCDEFL));
    }

    [Test]
    public void TransactionCommitComponent_WithSiteId_DifferentLayout_RoundTripsViaCodec()
    {
        // CommitComponent has its own kind-specific payload field (componentTypeId).
        Span<byte> buffer = stackalloc byte[128];
        var evt = new TransactionCommitComponentEvent
        {
            Header = new TraceSpanHeader { ThreadSlot = 4, StartTimestamp = 100, SpanId = 12, SourceLocationId = 0x3333 },
            Tsn = 42L,
            ComponentTypeId = 7,
        };

        evt.EncodeTo(buffer, endTimestamp: 200, out var written);
        var decoded = TransactionEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0x3333));
        Assert.That(decoded.ComponentTypeId, Is.EqualTo(7));
        Assert.That(decoded.Kind, Is.EqualTo(TraceEventKind.TransactionCommitComponent));
    }

    [Test]
    public void TransactionPersist_WithSiteId_RoundTripsViaPersistDecoder()
    {
        // Persist has its own codec path (EncodePersist/DecodePersist).
        Span<byte> buffer = stackalloc byte[128];
        var evt = new TransactionPersistEvent
        {
            Header = new TraceSpanHeader { ThreadSlot = 4, StartTimestamp = 100, SpanId = 13, SourceLocationId = 0x7777 },
            Tsn = 0xABCD1234L,
        };
        evt.WalLsn = 999L; // sets optMask

        evt.EncodeTo(buffer, endTimestamp: 200, out var written);
        var decoded = TransactionEventCodec.DecodePersist(buffer[..written]);

        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0x7777));
        Assert.That(decoded.HasWalLsn, Is.True);
        Assert.That(decoded.WalLsn, Is.EqualTo(999L));
        Assert.That(decoded.Tsn, Is.EqualTo(0xABCD1234L));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hand-written EcsQueryEventCodec path (EcsQueryAny) — and verify shared
    // Decode handles generator-emitted EcsQueryExecute too
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void EcsQueryAny_WithSiteId_RoundTripsViaCodec()
    {
        Span<byte> buffer = stackalloc byte[128];
        var evt = new EcsQueryAnyEvent
        {
            Header = new TraceSpanHeader { ThreadSlot = 5, StartTimestamp = 100, SpanId = 21, SourceLocationId = 0x5A5A },
            ArchetypeTypeId = 8,
        };
        evt.Found = true; // sets OptFound

        evt.EncodeTo(buffer, endTimestamp: 200, out var written);
        var decoded = EcsQueryEventCodec.Decode(buffer[..written]);

        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0x5A5A));
        Assert.That(decoded.HasFound, Is.True);
        Assert.That(decoded.Found, Is.True);
        Assert.That(decoded.ArchetypeTypeId, Is.EqualTo((ushort)8));
    }

    [Test]
    public void PageCacheBackpressure_WithSiteId_DecoderHandlesGeneratorEmittedEncodeTo()
    {
        // PageCacheBackpressure has [TraceEvent(EmitEncoder = true)] — the EncodeTo is generator-emitted, but
        // the Decode lives in the hand-written PageCacheBackpressureCodec class. Confirms the decoder reads
        // the optional source-location bytes that the generator-emitted encoder writes.
        Span<byte> buffer = stackalloc byte[128];
        var evt = new PageCacheBackpressureEvent
        {
            Header = new TraceSpanHeader { ThreadSlot = 6, StartTimestamp = 100, SpanId = 31, SourceLocationId = 0xF00D },
            RetryCount = 3,
            DirtyCount = 7,
            EpochCount = 11,
        };

        evt.EncodeTo(buffer, endTimestamp: 200, out var written);
        var decoded = PageCacheBackpressureCodec.Decode(buffer[..written]);

        Assert.That(decoded.SourceLocationId, Is.EqualTo((ushort)0xF00D));
        Assert.That(decoded.HasSourceLocation, Is.True);
        Assert.That(decoded.RetryCount, Is.EqualTo(3));
        Assert.That(decoded.DirtyCount, Is.EqualTo(7));
        Assert.That(decoded.EpochCount, Is.EqualTo(11));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Generator-emitted factory pair sanity (compile-time existence check)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void BeginBTreeInsert_PassThroughForwarder_PreservesZeroSiteId()
    {
        // The generator emits TyphonEvent.BeginBTreeInsert() as a thin pass-through to BeginBTreeInsert_WithSiteId(0).
        // When the profiler is inactive (default in tests), the factory returns default(BTreeInsertEvent) —
        // siteId is 0. This test compiles only because both factories exist (the pair emission worked).
        var evt = TyphonEvent.BeginBTreeInsert();
        Assert.That(evt.Header.SourceLocationId, Is.EqualTo((ushort)0));
    }

    [Test]
    public void BeginBTreeInsert_WithSiteId_FactoryExistsAndAcceptsLiteral()
    {
        // Compile-time existence of the WithSiteId pair. Runtime behavior with a non-zero siteId requires
        // an active profiler; this test just asserts the factory binds.
        var evt = TyphonEvent.BeginBTreeInsert_WithSiteId(0x1234);
        // When inactive, returns default — SourceLocationId is 0. When active, would be 0x1234.
        Assert.That(evt.Header.SourceLocationId, Is.EqualTo(0).Or.EqualTo((ushort)0x1234));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Wire-format invariants — flag bit, offsets
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpanFlagsHasSourceLocation_BitIsDistinctFromTraceContext()
    {
        Assert.That(TraceRecordHeader.SpanFlagsHasSourceLocation,
            Is.Not.EqualTo(TraceRecordHeader.SpanFlagsHasTraceContext),
            "Flag bits must not collide");
        Assert.That(TraceRecordHeader.SpanFlagsHasSourceLocation & TraceRecordHeader.SpanFlagsHasTraceContext,
            Is.Zero, "Flag bits must be in different positions (bitwise AND must be zero)");
    }

    [Test]
    public void SpanHeaderSize_AccountsForOptionalSourceLocation()
    {
        Assert.That(TraceRecordHeader.SpanHeaderSize(false, false), Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize));
        Assert.That(TraceRecordHeader.SpanHeaderSize(true, false), Is.EqualTo(TraceRecordHeader.MaxSpanHeaderSize));
        Assert.That(TraceRecordHeader.SpanHeaderSize(false, true), Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize + 2));
        Assert.That(TraceRecordHeader.SpanHeaderSize(true, true), Is.EqualTo(TraceRecordHeader.MaxSpanHeaderSize + 2));
    }

    [Test]
    public void SourceLocationIdOffset_LandsAfterTraceContext()
    {
        Assert.That(TraceRecordHeader.SourceLocationIdOffset(false), Is.EqualTo(TraceRecordHeader.MinSpanHeaderSize),
            "Without trace ctx, siteId starts at offset 37");
        Assert.That(TraceRecordHeader.SourceLocationIdOffset(true), Is.EqualTo(TraceRecordHeader.MaxSpanHeaderSize),
            "With trace ctx, siteId starts at offset 53");
    }
}
