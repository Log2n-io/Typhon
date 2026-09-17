using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// Golden vectors at the protocol's index and width limits (05-sdks § 4): archetype and event indices past 127, so every index varint takes two bytes; a
/// state record with all eight groups and a 32-field group; a <c>u8</c> enum of 256 names and a <c>varu</c> enum value of 128; netId gaps at every varint
/// edge up to 2³² − 1. Colyseus desynced silently at field 63 — limits are where a decoder's assumptions break without an error.
/// </summary>
[TestFixture]
public class GoldenLimitsTests
{
    private const int ArchetypeCount = 130;
    private const int EventCount = 120;

    [Test]
    public void Limits()
    {
        var catalog = Wide();
        var bytes = CatalogSerializer.ToCanonicalUtf8(catalog);
        Golden.Assert("catalog-wide", bytes, new JsonObject
        {
            ["description"] = $"{ArchetypeCount} archetypes and {EventCount} events, so indices reach 129 and 135; A129 has 8 groups and 32 fields, a u8 enum "
                + "of 256 names and a varu enum of 300.",
            ["hash"] = CatalogSerializer.ToHex(CatalogSerializer.ComputeHash(catalog)),
            ["byteCount"] = bytes.Length,
        });

        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(bytes));
        var a128 = plan.ArchetypeByName("A128");
        var a129 = plan.ArchetypeByName("A129");
        Assert.That((a128.Idx, a129.Idx), Is.EqualTo((128, 129)));

        var buffer = new byte[16 * 1024];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 1, TickFlags.None);
        uint[] gaps = [127, 256, 16640, 33025, uint.MaxValue];
        TickWriter.WriteEntities(ref w, 1, a128, gaps.Select(id => new EnterRecord { NetId = id }).ToArray(), [], [], gaps);

        var all = new RecordValues();
        foreach (var f in a129.Fields)
        {
            all[f.Name] = FieldValue.Of(f.Name switch
            {
                "wide" => 255,
                "tall" => 128,
                _ => f.Ordinal,
            });
        }

        TickWriter.WriteEntities(ref w, 1, a129,
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
            ["description"] = "Against catalog-wide: archetype indices 128 and 129 and event index 135 as two-byte varints; netId gaps at the varint edges "
                + "127/128 and 16383/16384 and a netId of 2³² − 1; a state record with mask 0xFF over 32 fields, a u8 enum at 255 and a varu enum at 128.",
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

        archetypes.Add(new CatalogArchetype { Name = "A129", Groups = ["g0", "g1", "g2", "g3", "g4", "g5", "g6", "g7"], Fields = [.. fields] });

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
