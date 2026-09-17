using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// Access to the golden vectors, which <c>test/Typhon.Protocol.Tests/Golden</c> owns (05-sdks § 4), and the regeneration switch they share.
/// </summary>
internal static class GoldenFiles
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static bool Updating => Environment.GetEnvironmentVariable("TYPHON_UPDATE_GOLDEN") == "1";

    internal static byte[] ReadBin(string name) => File.ReadAllBytes(Path.Combine(Directory(), name + ".bin"));

    /// <summary>The committed expectation of a vector, for the few assertions that read it rather than regenerate it.</summary>
    internal static JsonNode ReadJson(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Directory(), name + ".json")));

    /// <summary>Asserts bytes and expectation equal the committed vector, or rewrites it under <c>TYPHON_UPDATE_GOLDEN=1</c>.</summary>
    internal static void Assert(string name, byte[] bytes, JsonObject expectation)
    {
        var binPath = Path.Combine(Directory(), name + ".bin");
        var jsonPath = Path.Combine(Directory(), name + ".json");
        var json = expectation.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
        if (Updating)
        {
            File.WriteAllBytes(binPath, bytes);
            File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
            return;
        }

        NUnit.Framework.Assert.That(File.Exists(binPath), Is.True, $"missing golden vector {name}.bin — regenerate with TYPHON_UPDATE_GOLDEN=1");
        NUnit.Framework.Assert.Multiple(() =>
        {
            NUnit.Framework.Assert.That(Convert.ToHexString(bytes), Is.EqualTo(Convert.ToHexString(File.ReadAllBytes(binPath))), $"{name}.bin drifted");
            NUnit.Framework.Assert.That(json, Is.EqualTo(File.ReadAllText(jsonPath).ReplaceLineEndings("\n")), $"{name}.json drifted");
        });
    }

    // NaN as a token, as the Protocol vectors write it: JavaScript cannot observe a NaN's sign or payload.
    internal static string Bits(double value) =>
        double.IsNaN(value) ? "nan" : BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture);

    internal static JsonArray Bits(ReadOnlySpan<double> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(Bits(v));
        }

        return array;
    }

    internal static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Directory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && directory.GetFiles("Typhon.slnx").Length == 0)
        {
            directory = directory.Parent;
        }

        NUnit.Framework.Assert.That(directory, Is.Not.Null, "could not locate the repository root from the test assembly location");
        return Path.Combine(directory!.FullName, "test", "Typhon.Protocol.Tests", "Golden");
    }
}

/// <summary>Records decoded events into JSON, in the implementation-neutral shape the stream vector uses.</summary>
internal sealed class EventRecorder(WorldStore store) : IEventHandler
{
    private JsonObject _event;
    private JsonObject _fields;

    internal JsonArray Events { get; } = [];

    public void Event(MessagePlan type)
    {
        _fields = new JsonObject();
        _event = new JsonObject { ["type"] = type.Name, ["fields"] = _fields };
        Events.Add(_event);
    }

    // An entityRef also records whether the store resolves it at the moment the event applies: the apply order of 03 § 5, observable.
    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
    {
        _fields[field.Name] = GoldenFiles.Bits(components);
        if (field.Kind == CodecKind.EntityRef)
        {
            var known = (JsonObject)(_event["known"] ??= new JsonObject());
            known[field.Name] = store.TryLocate((uint)components[0], out _, out _);
        }
    }

    public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8) => _fields[field.Name] = GoldenFiles.Hex(utf8);

    public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes) => _fields[field.Name] = GoldenFiles.Hex(bytes);

    public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components) =>
        _fields[field.Name] = new JsonObject { ["count"] = count, ["values"] = GoldenFiles.Bits(components) };
}

