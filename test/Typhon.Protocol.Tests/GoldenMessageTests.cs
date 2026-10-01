using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// Golden vectors for the catalogs and every message type: <c>HELLO</c>, <c>WELCOME</c> (with and without the catalog), <c>PING</c>, <c>PONG</c>,
/// <c>KICK</c>, <c>BYE</c> and <c>COMMANDS</c>. Each test also decodes what it encoded, so the committed expectation is known to round-trip.
/// </summary>
[TestFixture]
public class GoldenMessageTests
{
    private static readonly byte[] Token16 = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    [Test]
    public void Catalogs()
    {
        foreach (var (name, catalog, description) in new[]
        {
            ("catalog-swg", CatalogSamples.Swg(),
                "The worked example from 03-wire-protocol § 4 in its decided shape: moving positions, a packed enum, onEnter fields, ClientRegion."),
            ("catalog-kitchen-sink", CatalogSamples.KitchenSink(),
                "Every construct the wire has: every codec, all position kinds, an owner section, an unknown codec, labelled and session metrics."),
            ("catalog-exact", CatalogSamples.Exact(),
                "The exact wire (W32, W33): u64, i64, f64, varu64 and vari64 on onEnter, group and owner fields, count shapes with their hints, and the "
                + "same codecs on an event and a command."),
        })
        {
            var bytes = CatalogSerializer.ToCanonicalUtf8(catalog);
            var reordered = name == "catalog-swg" ? CatalogSerializer.ToCanonicalUtf8(CatalogSamples.SwgReordered()) : bytes;
            Assert.That(reordered, Is.EqualTo(bytes), "a reordered declaration must produce the same vector");
            Golden.Assert(name, bytes, new JsonObject
            {
                ["description"] = description,
                ["hash"] = CatalogSerializer.ToHex(CatalogSerializer.ComputeHash(catalog)),
                ["byteCount"] = bytes.Length,
            });
        }
    }

    [Test]
    public void Hello()
    {
        var full = new HelloMessage
        {
            Caps = Capabilities.Stats | Capabilities.Debug, Kind = "player", Token = "opaque-token", ResumeToken = Token16,
            ClientCatalogHash = 0x689b085895503760UL, HelloPayload = [0xCA, 0xFE],
        };

        AssertMessage("message-hello", "HELLO with every optional field present.", (ref WireWriter w) => full.Write(ref w), HelloJson);
        AssertMessage("message-hello-minimal", "HELLO with every optional field absent: zero resume token, zero hash, empty payload (W19).",
            (ref WireWriter w) => new HelloMessage { Kind = "god", Token = "" }.Write(ref w), HelloJson);
    }

    [Test]
    public void Welcome()
    {
        var catalog = CatalogSerializer.ToCanonicalUtf8(CatalogSamples.Swg());
        var hash = CatalogSerializer.ComputeHash(CatalogSamples.Swg());

        AssertMessage("message-welcome", "WELCOME carrying the catalog-swg vector's bytes and hash.",
            (ref WireWriter w) => new WelcomeMessage
            {
                CapsGranted = Capabilities.Stats, SessionId = 42, ResumeToken = Token16, Tick = 70_000, TickPeriodUs = 100_000, CatalogHash = hash,
                CatalogJson = catalog,
            }.Write(ref w), WelcomeJson);
        AssertMessage("message-welcome-skip", "WELCOME whose catalog is skipped because the client's hash matched (length 0), resume disabled.",
            (ref WireWriter w) => new WelcomeMessage { SessionId = 7, Tick = 1, TickPeriodUs = 50_000, CatalogHash = hash }.Write(ref w), WelcomeJson);
    }

    [Test]
    public void SmallMessages()
    {
        AssertMessage("message-ping", "PING.", (ref WireWriter w) => new PingMessage(123456, 69_999).Write(ref w), (ref WireReader r) =>
        {
            var m = PingMessage.Read(ref r);
            return new JsonObject { ["clientMs"] = m.ClientMs, ["lastAppliedTick"] = m.LastAppliedTick };
        });

        AssertMessage("message-pong", "PONG with usIntoTick 99 999, which a u16 could not hold (W31).",
            (ref WireWriter w) => new PongMessage(123456, 70_000, 99_999).Write(ref w), (ref WireReader r) =>
        {
            var m = PongMessage.Read(ref r);
            return new JsonObject { ["clientMs"] = m.ClientMs, ["tick"] = m.Tick, ["usIntoTick"] = m.UsIntoTick };
        });

        AssertMessage("message-kick", "KICK 1013 with a reason truncated to 123 UTF-8 bytes at a code-point boundary (W24).",
            (ref WireWriter w) => new KickMessage(CloseCodes.TryAgainLater, new string('a', 122) + "€ lagging").Write(ref w), (ref WireReader r) =>
            {
                var m = KickMessage.Read(ref r);
                return new JsonObject { ["code"] = m.Code, ["reason"] = Golden.Hex(Encoding.UTF8.GetBytes(m.Reason)) };
            });

        AssertMessage("message-bye", "BYE with an application code.", (ref WireWriter w) => new ByeMessage(4150).Write(ref w), (ref WireReader r) =>
        {
            var m = ByeMessage.Read(ref r);
            return new JsonObject { ["code"] = m.Code };
        });
    }

