using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Phase 3 (#351) round-trip: write a trace with a CPU-sample trailer section, read it back, confirm the records / interned stacks / frame symbols
/// survive and the header offset is patched. Also covers the absent-section case and v9-trace forward-compat under the v10 reader.
/// </summary>
/// <remarks>
/// <see cref="TraceFileWriter.Dispose"/> closes the underlying stream, so these tests write to a <see cref="MemoryStream"/> and skip Dispose, reading the
/// same stream back — the same pattern as <see cref="SourceLocationManifestRoundTripTests"/>.
/// </remarks>
[TestFixture]
public sealed class CpuSampleSectionRoundTripTests
{
    private static TraceFileHeader NewHeader() => new()
    {
        Magic = TraceFileHeader.MagicValue,
        Version = TraceFileHeader.CurrentVersion,
        Flags = 0,
        TimestampFrequency = 10_000_000,
        BaseTickRate = 60.0f,
        WorkerCount = 4,
        SystemCount = 0,
        ArchetypeCount = 0,
        ComponentTypeCount = 0,
        CreatedUtcTicks = 0,
        SamplingSessionStartQpc = 0,
    };

    private static void WriteScaffold(TraceFileWriter writer, in TraceFileHeader header)
    {
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord>.Empty);
        writer.WriteArchetypes(ReadOnlySpan<ArchetypeRecord>.Empty);
        writer.WriteComponentTypes(ReadOnlySpan<ComponentTypeRecord>.Empty);
        writer.WritePhases(ReadOnlySpan<string>.Empty);
        writer.WriteEmptyStaticStructures();
    }

    [Test]
    public void CpuSampleSection_RoundTripsThroughTraceFile()
    {
        var stream = new MemoryStream();
        var writer = new TraceFileWriter(stream);
        var header = NewHeader();
        WriteScaffold(writer, in header);

        var samples = new[]
        {
            new CpuSampleRecord(qpc: 1000, threadSlot: 3, sampleType: 0, stackIndex: 0),
            new CpuSampleRecord(qpc: 2000, threadSlot: -1, sampleType: 1, stackIndex: 1),
            new CpuSampleRecord(qpc: 3000, threadSlot: 3, sampleType: 0, stackIndex: 0),
        };
        var stacks = new[]
        {
            new ushort[] { 0, 1, 2 },
            new ushort[] { 2 },
        };
        var frameSymbols = new[]
        {
            new CpuFrameSymbol(frameId: 0, fileId: 5, line: 100, method: "Engine.Foo"),
            new CpuFrameSymbol(frameId: 1, fileId: 0, line: 0, method: "System.Bar"),      // name-only frame
            new CpuFrameSymbol(frameId: 2, fileId: 7, line: 250, method: "Engine.Baz"),
        };

        var offset = writer.WriteCpuSampleSection(samples, stacks, frameSymbols);
        Assert.That(offset, Is.GreaterThan(0));
        header.CpuSampleSectionOffset = offset;
        writer.RewriteHeader(in header);
        writer.Flush();

        stream.Position = 0;
        var reader = new TraceFileReader(stream);
        var readHeader = reader.ReadHeader();
        Assert.That(readHeader.Version, Is.EqualTo((ushort)10));
        Assert.That(readHeader.CpuSampleSectionOffset, Is.EqualTo(offset));

        var ok = reader.TryReadCpuSampleSection(out var rtSamples, out var rtStacks, out var rtFrames);
        Assert.That(ok, Is.True);

        Assert.That(rtSamples.Length, Is.EqualTo(samples.Length));
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.That(rtSamples[i].Qpc, Is.EqualTo(samples[i].Qpc));
            Assert.That(rtSamples[i].ThreadSlot, Is.EqualTo(samples[i].ThreadSlot), "thread slot must survive — including -1 (0xFF on the wire)");
            Assert.That(rtSamples[i].SampleType, Is.EqualTo(samples[i].SampleType));
            Assert.That(rtSamples[i].StackIndex, Is.EqualTo(samples[i].StackIndex));
        }

        Assert.That(rtStacks.Length, Is.EqualTo(stacks.Length));
        for (var i = 0; i < stacks.Length; i++)
        {
            Assert.That(rtStacks[i], Is.EqualTo(stacks[i]));
        }

        Assert.That(rtFrames.Length, Is.EqualTo(frameSymbols.Length));
        for (var i = 0; i < frameSymbols.Length; i++)
        {
            Assert.That(rtFrames[i].FrameId, Is.EqualTo(frameSymbols[i].FrameId));
            Assert.That(rtFrames[i].FileId, Is.EqualTo(frameSymbols[i].FileId));
            Assert.That(rtFrames[i].Line, Is.EqualTo(frameSymbols[i].Line));
            Assert.That(rtFrames[i].Method, Is.EqualTo(frameSymbols[i].Method));
        }
        // The name-only frame (line 0) reports HasSource == false; the resolved frames report true.
        Assert.That(rtFrames[1].HasSource, Is.False);
        Assert.That(rtFrames[0].HasSource, Is.True);
        Assert.That(rtFrames[2].HasSource, Is.True);
    }

    [Test]
    public void CpuSampleSection_AbsentWhenOffsetIsZero()
    {
        var stream = new MemoryStream();
        var writer = new TraceFileWriter(stream);
        var header = NewHeader();
        WriteScaffold(writer, in header);
        writer.Flush();

        stream.Position = 0;
        var reader = new TraceFileReader(stream);
        reader.ReadHeader();
        var ok = reader.TryReadCpuSampleSection(out var samples, out var stacks, out var frames);
        Assert.That(ok, Is.False);
        Assert.That(samples, Is.Empty);
        Assert.That(stacks, Is.Empty);
        Assert.That(frames, Is.Empty);
    }

    [Test]
    public void ReadHeader_V9OnDiskLayout_DefaultsCpuSampleOffsetToZero()
    {
        // Synthesize a genuine v9 on-disk header (79 bytes): the v10 struct (87 bytes) without the 8-byte CpuSampleSectionOffset at [75..83). A v10 reader
        // must parse it version-conditionally, leave the stream positioned exactly past byte 79, and default CpuSampleSectionOffset to 0.
        var v10 = NewHeader();
        v10.QuerySourceStringTableOffset = 0;
        v10.QueryDefinitionTableOffset = 0;
        v10.CpuSampleSectionOffset = 12345; // must be ignored — absent on disk for v9
        var v10Bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in v10, 1)).ToArray();
        Assert.That(v10Bytes.Length, Is.EqualTo(87), "v10 header struct must be 87 bytes");

        var v9Bytes = new byte[79];
        Array.Copy(v10Bytes, 0, v9Bytes, 0, 75);    // common prefix + query offsets
        Array.Copy(v10Bytes, 83, v9Bytes, 75, 4);   // reserved pad — skip CpuSampleSectionOffset at [75..83)
        BinaryPrimitives.WriteUInt16LittleEndian(v9Bytes.AsSpan(4), 9); // stamp the on-disk version as v9

        var stream = new MemoryStream();
        stream.Write(v9Bytes);
        stream.Position = 0;

        var reader = new TraceFileReader(stream);
        var header = reader.ReadHeader();
        Assert.That(header.Version, Is.EqualTo((ushort)9));
        Assert.That(header.CpuSampleSectionOffset, Is.Zero, "a v9 trace carries no CPU-sample section");
        Assert.That(stream.Position, Is.EqualTo(79), "the v9 header must be consumed exactly — no over/under-read");
    }
}