/// <summary>
/// Renders a <see cref="WorldStore"/> as the implementation-neutral JSON a stream vector commits: every collection sorted by its key, every number as its
/// IEEE bits, text and bytes as lower-case hex. A TypeScript store applying the same frames must render the same document.
/// </summary>
internal static class StreamSnapshot
{
    internal static JsonObject Render(WorldStore store, JsonArray events)
    {
        var archetypes = new JsonObject();
        foreach (var a in store.Archetypes)
        {
            var entities = new List<(uint NetId, JsonObject Json)>();
            for (var i = 0; i < a.LiveCount; i++)
            {
                var slot = a.Live[i];
                var fields = new JsonObject();
                foreach (var f in a.Plan.Fields)
                {
                    var value = FieldJson(a, f, slot);
                    if (value != null)
                    {
                        fields[f.Name] = value;
                    }
                }

                var entity = new JsonObject { ["netId"] = a.NetIds[slot] };
                if (a.Dims > 0)
                {
                    // The newest segment of the ring, which is what the TypeScript store renders too.
                    entity["position"] = GoldenFiles.Bits(a.HeadPosition(slot));
                    entity["velocity"] = GoldenFiles.Bits(a.HeadVelocity(slot));
                    entity["t0"] = a.HeadT0(slot);
                    entity["epoch"] = a.HeadEpoch(slot);
                }

                entity["fields"] = fields;
                entities.Add((a.NetIds[slot], entity));
            }

            var entered = a.Entered.Take(a.EnteredCount).Where(a.IsLive).Select(s => a.NetIds[s]).Order();
            var updated = a.Updated.Take(a.UpdatedCount).Where(a.IsLive).OrderBy(s => a.NetIds[s])
                .Select(s => (JsonNode)new JsonObject { ["netId"] = a.NetIds[s], ["groups"] = a.UpdateMask[s], ["moved"] = a.Moved[s] });

            archetypes[a.Plan.Name] = new JsonObject
            {
                ["entities"] = new JsonArray([.. entities.OrderBy(e => e.NetId).Select(e => (JsonNode)e.Json)]),
                ["entered"] = new JsonArray([.. entered.Select(n => (JsonNode)n)]),
                ["updated"] = new JsonArray([.. updated]),
                ["left"] = new JsonArray([.. a.Left.Take(a.LeftCount).Order().Select(n => (JsonNode)n)]),
            };
        }

        JsonNode self = null;
        if (store.Self.Archetype != null)
        {
            var fields = new JsonObject();
            foreach (var f in store.Self.Archetype.OwnerFields)
            {
                JsonNode value = f.ValueKind switch
                {
                    FieldValueKind.Number when store.Self.Numbers[f.Ordinal] != null => GoldenFiles.Bits(store.Self.Numbers[f.Ordinal]),
                    FieldValueKind.Text when store.Self.Texts[f.Ordinal] != null => GoldenFiles.Hex(Encoding.UTF8.GetBytes(store.Self.Texts[f.Ordinal])),
                    FieldValueKind.Bytes when store.Self.Bytes[f.Ordinal] != null => GoldenFiles.Hex(store.Self.Bytes[f.Ordinal]),
                    _ => null,
                };

                if (value != null)
                {
                    fields[f.Name] = value;
                }
            }

            self = new JsonObject
            {
                ["archetype"] = store.Self.Archetype.Name,
                ["netId"] = store.Self.NetId,
                ["lastSeq"] = store.Self.LastSeq,
                ["received"] = store.Self.Received,
                ["ownerMask"] = store.Self.OwnerMask,
                ["fields"] = fields,
            };
        }

        var aggregates = new JsonArray();
        foreach (var g in store.Aggregates)
        {
            var cells = new JsonArray();
            for (var cell = 0; cell * g.ArchetypeCount < g.Counts.Length; cell++)
            {
                var counts = g.Counts.AsSpan(cell * g.ArchetypeCount, g.ArchetypeCount);
                if (counts.ContainsAnyExcept(0u))
                {
                    cells.Add(new JsonObject { ["cell"] = cell, ["counts"] = new JsonArray([.. counts.ToArray().Select(c => (JsonNode)c)]) });
                }
            }

            aggregates.Add(new JsonObject
            {
                ["grid"] = g.Grid.Idx,
                ["cells"] = cells,
                ["changed"] = new JsonArray([.. g.Changed.Distinct().Order().Select(c => (JsonNode)c)]),
            });
        }

        var metrics = new JsonObject();
        for (var i = 0; i < store.Plan.ServerMetrics.Length; i++)
        {
            metrics[store.Plan.ServerMetrics[i].Name] = GoldenFiles.Bits(store.ServerMetricValues[i]);
        }

        for (var i = 0; i < store.Plan.SessionMetrics.Length; i++)
        {
            metrics[store.Plan.SessionMetrics[i].Name] = GoldenFiles.Bits(store.SessionMetricValues[i]);
        }

        return new JsonObject
        {
            ["tick"] = store.Tick,
            ["flags"] = (int)store.Flags,
            ["periodUs"] = store.PeriodUs,
            ["archetypes"] = archetypes,
            ["events"] = events.DeepClone(),
            ["self"] = self,
            ["acks"] = new JsonArray([.. store.Acks.Select(x => (JsonNode)new JsonObject { ["seq"] = x.Seq, ["reason"] = x.Reason })]),
            ["sources"] = new JsonArray([.. store.Sources.Select(x => (JsonNode)new JsonObject
            {
                ["requestId"] = x.RequestId, ["status"] = x.Status, ["code"] = x.Code,
            })]),
            ["aggregates"] = aggregates,
            ["metrics"] = metrics,
            ["anomalies"] = store.Anomalies,
        };
    }

    private static JsonNode FieldJson(ArchetypeStore a, FieldPlan f, int slot) => f.ValueKind switch
    {
        FieldValueKind.Number => GoldenFiles.Bits(a.Numbers[f.Ordinal].AsSpan(slot * f.Components, f.Components)),
        FieldValueKind.Text => a.Texts[f.Ordinal][slot] == null ? null : GoldenFiles.Hex(Encoding.UTF8.GetBytes(a.Texts[f.Ordinal][slot])),
        FieldValueKind.Bytes => a.BytesColumns[f.Ordinal][slot] == null ? null : GoldenFiles.Hex(a.BytesColumns[f.Ordinal][slot]),
        _ => null,
    };
}
