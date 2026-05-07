using System.IO;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Wire-format tests for the trace v6 SystemDefinitionTable + PhasesTable extension introduced for #310.
///
/// Goals:
///   1. Round-trip every RFC 07 field declared on <see cref="SystemDefinitionRecord"/>.
///   2. Backward-compat: a v5 trace (no RFC 07 fields, no Phases section) reads with empty defaults
///      and does not throw.
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
                writer.WritePhases(["Input", "Simulation", "Output"]);
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
        reader.ReadPhases();

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

        Assert.That(reader.Phases, Is.EqualTo(new[] { "Input", "Simulation", "Output" }));
    }

    [Test]
    public void V5_BackwardCompat_RfcFieldsDefaultToEmpty()
    {
        // Craft a v5 system-definition table — same byte layout as v6 minus the RFC 07 trailer and
        // minus the PhasesTable section. The reader must populate empty defaults and not consume bytes
        // for the missing trailer / phases. We serialize the header through MemoryMarshal so the on-disk
        // struct shape is whatever the runtime would produce, regardless of packing nuances.
        var v5Header = DefaultHeaderV6;
        v5Header.Version = 5;

        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var bw = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: false))
        {
            var headerBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref v5Header, 1));
            ms.Write(headerBytes);

            // SystemDefinitionTable — v5 layout (no RFC 07 trailer).
            bw.Write((ushort)1);                          // count
            bw.Write((ushort)0);                          // index
            WriteShortString(bw, "Legacy");                // name
            bw.Write((byte)0);                             // type
            bw.Write((byte)0);                             // priority
            bw.Write(false);                               // isParallel
            bw.Write((byte)0x0F);                          // tierFilter
            bw.Write((byte)0);                             // predCount
            bw.Write((byte)0);                             // succCount

            // Archetypes + ComponentTypes — empty. (No PhasesTable in v5.)
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Flush();
            bytes = ms.ToArray();
        }

        using var readStream = new MemoryStream(bytes, writable: false);
        using var reader = new TraceFileReader(readStream);
        reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();
        reader.ReadPhases();

        Assert.That(reader.Header.Version, Is.EqualTo(5));
        Assert.That(reader.Systems, Has.Count.EqualTo(1));
        var s = reader.Systems[0];
        Assert.That(s.Name, Is.EqualTo("Legacy"));
        Assert.That(s.PhaseName, Is.EqualTo(string.Empty));
        Assert.That(s.IsExclusivePhase, Is.False);
        Assert.That(s.Reads, Is.Empty);
        Assert.That(s.Writes, Is.Empty);
        Assert.That(s.WritesEvents, Is.Empty);
        Assert.That(s.ReadsEvents, Is.Empty);
        Assert.That(s.ExplicitAfter, Is.Empty);
        Assert.That(s.ExplicitBefore, Is.Empty);
        Assert.That(reader.Phases, Is.Empty);
    }

    private static void WriteShortString(System.IO.BinaryWriter bw, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        var len = (byte)System.Math.Min(bytes.Length, 255);
        bw.Write(len);
        bw.Write(bytes, 0, len);
    }
}
