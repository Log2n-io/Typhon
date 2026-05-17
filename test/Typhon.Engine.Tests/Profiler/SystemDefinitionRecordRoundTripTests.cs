using System.IO;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Wire-format tests for the trace SystemDefinitionTable + Track→DAG hierarchy (v11, #354).
///
/// Goals:
///   1. Round-trip every RFC 07 field declared on <see cref="SystemDefinitionRecord"/>, including the v11 DagId.
///   2. Round-trip the TracksTable + DagsTable that replaced the v6 PhasesTable.
///   3. Hard-reject layout-incompatible old versions (v10 and earlier).
/// </summary>
[TestFixture]
public sealed class SystemDefinitionRecordRoundTripTests
{
    private static readonly TraceFileHeader DefaultHeaderV6 = new()
    {
        Magic = TraceFileHeader.MagicValue,
        Version = TraceFileHeader.CurrentVersion,
        Flags = 0,
        TimestampFrequency = 10_000_000,
        BaseTickRate = 60f,
        WorkerCount = 1,
        SystemCount = 1,
        ArchetypeCount = 0,
        ComponentTypeCount = 0,
        CreatedUtcTicks = 0,
        SamplingSessionStartQpc = 0,
    };

    [Test]
    public void V6_RoundTrips_AllRfc07Fields()
    {
        var input = new SystemDefinitionRecord
        {
            Index = 7,
            Name = "Movement",
            Type = 0,
            Priority = 1,
            IsParallel = true,
            TierFilter = 0x0F,
            Predecessors = [3, 5],
            Successors = [11],
            PhaseName = "Simulation",
            IsExclusivePhase = true,
            Reads = ["A", "B"],
            ReadsFresh = ["C"],
            ReadsSnapshot = ["D", "E"],
            AdditionalReads = ["F"],
            Writes = ["G"],
            SideWrites = ["H", "I"],
            WritesEvents = ["q1"],
            ReadsEvents = ["q2", "q3"],
            WritesResources = ["r1"],
            ReadsResources = ["r2"],
            ExplicitAfter = ["X"],
            ExplicitBefore = ["Y"],
            DagId = 4,
        };

        byte[] bytes;
        using (var writeStream = new MemoryStream())
        {
            using (var writer = new TraceFileWriter(writeStream))
            {
                var hdr = DefaultHeaderV6;
                writer.WriteHeader(in hdr);
                writer.WriteSystemDefinitions([input]);
                writer.WriteArchetypes([]);
                writer.WriteComponentTypes([]);
                writer.WriteTracks([new TrackRecord { Name = "Main", OrderIndex = 0, Tags = ["engine"] }]);
                writer.WriteDags([new DagRecord { Id = 4, Name = "SimDag", TrackIndex = 0, PhaseNames = ["Input", "Simulation", "Output"] }]);
                writer.WriteEmptyStaticStructures();
                writer.Flush();
                bytes = writeStream.ToArray();
            }
        }

        using var ms = new MemoryStream(bytes, writable: false);
        using var reader = new TraceFileReader(ms);
        reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();
        reader.ReadTracks();
        reader.ReadDags();
        reader.ReadComponentDefinitions();
        reader.ReadArchetypeDefinitions();
        reader.ReadIndexCatalog();
        reader.ReadRuntimeConfig();
        reader.ReadEventQueueCatalog();
        reader.ReadResourceGraphSnapshot();

        Assert.That(reader.Systems, Has.Count.EqualTo(1));
        var output = reader.Systems[0];
        Assert.That(output.Index, Is.EqualTo(7));
        Assert.That(output.Name, Is.EqualTo("Movement"));
        Assert.That(output.IsParallel, Is.True);
        Assert.That(output.Predecessors, Is.EqualTo(new ushort[] { 3, 5 }));
        Assert.That(output.Successors, Is.EqualTo(new ushort[] { 11 }));
        Assert.That(output.PhaseName, Is.EqualTo("Simulation"));
        Assert.That(output.IsExclusivePhase, Is.True);
        Assert.That(output.Reads, Is.EqualTo(new[] { "A", "B" }));
        Assert.That(output.ReadsFresh, Is.EqualTo(new[] { "C" }));
        Assert.That(output.ReadsSnapshot, Is.EqualTo(new[] { "D", "E" }));
        Assert.That(output.AdditionalReads, Is.EqualTo(new[] { "F" }));
        Assert.That(output.Writes, Is.EqualTo(new[] { "G" }));
        Assert.That(output.SideWrites, Is.EqualTo(new[] { "H", "I" }));
        Assert.That(output.WritesEvents, Is.EqualTo(new[] { "q1" }));
        Assert.That(output.ReadsEvents, Is.EqualTo(new[] { "q2", "q3" }));
        Assert.That(output.WritesResources, Is.EqualTo(new[] { "r1" }));
        Assert.That(output.ReadsResources, Is.EqualTo(new[] { "r2" }));
        Assert.That(output.ExplicitAfter, Is.EqualTo(new[] { "X" }));
        Assert.That(output.ExplicitBefore, Is.EqualTo(new[] { "Y" }));
        Assert.That(output.DagId, Is.EqualTo(4));

        Assert.That(reader.Tracks, Has.Count.EqualTo(1));
        Assert.That(reader.Tracks[0].Name, Is.EqualTo("Main"));
        Assert.That(reader.Tracks[0].Tags, Is.EqualTo(new[] { "engine" }));
        Assert.That(reader.Dags, Has.Count.EqualTo(1));
        Assert.That(reader.Dags[0].Id, Is.EqualTo(4));
        Assert.That(reader.Dags[0].Name, Is.EqualTo("SimDag"));
        Assert.That(reader.Dags[0].TrackIndex, Is.EqualTo(0));
        Assert.That(reader.Dags[0].PhaseNames, Is.EqualTo(new[] { "Input", "Simulation", "Output" }));
    }

    [TestCase((ushort)8)]
    [TestCase((ushort)10)]
    public void OldVersionsAreHardRejected(ushort version)
    {
        // v11 is a layout-breaking change (SystemDefinitionTable gained a DagId field; PhasesTable replaced by
        // TracksTable + DagsTable). Older traces would mis-decode, so the reader hard-rejects them.
        //
        // Critically: we craft a header with VALID magic + the old version. With magic correct, the reader's
        // magic-check (which fires first per #6 of the v7 review) passes and the version-check is what rejects.
        // An earlier version of this test relied on zero magic/version and was actually rejected on magic, leaving
        // the version-rejection path uncovered.
        var oldHeader = DefaultHeaderV6;
        oldHeader.Version = version;
        oldHeader.Magic = TraceFileHeader.MagicValue;

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            var headerBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref oldHeader, 1));
            ms.Write(headerBytes);
            bytes = ms.ToArray();
        }

        using var readStream = new MemoryStream(bytes, writable: false);
        using var reader = new TraceFileReader(readStream);

        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadHeader());
        Assert.That(ex.Message, Does.Contain($"version: {version}"));
        Assert.That(ex.Message, Does.Contain("Re-record"));
    }

    [Test]
    public void InvalidMagicIsRejected_BeforeVersionCheck()
    {
        // Companion to OldVersionsAreHardRejected: a file with wrong magic should fail with a magic-specific error,
        // not a misleading "Unsupported version" error. Guards the magic-before-version order in ReadHeader.
        var hdr = DefaultHeaderV6;
        hdr.Magic = 0xDEADBEEF;
        hdr.Version = TraceFileHeader.CurrentVersion;

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            var headerBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref hdr, 1));
            ms.Write(headerBytes);
            bytes = ms.ToArray();
        }

        using var readStream = new MemoryStream(bytes, writable: false);
        using var reader = new TraceFileReader(readStream);

        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadHeader());
        Assert.That(ex.Message, Does.Contain("magic"));
        Assert.That(ex.Message, Does.Not.Contain("version"), "magic check must fire before version check");
    }
}
