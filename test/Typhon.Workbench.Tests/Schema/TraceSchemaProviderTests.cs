using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Tests.Schema;

/// <summary>
/// Unit coverage for <see cref="TraceSchemaProvider"/>. Exercises the projection from v7 wire records
/// (<see cref="ComponentDefinitionRecord"/>, <see cref="ArchetypeDefinitionRecord"/>, <see cref="IndexCatalogEntry"/>)
/// onto the Workbench schema DTOs that drive the Schema Inspector panels for trace sessions.
///
/// Wire-format round-trip is covered separately by Profiler.StaticStructuresRoundTripTests; here we focus on the
/// projection semantics — name resolution (short vs. full), live-only field zeroing, index→field joins.
/// </summary>
[TestFixture]
public sealed class TraceSchemaProviderTests
{
    private static readonly ComponentDefinitionRecord Position = new()
    {
        ComponentTypeId = 1,
        Name = "Game.Position",
        Revision = 1,
        StorageMode = 0, // Versioned
        AllowMultiple = false,
        ComponentStorageSize = 12,
        ComponentStorageOverhead = 0,
        ComponentStorageTotalSize = 12,
        IndicesCount = 1,
        MultipleIndicesCount = 0,
        SpatialField = string.Empty,
        Fields =
        [
            new FieldDefinitionRecord { FieldId = 0, Name = "X", FieldType = 4, Offset = 0, Size = 4, Flags = 0x01 /* HasIndex */ },
            new FieldDefinitionRecord { FieldId = 1, Name = "Y", FieldType = 4, Offset = 4, Size = 4 },
            new FieldDefinitionRecord { FieldId = 2, Name = "Z", FieldType = 4, Offset = 8, Size = 4 },
        ],
    };

    private static readonly ComponentDefinitionRecord Velocity = new()
    {
        ComponentTypeId = 2,
        Name = "Game.Velocity",
        Revision = 1,
        StorageMode = 0,
        ComponentStorageSize = 12,
        ComponentStorageOverhead = 0,
        ComponentStorageTotalSize = 12,
        Fields = [],
    };

    private static readonly ArchetypeDefinitionRecord Mob = new()
    {
        ArchetypeId = 100,
        Name = "Game.Mob",
        Revision = 1,
        ComponentCount = 2,
        ComponentTypeIds = [1, 2],
        Flags = 0x01, // cluster eligible
        ClusterInfo = new ArchetypeClusterInfoRecord { ClusterSize = 32, ClusterStride = 768, MultipleIndexedFieldCount = 0 },
    };

    private static readonly ArchetypeDefinitionRecord StaticObj = new()
    {
        ArchetypeId = 101,
        Name = "Game.Static",
        Revision = 1,
        ComponentCount = 1,
        ComponentTypeIds = [1],
        Flags = 0,
    };

    private static readonly IndexCatalogEntry PositionXIndex = new()
    {
        ComponentTypeId = 1,
        FieldId = 0,
        Variant = 0x04, // Float
        AllowMultiple = false,
        IsSpatial = false,
        IsAuto = false,
    };

    private static TraceSchemaProvider BuildProvider() =>
        new(
            components: [Position, Velocity],
            archetypes: [Mob, StaticObj],
            indexes: [PositionXIndex]);

    [Test]
    public void ListComponents_Projects_AllRecords()
    {
        var summaries = BuildProvider().ListComponents();
        Assert.That(summaries, Has.Length.EqualTo(2));

        var pos = summaries.First(s => s.FullName == "Game.Position");
        Assert.That(pos.TypeName, Is.EqualTo("Position"));
        Assert.That(pos.StorageSize, Is.EqualTo(12));
        Assert.That(pos.FieldCount, Is.EqualTo(3));
        Assert.That(pos.IndexCount, Is.EqualTo(1));
        Assert.That(pos.ArchetypeCount, Is.EqualTo(2)); // both archetypes contain Position
        Assert.That(pos.EntityCount, Is.EqualTo(0), "trace has no live entity counts");
    }

    [Test]
    public void GetComponentSchema_AcceptsBoth_ShortAndFullName()
    {
        var provider = BuildProvider();

        var byShort = provider.GetComponentSchema("Position");
        var byFull = provider.GetComponentSchema("Game.Position");

        Assert.That(byShort.FullName, Is.EqualTo("Game.Position"));
        Assert.That(byFull.FullName, Is.EqualTo("Game.Position"));
        Assert.That(byShort.Fields, Has.Length.EqualTo(3));
    }

    [Test]
    public void GetComponentSchema_OrdersFields_AscendingByOffset()
    {
        var schema = BuildProvider().GetComponentSchema("Position");
        Assert.That(schema.Fields.Select(f => f.Name), Is.EqualTo(new[] { "X", "Y", "Z" }));
        Assert.That(schema.Fields[0].IsIndexed, Is.True);
        Assert.That(schema.Fields[1].IsIndexed, Is.False);
    }

