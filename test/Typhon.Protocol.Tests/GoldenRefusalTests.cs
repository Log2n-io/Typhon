using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The refusal vector: byte sequences every decoder must reject, each with the close code it must reject them with. Acceptance vectors pin what a decoder
/// reads; this one pins what it must not — the half a lenient decoder gets wrong silently.
/// </summary>
/// <remarks>
/// Each case is one of four kinds, so a client runs it through the matching entry point: <c>codec</c> (decode one value of <c>codec</c> from the bytes),
/// <c>tick</c> (decode a TICK message against <c>catalog-kitchen-sink</c>), <c>commands</c> (decode a COMMANDS message against the same catalog, the
/// server's side) and <c>message</c> (parse a whole control message of the given type).
/// </remarks>
[TestFixture]
public class GoldenRefusalTests
{
    private static readonly CatalogPlan Kitchen = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink()));

    [Test]
    public void Refusals()
    {
        var cases = new JsonArray();

        Codec(cases, "varu-over-32-bits", new CatalogCodec { Kind = CodecKind.Varu }, [0xFF, 0xFF, 0xFF, 0xFF, 0x10]);
        Codec(cases, "varu-six-bytes", new CatalogCodec { Kind = CodecKind.Varu }, [0x80, 0x80, 0x80, 0x80, 0x80, 0x00]);
        Codec(cases, "varu-truncated", new CatalogCodec { Kind = CodecKind.Varu }, [0x80]);
        Codec(cases, "u32-truncated", new CatalogCodec { Kind = CodecKind.U32 }, [0x01, 0x02]);
        Codec(cases, "bytes-truncated", new CatalogCodec { Kind = CodecKind.Bytes, N = 4 }, [0x01, 0x02]);
        Codec(cases, "str-over-cap", new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 4 }, [0x05, 0x31, 0x32, 0x33, 0x34, 0x35]);
        Codec(cases, "str-invalid-utf8", new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 8 }, [0x02, 0xC3, 0x28]);
        Codec(cases, "str-overlong-utf8", new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 8 }, [0x02, 0xC0, 0x80]);
        Codec(cases, "str-encoded-surrogate", new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 8 }, [0x03, 0xED, 0xA0, 0x80]);
        Codec(cases, "str-above-u10ffff", new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 8 }, [0x04, 0xF4, 0x90, 0x80, 0x80]);
        Codec(cases, "str-truncated-sequence", new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 8 }, [0x02, 0xE2, 0x82]);
        Codec(cases, "blob-over-cap", new CatalogCodec { Kind = CodecKind.Blob, MaxBytes = 2 }, [0x03, 0x01, 0x02, 0x03]);
        var u8List = new CatalogCodec { Kind = CodecKind.List, Of = new CatalogCodec { Kind = CodecKind.U8 }, MinCount = 2, MaxCount = 3 };
        Codec(cases, "list-over-max", u8List, [0x04, 0x01, 0x02, 0x03, 0x04]);
        Codec(cases, "list-under-min", u8List, [0x01, 0x05]);

        var ledger = (byte)Kitchen.ArchetypeByName("Ledger").Idx;
        var beacon = (byte)Kitchen.ArchetypeByName("Beacon").Idx;
        Tick(cases, "state-mask-zero", Block(BlockTypes.Entities, ledger, 0x00, 0x00, 0x01, 0x07, 0x00, 0x00));
        Tick(cases, "state-mask-unknown-group", Block(BlockTypes.Entities, ledger, 0x00, 0x00, 0x01, 0x07, 0x02, 0x00));
        Tick(cases, "segments-on-a-static-archetype", Block(BlockTypes.Entities, beacon, 0x00, 0x01, 0x03, 0x00, 0x00, 0x00));
        Tick(cases, "unknown-archetype-index", Block(BlockTypes.Entities, 0x63, 0x00, 0x00, 0x00, 0x00));
        Tick(cases, "unknown-event-index", Block(BlockTypes.Events, 0x01, 0x63));
        Tick(cases, "block-content-shorter-than-declared", Block(BlockTypes.Acks, 0x00, 0xEE));
        Tick(cases, "block-length-beyond-message", [MessageTypes.Tick, 0, 0, 0, 0, 0, BlockTypes.Acks, 0x0A, 0x00]);
        Tick(cases, "aggregate-cell-outside-grid", Block(BlockTypes.Agg, 0x00, 0x00, 0x01, 0x80, 0x02, 0x00, 0x00));
        Tick(cases, "sources-unknown-status", Block(BlockTypes.Sources, 0x01, 0x01, 0x00, 0x02));
        Tick(cases, "netid-gap-overflows-32-bits", Block(BlockTypes.Entities, ledger, 0x00, 0x00, 0x00, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x00));
        Tick(cases, "not-a-tick", [MessageTypes.Welcome, 0, 0, 0, 0, 0], CloseCodes.ProtocolError);

        var steer = (byte)Kitchen.CommandByName("Steer").Idx;
        Commands(cases, "commands-count-zero", [MessageTypes.Commands, 0, 0, 0, 0, 0x00]);
        Commands(cases, "commands-unknown-type", [MessageTypes.Commands, 0, 0, 0, 0, 0x01, 0x63, 0x00, 0x00]);
        // Steer: pack (boost, stance), heading i8, note str, speed u8. Stance = 3 with 3 names is one past the last name.
        Commands(cases, "commands-enum-out-of-range", [MessageTypes.Commands, 0, 0, 0, 0, 0x01, steer, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00]);
        Commands(cases, "commands-trailing-bytes", [MessageTypes.Commands, 0, 0, 0, 0, 0x01, steer, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEE]);

        var kind33 = new byte[33];
        kind33.AsSpan().Fill((byte)'k');
        Message(cases, "hello-kind-over-cap", MessageTypes.Hello, [MessageTypes.Hello, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x21, .. kind33]);
        Message(cases, "bye-browser-invalid-code", MessageTypes.Bye, [MessageTypes.Bye, 0xE9, 0x03]);
        Message(cases, "ping-trailing-byte", MessageTypes.Ping, [MessageTypes.Ping, 0, 0, 0, 0, 0, 0, 0, 0, 0xEE]);
        Message(cases, "wrong-message-type", MessageTypes.Pong, [MessageTypes.Ping, 0, 0, 0, 0, 0, 0, 0, 0], CloseCodes.ProtocolError);

        Golden.Assert("wire-refusals", [], new JsonObject
        {
            ["description"] = "Byte sequences every decoder must refuse, with the close code it must refuse them with. kind: codec (one value of `codec`), "
                + "tick and commands (a whole message against catalog-kitchen-sink), message (a whole control message of `type`).",
            ["catalog"] = "catalog-kitchen-sink",
            ["cases"] = cases,
        });
    }

    private static byte[] Block(byte type, params byte[] payload)
    {
        var bytes = new List<byte> { MessageTypes.Tick, 0x70, 0x11, 0x01, 0x00, 0x00, type, (byte)payload.Length };
        bytes.AddRange(payload);
        return [.. bytes];
    }

    private static void Codec(JsonArray cases, string name, CatalogCodec codec, byte[] bytes)
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(new Catalog
        {
            Protocol = new CatalogProtocolVersion { Major = 2 },
            App = new CatalogApp { Name = "Refusal" },
            Tick = new CatalogTick { PeriodUs = 1, PingHz = 1 },
            Limits = new CatalogLimits { FrameBytes = 1 << 20, ClientMessageBytes = 1024 },
            Events = [new CatalogEvent { Name = "E", Scope = "all", Fields = [new CatalogField { Name = "v", Codec = codec }] }],
        })).EventByName("E").Body;

        var code = Refusal(name, bytes, b =>
        {
            var r = new WireReader(b);
            var sink = new RecordingSink();
            FieldCodec.ReadSection(ref r, plan, 0, ref sink);
        });

        cases.Add(new JsonObject
        {
            ["name"] = name, ["kind"] = "codec", ["codec"] = Golden.CodecJson(codec), ["hex"] = Golden.Hex(bytes), ["closeCode"] = code,
        });
    }

    private static void Tick(JsonArray cases, string name, byte[] bytes, ushort expected = CloseCodes.MalformedPayload)
    {
        var code = Refusal(name, bytes, b =>
        {
            var sink = new RecordingSink();
            TickReader.Read(b, Kitchen, ref sink);
        });

        Assert.That(code, Is.EqualTo(expected), name);
        cases.Add(new JsonObject { ["name"] = name, ["kind"] = "tick", ["hex"] = Golden.Hex(bytes), ["closeCode"] = code });
    }

    private static void Commands(JsonArray cases, string name, byte[] bytes)
    {
        var delivered = 0;
        var code = Refusal(name, bytes, b =>
        {
            var sink = new RecordingSink();
            CommandsMessage.Read(b, Kitchen, ref sink);
            delivered = sink.Log.Count;
        });

        Assert.That(delivered, Is.Zero, $"{name}: nothing reaches the sink when the message is refused");
        cases.Add(new JsonObject { ["name"] = name, ["kind"] = "commands", ["hex"] = Golden.Hex(bytes), ["closeCode"] = code });
    }

    private static void Message(JsonArray cases, string name, byte type, byte[] bytes, ushort expected = CloseCodes.MalformedPayload)
    {
        var code = Refusal(name, bytes, b =>
        {
            switch (type)
            {
                case MessageTypes.Hello:
                    HelloMessage.Parse(b);
                    break;
                case MessageTypes.Bye:
                    ByeMessage.Parse(b);
                    break;
                case MessageTypes.Ping:
                    PingMessage.Parse(b);
                    break;
                case MessageTypes.Pong:
                    PongMessage.Parse(b);
                    break;
            }
        });

        Assert.That(code, Is.EqualTo(expected), name);
        cases.Add(new JsonObject { ["name"] = name, ["kind"] = "message", ["type"] = type, ["hex"] = Golden.Hex(bytes), ["closeCode"] = code });
    }

    private static ushort Refusal(string name, byte[] bytes, Action<byte[]> decode)
    {
        var ex = Assert.Throws<WireFormatException>(() => decode(bytes), $"{name} must be refused");
        return ex!.CloseCode;
    }
}
