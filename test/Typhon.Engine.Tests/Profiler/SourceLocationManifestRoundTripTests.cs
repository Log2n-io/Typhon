using System;
using System.IO;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Phase 2 round-trip: write a trace with a source-location manifest in the trailer, read it back,
/// confirm contents and that the file header offsets are correctly patched.
///
/// NOTE: <see cref="TraceFileWriter.Dispose"/> closes the underlying stream. These tests read the
/// same MemoryStream after writing, so they explicitly skip Dispose. Production callers writing to
/// a file path don't hit this — they re-open the file for reading.
/// </summary>
[TestFixture]
public class SourceLocationManifestRoundTripTests
{
    [Test]
    public void Manifest_RoundTripsThroughTraceFile()
    {
        var stream = new MemoryStream();

        var files = new[]
        {
            "/_/src/Typhon.Engine/Data/Index/BTree.cs",
            "/_/src/Typhon.Engine/Data/Transaction/Transaction.cs",
        };
        var entries = new[]
        {
            new SourceLocationManifestEntry(id: 1, fileId: 0, line: 1124, kind: 12, method: "BTree.Add"),
            new SourceLocationManifestEntry(id: 2, fileId: 1, line: 217, kind: 1, method: "Transaction.Commit"),
        };

        var writer = new TraceFileWriter(stream);
        var header = new TraceFileHeader
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
            FileTableOffset = 0,
            SourceLocationManifestOffset = 0,
        };
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord>.Empty);
        writer.WriteArchetypes(ReadOnlySpan<ArchetypeRecord>.Empty);
        writer.WriteComponentTypes(ReadOnlySpan<ComponentTypeRecord>.Empty);

        var (fileTableOffset, manifestOffset) = writer.WriteSourceLocationManifest(files, entries);

        header.FileTableOffset = fileTableOffset;
        header.SourceLocationManifestOffset = manifestOffset;
        writer.RewriteHeader(in header);
        writer.Flush();

        Assert.That(fileTableOffset, Is.GreaterThan(0));
        Assert.That(manifestOffset, Is.GreaterThan(fileTableOffset));

        // Read it back from the same stream.
        stream.Position = 0;
        var reader = new TraceFileReader(stream);
        var readHeader = reader.ReadHeader();
        Assert.That(readHeader.Version, Is.EqualTo(TraceFileHeader.CurrentVersion));
        Assert.That(readHeader.FileTableOffset, Is.EqualTo(fileTableOffset));
        Assert.That(readHeader.SourceLocationManifestOffset, Is.EqualTo(manifestOffset));

        // TryReadSourceLocationManifest seeks absolutely and restores position, so order-independent.
        var ok = reader.TryReadSourceLocationManifest(out var roundtripFiles, out var roundtripEntries);
        Assert.That(ok, Is.True);
        Assert.That(roundtripFiles, Is.EqualTo(files));
        Assert.That(roundtripEntries.Length, Is.EqualTo(entries.Length));
        for (int i = 0; i < entries.Length; i++)
        {
            Assert.That(roundtripEntries[i].Id, Is.EqualTo(entries[i].Id));
            Assert.That(roundtripEntries[i].FileId, Is.EqualTo(entries[i].FileId));
            Assert.That(roundtripEntries[i].Line, Is.EqualTo(entries[i].Line));
            Assert.That(roundtripEntries[i].Kind, Is.EqualTo(entries[i].Kind));
            Assert.That(roundtripEntries[i].Method, Is.EqualTo(entries[i].Method));
        }
    }

    [Test]
    public void Manifest_AbsentWhenOffsetsAreZero()
    {
        var stream = new MemoryStream();
        var writer = new TraceFileWriter(stream);

        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = 10_000_000,
            BaseTickRate = 60.0f,
            WorkerCount = 1,
            SystemCount = 0,
            ArchetypeCount = 0,
            ComponentTypeCount = 0,
            CreatedUtcTicks = 0,
            SamplingSessionStartQpc = 0,
            FileTableOffset = 0,
            SourceLocationManifestOffset = 0,
        };
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord>.Empty);
        writer.WriteArchetypes(ReadOnlySpan<ArchetypeRecord>.Empty);
        writer.WriteComponentTypes(ReadOnlySpan<ComponentTypeRecord>.Empty);
        writer.Flush();

        stream.Position = 0;
        var reader = new TraceFileReader(stream);
        reader.ReadHeader();
        var ok = reader.TryReadSourceLocationManifest(out var files, out var entries);
        Assert.That(ok, Is.False);
        Assert.That(files, Is.Empty);
        Assert.That(entries, Is.Empty);
    }
}
