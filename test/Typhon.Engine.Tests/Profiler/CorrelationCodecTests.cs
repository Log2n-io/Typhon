using System;
using System.Text;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Profiler.Events;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>The request-correlation records (#1517) round-trip through their codecs and the decoder, and are classified by shape.</summary>
[TestFixture]
public class CorrelationCodecTests
{
    [Test]
    public void ExternalRequest_round_trips_with_its_trace_context_and_route()
    {
        var route = Encoding.UTF8.GetBytes("/swg/inventory");
        var buffer = new byte[ExternalRequestEventCodec.ComputeSize(true, route.Length)];
        ExternalRequestEventCodec.Encode(buffer, 4, 1_000, 1_750, 99, 0, 0x1111, 0x2222, ExternalRequestMethod.Post, 201, route);

        var d = ExternalRequestEventCodec.Decode(buffer);
        Assert.Multiple(() =>
        {
            Assert.That(d.ThreadSlot, Is.EqualTo(4));
            Assert.That(d.StartTimestamp, Is.EqualTo(1_000));
            Assert.That(d.DurationTicks, Is.EqualTo(750));
            Assert.That(d.SpanId, Is.EqualTo(99UL));
            Assert.That((d.TraceIdHi, d.TraceIdLo), Is.EqualTo((0x1111UL, 0x2222UL)));
            Assert.That(d.Method, Is.EqualTo(ExternalRequestMethod.Post));
            Assert.That(d.Status, Is.EqualTo(201));
            Assert.That(d.Route, Is.EqualTo("/swg/inventory"));
        });

        var dto = (ExternalRequestEventDto)TraceEventDecoder.Decode(buffer, 7, 1);
        Assert.Multiple(() =>
        {
            Assert.That(dto.Route, Is.EqualTo("/swg/inventory"));
            Assert.That(dto.Method, Is.EqualTo("Post"));
            Assert.That(dto.Status, Is.EqualTo(201));
            Assert.That(dto.DurationUs, Is.EqualTo(750));
            Assert.That(dto.TraceIdHi, Is.EqualTo("4369"));
        });
    }

    [Test]
    public void ExternalRequest_without_a_trace_id_omits_the_context_and_cuts_a_long_route()
    {
        var route = Encoding.UTF8.GetBytes(new string('r', 300));
        var buffer = new byte[ExternalRequestEventCodec.ComputeSize(false, route.Length)];
        ExternalRequestEventCodec.Encode(buffer, 0, 10, 20, 1, 0, 0, 0, ExternalRequestMethod.Get, 200, route);

        var d = ExternalRequestEventCodec.Decode(buffer);
        Assert.That((d.TraceIdHi, d.TraceIdLo), Is.EqualTo((0UL, 0UL)));
        Assert.That(d.Route, Has.Length.EqualTo(ExternalRequestEventCodec.MaxRouteBytes));
        Assert.That(buffer, Has.Length.EqualTo(TraceRecordHeader.MinSpanHeaderSize + 5 + ExternalRequestEventCodec.MaxRouteBytes));
    }

    [Test]
    public void Lifecycle_instants_round_trip()
    {
        var b = new byte[64];

        CorrelationInstantCodec.WriteReceived(b, 1, 100, 7, 3, 12, 300);
        AssertDecoded(b, TraceEventKind.IngressReceived,
            d => Assert.That((d.Session, d.Seq, d.WireIdx, d.Bytes), Is.EqualTo((7u, (ushort)3, (ushort)12, 300u))));

        CorrelationInstantCodec.WriteAdmitted(b, 1, 200, 7, 3, 12, 4_000_000);
        AssertDecoded(b, TraceEventKind.IngressAdmitted,
            d => Assert.That((d.Session, d.Seq, d.WireIdx, d.Tick), Is.EqualTo((7u, (ushort)3, (ushort)12, 4_000_000u))));

        CorrelationInstantCodec.WriteDropped(b, 1, 300, 7, 4, IngressDropReason.RingFull);
        AssertDecoded(b, TraceEventKind.IngressDropped,
            d => Assert.That((d.Session, d.Seq, d.Reason), Is.EqualTo((7u, (ushort)4, IngressDropReason.RingFull))));

        CorrelationInstantCodec.WriteConsumed(b, 2, 400, 7, 3, 21);
        AssertDecoded(b, TraceEventKind.CommandConsumed, d => Assert.That((d.Session, d.Seq, d.SysIdx), Is.EqualTo((7u, (ushort)3, (ushort)21))));

        CorrelationInstantCodec.WriteSent(b, 3, 500, 7, 4_000_000, 70_000, 2);
        AssertDecoded(b, TraceEventKind.EgressSent, d => Assert.That((d.Session, d.Tick, d.Bytes, d.Acks), Is.EqualTo((7u, 4_000_000u, 70_000u, (ushort)2))));
    }

    [Test]
    public void Lifecycle_instants_decode_to_the_correlation_dto()
    {
        var b = new byte[CorrelationInstantCodec.SizeOf(TraceEventKind.IngressDropped)];
        CorrelationInstantCodec.WriteDropped(b, 1, 300, 7, 4, IngressDropReason.RateLimited);

        var dto = (CorrelationInstantEventDto)TraceEventDecoder.Decode(b, 0, 1);
        Assert.That((dto.Event, dto.Session, dto.Seq, dto.Reason), Is.EqualTo(("IngressDropped", 7L, 4, "RateLimited")));
    }

    [TestCase(TraceEventKind.ExternalRequest, ExpectedResult = true)]
    [TestCase(TraceEventKind.IngressReceived, ExpectedResult = false)]
    [TestCase(TraceEventKind.IngressAdmitted, ExpectedResult = false)]
    [TestCase(TraceEventKind.IngressDropped, ExpectedResult = false)]
    [TestCase(TraceEventKind.CommandConsumed, ExpectedResult = false)]
    [TestCase(TraceEventKind.EgressSent, ExpectedResult = false)]
    public bool Correlation_kinds_are_classified_by_their_wire_shape(TraceEventKind kind) => kind.IsSpan();

    [Test]
    public void A_command_causal_id_is_marked_unique_per_tick_and_never_zero()
    {
        var a = CorrelationInstantCodec.CommandCausalId(7, 100, 3);
        var b = CorrelationInstantCodec.CommandCausalId(7, 101, 3);
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.Not.EqualTo(b), "the same seq in another tick is another command");
            Assert.That(CorrelationInstantCodec.IsCommandCausalId(a.Hi), Is.True);
            Assert.That(CorrelationInstantCodec.CommandCausalId(0, 0, 0).Hi, Is.Not.Zero);
            Assert.That(CorrelationInstantCodec.IsCommandCausalId(0x0af7651916cd43ddUL), Is.False, "a W3C trace id is not a command's");
        });
    }

    /// <summary>
    /// The encoders against fixed bytes — the same goldens the Workbench's <c>correlationKinds.test.ts</c> decodes, so a field moved on either side
    /// fails one of the two.
    /// </summary>
    [Test]
    public void Encoders_match_the_goldens_the_workbench_decodes()
    {
        var route = Encoding.UTF8.GetBytes("/a/{id}");
        var er = new byte[ExternalRequestEventCodec.ComputeSize(true, route.Length)];
        ExternalRequestEventCodec.Encode(er, 3, 1000, 1750, 99, 0, 0x1111, 0x2222, ExternalRequestMethod.Post, 201, route);
        Assert.That(Convert.ToHexString(er), Is.EqualTo(
            "4100EB03E803000000000000EE0200000000000063000000000000000000000000000000011111000000000000222200000000000002C90007002F612F7B69647D"));

        var rc = new byte[CorrelationInstantCodec.SizeOf(TraceEventKind.IngressReceived)];
        CorrelationInstantCodec.WriteReceived(rc, 1, 100, 7, 3, 12, 300);
        Assert.That(Convert.ToHexString(rc), Is.EqualTo("1600EC0164000000000000000700000003000C002C01"));

        var st = new byte[CorrelationInstantCodec.SizeOf(TraceEventKind.EgressSent)];
        CorrelationInstantCodec.WriteSent(st, 1, 500, 7, 4_000_000, 70_000, 2);
        Assert.That(Convert.ToHexString(st), Is.EqualTo("1A00F001F4010000000000000700000000093D00701101000200"));

        var ad = new byte[CorrelationInstantCodec.SizeOf(TraceEventKind.IngressAdmitted)];
        CorrelationInstantCodec.WriteAdmitted(ad, 1, 200, 7, 3, 12, 4_000_000);
        Assert.That(Convert.ToHexString(ad), Is.EqualTo("1800ED01C8000000000000000700000003000C0000093D00"));

        var dr = new byte[CorrelationInstantCodec.SizeOf(TraceEventKind.IngressDropped)];
        CorrelationInstantCodec.WriteDropped(dr, 1, 300, 7, 4, IngressDropReason.RateLimited);
        Assert.That(Convert.ToHexString(dr), Is.EqualTo("1300EE012C0100000000000007000000040002"));

        var co = new byte[CorrelationInstantCodec.SizeOf(TraceEventKind.CommandConsumed)];
        CorrelationInstantCodec.WriteConsumed(co, 2, 400, 7, 3, 21);
        Assert.That(Convert.ToHexString(co), Is.EqualTo("1400EF0290010000000000000700000003001500"));
    }

    private static void AssertDecoded(byte[] b, TraceEventKind kind, Action<CorrelationInstantData> check)
    {
        var d = CorrelationInstantCodec.Decode(b.AsSpan(0, CorrelationInstantCodec.SizeOf(kind)));
        Assert.That(d.Kind, Is.EqualTo(kind));
        Assert.That(BitConverter.ToUInt16(b, 0), Is.EqualTo(CorrelationInstantCodec.SizeOf(kind)), "the size prefix");
        check(d);
    }
}
