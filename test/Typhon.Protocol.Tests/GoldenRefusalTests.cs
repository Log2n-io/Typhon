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
        var drone = (byte)Kitchen.ArchetypeByName("Drone").Idx;
        // Drone has two owner groups, so ownerMask bit 2 names none.
        Tick(cases, "self-owner-mask-unknown-group", Block(BlockTypes.Self, drone, 0x01, 0x00, 0x00, 0x04));
        // netId 0 is no controlled entity (W17′): it names archetype 0 and carries no owner group.
        Tick(cases, "self-no-entity-with-owner-mask", Block(BlockTypes.Self, 0x00, 0x00, 0x00, 0x00, 0x01));
        Tick(cases, "self-no-entity-naming-an-archetype", Block(BlockTypes.Self, 0x01, 0x00, 0x00, 0x00, 0x00));
        Tick(cases, "debug-sub-block-overrun", Block(BlockTypes.Debug, 0x01, 0x05, 0xAA));
        Tick(cases, "period-truncated", [MessageTypes.Tick, 0x70, 0x11, 0x01, 0x00, (byte)TickFlags.Period, 0x01, 0x02]);
        byte[] emptyLedger = [BlockTypes.Entities, 0x05, ledger, 0x00, 0x00, 0x00, 0x00];
        Tick(cases, "entities-block-twice-for-one-archetype", [MessageTypes.Tick, 0x70, 0x11, 0x01, 0x00, 0x00, .. emptyLedger, .. emptyLedger]);

        // typhon.3 (12-realms § 5.2): a REALM only as the first block of a RESET frame, with a valid frame; nothing positioned while no realm is held.
        byte[] realm = Realm(CatalogSamples.KitchenFrame);
        byte[] reset = [MessageTypes.Tick, 0x70, 0x11, 0x01, 0x00, (byte)TickFlags.Reset];
        Tick(cases, "realm-without-reset", [MessageTypes.Tick, 0x70, 0x11, 0x01, 0x00, 0x00, .. realm]);
        Tick(cases, "realm-not-first", [.. reset, .. emptyLedger, .. realm]);
        Tick(cases, "realm-bits-20", [.. reset, .. RealmWith(realm, 10, 20)]);
        Tick(cases, "realm-min-not-below-max", [.. reset, .. RealmWith(realm, 43, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0xC0)]);
        Tick(cases, "realm-kind-out-of-range", [.. reset, .. RealmWith(realm, 5, 0x03)]);
        Tick(cases, "realm-reserved-flag", [.. reset, .. RealmWith(realm, 4, 0x06)]);
        Tick(cases, "realm-cell-zero", [.. reset, .. RealmWith(realm, 11, 0, 0, 0, 0, 0, 0, 0, 0)]);
        Tick(cases, "realm-nan-bound", [.. reset, .. RealmWith(realm, 19, 0, 0, 0, 0, 0, 0, 0xF8, 0x7F)]);
        // −f64.Max … +f64.Max: each finite, but the extent is not, so no quantum exists.
        Tick(cases, "realm-extent-overflows", [.. reset, .. RealmWith(RealmWith(realm, 19, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEF, 0xFF), 43,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEF, 0x7F)]);
        // REALM(NONE) is id 0xFFFF, generation 0: one that names realm 3 is two answers in one block.
        Tick(cases, "realm-none-naming-a-realm", [.. reset, BlockTypes.Realm, 0x05, 0x03, 0x00, 0x00, 0x00, 0x01]);
        Tick(cases, "debug-geometry-without-realm", Block(BlockTypes.Debug, DebugSubTypes.PushGeometry, 0x00), CloseCodes.ProtocolError, held: false);
        Tick(cases, "positioned-entities-without-realm", Block(BlockTypes.Entities, beacon, 0x00, 0x00, 0x00, 0x00), CloseCodes.ProtocolError, held: false);
        Tick(cases, "aggregate-without-realm", Block(BlockTypes.Agg, 0x00, 0x00, 0x00), CloseCodes.ProtocolError, held: false);

        var steer = (byte)Kitchen.CommandByName("Steer").Idx;
        Commands(cases, "commands-count-zero", [MessageTypes.Commands, 0, 0, 0, 0, 0x00]);
        Commands(cases, "commands-unknown-type", [MessageTypes.Commands, 0, 0, 0, 0, 0x01, 0x63, 0x00, 0x00]);
        // Steer: pack (boost, stance), heading i8, note str, speed u8. Stance = 3 with 3 names is one past the last name.
        Commands(cases, "commands-enum-out-of-range", [MessageTypes.Commands, 0, 0, 0, 0, 0x01, steer, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00]);
        Commands(cases, "commands-trailing-bytes", [MessageTypes.Commands, 0, 0, 0, 0, 0x01, steer, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xEE]);

        var kind33 = new byte[33];
        kind33.AsSpan().Fill((byte)'k');
        Message(cases, "hello-kind-over-cap", MessageTypes.Hello, [MessageTypes.Hello, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x21, .. kind33]);
        // A token of 8193 bytes (varu 0x81 0x40), one over the cap, then a complete rest of the message: only the cap refuses it.
        var token = new byte[8193];
        token.AsSpan().Fill((byte)'t');
        Message(cases, "hello-token-over-cap", MessageTypes.Hello,
            [MessageTypes.Hello, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x81, 0x40, .. token, .. new byte[24], 0x00]);
        var reason124 = new byte[124];
        reason124.AsSpan().Fill((byte)'r');
        Message(cases, "kick-reason-over-cap", MessageTypes.Kick, [MessageTypes.Kick, 0xE8, 0x03, 0x7C, .. reason124]);
        // minor, caps, sessionId, resumeToken, tick, period and hash (42 bytes), then a catalog length of 10 with nothing after it.
        Message(cases, "welcome-catalog-length-overrun", MessageTypes.Welcome, [MessageTypes.Welcome, 0x02, 0x00, .. new byte[42], 0x0A]);
        Message(cases, "bye-browser-invalid-code", MessageTypes.Bye, [MessageTypes.Bye, 0xE9, 0x03]);
        Message(cases, "ping-trailing-byte", MessageTypes.Ping, [MessageTypes.Ping, 0, 0, 0, 0, 0, 0, 0, 0, 0xEE]);
        Message(cases, "wrong-message-type", MessageTypes.Pong, [MessageTypes.Ping, 0, 0, 0, 0, 0, 0, 0, 0], CloseCodes.ProtocolError);

        Golden.Assert("wire-refusals", [], new JsonObject
        {
            ["description"] = "Byte sequences every decoder must refuse, with the close code it must refuse them with. kind: codec (one value of `codec`), "
                + "tick and commands (a whole message against catalog-kitchen-sink; a tick case decodes with `frame` held unless it says held: false), "
                + "message (a whole control message of `type`).",
            ["catalog"] = "catalog-kitchen-sink",
            ["frame"] = CatalogSamples.FrameJson(CatalogSamples.KitchenFrame),
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
            Protocol = new CatalogProtocolVersion { Major = 3 },
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

    private static void Tick(JsonArray cases, string name, byte[] bytes, ushort expected = CloseCodes.MalformedPayload, bool held = true)
    {
        var code = Refusal(name, bytes, b =>
        {
            var sink = new RecordingSink();
            var frame = held ? CatalogSamples.KitchenFrame : null;
            TickReader.Read(b, Kitchen, ref frame, ref sink);
        });

        Assert.That(code, Is.EqualTo(expected), name);
        var json = new JsonObject { ["name"] = name, ["kind"] = "tick", ["hex"] = Golden.Hex(bytes), ["closeCode"] = code };
        if (!held)
        {
            json["held"] = false;
        }

        cases.Add(json);
    }

    /// <summary>A whole REALM block: type, length, content.</summary>
    private static byte[] Realm(RealmFrame frame)
    {
        var buffer = new byte[128];
        var w = new WireWriter(buffer);
        TickWriter.WriteRealm(ref w, frame);
        return w.Written.ToArray();
    }

    /// <summary>
    /// A REALM block with the bytes at content offset <paramref name="at"/> replaced: id 0, generation 2, flags 4, kind 5, tag 6, bits 10, cell 11, min 19,
    /// max 43 (after the type and a one-byte length).
    /// </summary>
    private static byte[] RealmWith(byte[] block, int at, params byte[] bytes)
    {
        var copy = (byte[])block.Clone();
        bytes.CopyTo(copy, 2 + at);
        return copy;
    }

    private static void Commands(JsonArray cases, string name, byte[] bytes)
    {
        var delivered = 0;
        var code = Refusal(name, bytes, b =>
        {
            var sink = new RecordingSink();
            CommandsMessage.Read(b, Kitchen, ref sink, CatalogSamples.KitchenFrame);
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
                case MessageTypes.Welcome:
                    WelcomeMessage.Parse(b);
                    break;
                case MessageTypes.Kick:
                    KickMessage.Parse(b);
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
