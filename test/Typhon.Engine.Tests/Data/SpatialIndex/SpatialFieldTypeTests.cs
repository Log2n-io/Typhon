// CS0649: these component payload fields are written by the engine through spans/pointers, never by C# assignment
// in this fixture — the declarations exist to define the blittable layout the schema is derived from. Same
// rationale (and same pragma) as src/Typhon.Engine/Profiler/internals/InstantEvents.cs.
#pragma warning disable CS0649

using System.Reflection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[TestFixture]
public class SpatialFieldTypeTests
{
    // ── FieldType.FromType mapping ──────────────────────────────────────────

    [Test]
    public void FromType_AABB2F_ReturnsCorrectFieldType()
    {
        var (field, under) = DatabaseSchemaExtensions.FromType(typeof(AABB2F));
        Assert.That(field, Is.EqualTo(FieldType.AABB2F));
        Assert.That(under, Is.EqualTo(FieldType.None));
    }

    [Test]
    public void FromType_AABB3F_ReturnsCorrectFieldType()
    {
        var (field, _) = DatabaseSchemaExtensions.FromType(typeof(AABB3F));
        Assert.That(field, Is.EqualTo(FieldType.AABB3F));
    }

    [Test]
    public void FromType_BSphere2F_ReturnsCorrectFieldType()
    {
        var (field, _) = DatabaseSchemaExtensions.FromType(typeof(BSphere2F));
        Assert.That(field, Is.EqualTo(FieldType.BSphere2F));
    }

    [Test]
    public void FromType_BSphere3F_ReturnsCorrectFieldType()
    {
        var (field, _) = DatabaseSchemaExtensions.FromType(typeof(BSphere3F));
        Assert.That(field, Is.EqualTo(FieldType.BSphere3F));
    }

    [Test]
    public void FromType_AABB2D_ReturnsCorrectFieldType()
    {
        var (field, _) = DatabaseSchemaExtensions.FromType(typeof(AABB2D));
        Assert.That(field, Is.EqualTo(FieldType.AABB2D));
    }

    [Test]
    public void FromType_AABB3D_ReturnsCorrectFieldType()
    {
        var (field, _) = DatabaseSchemaExtensions.FromType(typeof(AABB3D));
        Assert.That(field, Is.EqualTo(FieldType.AABB3D));
    }

    [Test]
    public void FromType_BSphere2D_ReturnsCorrectFieldType()
    {
        var (field, _) = DatabaseSchemaExtensions.FromType(typeof(BSphere2D));
        Assert.That(field, Is.EqualTo(FieldType.BSphere2D));
    }

    [Test]
    public void FromType_BSphere3D_ReturnsCorrectFieldType()
    {
        var (field, _) = DatabaseSchemaExtensions.FromType(typeof(BSphere3D));
        Assert.That(field, Is.EqualTo(FieldType.BSphere3D));
    }

    // ── FieldSizeInComp ─────────────────────────────────────────────────────

    [Test]
    public void FieldSizeInComp_SpatialTypes_ReturnCorrectSizes()
    {
        Assert.That(FieldType.AABB2F.FieldSizeInComp(), Is.EqualTo(16), "AABB2F = 4×f32");
        Assert.That(FieldType.AABB3F.FieldSizeInComp(), Is.EqualTo(24), "AABB3F = 6×f32");
        Assert.That(FieldType.BSphere2F.FieldSizeInComp(), Is.EqualTo(12), "BSphere2F = 3×f32");
        Assert.That(FieldType.BSphere3F.FieldSizeInComp(), Is.EqualTo(16), "BSphere3F = 4×f32");
        Assert.That(FieldType.AABB2D.FieldSizeInComp(), Is.EqualTo(32), "AABB2D = 4×f64");
        Assert.That(FieldType.AABB3D.FieldSizeInComp(), Is.EqualTo(48), "AABB3D = 6×f64");
        Assert.That(FieldType.BSphere2D.FieldSizeInComp(), Is.EqualTo(24), "BSphere2D = 3×f64");
        Assert.That(FieldType.BSphere3D.FieldSizeInComp(), Is.EqualTo(32), "BSphere3D = 4×f64");
    }

    // ── SpatialIndexAttribute reflection ────────────────────────────────────

