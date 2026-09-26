using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The catalog refusal vector: raw catalog JSON texts a client must accept or refuse at <c>WELCOME</c>. The C# and TypeScript validators are written by
/// hand on both sides, so this vector — not either implementation — is what keeps them from drifting: every case runs through both.
/// </summary>
/// <remarks>
/// Each refused text differs from a canonical catalog in exactly the one way its name says, so a client refuses it for that reason and no other. The
/// vector records accept or refuse only, since problem texts differ between languages; this generator also pins the C# reason per case. A JSON number
/// token such as <c>1.0</c> in an integer field is refused by System.Text.Json but cannot be told from <c>1</c> after <c>JSON.parse</c>, so no case
/// depends on the token's spelling.
/// </remarks>
[TestFixture]
public class GoldenCatalogRefusalTests
{
    [Test]
    public void CatalogRefusals()
    {
        var baseText = Encoding.UTF8.GetString(CatalogSerializer.ToCanonicalUtf8(Base()));
        Assert.That(JsonNode.Parse(baseText)!.ToJsonString(), Is.EqualTo(baseText), "JsonNode round-trips a canonical text byte for byte");

        var cases = new JsonArray();
        Accept(cases, "base", baseText);
        // Canonical order puts "10" before "9"; JSON.parse enumerates integer-like keys first, in numeric order.
        var integerLike = new Dictionary<string, string[]> { ["Kind"] = ["small", "large"], ["9"] = ["a"], ["10"] = ["b"] };
        Accept(cases, "enum-names-look-like-integers", Canonical(Base(integerLike)));
        Accept(cases, "enum-named-proto", Canonical(Base(new Dictionary<string, string[]> { ["Kind"] = ["small", "large"], ["__proto__"] = ["x"] })));
        Accept(cases, "unknown-codec-with-fixed-bytes",
            Canonical(Base(extra: new CatalogField { Name = "zz", Codec = new CatalogCodec { Type = "future", FixedBytes = 3 }, Group = "state" })));

        const string parse = "does not parse";
        const string canonical = "not canonical";
        Refuse(cases, "not-an-object", parse, "[]");
        Refuse(cases, "truncated", parse, baseText[..(baseText.Length / 2)]);
        Refuse(cases, "missing-grids", canonical, Mutate(baseText, c => c.AsObject().Remove("grids")));
        Refuse(cases, "null-events", canonical, Mutate(baseText, c => c["events"] = null));
        Refuse(cases, "null-enum-names", "has no names", Mutate(baseText, c => c["enums"]!["Unused"] = null));
        Refuse(cases, "int-field-above-int32", parse, Mutate(baseText, c => c["limits"]!["frameBytes"] = 3_000_000_000L));
        Refuse(cases, "int-field-not-an-integer", parse, Mutate(baseText, c => c["tick"]!["periodUs"] = 50_000.5));
        Refuse(cases, "metric-without-unit", "unit is missing", Mutate(baseText, c => AppMetric(c).AsObject().Remove("unit")));
        Refuse(cases, "list-of-list", "numeric byte-aligned", Mutate(baseText, c => EventField(c)["codec"] = JsonNode.Parse(
            """{"t":"list","of":{"t":"list","of":{"t":"u8"},"maxCount":2},"maxCount":2}""")));
        // System.Text.Json stops at depth 64, before any catalog rule; a TypeScript client refuses it as a list of lists.
        Refuse(cases, "list-nested-200-deep", parse, DeepList(baseText, 200));
        Refuse(cases, "archetypes-out-of-order", canonical, Mutate(baseText, c =>
        {
            var archetypes = c["archetypes"]!.AsArray();
            var first = archetypes[0]!.DeepClone();
            archetypes[0] = archetypes[1]!.DeepClone();
            archetypes[1] = first;
        }));
        Refuse(cases, "event-index-huge", canonical, Mutate(baseText, c => c["events"]![0]!["idx"] = 1_000_000_000));
        Refuse(cases, "duplicate-grid", "duplicates grid", Mutate(baseText, c =>
        {
            var copy = c["grids"]![0]!.DeepClone();
            copy["idx"] = 1;
            c["grids"]!.AsArray().Add(copy);
        }));
        Refuse(cases, "unknown-codec-without-fixed-bytes", "cannot be skipped",
            Mutate(baseText, c => EventField(c)["codec"] = JsonNode.Parse("""{"t":"future"}""")));
        // Names sorting after "state", so the list stays in canonical order and only the count breaks a rule.
        Refuse(cases, "nine-groups", "groups", Mutate(baseText, c =>
        {
            var groups = c["archetypes"]![0]!["groups"]!.AsArray();
            for (var i = groups.Count; i < 9; i++)
            {
                groups.Add($"z{i}");
            }
        }));

        Golden.Assert("catalog-refusals", [], new JsonObject
        {
            ["description"] = "Raw catalog JSON texts a client must accept or refuse at WELCOME; each refused text breaks exactly the rule its name says. "
                + "Only accept or refuse is compared.",
            ["cases"] = cases,
        });
    }