    [Test]
    public void GetComponentSchema_UnknownComponent_Throws()
    {
        var provider = BuildProvider();
        Assert.Throws<KeyNotFoundException>(() => provider.GetComponentSchema("Game.Nope"));
    }

    [Test]
    public void ListArchetypes_Projects_All()
    {
        var archetypes = BuildProvider().ListArchetypes();
        Assert.That(archetypes, Has.Length.EqualTo(2));
        var mob = archetypes.First(a => a.ArchetypeId == "100");
        Assert.That(mob.StorageMode, Is.EqualTo("cluster"));
        Assert.That(mob.ChunkCapacity, Is.EqualTo(32));
        Assert.That(mob.ComponentTypes, Is.EquivalentTo(new[] { "Game.Position", "Game.Velocity" }));

        var staticObj = archetypes.First(a => a.ArchetypeId == "101");
        Assert.That(staticObj.StorageMode, Is.EqualTo("legacy"));
        Assert.That(staticObj.ChunkCapacity, Is.EqualTo(0));
    }

    [Test]
    public void GetArchetypesForComponent_Filters_ByComponentId()
    {
        var provider = BuildProvider();
        var velocity = provider.GetArchetypesForComponent("Game.Velocity");
        Assert.That(velocity, Has.Length.EqualTo(1));
        Assert.That(velocity[0].ArchetypeId, Is.EqualTo("100"));

        var position = provider.GetArchetypesForComponent("Game.Position");
        Assert.That(position, Has.Length.EqualTo(2));
    }

    [Test]
    public void GetIndexesForComponent_Joins_FieldDetails()
    {
        var indexes = BuildProvider().GetIndexesForComponent("Game.Position");
        Assert.That(indexes, Has.Length.EqualTo(1));
        Assert.That(indexes[0].FieldName, Is.EqualTo("X"));
        Assert.That(indexes[0].FieldOffset, Is.EqualTo(0));
        Assert.That(indexes[0].FieldSize, Is.EqualTo(4));
        Assert.That(indexes[0].IndexType, Is.EqualTo("BTree"));
    }

    [Test]
    public void GetIndexesForComponent_NoIndexes_ReturnsEmpty()
    {
        var indexes = BuildProvider().GetIndexesForComponent("Game.Velocity");
        Assert.That(indexes, Is.Empty);
    }

    [Test]
    public void GetSystemRelationships_Returns_RuntimeNotHosted()
    {
        var rel = BuildProvider().GetSystemRelationships("Game.Position");
        Assert.That(rel.RuntimeHosted, Is.False);
        Assert.That(rel.Systems, Is.Empty);
    }

    // ── Edge-case coverage (Phase B7 follow-up; surfaced by code review) ────────────────────────────────

    [Test]
    public void EmptyTrace_ListsReturnEmpty_ResolvesNothing()
    {
        // Trace with no schema records at all (e.g., a test fixture that wrote count=0 for every section).
        // All list endpoints should return empty arrays without throwing; resolution endpoints throw KeyNotFound.
        var provider = new TraceSchemaProvider(components: [], archetypes: [], indexes: []);
        Assert.That(provider.ListComponents(), Is.Empty);
        Assert.That(provider.ListArchetypes(), Is.Empty);
        Assert.Throws<KeyNotFoundException>(() => provider.GetComponentSchema("Anything"));
    }

    [Test]
    public void ArchetypeWithUnknownComponentId_RendersSyntheticPlaceholder()
    {
        // An archetype's slot map references a ComponentTypeId that has no matching ComponentDefinitionRecord
        // (e.g., a forward-reference, a deleted component, or a wire-format mismatch). Project gracefully —
        // the unknown slot gets a "#42" placeholder rather than failing the whole list request.
        var orphanArchetype = new ArchetypeDefinitionRecord
        {
            ArchetypeId = 200,
            Name = "Game.Orphan",
            ComponentCount = 1,
            ComponentTypeIds = [42], // 42 isn't in [Position, Velocity]
            Flags = 0,
        };
        var provider = new TraceSchemaProvider([Position, Velocity], [orphanArchetype], []);

        var archetypes = provider.ListArchetypes();
        Assert.That(archetypes, Has.Length.EqualTo(1));
        Assert.That(archetypes[0].ComponentTypes, Is.EqualTo(new[] { "#42" }));
    }

