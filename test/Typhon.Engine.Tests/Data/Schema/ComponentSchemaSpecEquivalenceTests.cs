using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Proves the source-generated acceptance criterion (AC4.1/AC4.2, updated for #514 phase 5): a source-generated component's reflection-free
/// <see cref="ComponentSchemaSpec"/> — registered by the assembly's generated <c>[ModuleInitializer]</c> — produces a <see cref="DBComponentDefinition"/>
/// byte-identical to the one reflection builds.
/// <para>
/// <see cref="RepComp"/> is a real <c>[Component]</c> (reachable from the registrar, so the generator registers its spec), covering every attribute kind:
/// a plain scalar, a non-unique index, a unique foreign-key index, and a spatial index. The reflection path
/// (<see cref="DatabaseDefinitions.CreateFromAccessor(Type, FieldIdResolver)"/>) and the generated-spec path (the generic overload's registry dispatch) feed the
/// same engine build core, so equivalence is structural — this test guards against future drift. Registration is off the struct now (no
/// <c>IComponentSchemaProvider</c>), so the component need not be <c>partial</c>.
/// </para>
/// </summary>
class ComponentSchemaSpecEquivalenceTests
{
    // FK target — only needs to be a valid Type reference; the FK build path stores the type, it does not validate registration. `internal` so the generated
    // registrar can reference it (private-nested would be skipped → reflection fallback, defeating the generated-vs-reflection comparison).
    [Component("Typhon.Test.Codegen.FkTarget", 1)]
    [StructLayout(LayoutKind.Sequential)]
    internal struct RepFkTarget
    {
        [Field] public long Id;
    }

    /// <summary>
    /// Representative component covering all attribute kinds; a real <c>[Component]</c> so the generator registers its spec. <c>Health</c> carries a
    /// <c>PreviousName</c> so the field-id-migration test exercises rename resolution through the spec path. <c>internal</c> for registrar reachability.
    /// </summary>
    [Component("Typhon.Test.Codegen.Rep", 3, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = DurabilityDiscipline.Commit)]
    [StructLayout(LayoutKind.Sequential)]
    internal struct RepComp
    {
        [Field] public float X;

        [Field(PreviousName = "Hitpoints")]
        [Index(AllowMultiple = true)]
        public int Health;

        [Field]
        [ForeignKey(typeof(RepFkTarget))]
        [Index] // unique
        public long ParentLink;

        [Field]
        [SpatialIndex(2.0f, cellSize: 4.0f, Mode = SpatialMode.Static, Category = 7)]
        public AABB2F Bounds;
    }

    [Test]
    public void EngineComponents_TakeSourceGeneratedPath()
    {
        // AC4.2 regression guard: the engine's own persisted-schema components must build via the generated (reflection-free) spec — registered by the engine
        // assembly's [ModuleInitializer] into the schema-contract-level registry — not the runtime-reflection fallback. If the generator stops emitting for them,
        // TryGetComponentSpec flips to false.
        Assert.Multiple(() =>
        {
            Assert.That(GeneratedSchemaRegistry.TryGetComponentSpec(typeof(ComponentR1), out _), Is.True, "ComponentR1");
            Assert.That(GeneratedSchemaRegistry.TryGetComponentSpec(typeof(ArchetypeR1), out _), Is.True, "ArchetypeR1");
            Assert.That(GeneratedSchemaRegistry.TryGetComponentSpec(typeof(AssemblyR1), out _), Is.True, "AssemblyR1");
            Assert.That(GeneratedSchemaRegistry.TryGetComponentSpec(typeof(SchemaHistoryR1), out _), Is.True, "SchemaHistoryR1");
        });
    }

    [Test]
    public void GeneratedSpec_MatchesReflection_AllAttributeKinds()
    {
        var reflected = new DatabaseDefinitions().CreateFromAccessor(typeof(RepComp), null);
        var generated = new DatabaseDefinitions().CreateFromAccessor<RepComp>(null);

        AssertDefinitionsEqual(reflected, generated);
    }

    [Test]
    public void GeneratedSpec_MatchesReflection_UnderFieldIdMigration()
    {
        // Persisted schema with non-declaration-order ids; "Hitpoints" is the pre-rename name of "Health".
        // Both paths must resolve every field id identically through the resolver.
        FieldR1[] Persisted() => new[]
        {
            new FieldR1 { Name = (String64)"X", FieldId = 6 },
            new FieldR1 { Name = (String64)"Hitpoints", FieldId = 5 },
            new FieldR1 { Name = (String64)"ParentLink", FieldId = 7 },
            new FieldR1 { Name = (String64)"Bounds", FieldId = 8 },
        };

        var reflected = new DatabaseDefinitions().CreateFromAccessor(typeof(RepComp), new FieldIdResolver(Persisted()));
        var generated = new DatabaseDefinitions().CreateFromAccessor<RepComp>(new FieldIdResolver(Persisted()));

        AssertDefinitionsEqual(reflected, generated);

        // Sanity: ids actually came from the persisted schema (rename carried Hitpoints' id onto Health), not declaration order.
        Assert.That(generated.GetFieldId("Health"), Is.EqualTo(5));
        Assert.That(generated.GetFieldId("X"), Is.EqualTo(6));
        Assert.That(generated.GetFieldId("ParentLink"), Is.EqualTo(7));
        Assert.That(generated.GetFieldId("Bounds"), Is.EqualTo(8));
    }

    private static void AssertDefinitionsEqual(DBComponentDefinition reflected, DBComponentDefinition generated)
    {
        Assert.That(generated, Is.Not.Null, "generated definition");
        Assert.That(reflected, Is.Not.Null, "reflected definition");

        Assert.Multiple(() =>
        {
            Assert.That(generated.Name, Is.EqualTo(reflected.Name), "Name");
            Assert.That(generated.Revision, Is.EqualTo(reflected.Revision), "Revision");
            Assert.That(generated.StorageMode, Is.EqualTo(reflected.StorageMode), "StorageMode");
            Assert.That(generated.DefaultDiscipline, Is.EqualTo(reflected.DefaultDiscipline), "DefaultDiscipline");
            Assert.That(generated.POCOType, Is.EqualTo(reflected.POCOType), "POCOType");
            Assert.That(generated.ComponentStorageSize, Is.EqualTo(reflected.ComponentStorageSize), "ComponentStorageSize");
            Assert.That(generated.ComponentStorageOverhead, Is.EqualTo(reflected.ComponentStorageOverhead), "ComponentStorageOverhead");
            Assert.That(generated.ComponentStorageTotalSize, Is.EqualTo(reflected.ComponentStorageTotalSize), "ComponentStorageTotalSize");
            Assert.That(generated.MaxFieldId, Is.EqualTo(reflected.MaxFieldId), "MaxFieldId");
            Assert.That(generated.IndicesCount, Is.EqualTo(reflected.IndicesCount), "IndicesCount");
            Assert.That(generated.MultipleIndicesCount, Is.EqualTo(reflected.MultipleIndicesCount), "MultipleIndicesCount");
            Assert.That(generated.EntityPKOverheadSize, Is.EqualTo(reflected.EntityPKOverheadSize), "EntityPKOverheadSize");
            Assert.That(generated.SpatialField?.Name, Is.EqualTo(reflected.SpatialField?.Name), "SpatialField");
            Assert.That(generated.FieldsByName.Count, Is.EqualTo(reflected.FieldsByName.Count), "field count");
        });

        foreach (var kvp in reflected.FieldsByName)
        {
            var rf = kvp.Value;
            Assert.That(generated.FieldsByName.ContainsKey(kvp.Key), Is.True, $"generated missing field '{kvp.Key}'");
            var gf = generated.FieldsByName[kvp.Key];

            Assert.Multiple(() =>
            {
                Assert.That(gf.FieldId, Is.EqualTo(rf.FieldId), $"{kvp.Key}.FieldId");
                Assert.That(gf.Type, Is.EqualTo(rf.Type), $"{kvp.Key}.Type");
                Assert.That(gf.UnderlyingType, Is.EqualTo(rf.UnderlyingType), $"{kvp.Key}.UnderlyingType");
                Assert.That(gf.OffsetInComponentStorage, Is.EqualTo(rf.OffsetInComponentStorage), $"{kvp.Key}.Offset");
                Assert.That(gf.DotNetType, Is.EqualTo(rf.DotNetType), $"{kvp.Key}.DotNetType");
                Assert.That(gf.DotNetUnderlyingType, Is.EqualTo(rf.DotNetUnderlyingType), $"{kvp.Key}.DotNetUnderlyingType");
                Assert.That(gf.FieldSize, Is.EqualTo(rf.FieldSize), $"{kvp.Key}.FieldSize");
                Assert.That(gf.SizeInComponentStorage, Is.EqualTo(rf.SizeInComponentStorage), $"{kvp.Key}.SizeInComponentStorage");
                Assert.That(gf.IsStatic, Is.EqualTo(rf.IsStatic), $"{kvp.Key}.IsStatic");
                Assert.That(gf.IsArray, Is.EqualTo(rf.IsArray), $"{kvp.Key}.IsArray");
                Assert.That(gf.ArrayLength, Is.EqualTo(rf.ArrayLength), $"{kvp.Key}.ArrayLength");
                Assert.That(gf.HasIndex, Is.EqualTo(rf.HasIndex), $"{kvp.Key}.HasIndex");
                Assert.That(gf.IndexAllowMultiple, Is.EqualTo(rf.IndexAllowMultiple), $"{kvp.Key}.IndexAllowMultiple");
                Assert.That(gf.IsIndexAuto, Is.EqualTo(rf.IsIndexAuto), $"{kvp.Key}.IsIndexAuto");
                Assert.That(gf.IsForeignKey, Is.EqualTo(rf.IsForeignKey), $"{kvp.Key}.IsForeignKey");
                Assert.That(gf.ForeignKeyTargetType, Is.EqualTo(rf.ForeignKeyTargetType), $"{kvp.Key}.ForeignKeyTargetType");
                Assert.That(gf.HasSpatialIndex, Is.EqualTo(rf.HasSpatialIndex), $"{kvp.Key}.HasSpatialIndex");
                Assert.That(gf.SpatialFieldType, Is.EqualTo(rf.SpatialFieldType), $"{kvp.Key}.SpatialFieldType");
                Assert.That(gf.SpatialMargin, Is.EqualTo(rf.SpatialMargin), $"{kvp.Key}.SpatialMargin");
                Assert.That(gf.SpatialCellSize, Is.EqualTo(rf.SpatialCellSize), $"{kvp.Key}.SpatialCellSize");
                Assert.That(gf.SpatialMode, Is.EqualTo(rf.SpatialMode), $"{kvp.Key}.SpatialMode");
                Assert.That(gf.SpatialCategory, Is.EqualTo(rf.SpatialCategory), $"{kvp.Key}.SpatialCategory");
            });
        }
    }
}
