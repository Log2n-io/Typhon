using System;
using System.IO;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Wire-format round-trip tests for the v7 static-structure tables (component definitions, archetype definitions,
/// index catalog, runtime config, event-queue catalog, resource graph snapshot). One test per section — drives the
/// writer with a known record and asserts the reader produces an equivalent value. Plus a single end-to-end test
/// that exercises all six in one pass to lock the on-disk ordering.
///
/// The empty-section path is already covered indirectly by every other Profiler test (they all use
/// <see cref="TraceFileWriter.WriteEmptyStaticStructures"/> after WriteTracks/WriteDags); these tests focus on the
/// non-empty wire paths so a bug there can't ship silently.
/// </summary>
[TestFixture]
public sealed class StaticStructuresRoundTripTests
{
    private static readonly TraceFileHeader DefaultHeader = new()
    {
        Magic = TraceFileHeader.MagicValue,
        Version = TraceFileHeader.CurrentVersion,
        Flags = 0,
        TimestampFrequency = 10_000_000,
        BaseTickRate = 60f,
        WorkerCount = 1,
        SystemCount = 0,
        ArchetypeCount = 0,
        ComponentTypeCount = 0,
        CreatedUtcTicks = 0,
        SamplingSessionStartQpc = 0,
    };

    [Test]
    public void ComponentDefinitions_RoundTrip_Preserves_AllFields()
    {
        var input = new ComponentDefinitionRecord
        {
            ComponentTypeId = 42,
            Name = "Game.Position",
            Revision = 3,
            StorageMode = 1,
            AllowMultiple = false,
            ComponentStorageSize = 24,
            ComponentStorageOverhead = 8,
            ComponentStorageTotalSize = 32,
            IndicesCount = 2,
            MultipleIndicesCount = 1,
            SpatialField = "World",
            Fields =
            [
                new FieldDefinitionRecord
                {
                    FieldId = 0, Name = "X", FieldType = 4 /* float */, UnderlyingType = 0,
                    Offset = 0, Size = 4, ArrayLength = 0,
                    Flags = 0x09 /* HasIndex | HasSpatialIndex */,
                    SpatialFieldType = 1, SpatialMode = 2,
                    SpatialCellSize = 16f, SpatialMargin = 0.5f,
                    SpatialCategory = 0xCAFEBABEu,
                    ForeignKeyTargetType = string.Empty,
                },
                new FieldDefinitionRecord
                {
                    FieldId = 1, Name = "Owner", FieldType = 3 /* long */, UnderlyingType = 0,
                    Offset = 8, Size = 8, ArrayLength = 0,
                    Flags = 0x12 /* IndexAllowMultiple | IsForeignKey */,
                    ForeignKeyTargetType = "Game.Player",
                },
            ],
        };

        var output = RoundTrip(input,
            (writer, value) => writer.WriteComponentDefinitions(new[] { value }),
            reader => reader.ComponentDefinitions[0]);

        Assert.That(output.ComponentTypeId, Is.EqualTo(input.ComponentTypeId));
        Assert.That(output.Name, Is.EqualTo(input.Name));
        Assert.That(output.Revision, Is.EqualTo(input.Revision));
        Assert.That(output.StorageMode, Is.EqualTo(input.StorageMode));
        Assert.That(output.AllowMultiple, Is.EqualTo(input.AllowMultiple));
        Assert.That(output.ComponentStorageSize, Is.EqualTo(input.ComponentStorageSize));
        Assert.That(output.ComponentStorageOverhead, Is.EqualTo(input.ComponentStorageOverhead));
        Assert.That(output.ComponentStorageTotalSize, Is.EqualTo(input.ComponentStorageTotalSize));
        Assert.That(output.IndicesCount, Is.EqualTo(input.IndicesCount));
        Assert.That(output.MultipleIndicesCount, Is.EqualTo(input.MultipleIndicesCount));
        Assert.That(output.SpatialField, Is.EqualTo(input.SpatialField));
        Assert.That(output.Fields, Has.Length.EqualTo(2));

        var f0 = output.Fields[0];
        Assert.That(f0.Name, Is.EqualTo("X"));
        Assert.That(f0.Flags, Is.EqualTo(0x09));
        Assert.That(f0.SpatialCellSize, Is.EqualTo(16f));
        Assert.That(f0.SpatialCategory, Is.EqualTo(0xCAFEBABEu));

        var f1 = output.Fields[1];
        Assert.That(f1.Name, Is.EqualTo("Owner"));
        Assert.That(f1.Flags, Is.EqualTo(0x12));
        Assert.That(f1.ForeignKeyTargetType, Is.EqualTo("Game.Player"));
    }

    [Test]
    public void ArchetypeDefinitions_RoundTrip_WithClusterInfo()
    {
        var input = new ArchetypeDefinitionRecord
        {
            ArchetypeId = 7,
            Name = "Game.Mob",
            Revision = 2,
            ParentArchetypeId = 1,
            ChildArchetypeIds = [11, 12, 13],
            ComponentCount = 3,
            ComponentTypeIds = [101, 102, 103],
            VersionedSlotMask = 0b011,
            TransientSlotMask = 0b100,
            CascadeTargets = [11],
            Flags = 0x07, // cluster eligible + indexes + spatial
            ClusterInfo = new ArchetypeClusterInfoRecord
            {
                ClusterSize = 32,
                ClusterStride = 8192,
                HeaderSize = 32,
                EntityIdsOffset = 32,
                IndexElementIdsBaseOffset = 7000,
                MultipleIndexedFieldCount = 2,
            },
        };

        var output = RoundTrip(input,
            (writer, value) => writer.WriteArchetypeDefinitions(new[] { value }),
            reader => reader.ArchetypeDefinitions[0]);

        Assert.That(output.ArchetypeId, Is.EqualTo(7));
        Assert.That(output.Name, Is.EqualTo("Game.Mob"));
        Assert.That(output.ChildArchetypeIds, Is.EqualTo(new ushort[] { 11, 12, 13 }));
        Assert.That(output.ComponentTypeIds, Is.EqualTo(new[] { 101, 102, 103 }));
        Assert.That(output.VersionedSlotMask, Is.EqualTo(0b011));
        Assert.That(output.TransientSlotMask, Is.EqualTo(0b100));
        Assert.That(output.CascadeTargets, Is.EqualTo(new ushort[] { 11 }));
        Assert.That(output.Flags, Is.EqualTo(0x07));
        Assert.That(output.ClusterInfo, Is.Not.Null);
        Assert.That(output.ClusterInfo.ClusterSize, Is.EqualTo(32));
        Assert.That(output.ClusterInfo.ClusterStride, Is.EqualTo(8192u));
        Assert.That(output.ClusterInfo.MultipleIndexedFieldCount, Is.EqualTo(2));
    }

    [Test]
    public void ArchetypeDefinitions_RoundTrip_NoClusterInfo()
    {
        var input = new ArchetypeDefinitionRecord
        {
            ArchetypeId = 3,
            Name = "Legacy",
            ComponentCount = 1,
            ComponentTypeIds = [200],
            ClusterInfo = null,
        };
        var output = RoundTrip(input,
            (writer, value) => writer.WriteArchetypeDefinitions(new[] { value }),
            reader => reader.ArchetypeDefinitions[0]);
        Assert.That(output.ClusterInfo, Is.Null);
    }

    [Test]
    public void IndexCatalog_RoundTrip()
    {
        var input = new IndexCatalogEntry
        {
            ComponentTypeId = 5,
            FieldId = 2,
            Variant = 0x12, // Multiple | Int
            AllowMultiple = true,
            IsSpatial = false,
            IsAuto = true,
        };
        var output = RoundTrip(input,
            (writer, value) => writer.WriteIndexCatalog(new[] { value }),
            reader => reader.IndexCatalog[0]);
        Assert.That(output.ComponentTypeId, Is.EqualTo(5));
        Assert.That(output.FieldId, Is.EqualTo(2));
        Assert.That(output.Variant, Is.EqualTo(0x12));
        Assert.That(output.AllowMultiple, Is.True);
        Assert.That(output.IsAuto, Is.True);
    }

    [Test]
    public void RuntimeConfig_RoundTrip_Present()
    {
        var input = new RuntimeConfigRecord
        {
            BaseTickRate = 144,
            WorkerCount = 8,
            TelemetryRingCapacity = 4096,
            ParallelQueryMinChunkSize = 128,
        };
        var output = RoundTrip(input,
            (writer, value) => writer.WriteRuntimeConfig(value),
            reader => reader.RuntimeConfig);
        Assert.That(output, Is.Not.Null);
        Assert.That(output.BaseTickRate, Is.EqualTo(144));
        Assert.That(output.WorkerCount, Is.EqualTo(8));
        Assert.That(output.TelemetryRingCapacity, Is.EqualTo(4096));
        Assert.That(output.ParallelQueryMinChunkSize, Is.EqualTo(128));
    }

    [Test]
    public void RuntimeConfig_RoundTrip_NullStaysNull()
    {
        var output = RoundTrip<RuntimeConfigRecord>(null,
            (writer, value) => writer.WriteRuntimeConfig(value),
            reader => reader.RuntimeConfig);
        Assert.That(output, Is.Null);
    }

    [Test]
    public void EventQueueCatalog_RoundTrip()
    {
        var input = new EventQueueRecord
        {
            QueueIndex = 3,
            Name = "Damage",
            Capacity = 1024,
            EventTypeName = "Game.Events.DamageEvent",
        };
        var output = RoundTrip(input,
            (writer, value) => writer.WriteEventQueueCatalog(new[] { value }),
            reader => reader.EventQueues[0]);
        Assert.That(output.QueueIndex, Is.EqualTo(3));
        Assert.That(output.Name, Is.EqualTo("Damage"));
        Assert.That(output.Capacity, Is.EqualTo(1024));
        Assert.That(output.EventTypeName, Is.EqualTo("Game.Events.DamageEvent"));
    }

    [Test]
    public void ResourceGraphSnapshot_RoundTrip_Tree()
    {
        var nodes = new[]
        {
            new ResourceGraphNodeRecord { Id = 1, Name = "Root", Type = 0, ParentId = -1, CreatedAtUtcTicks = 100 },
            new ResourceGraphNodeRecord { Id = 2, Name = "Child", Type = 1, ParentId = 1, CreatedAtUtcTicks = 200, ExhaustionPolicy = 2 },
            new ResourceGraphNodeRecord { Id = 3, Name = "Grandchild", Type = 2, ParentId = 2, CreatedAtUtcTicks = 300 },
        };

        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var writer = new TraceFileWriter(ms))
        {
            writer.WriteHeader(in DefaultHeader);
            writer.WriteSystemDefinitions([]);
            writer.WriteArchetypes([]);
            writer.WriteComponentTypes([]);
            writer.WriteTracks([]);
            writer.WriteDags([]);
            writer.WriteComponentDefinitions(ReadOnlySpan<ComponentDefinitionRecord>.Empty);
            writer.WriteArchetypeDefinitions(ReadOnlySpan<ArchetypeDefinitionRecord>.Empty);
            writer.WriteIndexCatalog(ReadOnlySpan<IndexCatalogEntry>.Empty);
            writer.WriteRuntimeConfig(null);
            writer.WriteEventQueueCatalog(ReadOnlySpan<EventQueueRecord>.Empty);
            writer.WriteResourceGraphSnapshot(nodes);
            writer.Flush();
            bytes = ms.ToArray();
        }

        using var rs = new MemoryStream(bytes, writable: false);
        using var reader = new TraceFileReader(rs);
        reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();
        reader.ReadTracks();
        reader.ReadDags();
        reader.ReadStaticStructures();

        Assert.That(reader.ResourceGraphNodes, Has.Count.EqualTo(3));
        Assert.That(reader.ResourceGraphNodes[0].Name, Is.EqualTo("Root"));
        Assert.That(reader.ResourceGraphNodes[0].ParentId, Is.EqualTo(-1));
        Assert.That(reader.ResourceGraphNodes[1].ParentId, Is.EqualTo(1));
        Assert.That(reader.ResourceGraphNodes[1].ExhaustionPolicy, Is.EqualTo(2));
        Assert.That(reader.ResourceGraphNodes[2].Id, Is.EqualTo(3));
        Assert.That(reader.ResourceGraphNodes[2].ParentId, Is.EqualTo(2));
    }

    [Test]
    public void AllSixSections_InOrder_ReadCorrectly()
    {
        // End-to-end positional ordering check — guards against a future refactor that swaps the
        // write order vs. the read order. Each section gets one populated record so a misaligned
        // walk surfaces as a deserialisation error or garbled values, not a quietly-empty list.
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var writer = new TraceFileWriter(ms))
        {
            writer.WriteHeader(in DefaultHeader);
            writer.WriteSystemDefinitions([]);
            writer.WriteArchetypes([]);
            writer.WriteComponentTypes([]);
            writer.WriteTracks([]);
            writer.WriteDags([]);

            writer.WriteComponentDefinitions(new[] {
                new ComponentDefinitionRecord { ComponentTypeId = 1, Name = "A", Fields = [] }
            });
            writer.WriteArchetypeDefinitions(new[] {
                new ArchetypeDefinitionRecord { ArchetypeId = 2, Name = "B" }
            });
            writer.WriteIndexCatalog(new[] {
                new IndexCatalogEntry { ComponentTypeId = 1, FieldId = 0, Variant = 0x02 }
            });
            writer.WriteRuntimeConfig(new RuntimeConfigRecord { BaseTickRate = 60 });
            writer.WriteEventQueueCatalog(new[] {
                new EventQueueRecord { QueueIndex = 0, Name = "Q", Capacity = 16, EventTypeName = "E" }
            });
            writer.WriteResourceGraphSnapshot(new[] {
                new ResourceGraphNodeRecord { Id = 1, Name = "Root", ParentId = -1 }
            });
            writer.Flush();
            bytes = ms.ToArray();
        }

        using var rs = new MemoryStream(bytes, writable: false);
        using var reader = new TraceFileReader(rs);
        reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();
        reader.ReadTracks();
        reader.ReadDags();
        reader.ReadStaticStructures();

        Assert.That(reader.ComponentDefinitions, Has.Count.EqualTo(1));
        Assert.That(reader.ComponentDefinitions[0].Name, Is.EqualTo("A"));
        Assert.That(reader.ArchetypeDefinitions, Has.Count.EqualTo(1));
        Assert.That(reader.ArchetypeDefinitions[0].Name, Is.EqualTo("B"));
        Assert.That(reader.IndexCatalog, Has.Count.EqualTo(1));
        Assert.That(reader.IndexCatalog[0].Variant, Is.EqualTo(0x02));
        Assert.That(reader.RuntimeConfig, Is.Not.Null);
        Assert.That(reader.RuntimeConfig.BaseTickRate, Is.EqualTo(60));
        Assert.That(reader.EventQueues, Has.Count.EqualTo(1));
        Assert.That(reader.EventQueues[0].EventTypeName, Is.EqualTo("E"));
        Assert.That(reader.ResourceGraphNodes, Has.Count.EqualTo(1));
        Assert.That(reader.ResourceGraphNodes[0].Name, Is.EqualTo("Root"));
    }

    /// <summary>
    /// Helper: write the full v7 prefix (header + thin tables + the section under test, padded with empty
    /// versions for the other v7 sections), then read it back and project the section of interest.
    /// </summary>
    private static T RoundTrip<T>(T input,
        Action<TraceFileWriter, T> writeOne,
        Func<TraceFileReader, T> readOne)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var writer = new TraceFileWriter(ms))
        {
            writer.WriteHeader(in DefaultHeader);
            writer.WriteSystemDefinitions([]);
            writer.WriteArchetypes([]);
            writer.WriteComponentTypes([]);
            writer.WriteTracks([]);
            writer.WriteDags([]);

            // Default placeholders for the five sections we're not exercising; the test's section then
            // overwrites the appropriate one. The writer order MUST match the v7 wire-format order,
            // so any individual writeOne lambda must be the only writer producing its section's bytes.
            // To keep the helper simple, we always emit ALL six sections — the lambda picks which one
            // gets the populated value. This keeps a single positional read path.
            if (typeof(T) == typeof(ComponentDefinitionRecord)) { writeOne(writer, input); writer.WriteArchetypeDefinitions([]); writer.WriteIndexCatalog([]); writer.WriteRuntimeConfig(null); writer.WriteEventQueueCatalog([]); writer.WriteResourceGraphSnapshot([]); }
            else if (typeof(T) == typeof(ArchetypeDefinitionRecord)) { writer.WriteComponentDefinitions([]); writeOne(writer, input); writer.WriteIndexCatalog([]); writer.WriteRuntimeConfig(null); writer.WriteEventQueueCatalog([]); writer.WriteResourceGraphSnapshot([]); }
            else if (typeof(T) == typeof(IndexCatalogEntry)) { writer.WriteComponentDefinitions([]); writer.WriteArchetypeDefinitions([]); writeOne(writer, input); writer.WriteRuntimeConfig(null); writer.WriteEventQueueCatalog([]); writer.WriteResourceGraphSnapshot([]); }
            else if (typeof(T) == typeof(RuntimeConfigRecord)) { writer.WriteComponentDefinitions([]); writer.WriteArchetypeDefinitions([]); writer.WriteIndexCatalog([]); writeOne(writer, input); writer.WriteEventQueueCatalog([]); writer.WriteResourceGraphSnapshot([]); }
            else if (typeof(T) == typeof(EventQueueRecord)) { writer.WriteComponentDefinitions([]); writer.WriteArchetypeDefinitions([]); writer.WriteIndexCatalog([]); writer.WriteRuntimeConfig(null); writeOne(writer, input); writer.WriteResourceGraphSnapshot([]); }
            else { Assert.Fail($"Unhandled record type {typeof(T)}"); }

            writer.Flush();
            bytes = ms.ToArray();
        }

        using var rs = new MemoryStream(bytes, writable: false);
        using var reader = new TraceFileReader(rs);
        reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();
        reader.ReadTracks();
        reader.ReadDags();
        reader.ReadStaticStructures();

        return readOne(reader);
    }
}