    private struct TestSpatialComponent
    {
        [SpatialIndex(5.0f)]
        public AABB3F Bounds;

        [SpatialIndex(3.0f, 100f)]
        public AABB2F Zone;
    }

    [Test]
    public void SpatialIndexAttribute_MarginOnly_DefaultCellSize()
    {
        var field = typeof(TestSpatialComponent).GetField("Bounds");
        var attr = field.GetCustomAttribute<SpatialIndexAttribute>();
        Assert.That(attr, Is.Not.Null, "Attribute should be found via reflection");
        Assert.That(attr.Margin, Is.EqualTo(5.0f));
        Assert.That(attr.CellSize, Is.EqualTo(0f), "Default CellSize should be 0");
    }

    [Test]
    public void SpatialIndexAttribute_WithCellSize_BothSet()
    {
        var field = typeof(TestSpatialComponent).GetField("Zone");
        var attr = field.GetCustomAttribute<SpatialIndexAttribute>();
        Assert.That(attr, Is.Not.Null);
        Assert.That(attr.Margin, Is.EqualTo(3.0f));
        Assert.That(attr.CellSize, Is.EqualTo(100f));
    }

    // ── SpatialFieldInfo.ToVariant ──────────────────────────────────────────

    [Test]
    public void ToVariant_MapsCorrectly()
    {
        Assert.That(new SpatialFieldInfo(0, 16, SpatialFieldType.AABB2F, 0, 0).ToVariant(),
            Is.EqualTo(SpatialVariant.R2Df32));
        Assert.That(new SpatialFieldInfo(0, 24, SpatialFieldType.AABB3F, 0, 0).ToVariant(),
            Is.EqualTo(SpatialVariant.R3Df32));
        Assert.That(new SpatialFieldInfo(0, 32, SpatialFieldType.AABB2D, 0, 0).ToVariant(),
            Is.EqualTo(SpatialVariant.R2Df64));
        Assert.That(new SpatialFieldInfo(0, 48, SpatialFieldType.AABB3D, 0, 0).ToVariant(),
            Is.EqualTo(SpatialVariant.R3Df64));
    }

    [Test]
    public void ToVariant_Sphere_MapsSameAsAABB()
    {
        Assert.That(new SpatialFieldInfo(0, 12, SpatialFieldType.BSphere2F, 0, 0).ToVariant(),
            Is.EqualTo(SpatialVariant.R2Df32));
        Assert.That(new SpatialFieldInfo(0, 16, SpatialFieldType.BSphere3F, 0, 0).ToVariant(),
            Is.EqualTo(SpatialVariant.R3Df32));
        Assert.That(new SpatialFieldInfo(0, 24, SpatialFieldType.BSphere2D, 0, 0).ToVariant(),
            Is.EqualTo(SpatialVariant.R2Df64));
        Assert.That(new SpatialFieldInfo(0, 32, SpatialFieldType.BSphere3D, 0, 0).ToVariant(),
            Is.EqualTo(SpatialVariant.R3Df64));
    }

    [Test]
    public void IsSphere_CorrectForAllTypes()
    {
        Assert.That(new SpatialFieldInfo(0, 0, SpatialFieldType.AABB2F, 0, 0).IsSphere, Is.False);
        Assert.That(new SpatialFieldInfo(0, 0, SpatialFieldType.AABB3F, 0, 0).IsSphere, Is.False);
        Assert.That(new SpatialFieldInfo(0, 0, SpatialFieldType.BSphere2F, 0, 0).IsSphere, Is.True);
        Assert.That(new SpatialFieldInfo(0, 0, SpatialFieldType.BSphere3F, 0, 0).IsSphere, Is.True);
        Assert.That(new SpatialFieldInfo(0, 0, SpatialFieldType.AABB2D, 0, 0).IsSphere, Is.False);
        Assert.That(new SpatialFieldInfo(0, 0, SpatialFieldType.AABB3D, 0, 0).IsSphere, Is.False);
        Assert.That(new SpatialFieldInfo(0, 0, SpatialFieldType.BSphere2D, 0, 0).IsSphere, Is.True);
        Assert.That(new SpatialFieldInfo(0, 0, SpatialFieldType.BSphere3D, 0, 0).IsSphere, Is.True);
    }
}