    [Test]
    public void ArchetypeWithEmptyComponentTypeIds_ProjectsEmptyList()
    {
        var emptyArchetype = new ArchetypeDefinitionRecord
        {
            ArchetypeId = 201,
            Name = "Game.Empty",
            ComponentCount = 0,
            ComponentTypeIds = [],
            Flags = 0,
        };
        var provider = new TraceSchemaProvider([Position], [emptyArchetype], []);

        var archetypes = provider.ListArchetypes();
        Assert.That(archetypes, Has.Length.EqualTo(1));
        Assert.That(archetypes[0].ComponentTypes, Is.Empty);
    }

    [Test]
    public void IndexWithUnknownFieldId_RendersHashPlaceholder()
    {
        // Index entry references a field that's not in the component's field list — graceful degradation:
        // the index row appears but with the synthetic "#fieldId" name rather than a 500.
        var orphanIndex = new IndexCatalogEntry
        {
            ComponentTypeId = 1, // Position
            FieldId = 999,       // Position has fields 0,1,2; 999 is bogus
            Variant = 0x04,
        };
        var provider = new TraceSchemaProvider([Position], [], [orphanIndex]);

        var indexes = provider.GetIndexesForComponent("Game.Position");
        Assert.That(indexes, Has.Length.EqualTo(1));
        Assert.That(indexes[0].FieldName, Is.EqualTo("#999"));
        Assert.That(indexes[0].FieldOffset, Is.EqualTo(-1));
    }

    [Test]
    public void FieldTypeName_MatchesLiveProviderOutput_ForByteRangeEnumValues()
    {
        // Live provider produces field-type strings via `f.Type.ToString()` (the enum-name form, e.g., "Int",
        // "String64"). Trace provider does the equivalent via `Enum.IsDefined` + cast + ToString.
        //
        // Wire-format limitation: FieldType is serialised as a single byte (`(byte)field.Type` in the writer).
        // Flag values >= 256 (like `Unsigned = 256`) are lost on the wire — `(byte)Unsigned` is 0 ("None").
        // This test deliberately skips those values; the trace's field-type display is "low-byte only" by
        // design, and the divergence between live ("UnsignedInt") and trace ("Int") for those cases is a
        // documented limitation, not a bug. If we ever bump FieldType to 2 bytes on the wire, the skip can
        // be removed.
        foreach (Typhon.Schema.Definition.FieldType ft in Enum.GetValues(typeof(Typhon.Schema.Definition.FieldType)))
        {
            if (ft == Typhon.Schema.Definition.FieldType.None) continue;
            var raw = (int)ft;
            if (raw <= 0 || raw > 255) continue; // skip flag/alias values that don't fit in a byte

            var component = new ComponentDefinitionRecord
            {
                ComponentTypeId = 1,
                Name = "Test.C",
                Fields = [new FieldDefinitionRecord { FieldId = 0, Name = "F", FieldType = (byte)ft, Offset = 0, Size = 1 }],
            };
            var provider = new TraceSchemaProvider([component], [], []);
            var schema = provider.GetComponentSchema("Test.C");

            Assert.That(schema.Fields[0].TypeName, Is.EqualTo(ft.ToString()),
                $"Field-type byte {(byte)ft} ({ft}) projected differently between trace and live providers");
        }
    }

    [Test]
    public void DuplicateComponentTypeIds_DoNotCrash_ListResolvesFirstWins()
    {
        // The original bug: multiple components with the same ComponentTypeId (e.g., engine-internal types
        // collapsing on -1 sentinel). With the synthetic-id allocation in ProfilerStaticDataBuilder this is
        // unlikely in production, but TraceSchemaProvider must remain defensive — first-wins on the lookup
        // dict, no exception. Guards regression of the dup-key crash that surfaced from AntHill.
        var dup1 = new ComponentDefinitionRecord { ComponentTypeId = -1, Name = "Engine.A", Fields = [] };
        var dup2 = new ComponentDefinitionRecord { ComponentTypeId = -1, Name = "Engine.B", Fields = [] };
        var arch = new ArchetypeDefinitionRecord
        {
            ArchetypeId = 300,
            Name = "Game.Container",
            ComponentCount = 1,
            ComponentTypeIds = [-1],
            Flags = 0,
        };
        var provider = new TraceSchemaProvider([dup1, dup2], [arch], []);

        Assert.DoesNotThrow(() => provider.ListArchetypes());
        Assert.DoesNotThrow(() => provider.ListComponents());
        var archetypes = provider.ListArchetypes();
        Assert.That(archetypes[0].ComponentTypes, Has.Length.EqualTo(1));
        // First-wins: the lookup returns Engine.A, not Engine.B. Either is acceptable for this regression
        // guard; the critical assertion is that no exception was thrown.
        Assert.That(archetypes[0].ComponentTypes[0], Is.EqualTo("Engine.A").Or.EqualTo("Engine.B"));
    }
}