    /// <summary>
    /// COMMANDS as a client encodes them: a ClientRegion quad, then two queued Steer commands whose sequence wraps 65535 → 0 inside one message. The
    /// expectation is the decode log a server produces; a TypeScript client must encode the same inputs to the same bytes.
    /// </summary>
    [Test]
    public void Commands()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink()));
        var region = plan.CommandByName(BuiltInCommands.ClientRegion);
        var steer = plan.CommandByName("Steer");
        var commands = new List<(MessagePlan, ushort, RecordValues)>
        {
            (region, 65534, new RecordValues
            {
                [BuiltInCommands.RegionVerticesField] = FieldValue.Of(-100, -100, 0, 100, -100, 0, 150, 200, 0, -150, 200, 0),
                [BuiltInCommands.RegionAltitudeField] = FieldValue.Of(350.5),
                [BuiltInCommands.RegionBudgetField] = FieldValue.Of(512),
            }),
            (steer, 65535, SteerValues(1.25, true, 0.75, 2, "go")),
            (steer, 0, SteerValues(-3, false, 0, 0, "")),
        };

        var buffer = new byte[1024];
        var w = new WireWriter(buffer);
        var frame = CatalogSamples.KitchenFrame;
        CommandsMessage.Write(ref w, 69_990, commands, frame);
        var bytes = w.Written.ToArray();

        var sink = new RecordingSink();
        CommandsMessage.Read(bytes, plan, ref sink, frame);

        Golden.Assert("message-commands", bytes, new JsonObject
        {
            ["description"] = "COMMANDS against catalog-kitchen-sink: a ClientRegion quad at built-in index 0 (pos3 vertices at the command width, 32 bits, "
                + "over the session's frame), then Steer at 16 twice, seq wrapping 65535 → 0.",
            ["catalog"] = "catalog-kitchen-sink",
            ["frame"] = CatalogSamples.FrameJson(frame),
            ["inputs"] = new JsonArray(
                CommandInput("ClientRegion", 65534, new JsonObject
                {
                    ["vertices"] = Golden.Bits([-100, -100, 0, 100, -100, 0, 150, 200, 0, -150, 200, 0]),
                    ["altitudeM"] = Golden.Bits([350.5]),
                    ["budgetKiBps"] = Golden.Bits([512]),
                }),
                CommandInput("Steer", 65535, SteerJson(1.25, true, 0.75, 2, "go")),
                CommandInput("Steer", 0, SteerJson(-3, false, 0, 0, ""))),
            ["clientTick"] = 69_990,
            ["log"] = sink.Log.DeepClone(),
        });
    }

    /// <summary>
    /// COMMANDS against catalog-exact (W32, W33): a Transfer carrying a u64 above 2⁵³, a varu64 × 2, an f64, a negative vari64 and an f32 × 2 point — the
    /// 64-bit values as bit patterns a TypeScript client builds from bigints and a C client from int64_t.
    /// </summary>
    [Test]
    public void CommandsExact()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.Exact()));
        var transfer = plan.CommandByName("Transfer");
        var commands = new List<(MessagePlan, ushort, RecordValues)>
        {
            (transfer, 1, TransferValues((1UL << 53) + 1, [0, ulong.MaxValue], 0.1, long.MinValue, [1.5, -2.25])),
            (transfer, 2, TransferValues(ulong.MaxValue, [127, 128], double.NegativeInfinity, -1, [0, -0.0])),
        };

        var buffer = new byte[1024];
        var w = new WireWriter(buffer);
        CommandsMessage.Write(ref w, 12_345, commands);
        var bytes = w.Written.ToArray();

        var sink = new RecordingSink();
        CommandsMessage.Read(bytes, plan, ref sink);

        Golden.Assert("message-commands-exact", bytes, new JsonObject
        {
            ["description"] = "COMMANDS against catalog-exact: two Transfers — a u64 at 2⁵³ + 1 and 2⁶⁴ − 1, a varu64 × 2 at its edges, an f64 (0.1, −∞), a "
                + "vari64 at its minimum and −1, an f32 × 2 point with a negative zero.",
            ["catalog"] = "catalog-exact",
            ["inputs"] = new JsonArray(
                CommandInput("Transfer", 1, TransferJson((1UL << 53) + 1, [0, ulong.MaxValue], 0.1, long.MinValue, [1.5, -2.25])),
                CommandInput("Transfer", 2, TransferJson(ulong.MaxValue, [127, 128], double.NegativeInfinity, -1, [0, -0.0]))),
            ["clientTick"] = 12_345,
            ["log"] = sink.Log.DeepClone(),
        });
    }

    private static RecordValues TransferValues(ulong amount, ulong[] memo, double ratio, long target, double[] where) => new()
    {
        ["amount"] = FieldValue.OfUInt64(amount), ["memo"] = FieldValue.OfUInt64(memo), ["ratio"] = FieldValue.Of(ratio),
        ["target"] = FieldValue.OfInt64(target), ["where"] = FieldValue.Of(where),
    };

    private static JsonObject TransferJson(ulong amount, ulong[] memo, double ratio, long target, double[] where) => new()
    {
        ["amount"] = Golden.Bits64([amount]), ["memo"] = Golden.Bits64(memo), ["ratio"] = Golden.Bits([ratio]),
        ["target"] = Golden.Bits64([unchecked((ulong)target)]), ["where"] = Golden.Bits(where),
    };

    private static RecordValues SteerValues(double heading, bool boost, double speed, int stance, string note) => new()
    {
        ["heading"] = FieldValue.Of(heading), ["boost"] = FieldValue.Of(boost ? 1 : 0), ["speed"] = FieldValue.Of(speed), ["stance"] = FieldValue.Of(stance),
        ["note"] = FieldValue.Of(note),
    };

    private static JsonObject SteerJson(double heading, bool boost, double speed, int stance, string note) => new()
    {
        ["heading"] = Golden.Bits([heading]), ["boost"] = Golden.Bits([boost ? 1 : 0]), ["speed"] = Golden.Bits([speed]), ["stance"] = Golden.Bits([stance]),
        ["note"] = Golden.Hex(Encoding.UTF8.GetBytes(note)),
    };

    private static JsonObject CommandInput(string type, int seq, JsonObject values) => new() { ["type"] = type, ["seq"] = seq, ["values"] = values };

    private delegate void WriteAction(ref WireWriter w);

    private delegate JsonObject ReadAction(ref WireReader r);

    private static void AssertMessage(string name, string description, WriteAction write, ReadAction read)
    {
        var buffer = new byte[16 * 1024];
        var w = new WireWriter(buffer);
        write(ref w);
        var bytes = w.Written.ToArray();
        var r = new WireReader(bytes);
        var type = r.ReadU8();
        var decoded = read(ref r);
        Assert.That(r.IsAtEnd, Is.True, $"{name}: {r.Remaining} byte(s) left unread");
        Golden.Assert(name, bytes, new JsonObject { ["description"] = description, ["type"] = type, ["message"] = decoded });
    }

    private static JsonObject HelloJson(ref WireReader r)
    {
        var m = HelloMessage.Read(ref r);
        return new JsonObject
        {
            ["major"] = m.Major, ["minor"] = m.Minor, ["caps"] = (uint)m.Caps, ["kind"] = m.Kind, ["token"] = Golden.Hex(Encoding.UTF8.GetBytes(m.Token)),
            ["resumeToken"] = Golden.Hex(m.ResumeToken),
            ["clientCatalogHash"] = CatalogSerializer.ToHex(m.ClientCatalogHash),
            ["helloPayload"] = Golden.Hex(m.HelloPayload),
        };
    }

    private static JsonObject WelcomeJson(ref WireReader r)
    {
        var m = WelcomeMessage.Read(ref r);
        return new JsonObject
        {
            ["major"] = m.Major, ["minor"] = m.Minor, ["capsGranted"] = (uint)m.CapsGranted, ["sessionId"] = m.SessionId,
            ["resumeToken"] = Golden.Hex(m.ResumeToken), ["tick"] = m.Tick, ["tickPeriodUs"] = m.TickPeriodUs,
            ["catalogHash"] = CatalogSerializer.ToHex(m.CatalogHash), ["catalogBytes"] = m.CatalogJson.Length,
        };
    }
}