    private static Catalog Base(Dictionary<string, string[]> enums = null, CatalogField extra = null) => new()
    {
        Protocol = new CatalogProtocolVersion { Major = 3 },
        App = new CatalogApp { Name = "Refusals", Revision = 1 },
        Tick = new CatalogTick { PeriodUs = 50_000, PingHz = 4 },
        Limits = new CatalogLimits { FrameBytes = 65_536, ClientMessageBytes = 1024 },
        Archetypes =
        [
            new CatalogArchetype
            {
                Name = "Crate", Groups = ["state"],
                Position = new CatalogPosition { Kind = CatalogPosition.StaticKind, Pos = CatalogSamples.Pos2() },
                Fields =
                [
                    new CatalogField { Name = "kind", Codec = new CatalogCodec { Kind = CodecKind.U8 }, Group = "state", Enum = "Kind" },
                    .. extra == null ? Array.Empty<CatalogField>() : [extra],
                ],
            },
            new CatalogArchetype
            {
                Name = "Mover", Groups = ["vitals"],
                Position = new CatalogPosition
                {
                    Kind = CatalogPosition.MotionKind, Model = CatalogPosition.LinearModel, Pos = CatalogSamples.Pos2(),
                    Vel = new CatalogCodec { Kind = CodecKind.Vel2, UnitExp = -13, Bits = 16 },
                },
                Fields = [new CatalogField { Name = "hp", Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 8 }, Group = "vitals" }],
            },
        ],
        Enums = enums ?? new Dictionary<string, string[]> { ["Kind"] = ["small", "large"] },
        Events =
        [
            new CatalogEvent
            {
                Name = "Blip", Scope = "all",
                Fields = [new CatalogField { Name = "path", Codec = new CatalogCodec { Kind = CodecKind.List, Of = CatalogSamples.Pos2(), MaxCount = 4 } }],
            },
        ],
        Grids = [new CatalogGrid { Origin = [-8192, -8192], Cell = 512, Dims = [32, 32], Archetypes = [0] }],
        Metrics =
        [
            new CatalogMetric
            {
                Name = "app.load", Unit = "ratio", Codec = new CatalogCodec { Kind = CodecKind.F16 }, Kind = CatalogMetric.GaugeKind,
                Scope = CatalogMetric.ServerScope,
            },
        ],
    };

    private static string Canonical(Catalog catalog) => Encoding.UTF8.GetString(CatalogSerializer.ToCanonicalUtf8(catalog));

    private static JsonNode EventField(JsonNode catalog) => catalog["events"]![0]!["fields"]![0]!;

    private static JsonNode AppMetric(JsonNode catalog)
    {
        foreach (var m in catalog["metrics"]!.AsArray())
        {
            if (m!["name"]!.GetValue<string>() == "app.load")
            {
                return m;
            }
        }

        throw new InvalidOperationException("app.load is missing");
    }

    private static string Mutate(string text, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(text)!;
        change(node);
        return node.ToJsonString();
    }

    // Deeper than any JSON writer's default depth, so the nesting is spliced in as text around a placeholder codec.
    private static string DeepList(string baseText, int depth)
    {
        const string placeholder = """{"t":"placeholder"}""";
        var text = Mutate(baseText, c => EventField(c)["codec"] = JsonNode.Parse(placeholder));
        var deep = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            deep.Append("""{"t":"list","of":""");
        }

        deep.Append("""{"t":"u8"}""");
        for (var i = 0; i < depth; i++)
        {
            deep.Append(""","maxCount":2}""");
        }

        return text.Replace(placeholder, deep.ToString(), StringComparison.Ordinal);
    }

    private static void Accept(JsonArray cases, string name, string json)
    {
        Assert.DoesNotThrow(() => CatalogSerializer.FromUtf8(Encoding.UTF8.GetBytes(json)), name);
        cases.Add(new JsonObject { ["name"] = name, ["accept"] = true, ["json"] = json });
    }

    // The fragment pins the C# reason, so a case refused for another rule than its name fails here; the vector itself records accept or refuse only.
    private static void Refuse(JsonArray cases, string name, string fragment, string json)
    {
        Assert.That(json, Is.Not.Empty);
        var ex = Assert.Throws<CatalogException>(() => CatalogSerializer.FromUtf8(Encoding.UTF8.GetBytes(json)), name);
        Assert.That(ex!.Message, Does.Contain(fragment), name);
        cases.Add(new JsonObject { ["name"] = name, ["accept"] = false, ["json"] = json });
    }
}
