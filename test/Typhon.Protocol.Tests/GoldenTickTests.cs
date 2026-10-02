using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// Golden vectors for <c>TICK</c> frames against the kitchen-sink catalog: the four <c>ENTITIES</c> sub-lists for every position kind, and every other
/// block type — events with a list, <c>SELF</c> with owner groups, <c>ACKS</c>, <c>SOURCES</c>, <c>AGG</c>, <c>STATS</c>, <c>DEBUG</c>, <c>EXT</c> and an
/// unknown block that must be skipped.
/// </summary>
/// <remarks>
/// The expectation is the decoder's call log (<see cref="RecordingSink"/>): the implementation-neutral contract every client reproduces call for call,
/// with every decoded number as its IEEE bits. Each test also checks decoded values against the encoder's inputs, so a log that decodes wrongly cannot be
/// committed just because it decodes consistently.
/// </remarks>
[TestFixture]
public class GoldenTickTests
{
    private const uint Tick = 70_000;

    private static readonly CatalogPlan Plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink()));

    private static readonly RealmFrame Frame = CatalogSamples.KitchenFrame;

    [Test]
    public void Entities()
    {
        var buffer = new byte[8192];
        var w = new WireWriter(buffer);
        // RESET and a REALM first (typhon.3): the vector carries its own frame, so a decoder starting from nothing decodes every position.
        TickWriter.WriteHeader(ref w, Tick, TickFlags.ViewComplete | TickFlags.Reset);
        TickWriter.WriteRealm(ref w, Frame);

        TickWriter.WriteEntities(ref w, Tick, Plan.ArchetypeByName("Beacon"),
            [
                new EnterRecord { NetId = 3, Position = [-8192, 8191.5], Values = Values(("channel", 7), ("strength", 0.5), ("drift", -3)) },
                new EnterRecord { NetId = 4, Position = [0, 0], Values = Values(("channel", 255), ("strength", 65504), ("drift", 1000)) },
            ],
            [],
            [new StateRecord { NetId = 4, GroupMask = 1, Values = Values(("strength", -2.25), ("drift", 0)) }],
            [9], Frame);

        TickWriter.WriteEntities(ref w, Tick, Plan.ArchetypeByName("Buoy"),
            [new EnterRecord { NetId = 10, Position = [12.5, -7.25], T0 = Tick - 3, Epoch = 255, Values = Values(("depth", -12), ("reading", -100000)) }],
            [
                new SegmentRecord { NetId = 10, Position = [13, -7], T0 = Tick, Epoch = 0 },
                new SegmentRecord { NetId = 11, Position = [100, 100], T0 = 65535, Epoch = 1 },
            ],
            [],
            [], Frame);

        var drone = new RecordValues
        {
            ["serial"] = FieldValue.Of([0xDE, 0xAD, 0xBE, 0xEF]),
            ["label"] = FieldValue.Of("drône-1"),
            ["armed"] = FieldValue.Of(1),
            ["lights"] = FieldValue.Of(0),
            ["stance"] = FieldValue.Of(2),
            ["heading"] = FieldValue.Of(-1.5),
            ["rotation"] = FieldValue.Of(0, 0.6, 0, 0.8),
            ["thrust"] = FieldValue.Of(1.5, -0.25, 63.5),
            ["battery"] = FieldValue.Of(0.875),
            ["lastHit"] = FieldValue.Of(Tick - 20),
            ["target"] = FieldValue.Of(0),
            ["temperature"] = FieldValue.Of(21.5),
            ["tilt"] = FieldValue.Of(-0.5),
        };

        TickWriter.WriteEntities(ref w, Tick, Plan.ArchetypeByName("Drone"),
            [new EnterRecord { NetId = 100, Position = [10, 20, -30], Velocity = [0.5, 0, -0.25], T0 = Tick, Epoch = 3, Values = drone }],
            [
                new SegmentRecord { NetId = 100, Position = [10.5, 20, -30.25], Velocity = [0.5, 0, -0.25], T0 = Tick, Epoch = 3 },
                new SegmentRecord { NetId = 101, Position = [-1024, 63.99, 1023.97], Velocity = [100, -100, 0], T0 = Tick - 65535, Epoch = 0 },
            ],
            [
                new StateRecord { NetId = 101, GroupMask = 0b101, Values = drone },
                new StateRecord { NetId = 102, GroupMask = 0b010, Values = drone },
            ],
            [5, 6, 200_000], Frame);

        TickWriter.WriteEntities(ref w, Tick, Plan.ArchetypeByName("Ledger"),
            [new EnterRecord { NetId = 7, Values = Ledger(4_000_000_000, -128) }],
            [],
            [new StateRecord { NetId = 7, GroupMask = 1, Values = Ledger(0, 127) }],
            []);

        var log = Record(w.Written);

        var enter = log.First(e => e["call"]!.GetValue<string>() == "enter" && e["netId"]!.GetValue<uint>() == 100);
        Assert.Multiple(() =>
        {
            Assert.That(enter["position"]!.AsArray().Select(n => n!.GetValue<string>()),
                Is.EqualTo(new[] { Golden.Bits(10), Golden.Bits(20), Golden.Bits(-30) }),
                "these positions are exact at the frame's 2^-10 m step");
            Assert.That(log.Where(e => e["call"]!.GetValue<string>() == "leave").Select(e => e["netId"]!.GetValue<uint>()),
                Is.EqualTo(new uint[] { 9, 5, 6, 200_000 }));
            Assert.That(log.Count(e => e["call"]!.GetValue<string>() == "number" && e["field"]!.GetValue<string>() == "future"), Is.Zero,
                "an unknown codec is skipped by its width, never surfaced");
        });

        Golden.Assert("tick-entities", w.Written.ToArray(), Expectation(
            "RESET, then REALM (deep, 24 bits), then ENTITIES for every position kind over its frame: static Beacon, none-model Buoy, linear 3D Drone with "
            + "packs and every group, non-spatial Ledger with an unknown codec skipped by fixedBytes. Epoch 255, t0 low bits wrapping, netId gaps up to "
            + "200 000.", log));
    }

    /// <summary>
    /// typhon.3's REALM block (12-realms § 5.2): a flat realm at 16 bits whose positions decode over it, then REALM(NONE), after which only unpositioned
    /// content may follow.
    /// </summary>
    [Test]
    public void RealmBlocks()
    {
        var flat = new RealmFrame(41, 3, 2, 0xDEADBEEF, 16, 8, deep: false, [0, 0, 0], [64, 64, 8]);
        var buffer = new byte[512];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick, TickFlags.Reset);
        TickWriter.WriteRealm(ref w, flat);
        TickWriter.WriteEntities(ref w, Tick, Plan.ArchetypeByName("Beacon"),
            [new EnterRecord { NetId = 1, Position = [32, 63.999], Values = Values(("channel", 1), ("strength", 1), ("drift", 0)) }], [], [], [], flat);
        var log = Record(w.Written);
        var enter = log.First(e => e["call"]!.GetValue<string>() == "enter");
        Assert.That(enter["position"]!.AsArray().Select(n => n!.GetValue<string>()), Is.EqualTo(new[] { Golden.Bits(32), Golden.Bits(64 - (64.0 / 65536)) }),
            "a 16-bit position over [0, 64) steps by 2^-10 and clamps to the top code");
        Golden.Assert("tick-realm", w.Written.ToArray(), Expectation(
            "RESET, REALM (flat, 16 bits, kind 2, app tag 0xDEADBEEF, cell 8 m over [0, 64)²), then a Beacon enter decoded over that frame.", log));

        w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick + 1, TickFlags.Reset);
        TickWriter.WriteRealm(ref w, null);
        TickWriter.WriteEntities(ref w, Tick + 1, Plan.ArchetypeByName("Ledger"), [new EnterRecord { NetId = 7, Values = Ledger(1, 2) }], [], [], []);
        log = Record(w.Written);
        Assert.That(log[1]!["frame"], Is.Null, "REALM(NONE) delivers no frame");
        Golden.Assert("tick-realm-none", w.Written.ToArray(), Expectation(
            "RESET, REALM(NONE): the session is in no realm, and only unpositioned content (a Ledger enter) may follow.", log));
    }

    [Test]
    public void EveryOtherBlock()
    {
        var buffer = new byte[8192];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick + 1, TickFlags.Overload | TickFlags.Reset, periodUs: 150_000);
        TickWriter.WriteRealm(ref w, Frame);

        TickWriter.WriteEvents(ref w,
        [
            (Plan.EventByName("Ping"), new RecordValues
            {
                ["from"] = FieldValue.Of(100), ["path"] = FieldValue.Of(0.5, -0.5, 8000, -8000), ["loud"] = FieldValue.Of(1),
            }),
            (Plan.EventByName("Chat"), new RecordValues { ["text"] = FieldValue.Of("héllo ☀"), ["attachment"] = new FieldValue { Bytes = [0xCA, 0xFE] } }),
            (Plan.EventByName("Ping"), new RecordValues
            {
                ["from"] = FieldValue.Of(0), ["path"] = new FieldValue { Numbers = [] }, ["loud"] = FieldValue.Of(0),
            }),
        ], Frame);

        TickWriter.WriteSelf(ref w, Plan.ArchetypeByName("Drone"), 100, 65535, 0b11, new RecordValues
        {
            ["manifest"] = FieldValue.Of([1, 2, 3]), ["fuel"] = FieldValue.Of(0.1), ["pin"] = FieldValue.Of(4321), ["vault"] = FieldValue.Of(1),
        }, Frame);
        TickWriter.WriteAcks(ref w, [(65534, AckReasons.RegionInvalid), (3, (byte)200)]);
        TickWriter.WriteSources(ref w, [(1, SourceStatus.Applied, 0), (2, SourceStatus.Error, 404)]);
        TickWriter.WriteAggregate(ref w, Plan.Grids[0], reset: true, [(0u, [1u, 0u]), (17u, [3u, 400u]), (255u, [0u, 9u])]);
        TickWriter.WriteStats(ref w, Plan, new Dictionary<string, double[]>
        {
            ["typhon.tick.p50"] = [4.25],
            ["typhon.system.mean"] = [1.5, 0.25, 12.5],
            ["typhon.session.skippedFrames"] = [3],
            ["app.load"] = [0.5],
            ["app.queue"] = [7],
        });
        TickWriter.WriteDebug(ref w, [(0x01, [0x01, 0x02]), (0x99, [0xFF])]);
        TickWriter.WriteExt(ref w, 300, [0xAB, 0xCD]);
        var unknown = TickWriter.BeginBlock(ref w, 0x42);
        w.WriteBytes([0x10, 0x20, 0x30]);
        TickWriter.EndBlock(ref w, unknown);

        var log = Record(w.Written);

        Assert.Multiple(() =>
        {
            Assert.That(log[0]!["periodUs"]!.GetValue<uint>(), Is.EqualTo(150_000u));
            Assert.That(log.Count(e => e["call"]!.GetValue<string>() == "metric"), Is.EqualTo(7), "3 labelled + 4 scalar values");
            Assert.That(log.Any(e => e["call"]!.GetValue<string>() == "unknownBlock" && e["blockType"]!.GetValue<byte>() == 0x42), Is.True);
            Assert.That(log[^1]!["call"]!.GetValue<string>(), Is.EqualTo("endTick"));
        });

        Golden.Assert("tick-blocks", w.Written.ToArray(), Expectation(
            "RESET and REALM, then every non-ENTITIES block in stream order, with PERIOD and OVERLOAD set: EVENTS (a list of 2, a UTF-8 chat, an empty list), SELF with both owner "
            + "groups and lastSeq 65535, ACKS, SOURCES (applied and error), AGG with RESET, STATS (labelled, server and session), DEBUG (known and unknown "
            + "sub-types), EXT, and an unknown block 0x42 skipped by its length.", log));
    }

    [Test]
    public void ASelfWithNoControlledEntity()
    {
        var buffer = new byte[32];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick + 2, TickFlags.None);
        TickWriter.WriteSelfNone(ref w, 42);
        var log = Record(w.Written);

        Assert.That(log[1]!["archetype"], Is.Null, "no controlled entity names no archetype");
        Golden.Assert("tick-self-none", w.Written.ToArray(), Expectation(
            "SELF with netId 0 (W17′): a session that controls no entity — a spectator's acknowledgement, lastSeq 42, archetype index 0 and no owner "
            + "group; the client drops any owner state it holds.", log));
    }

    [Test]
    public void TheEntityWriterRefusesNetIdZero()
    {
        var buffer = new byte[64];
        Assert.Throws<ArgumentException>(() =>
        {
            var w = new WireWriter(buffer);
            TickWriter.WriteSelf(ref w, Plan.ArchetypeByName("Drone"), 0, 1, 0, new RecordValues());
        }, "netId 0 is WriteSelfNone's");
    }

    [Test]
    public void Keepalive()
    {
        var buffer = new byte[16];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 0xFFFFFFFF, TickFlags.Reset | TickFlags.ViewComplete);
        var log = Record(w.Written);
        Golden.Assert("tick-keepalive", w.Written.ToArray(), Expectation("A header-only keepalive at the largest tick, with RESET and VIEW_COMPLETE.", log));
    }

    [Test]
    public void AStateRecordWithAnUnknownGroupBitIsMalformed()
    {
        var buffer = new byte[64];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick, TickFlags.None);
        var mark = TickWriter.BeginBlock(ref w, BlockTypes.Entities);
        w.WriteVaru((uint)Plan.ArchetypeByName("Ledger").Idx);
        w.WriteVaru(0);
        w.WriteVaru(0);
        w.WriteVaru(1);
        w.WriteVaru(7);
        w.WriteU8(0b10);
        w.WriteVaru(0);
        TickWriter.EndBlock(ref w, mark);

        var bytes = w.Written.ToArray();
        var ex = Assert.Throws<WireFormatException>(() =>
        {
            var sink = new RecordingSink();
            TickReader.Read(bytes, Plan, ref sink);
        });
        Assert.That(ex.CloseCode, Is.EqualTo(CloseCodes.MalformedPayload));
    }

    [Test]
    public void ABlockShorterThanItsDeclaredLengthIsMalformed()
    {
        var buffer = new byte[64];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick, TickFlags.None);
        var mark = TickWriter.BeginBlock(ref w, BlockTypes.Acks);
        w.WriteVaru(0);
        w.WriteU8(0xEE);
        TickWriter.EndBlock(ref w, mark);

        var bytes = w.Written.ToArray();
        var ex = Assert.Throws<WireFormatException>(() =>
        {
            var sink = new RecordingSink();
            TickReader.Read(bytes, Plan, ref sink);
        });
        Assert.That(ex.CloseCode, Is.EqualTo(CloseCodes.MalformedPayload));
    }

    private static RecordValues Ledger(double owner, double amount) => new()
    {
        ["owner"] = FieldValue.Of(owner), ["amount"] = FieldValue.Of(amount), ["future"] = FieldValue.Of([0x0A, 0x0B, 0x0C]),
    };

    private static RecordValues Values(params (string Name, double Value)[] values)
    {
        var record = new RecordValues();
        foreach (var (name, value) in values)
        {
            record[name] = FieldValue.Of(value);
        }

        return record;
    }

    /// <summary>
    /// The exact wire end to end (W32, W33): a TICK against catalog-exact whose entity, owner and event fields carry the 64-bit integers past 2⁵³, f64 at
    /// its edges, and count shapes — what no double-only decoder can render.
    /// </summary>
    [Test]
    public void Exact()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.Exact()));
        var vault = plan.ArchetypeByName("Vault");
        var buffer = new byte[4096];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick, TickFlags.ViewComplete | TickFlags.Reset);

        var first = VaultValues(long.MinValue, (1UL << 53) + 1, long.MinValue, double.Epsilon, ulong.MaxValue, true);
        var second = VaultValues(-1, ulong.MaxValue, -((1L << 53) + 1), double.NaN, 0, false);
        TickWriter.WriteEntities(ref w, Tick, vault,
            [new EnterRecord { NetId = 1, Values = first }, new EnterRecord { NetId = 2, Values = second }],
            [],
            [new StateRecord { NetId = 1, GroupMask = 0b11, Values = second }, new StateRecord { NetId = 2, GroupMask = 0b10, Values = first }],
            []);
        TickWriter.WriteEvents(ref w,
        [
            (plan.EventByName("Audit"), new RecordValues
            {
                ["amount"] = FieldValue.OfInt64(-(1L << 53) - 1), ["at"] = FieldValue.Of(-0.0), ["corner"] = FieldValue.Of(1, double.PositiveInfinity, -2.5),
                ["who"] = FieldValue.Of(2),
            }),
        ]);
        TickWriter.WriteSelf(ref w, vault, 1, 9, 0b1, new RecordValues
        {
            ["pin"] = FieldValue.OfUInt64(0x8000_0000_0000_0001UL), ["scale"] = FieldValue.Of(double.MaxValue, -double.Epsilon),
        });

        var sink = new RecordingSink();
        RealmFrame frame = null;
        TickReader.Read(w.Written, plan, ref frame, ref sink);
        var log = sink.Log;

        Assert.That(log.Where(e => e["call"]!.GetValue<string>() == "integer64" && e["field"]!.GetValue<string>() == "balance")
                .Select(e => e["values"]![0]!.GetValue<string>()),
            Is.EqualTo(new[] { "0020000000000001", "ffffffffffffffff", "ffffffffffffffff" }),
            "2⁵³ + 1 and 2⁶⁴ − 1 survive, which a double would round; netId 2's state carries the shape group alone");

        Golden.Assert("tick-exact", w.Written.ToArray(), new JsonObject
        {
            ["description"] = "RESET, then ENTITIES for catalog-exact's Vault — enters and states with u64/i64/varu64/vari64 past 2⁵³, f64 edges (the "
                + "smallest subnormal, NaN) and count shapes (aabb3, point3, bsphere3, quat) — then an Audit event (i64, −0, f32 × 3 with ∞, an entityRef) "
                + "and SELF with an owner u64 and an f64 × 2.",
            ["catalog"] = "catalog-exact",
            ["log"] = log.DeepClone(),
        });
    }

    /// <summary>
    /// Collections end to end (W34): a TICK against catalog-coll — an empty collection, one element, a full one at <c>maxCount</c>, a truncated one
    /// (<c>sent &lt; total</c>), elements with a pack, a <c>str</c> and an <c>entityRef</c>, an onEnter collection, and an owner one in <c>SELF</c>.
    /// </summary>
    [Test]
    public void Collections()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.Collections()));
        var locker = plan.ArchetypeByName("Locker");
        var items = Array.Find(locker.Fields, f => f.Name == "items");
        var tags = Array.Find(locker.Fields, f => f.Name == "tags");
        var keys = Array.Find(locker.OwnerFields, f => f.Name == "keys");
        var buffer = new byte[4096];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick, TickFlags.ViewComplete | TickFlags.Reset);

        var sword = Item(1, "sword", 7, 3, true, 2.5);
        var shield = Item(2, "bouclier ø", 0, 31, false, -0.0);
        var ring = Item(65535, "", 4_000_000_000, 0, true, float.MaxValue);
        TickWriter.WriteEntities(ref w, Tick, locker,
            [
                new EnterRecord
                {
                    NetId = 1,
                    Values = new RecordValues { ["items"] = Coll(items, 0), ["tags"] = Coll(tags, 0, Tag(9)), ["level"] = FieldValue.Of(1) },
                },
                new EnterRecord
                {
                    NetId = 2,
                    Values = new RecordValues
                    {
                        ["items"] = Coll(items, 0, sword, shield, ring, sword), ["tags"] = Coll(tags, 0, Tag(0), Tag(255), Tag(128)),
                        ["level"] = FieldValue.Of(255),
                    },
                },
            ],
            [],
            [
                new StateRecord { NetId = 1, GroupMask = 1, Values = new RecordValues { ["items"] = Coll(items, 0, ring), ["level"] = FieldValue.Of(2) } },
                new StateRecord
                {
                    NetId = 2,
                    GroupMask = 1,
                    Values = new RecordValues { ["items"] = Coll(items, 9, shield, sword, shield, ring), ["level"] = FieldValue.Of(3) },
                },
            ],
            []);
        TickWriter.WriteSelf(ref w, locker, 2, 4, 0b1, new RecordValues
        {
            ["keys"] = Coll(keys, 2,
                new RecordValues { ["code"] = FieldValue.OfUInt64(ulong.MaxValue), ["where"] = FieldValue.Of(1.5, -8192) },
                new RecordValues { ["code"] = FieldValue.OfUInt64(0), ["where"] = FieldValue.Of(0, 0) }),
        });

        var sink = new RecordingSink();
        RealmFrame frame = null;
        TickReader.Read(w.Written, plan, ref frame, ref sink);
        var log = sink.Log;

        Assert.Multiple(() =>
        {
            var collections = log.Where(e => e["call"]!.GetValue<string>() == "collection")
                .Select(e => $"{e["field"]!.GetValue<string>()} {e["total"]!.GetValue<int>()}/{e["sent"]!.GetValue<int>()}");
            Assert.That(collections, Is.EqualTo(new[] { "tags 1/1", "items 0/0", "tags 3/3", "items 4/4", "items 1/1", "items 9/4", "keys 2/2" }),
                "onEnter before the groups; the truncated list keeps its total");
            Assert.That(log.Where(e => e["call"]!.GetValue<string>() == "text").Select(e => Encoding.UTF8.GetString(Convert.FromHexString(
                e["utf8"]!.GetValue<string>()))).Take(2), Is.EqualTo(new[] { "sword", "bouclier ø" }));
            Assert.That(log.First(e => e["call"]!.GetValue<string>() == "number" && e["field"]!.GetValue<string>() == "owner"
                && e["values"]![0]!.GetValue<string>() == Golden.Bits(4_000_000_000)), Is.Not.Null, "an entityRef past 2³¹");
        });

        Golden.Assert("tick-coll", w.Written.ToArray(), new JsonObject
        {
            ["description"] = "RESET, then ENTITIES for catalog-coll's Locker — an empty collection, one element, a full one at maxCount, a truncated "
                + "one (9 held, 4 sent), elements with a bool and a bits pack, a str, an entityRef past 2³¹, an onEnter collection — and SELF with an owner "
                + "collection of a u64 and an f32 × 2 point.",
            ["catalog"] = "catalog-coll",
            ["log"] = log.DeepClone(),
        });
    }

    private static RecordValues Item(int id, string name, uint owner, int stack, bool lit, double weight) => new()
    {
        ["id"] = FieldValue.Of(id), ["name"] = FieldValue.Of(name), ["owner"] = FieldValue.Of(owner), ["stack"] = FieldValue.Of(stack),
        ["lit"] = FieldValue.Of(lit ? 1 : 0), ["weight"] = FieldValue.Of(weight),
    };

    private static RecordValues Tag(int tag) => new() { ["tag"] = FieldValue.Of(tag) };

    // A collection value: each element's named values in its element section's wire order.
    private static FieldValue Coll(FieldPlan field, int total, params RecordValues[] elements)
    {
        var ordered = new FieldValue[elements.Length][];
        for (var e = 0; e < elements.Length; e++)
        {
            ordered[e] = field.ElementSection.Fields.Select(f => elements[e][f.Name]).ToArray();
        }

        return new FieldValue { Elements = ordered, Total = total };
    }

    private static RecordValues VaultValues(long id, ulong balance, long delta, double rate, ulong seen, bool open) => new()
    {
        ["id"] = FieldValue.OfInt64(id),
        ["balance"] = FieldValue.OfUInt64(balance),
        ["delta"] = FieldValue.OfInt64(delta),
        ["rate"] = FieldValue.Of(rate),
        ["seen"] = FieldValue.OfUInt64(seen),
        ["open"] = FieldValue.Of(open ? 1 : 0),
        ["box"] = FieldValue.Of(-1, -2, -3, 1, 2, 3.5),
        ["spot"] = FieldValue.Of(0.1, -1e300, 1e-300),
        ["reach"] = FieldValue.Of(10, -20, 999.99, 1.5),
        ["spin"] = FieldValue.Of(0, 0, 0.70710678118654757, 0.70710678118654757),
    };

    private static JsonArray Record(ReadOnlySpan<byte> message)
    {
        var sink = new RecordingSink();
        RealmFrame frame = null;
        TickReader.Read(message, Plan, ref frame, ref sink);
        return sink.Log;
    }

    private static JsonObject Expectation(string description, JsonArray log) => new()
    {
        ["description"] = description,
        ["catalog"] = "catalog-kitchen-sink",
        ["log"] = log.DeepClone(),
    };
}
