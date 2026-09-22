using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The golden-vector pattern (05-sdks § 4): per case a <c>.bin</c> holding the exact bytes and a <c>.json</c> holding what decoding them must produce, both
/// regenerated only under <c>TYPHON_UPDATE_GOLDEN=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a committed artefact and not a computed expectation.</b> The .NET encoder, the TypeScript decoder and the engine must agree on bytes, and they cannot
/// agree on a value each computes for itself. A committed <c>.bin</c> is the only witness that survives a change to the code that produced it — a
/// self-computed expectation would silently follow the bug. Regenerating by default would defeat the mechanism: the vectors would rewrite themselves to match
/// whatever the code now does, and the test could never fail.
/// </para>
/// <para>
/// <b>Numbers in expectations are IEEE bit patterns</b>, as 16 hex digits (<see cref="Bits"/>): decoded values must be bit-exact across languages (W1), and a
/// JSON number cannot carry NaN, infinities or negative zero, nor promise the last bit survives a round trip through a formatter.
/// </para>
/// <para>
/// The directory is found by walking up to the project file rather than by copying content to the output directory, because regeneration has to write into
/// the source tree that gets committed, not into <c>bin/</c>.
/// </para>
/// </remarks>
internal static class Golden
{
    private const string UpdateVariable = "TYPHON_UPDATE_GOLDEN";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    /// <summary>Asserts <paramref name="bytes"/> and <paramref name="expectation"/> equal the committed vector, or rewrites it on a regeneration run.</summary>
    internal static void Assert(string caseName, byte[] bytes, JsonObject expectation)
    {
        var directory = Directory();
        var binPath = Path.Combine(directory, caseName + ".bin");
        var jsonPath = Path.Combine(directory, caseName + ".json");

        // "\n", not Environment.NewLine: regenerating on Windows and on Linux must produce the same file.
        var json = expectation.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";

        if (Updating)
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllBytes(binPath, bytes);
            File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
            return;
        }

        NUnit.Framework.Assert.That(File.Exists(binPath), Is.True, $"missing golden vector {caseName}.bin — regenerate with {UpdateVariable}=1 and commit it");
        var committedBytes = File.ReadAllBytes(binPath);
        var committedJson = File.ReadAllText(jsonPath).ReplaceLineEndings("\n");

        NUnit.Framework.Assert.Multiple(() =>
        {
            NUnit.Framework.Assert.That(Convert.ToHexString(bytes), Is.EqualTo(Convert.ToHexString(committedBytes)), $"{caseName}.bin drifted from the vector");
            NUnit.Framework.Assert.That(json, Is.EqualTo(committedJson), $"{caseName}.json drifted from the vector");
        });
    }

    /// <summary>
    /// A double's IEEE bits as 16 lower-case hex digits, or <c>"nan"</c>: JavaScript cannot observe a NaN's sign or payload, so NaN is compared as a token
    /// while every other value, signed zeros and infinities included, is compared bit for bit.
    /// </summary>
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

    internal static JsonNode CodecJson(CatalogCodec codec) => JsonSerializer.SerializeToNode(codec, CatalogJson);

    private static readonly JsonSerializerOptions CatalogJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    private static string Directory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && directory.GetFiles("Typhon.Protocol.Tests.csproj").Length == 0)
        {
            directory = directory.Parent;
        }

        NUnit.Framework.Assert.That(directory, Is.Not.Null, "could not locate the test project directory from the test assembly location");
        return Path.Combine(directory!.FullName, "Golden");
    }
}

/// <summary>
/// Records every call a decoder makes into its sink as a JSON log: the decode-level contract every client implementation reproduces call for call.
/// </summary>
internal sealed class RecordingSink : ITickSink, ICommandSink
{
    internal JsonArray Log { get; } = [];

    public void BeginTick(uint tick, TickFlags flags, uint periodUs) =>
        Log.Add(new JsonObject { ["call"] = "beginTick", ["tick"] = tick, ["flags"] = (int)flags, ["periodUs"] = periodUs });

