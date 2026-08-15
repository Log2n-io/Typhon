using System;
using System.Buffers.Binary;
using System.IO;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Feature #614 (F1) — the v12 trace revision that makes a capture self-describing. Covers the four decisions of
/// claude/design/Apps/Workbench/10-database-and-profiles.md that landed together: <b>D-2</b> (database identity + TSN window), <b>D-3</b>
/// (<see cref="ArchetypeRecord.RoutingId"/>), <b>D-5</b> (header-only listing fields) and <b>D-9</b> (multi-engine degradation).
/// </summary>
/// <remarks>
/// These are wire-format tests: they drive the writer and assert on what the reader — and in the D-9 case, the raw bytes — produce. What they deliberately do
/// NOT prove is that the routing ids a real capture writes are *correct*: in a freshly-created database registration order and routing order coincide, so any
/// fixture agrees with a naive catalog-id join. That check needs a database that gained archetypes over time and lives in the end-to-end verification.
/// </remarks>
[TestFixture]
public sealed class TraceV12SelfDescribingTests
{
    private static readonly Guid SampleDatabaseId = new("7f3a91e2-4c5d-4e6f-8a9b-0c1d2e3f4a5b");

    private static TraceFileHeader NewHeader()
    {
        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = 10_000_000,
            BaseTickRate = 60f,
            WorkerCount = 4,
            CreatedUtcTicks = 638_000_000_000_000_000,
            DatabaseId = SampleDatabaseId,
            TsnMin = 41_022,
            TsnMax = 58_110,
            DurationTicks = 123_456_789,
            TickCount = 216_000,
            SchemaFingerprint = 0xDEAD_BEEF_CAFE_F00DUL,
        };
        header.SetDatabaseName("world");
        return header;
    }

    /// <summary>Writes a minimal but positionally-complete v12 file so the reader can walk it, and returns the stream rewound to 0.</summary>
    private static MemoryStream WriteTrace(in TraceFileHeader header, ArchetypeRecord[] archetypes, Action<TraceFileWriter> afterTables = null)
    {
        var stream = new MemoryStream();
        var writer = new TraceFileWriter(stream);  // not disposed — that would close the stream we are about to read
        writer.WriteHeader(in header);
        writer.WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord>.Empty);
        writer.WriteArchetypes(archetypes);
        writer.WriteComponentTypes(ReadOnlySpan<ComponentTypeRecord>.Empty);
        writer.WriteTracks(ReadOnlySpan<TrackRecord>.Empty);
        writer.WriteDags(ReadOnlySpan<DagRecord>.Empty);
        writer.WriteEmptyStaticStructures();
        afterTables?.Invoke(writer);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    private static ArchetypeRecord[] SampleArchetypes() =>
    [
        // Catalog ids and routing ids deliberately DISAGREE — this is the real-database shape (registration order ≠ persisted routing order). A fixture where
        // they matched would let a catalog-id/routing-id confusion pass unnoticed, which is exactly §5.3's failure mode.
        new ArchetypeRecord { ArchetypeId = 1, Name = "Unit", RoutingId = 7 },
        new ArchetypeRecord { ArchetypeId = 2, Name = "Projectile", RoutingId = 3 },
        new ArchetypeRecord { ArchetypeId = 5, Name = "Building", RoutingId = ArchetypeRecord.UnknownRoutingId },
    ];

    // ── AC1 · the header round-trips every new field ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Header_RoundTrips_EveryCaptureIdentityField()
    {
        var written = NewHeader();
        using var stream = WriteTrace(in written, []);

        using var reader = new TraceFileReader(stream);
        var read = reader.ReadHeader();

        Assert.Multiple(() =>
        {
            Assert.That(read.Version, Is.EqualTo(12), "v12 is the revision that introduced these fields");
            Assert.That(read.DatabaseId, Is.EqualTo(SampleDatabaseId));
            Assert.That(read.GetDatabaseName(), Is.EqualTo("world"));
            Assert.That(read.TsnMin, Is.EqualTo(41_022));
            Assert.That(read.TsnMax, Is.EqualTo(58_110));
            Assert.That(read.DurationTicks, Is.EqualTo(123_456_789));
            Assert.That(read.TickCount, Is.EqualTo(216_000));
            Assert.That(read.SchemaFingerprint, Is.EqualTo(0xDEAD_BEEF_CAFE_F00DUL));
            Assert.That(read.MultipleEnginesObserved, Is.False);
            // The pre-v12 fields must survive the struct growing — a mis-sized identity segment would shift these.
            Assert.That(read.TimestampFrequency, Is.EqualTo(10_000_000));
            Assert.That(read.CreatedUtcTicks, Is.EqualTo(638_000_000_000_000_000));
        });
    }

    [Test]
    public void DatabaseName_TruncatesOnACharacterBoundary_NeverMidSequence()
    {
        // 40 × 2-byte 'é' = 80 bytes into a 64-byte field. The cut must land between characters: a split UTF-8 sequence would render as mojibake in the
        // profiles list, which is worse than a shorter name.
        var header = NewHeader();
        header.SetDatabaseName(new string('é', 40));

        using var stream = WriteTrace(in header, []);
        using var reader = new TraceFileReader(stream);
        var name = reader.ReadHeader().GetDatabaseName();

        Assert.That(name, Is.EqualTo(new string('é', 32)), "64 bytes holds exactly 32 two-byte characters");
    }

    [Test]
    public void DatabaseName_IsEmpty_WhenNoEngineWasAttached()
    {
        var header = NewHeader();
        header.SetDatabaseName(null);

        using var stream = WriteTrace(in header, []);
        using var reader = new TraceFileReader(stream);

        Assert.That(reader.ReadHeader().GetDatabaseName(), Is.Empty);
    }

    // ── AC2 · v11 is hard-rejected rather than mis-decoded ──────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ReadHeader_RejectsV11_RatherThanMisdecodingTheArchetypeTable()
    {
        // A v11 archetype record has no trailing RoutingId, so a v12 walk would consume the next record's ArchetypeId as this one's routing id and cascade from
        // there — producing a table of plausible, wholly wrong archetype identities. Rejecting on version is the only safe answer.
        var v11 = new byte[91];
        BinaryPrimitives.WriteUInt32LittleEndian(v11, TraceFileHeader.MagicValue);
        BinaryPrimitives.WriteUInt16LittleEndian(v11.AsSpan(4), 11);

        var stream = new MemoryStream(v11);
        using var reader = new TraceFileReader(stream);

        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadHeader());
        Assert.That(ex.Message, Does.Contain("version: 11"));
        Assert.That(ex.Message, Does.Contain("12"), "the message must name the supported range so the fix — re-record — is obvious");
    }

    // ── AC3 · D-3 routing ids survive the wire ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ArchetypeTable_RoundTrips_RoutingIds_IncludingTheUnknownSentinel()
    {
        var header = NewHeader();
        var input = SampleArchetypes();
        using var stream = WriteTrace(in header, input);

        using var reader = new TraceFileReader(stream);
        reader.ReadHeader();
        reader.ReadSystemDefinitions();
        var read = reader.ReadArchetypes();

        Assert.That(read, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            for (var i = 0; i < input.Length; i++)
            {
                Assert.That(read[i].ArchetypeId, Is.EqualTo(input[i].ArchetypeId));
                Assert.That(read[i].Name, Is.EqualTo(input[i].Name));
                Assert.That(read[i].RoutingId, Is.EqualTo(input[i].RoutingId));
            }
        });
    }

    [Test]
    public void UnknownRoutingIdSentinel_MatchesTheEnginesOwnNoRoutingIdValue()
    {
        // ProfilerSessionMetadataBuilder writes RoutingIdForCatalog's return value straight into the record, so the engine's "unmapped" sentinel and the
        // trace's "unknown" sentinel must be the same number. They are defined independently in two assemblies; this is what keeps them honest.
        Assert.That(ArchetypeRecord.UnknownRoutingId, Is.EqualTo(DatabaseEngine.NoRoutingId));
    }

    // ── AC9 · D-9 degradation, checked on the bytes ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void MultiEngineDegradation_StripsEveryRoutingIdFromTheFileItself()
    {
        var header = NewHeader();
        header.Flags |= (ushort)TraceHeaderFlags.MultipleEnginesObserved;

        using var stream = WriteTrace(in header, SampleArchetypes(), writer => writer.PatchArchetypeRoutingIdsToSentinel());

        using var reader = new TraceFileReader(stream);
        var read = reader.ReadHeader();
        reader.ReadSystemDefinitions();
        var archetypes = reader.ReadArchetypes();

        Assert.That(read.MultipleEnginesObserved, Is.True);
        Assert.Multiple(() =>
        {
            foreach (var a in archetypes)
            {
                // The point of asserting on the re-read file rather than on an in-memory object: no wrong id may SURVIVE ON DISK. A flag alone would leave the
                // ambiguous values there for the first reader that forgets to check it.
                Assert.That(a.RoutingId, Is.EqualTo(ArchetypeRecord.UnknownRoutingId), $"'{a.Name}' still carries a routing id after degradation");
            }
            Assert.That(archetypes.Count, Is.EqualTo(3), "degradation strips the ids, it does not drop the archetypes — name joins must still work");
            Assert.That(archetypes[0].Name, Is.EqualTo("Unit"));
        });
    }

    [Test]
    public void PatchArchetypeRoutingIds_IsANoOp_WhenNoTableWasWritten()
    {
        var stream = new MemoryStream();
        var writer = new TraceFileWriter(stream);
        var header = NewHeader();
        writer.WriteHeader(in header);

        Assert.DoesNotThrow(writer.PatchArchetypeRoutingIdsToSentinel);
    }

    // ── AC10 · the single resolution point fails closed ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void TraceArchetypeIdentity_ResolvesNamesAndRoutingIds_ByCatalogId()
    {
        var header = NewHeader();
        var identity = new TraceArchetypeIdentity(in header, SampleArchetypes());

        Assert.Multiple(() =>
        {
            Assert.That(identity.RoutingIdsAvailable, Is.True);

            Assert.That(identity.TryGetName(1, out var name), Is.True);
            Assert.That(name, Is.EqualTo("Unit"));

            // The whole point: catalog id 1 maps to routing id 7, not to 1. Anything that returned the input would be the §5.3 bug.
            Assert.That(identity.TryGetRoutingId(1, out var routing), Is.True);
            Assert.That(routing, Is.EqualTo(7));

            Assert.That(identity.TryGetRoutingId(5, out _), Is.False, "an archetype the trace recorded no routing id for must not resolve");
            Assert.That(identity.TryGetName(5, out var building), Is.True, "…but its name is still available — names degrade, they don't vanish");
            Assert.That(building, Is.EqualTo("Building"));

            Assert.That(identity.TryGetName(999, out _), Is.False, "an id outside the table resolves to nothing rather than to a neighbour");
            Assert.That(identity.TryGetRoutingId(999, out _), Is.False);
        });
    }

    [Test]
    public void TraceArchetypeIdentity_WithholdsRoutingIds_WhenMultipleEnginesWereObserved()
    {
        var header = NewHeader();
        header.Flags |= (ushort)TraceHeaderFlags.MultipleEnginesObserved;

        // Records still carrying routing ids — an in-memory table (live attach, hand-built fixture) never went through the on-disk patch, so the resolver has
        // to enforce the rule itself. The flag is the contract; the stripped bytes are belt and braces.
        var identity = new TraceArchetypeIdentity(in header, SampleArchetypes());

        Assert.Multiple(() =>
        {
            Assert.That(identity.RoutingIdsAvailable, Is.False);
            Assert.That(identity.TryGetRoutingId(1, out var routing), Is.False);
            Assert.That(routing, Is.EqualTo(ArchetypeRecord.UnknownRoutingId));
            Assert.That(identity.TryGetName(1, out var name), Is.True, "name-based correlation is what the trace degrades TO — it must still work");
            Assert.That(name, Is.EqualTo("Unit"));
        });
    }

    [Test]
    public void TraceArchetypeIdentity_HandlesAnEmptyTable()
    {
        var header = NewHeader();
        var identity = new TraceArchetypeIdentity(in header, []);

        Assert.That(identity.RoutingIdsAvailable, Is.False);
        Assert.That(identity.TryGetName(0, out _), Is.False);
        Assert.That(identity.TryGetRoutingId(0, out _), Is.False);
    }
}
