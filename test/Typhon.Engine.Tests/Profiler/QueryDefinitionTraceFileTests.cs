using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// File-level tests for the v9 Query Definition Export trace additions (#342):
/// <list type="bullet">
/// <item>Writing + reading a <c>QuerySourceStringTable</c> trailer section.</item>
/// <item>v8 trace file → v9 reader: header partial-read decodes correctly with the new offsets zeroed
/// and the absent <see cref="TraceFileReader.TryReadQuerySourceStringTable"/> path returns false.</item>
/// </list>
/// </summary>
[TestFixture]
public class QueryDefinitionTraceFileTests
{
    [Test]
    public void QuerySourceStringTable_WritesAndReadsBack()
    {
        var stream = new MemoryStream();
        var writer = new TraceFileWriter(stream);
        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            TimestampFrequency = 10_000_000,
            BaseTickRate = 60.0f,
            WorkerCount = 4,
            CreatedUtcTicks = 0,
        };
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord>.Empty);
        writer.WriteArchetypes(ReadOnlySpan<ArchetypeRecord>.Empty);
        writer.WriteComponentTypes(ReadOnlySpan<ComponentTypeRecord>.Empty);
        writer.WritePhases(ReadOnlySpan<string>.Empty);
        writer.WriteEmptyStaticStructures();

        var strings = new[]
        {
            null,  // sentinel
            "/_/src/Typhon.Engine/Querying/internals/PlanBuilder.cs",
            "BuildPlan",
            "/_/test/AntHill/ECS/Systems/AntUpdateSystem.cs",
        };
        var qsstOffset = writer.WriteQuerySourceStringTable(strings);
        Assert.That(qsstOffset, Is.GreaterThan(0));

        header.QuerySourceStringTableOffset = qsstOffset;
        writer.RewriteHeader(in header);
        writer.Flush();

        // Read it back
        stream.Position = 0;
        var reader = new TraceFileReader(stream);
        var readHeader = reader.ReadHeader();
        Assert.That(readHeader.Version, Is.EqualTo(TraceFileHeader.CurrentVersion));
        Assert.That(readHeader.QuerySourceStringTableOffset, Is.EqualTo(qsstOffset));

        var ok = reader.TryReadQuerySourceStringTable(out var roundtrip);
        Assert.That(ok, Is.True);
        Assert.That(roundtrip.Length, Is.EqualTo(4));
        Assert.That(roundtrip[0], Is.EqualTo(string.Empty));  // sentinel written as empty
        Assert.That(roundtrip[1], Is.EqualTo(strings[1]));
        Assert.That(roundtrip[2], Is.EqualTo(strings[2]));
        Assert.That(roundtrip[3], Is.EqualTo(strings[3]));
    }

    [Test]
    public void TryReadQuerySourceStringTable_ReturnsFalse_WhenOffsetIsZero()
    {
        var stream = new MemoryStream();
        var writer = new TraceFileWriter(stream);
        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            TimestampFrequency = 10_000_000,
            BaseTickRate = 60.0f,
            WorkerCount = 4,
            CreatedUtcTicks = 0,
        };
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord>.Empty);
        writer.WriteArchetypes(ReadOnlySpan<ArchetypeRecord>.Empty);
        writer.WriteComponentTypes(ReadOnlySpan<ComponentTypeRecord>.Empty);
        writer.WritePhases(ReadOnlySpan<string>.Empty);
        writer.WriteEmptyStaticStructures();
        writer.Flush();

        stream.Position = 0;
        var reader = new TraceFileReader(stream);
        reader.ReadHeader();
        var ok = reader.TryReadQuerySourceStringTable(out var strings);
        Assert.That(ok, Is.False);
        Assert.That(strings, Is.Empty);
    }

    [Test]
    public void V8Trace_LoadsGracefully_WithReader()
    {
        // Synthesize a v8 trace: shorter header (63 bytes), no QSST/QueryDefinitionTable trailer offsets.
        // The reader should decode the header, defaulting the new offsets to 0, and TryReadQuerySourceStringTable returns false.
        var stream = new MemoryStream();
        using var bw = new System.IO.BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        // Write a v8-shaped header (63 bytes total).
        bw.Write(TraceFileHeader.MagicValue);              // 4
        bw.Write((ushort)8);                                // Version=8
        bw.Write((ushort)0);                                // Flags
        bw.Write(10_000_000L);                              // TimestampFrequency
        bw.Write(60.0f);                                    // BaseTickRate
        bw.Write((byte)2);                                  // WorkerCount
        bw.Write((ushort)0);                                // SystemCount
        bw.Write((ushort)0);                                // ArchetypeCount
        bw.Write((ushort)0);                                // ComponentTypeCount
        bw.Write(0L);                                       // CreatedUtcTicks
        bw.Write(0L);                                       // SamplingSessionStartQpc
        bw.Write(0L);                                       // FileTableOffset
        bw.Write(0L);                                       // SourceLocationManifestOffset
        bw.Write((ushort)0);                                // Reserved0
        bw.Write((ushort)0);                                // Reserved1

        // Required tables (all empty).
        bw.Write((ushort)0);  // SystemCount
        bw.Write((ushort)0);  // ArchetypeCount
        bw.Write((ushort)0);  // ComponentTypeCount
        bw.Write((ushort)0);  // PhasesCount

        // v7+ static structures (all empty).
        bw.Write((ushort)0);  // ComponentDefinitions
        bw.Write((ushort)0);  // ArchetypeDefinitions
        bw.Write((ushort)0);  // IndexCatalog
        bw.Write(false);      // RuntimeConfig presence flag = false
        bw.Write((ushort)0);  // EventQueues
        bw.Write(0);          // ResourceGraphNodes (int32 length)
        bw.Flush();

        stream.Position = 0;
        var reader = new TraceFileReader(stream);
        var header = reader.ReadHeader();

        Assert.That(header.Version, Is.EqualTo((ushort)8));
        Assert.That(header.TimestampFrequency, Is.EqualTo(10_000_000L));
        Assert.That(header.WorkerCount, Is.EqualTo((byte)2));
        Assert.That(header.QuerySourceStringTableOffset, Is.EqualTo(0L), "v9-only trailer offset should be 0 for a v8 trace");
        Assert.That(header.QueryDefinitionTableOffset, Is.EqualTo(0L), "v9-only trailer offset should be 0 for a v8 trace");

        var ok = reader.TryReadQuerySourceStringTable(out var strings);
        Assert.That(ok, Is.False);
        Assert.That(strings, Is.Empty);
    }
}