    public void BeginEntities(ArchetypePlan archetype) => Log.Add(new JsonObject { ["call"] = "beginEntities", ["archetype"] = archetype.Name });

    public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
        Log.Add(new JsonObject
        {
            ["call"] = "enter", ["netId"] = netId, ["position"] = Golden.Bits(position), ["velocity"] = Golden.Bits(velocity), ["t0"] = t0, ["epoch"] = epoch,
        });

    public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
        Log.Add(new JsonObject
        {
            ["call"] = "segment", ["netId"] = netId, ["position"] = Golden.Bits(position), ["velocity"] = Golden.Bits(velocity), ["t0"] = t0, ["epoch"] = epoch,
        });

    public void State(uint netId, byte groupMask) => Log.Add(new JsonObject { ["call"] = "state", ["netId"] = netId, ["groupMask"] = groupMask });

    public void Leave(uint netId) => Log.Add(new JsonObject { ["call"] = "leave", ["netId"] = netId });

    public void Event(MessagePlan type) => Log.Add(new JsonObject { ["call"] = "event", ["type"] = type.Name, ["idx"] = type.Idx });

    public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask) =>
        Log.Add(new JsonObject { ["call"] = "self", ["archetype"] = archetype.Name, ["netId"] = netId, ["lastSeq"] = lastSeq, ["ownerMask"] = ownerMask });

    public void Ack(ushort seq, byte reason) => Log.Add(new JsonObject { ["call"] = "ack", ["seq"] = seq, ["reason"] = reason });

    public void Source(ushort requestId, byte status, ushort code) =>
        Log.Add(new JsonObject { ["call"] = "source", ["requestId"] = requestId, ["status"] = status, ["code"] = code });

    public void BeginAggregate(CatalogGrid grid, bool reset) => Log.Add(new JsonObject { ["call"] = "beginAggregate", ["grid"] = grid.Idx, ["reset"] = reset });

    public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts)
    {
        var array = new JsonArray();
        foreach (var c in counts)
        {
            array.Add(c);
        }

        Log.Add(new JsonObject { ["call"] = "aggregateCell", ["cell"] = cell, ["counts"] = array });
    }

    public void Metric(MetricPlan metric, int valueIndex, double value) =>
        Log.Add(new JsonObject { ["call"] = "metric", ["name"] = metric.Name, ["index"] = valueIndex, ["value"] = Golden.Bits(value) });

    public void Debug(byte subType, scoped ReadOnlySpan<byte> payload) =>
        Log.Add(new JsonObject { ["call"] = "debug", ["subType"] = subType, ["payload"] = Golden.Hex(payload) });

    public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload) =>
        Log.Add(new JsonObject { ["call"] = "ext", ["appTypeId"] = appTypeId, ["payload"] = Golden.Hex(payload) });

    public void UnknownBlock(byte blockType) => Log.Add(new JsonObject { ["call"] = "unknownBlock", ["blockType"] = blockType });

    public void EndTick() => Log.Add(new JsonObject { ["call"] = "endTick" });

    public void Command(MessagePlan type, ushort seq, uint clientTick) =>
        Log.Add(new JsonObject { ["call"] = "command", ["type"] = type.Name, ["idx"] = type.Idx, ["seq"] = seq, ["clientTick"] = clientTick });

    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components) =>
        Log.Add(new JsonObject { ["call"] = "number", ["field"] = field.Name, ["values"] = Golden.Bits(components) });

    public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8) =>
        Log.Add(new JsonObject { ["call"] = "text", ["field"] = field.Name, ["utf8"] = Golden.Hex(utf8) });

    public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes) =>
        Log.Add(new JsonObject { ["call"] = "bytes", ["field"] = field.Name, ["bytes"] = Golden.Hex(bytes) });

    public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components) =>
        Log.Add(new JsonObject { ["call"] = "list", ["field"] = field.Name, ["count"] = count, ["values"] = Golden.Bits(components) });
}
