using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// Golden vectors at the protocol's index and width limits (05-sdks § 4): archetype indices 127, 128 and 254 — the last one-byte varint, the first
/// two-byte one, and the largest index — and event indices past 127; a state record with all eight groups and a 32-field group; a <c>u8</c> enum of 256
/// names and a <c>varu</c> enum value of 128; netId gaps at every varint edge up to 2³² − 1. Colyseus desynced silently at field 63 — limits are where a
/// decoder's assumptions break without an error.
/// </summary>
[TestFixture]
public class GoldenLimitsTests
{
    private const int ArchetypeCount = ProtocolConstants.MaxArchetypes;
    private const int EventCount = 120;

    [Test]
    public void Limits()
    {
        var catalog = Wide();
        var bytes = CatalogSerializer.ToCanonicalUtf8(catalog);
        Golden.Assert("catalog-wide", bytes, new JsonObject
        {
            ["description"] = $"{ArchetypeCount} archetypes and {EventCount} events, so indices reach 254 and 135; A254 has 8 groups and 32 fields, a u8 enum "
                + "of 256 names and a varu enum of 300.",
            ["hash"] = CatalogSerializer.ToHex(CatalogSerializer.ComputeHash(catalog)),
            ["byteCount"] = bytes.Length,
        });

        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(bytes));
        var a127 = plan.ArchetypeByName("A127");
        var a128 = plan.ArchetypeByName("A128");
        var a254 = plan.ArchetypeByName("A254");
        Assert.That((a127.Idx, a128.Idx, a254.Idx), Is.EqualTo((127, 128, 254)));

        var buffer = new byte[16 * 1024];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 1, TickFlags.None);
        uint[] gaps = [127, 256, 16640, 33025, uint.MaxValue];
        TickWriter.WriteEntities(ref w, 1, a127, [new EnterRecord { NetId = 2 }], [], [], []);
        TickWriter.WriteEntities(ref w, 1, a128, [], [], [], gaps);

        var all = new RecordValues();
        foreach (var f in a254.Fields)
        {
            all[f.Name] = FieldValue.Of(f.Name switch
            {
                "wide" => 255,
                "tall" => 128,
                _ => f.Ordinal,
            });
        }

        TickWriter.WriteEntities(ref w, 1, a254,
            [new EnterRecord { NetId = 1, Values = all }], [], [new StateRecord { NetId = 1, GroupMask = 0xFF, Values = all }], []);
        TickWriter.WriteEvents(ref w, [(plan.EventByName($"E{EventCount - 1:D3}"), new RecordValues())]);

        var sink = new RecordingSink();
        TickReader.Read(w.Written, plan, ref sink);
        Assert.Multiple(() =>
        {
            Assert.That(sink.Log.Count(e => e!["call"]!.GetValue<string>() == "number"), Is.EqualTo(64), "32 fields on enter, 32 on the 0xFF state record");
            Assert.That(sink.Log.Where(e => e!["call"]!.GetValue<string>() == "leave").Select(e => e!["netId"]!.GetValue<uint>()), Is.EqualTo(gaps));
        });

        Golden.Assert("tick-limits", w.Written.ToArray(), new JsonObject
        {
            ["description"] = "Against catalog-wide: archetype index 127 as a one-byte varint, 128 and 254 and event index 135 as two-byte varints; leave "
                + "gaps at the varint edges 127/128 and 16383/16384 and a netId of 2³² − 1; a state record with mask 0xFF over 32 fields, a u8 enum at 255 "
                + "and a varu enum at 128.",
            ["catalog"] = "catalog-wide",
            ["log"] = sink.Log.DeepClone(),
        });
    }

    private static Catalog Wide()
    {
        var archetypes = new List<CatalogArchetype>();
        for (var i = 0; i < ArchetypeCount - 1; i++)
        {
            archetypes.Add(new CatalogArchetype { Name = $"A{i:D3}", Groups = [], Fields = [] });
        }

        var fields = new List<CatalogField>();
        for (var i = 0; i < 25; i++)
        {
            fields.Add(new CatalogField { Name = $"f{i:D2}", Codec = new CatalogCodec { Kind = CodecKind.U8 }, Group = "g0" });
        }

        fields.Add(new CatalogField { Name = "wide", Codec = new CatalogCodec { Kind = CodecKind.U8 }, Group = "g1", Enum = "Wide" });
        fields.Add(new CatalogField { Name = "tall", Codec = new CatalogCodec { Kind = CodecKind.Varu }, Group = "g2", Enum = "Tall" });
        for (var g = 3; g < 8; g++)
        {
            fields.Add(new CatalogField { Name = $"h{g}", Codec = new CatalogCodec { Kind = CodecKind.Varu }, Group = $"g{g}" });
        }

        archetypes.Add(new CatalogArchetype
        {
            Name = $"A{ArchetypeCount - 1:D3}", Groups = ["g0", "g1", "g2", "g3", "g4", "g5", "g6", "g7"], Fields = [.. fields],
        });

        var events = new List<CatalogEvent>();
        for (var i = 0; i < EventCount; i++)
        {
            events.Add(new CatalogEvent { Name = $"E{i:D3}", Scope = "all", Fields = [] });
        }

        return new Catalog
        {
            Protocol = new CatalogProtocolVersion { Major = 2 },
            App = new CatalogApp { Name = "Wide", Revision = 1 },
            Tick = new CatalogTick { PeriodUs = 100_000, PingHz = 4 },
            Limits = new CatalogLimits { FrameBytes = 262_144, ClientMessageBytes = 1024, ResumeGraceMs = 0 },
            Archetypes = [.. archetypes],
            Enums = new Dictionary<string, string[]>
            {
                ["Wide"] = Enumerable.Range(0, 256).Select(i => $"w{i}").ToArray(),
                ["Tall"] = Enumerable.Range(0, 300).Select(i => $"t{i}").ToArray(),
            },
            Events = [.. events],
        };
    }
}
