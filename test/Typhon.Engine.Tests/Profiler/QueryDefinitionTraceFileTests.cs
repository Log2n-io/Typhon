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
        writer.WriteTracks(ReadOnlySpan<TrackRecord>.Empty);
        writer.WriteDags(ReadOnlySpan<DagRecord>.Empty);
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
        writer.WriteTracks(ReadOnlySpan<TrackRecord>.Empty);
        writer.WriteDags(ReadOnlySpan<DagRecord>.Empty);
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
    public void V8Trace_IsHardRejected_ByReader()
    {
        // v11 (#354) is a layout-breaking change — the SystemDefinitionTable gained a DagId field and the
        // PhasesTable was replaced by a TracksTable + DagsTable. The reader hard-rejects v10-and-older traces;
        // a synthesized v8 header must throw at ReadHeader rather than mis-decode.
        var stream = new MemoryStream();
        using var bw = new System.IO.BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        bw.Write(TraceFileHeader.MagicValue);
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
        bw.Flush();

        stream.Position = 0;
        var reader = new TraceFileReader(stream);
        var ex = Assert.Throws<System.IO.InvalidDataException>(() => reader.ReadHeader());
        Assert.That(ex.Message, Does.Contain("version: 8"));
    }
}
