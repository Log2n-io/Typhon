using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[TestFixture]
public class SpatialGeometryTests
{
    // ── AABB2F Overlaps ─────────────────────────────────────────────────────

    [Test]
    public void Overlaps_AABB2F_Overlapping_ReturnsTrue()
    {
        var a = new AABB2F { MinX = 0, MinY = 0, MaxX = 10, MaxY = 10 };
        var b = new AABB2F { MinX = 5, MinY = 5, MaxX = 15, MaxY = 15 };
        Assert.That(SpatialGeometry.Overlaps(a, b), Is.True);
    }

    [Test]
    public void Overlaps_AABB2F_Disjoint_ReturnsFalse()
    {
        var a = new AABB2F { MinX = 0, MinY = 0, MaxX = 5, MaxY = 5 };
        var b = new AABB2F { MinX = 6, MinY = 6, MaxX = 10, MaxY = 10 };
        Assert.That(SpatialGeometry.Overlaps(a, b), Is.False);
    }

    [Test]
    public void Overlaps_AABB2F_SharedEdge_ReturnsTrue()
    {
        var a = new AABB2F { MinX = 0, MinY = 0, MaxX = 5, MaxY = 5 };
        var b = new AABB2F { MinX = 5, MinY = 0, MaxX = 10, MaxY = 5 };
        Assert.That(SpatialGeometry.Overlaps(a, b), Is.True, "Shared edge is closed-interval overlap");
    }

    [Test]
    public void Overlaps_AABB2F_Contained_ReturnsTrue()
    {
        var outer = new AABB2F { MinX = 0, MinY = 0, MaxX = 20, MaxY = 20 };
        var inner = new AABB2F { MinX = 5, MinY = 5, MaxX = 10, MaxY = 10 };
        Assert.That(SpatialGeometry.Overlaps(outer, inner), Is.True);
    }

    [Test]
    public void Overlaps_AABB2F_NegativeCoords_Works()
    {
        var a = new AABB2F { MinX = -10, MinY = -10, MaxX = -5, MaxY = -5 };
        var b = new AABB2F { MinX = -7, MinY = -7, MaxX = 0, MaxY = 0 };
        Assert.That(SpatialGeometry.Overlaps(a, b), Is.True);
    }

    // ── AABB2F Contains ─────────────────────────────────────────────────────

    [Test]
    public void Contains_AABB2F_OuterContainsInner_ReturnsTrue()
    {
        var outer = new AABB2F { MinX = 0, MinY = 0, MaxX = 20, MaxY = 20 };
        var inner = new AABB2F { MinX = 5, MinY = 5, MaxX = 10, MaxY = 10 };
        Assert.That(SpatialGeometry.Contains(outer, inner), Is.True);
    }

    [Test]
    public void Contains_AABB2F_InnerDoesNotContainOuter_ReturnsFalse()
    {
        var outer = new AABB2F { MinX = 0, MinY = 0, MaxX = 20, MaxY = 20 };
        var inner = new AABB2F { MinX = 5, MinY = 5, MaxX = 10, MaxY = 10 };
        Assert.That(SpatialGeometry.Contains(inner, outer), Is.False);
    }

    [Test]
    public void Contains_AABB2F_SelfContainment_ReturnsTrue()
    {
        var box = new AABB2F { MinX = 1, MinY = 2, MaxX = 3, MaxY = 4 };
        Assert.That(SpatialGeometry.Contains(box, box), Is.True);
    }

    [Test]
    public void Contains_AABB2F_PartialOverlap_ReturnsFalse()
    {
        var a = new AABB2F { MinX = 0, MinY = 0, MaxX = 10, MaxY = 10 };
        var b = new AABB2F { MinX = 5, MinY = 5, MaxX = 15, MaxY = 15 };
        Assert.That(SpatialGeometry.Contains(a, b), Is.False);
    }

    // ── AABB2F Union ────────────────────────────────────────────────────────

    [Test]
    public void Union_AABB2F_NonOverlapping_SmallestEnclosing()
    {
        var a = new AABB2F { MinX = 0, MinY = 0, MaxX = 5, MaxY = 5 };
        var b = new AABB2F { MinX = 10, MinY = 10, MaxX = 20, MaxY = 20 };
        var u = SpatialGeometry.Union(a, b);
        Assert.That(u.MinX, Is.EqualTo(0f));
        Assert.That(u.MinY, Is.EqualTo(0f));
        Assert.That(u.MaxX, Is.EqualTo(20f));
        Assert.That(u.MaxY, Is.EqualTo(20f));
    }

    // ── AABB2F Area ─────────────────────────────────────────────────────────

    [Test]
    public void Area_AABB2F_KnownBox_ReturnsCorrectArea()
    {
        var box = new AABB2F { MinX = 0, MinY = 0, MaxX = 4, MaxY = 5 };
        Assert.That(SpatialGeometry.Area(box), Is.EqualTo(20f));
    }

    [Test]
    public void Area_AABB2F_ZeroWidth_ReturnsZero()
    {
        var box = new AABB2F { MinX = 5, MinY = 0, MaxX = 5, MaxY = 10 };
        Assert.That(SpatialGeometry.Area(box), Is.EqualTo(0f));
    }

    [Test]
    public void Area_AABB2F_PointSized_ReturnsZero()
    {
        var box = new AABB2F { MinX = 3, MinY = 3, MaxX = 3, MaxY = 3 };
        Assert.That(SpatialGeometry.Area(box), Is.EqualTo(0f));
    }

    // ── AABB2F Enclosing ────────────────────────────────────────────────────

    [Test]
    public void Enclosing_BSphere2F_CorrectAABB()
    {
        var sphere = new BSphere2F { CenterX = 10, CenterY = 20, Radius = 5 };
        var aabb = SpatialGeometry.Enclosing(sphere);
        Assert.That(aabb.MinX, Is.EqualTo(5f));
        Assert.That(aabb.MinY, Is.EqualTo(15f));
        Assert.That(aabb.MaxX, Is.EqualTo(15f));
        Assert.That(aabb.MaxY, Is.EqualTo(25f));
    }

    // ── AABB2F IsDegenerate ─────────────────────────────────────────────────

    [Test]
    public void IsDegenerate_AABB2F_ValidBox_ReturnsFalse()
    {
        var box = new AABB2F { MinX = 0, MinY = 0, MaxX = 10, MaxY = 10 };
        Assert.That(SpatialGeometry.IsDegenerate(box), Is.False);
    }

    [Test]
    public void IsDegenerate_AABB2F_NaN_ReturnsTrue()
    {
        var box = new AABB2F { MinX = float.NaN, MinY = 0, MaxX = 10, MaxY = 10 };
        Assert.That(SpatialGeometry.IsDegenerate(box), Is.True, "NaN in MinX");

        box = new AABB2F { MinX = 0, MinY = 0, MaxX = 10, MaxY = float.NaN };
        Assert.That(SpatialGeometry.IsDegenerate(box), Is.True, "NaN in MaxY");
    }

    [Test]
    public void IsDegenerate_AABB2F_MinGtMax_ReturnsTrue()
    {
        var box = new AABB2F { MinX = 10, MinY = 0, MaxX = 5, MaxY = 10 };
        Assert.That(SpatialGeometry.IsDegenerate(box), Is.True, "MinX > MaxX");

        box = new AABB2F { MinX = 0, MinY = 10, MaxX = 10, MaxY = 5 };
        Assert.That(SpatialGeometry.IsDegenerate(box), Is.True, "MinY > MaxY");
    }

    // ── AABB3F Spot Checks ──────────────────────────────────────────────────

    [Test]
    public void Overlaps_AABB3F_Overlapping_ReturnsTrue()
    {
        var a = new AABB3F { MinX = 0, MinY = 0, MinZ = 0, MaxX = 10, MaxY = 10, MaxZ = 10 };
        var b = new AABB3F { MinX = 5, MinY = 5, MinZ = 5, MaxX = 15, MaxY = 15, MaxZ = 15 };
        Assert.That(SpatialGeometry.Overlaps(a, b), Is.True);
    }

    [Test]
    public void Overlaps_AABB3F_Disjoint_ReturnsFalse()
    {
        var a = new AABB3F { MinX = 0, MinY = 0, MinZ = 0, MaxX = 5, MaxY = 5, MaxZ = 5 };
        var b = new AABB3F { MinX = 0, MinY = 0, MinZ = 6, MaxX = 5, MaxY = 5, MaxZ = 10 };
        Assert.That(SpatialGeometry.Overlaps(a, b), Is.False, "Disjoint on Z axis");
    }

    [Test]
    public void Volume_AABB3F_KnownBox_ReturnsCorrectVolume()
    {
        var box = new AABB3F { MinX = 0, MinY = 0, MinZ = 0, MaxX = 3, MaxY = 4, MaxZ = 5 };
        Assert.That(SpatialGeometry.Volume(box), Is.EqualTo(60f));
    }

    [Test]
    public void Enclosing_BSphere3F_CorrectAABB()
    {
        var sphere = new BSphere3F { CenterX = 0, CenterY = 0, CenterZ = 0, Radius = 10 };
        var aabb = SpatialGeometry.Enclosing(sphere);
        Assert.That(aabb.MinX, Is.EqualTo(-10f));
        Assert.That(aabb.MinZ, Is.EqualTo(-10f));
        Assert.That(aabb.MaxX, Is.EqualTo(10f));
        Assert.That(aabb.MaxZ, Is.EqualTo(10f));
    }

    [Test]
    public void IsDegenerate_AABB3F_MinZGtMaxZ_ReturnsTrue()
    {
        var box = new AABB3F { MinX = 0, MinY = 0, MinZ = 10, MaxX = 10, MaxY = 10, MaxZ = 5 };
        Assert.That(SpatialGeometry.IsDegenerate(box), Is.True, "MinZ > MaxZ");
    }

    // ── AABB2D Spot Checks ──────────────────────────────────────────────────

    [Test]
    public void Overlaps_AABB2D_Overlapping_ReturnsTrue()
    {
        var a = new AABB2D { MinX = 0, MinY = 0, MaxX = 1e10, MaxY = 1e10 };
        var b = new AABB2D { MinX = 5e9, MinY = 5e9, MaxX = 2e10, MaxY = 2e10 };
        Assert.That(SpatialGeometry.Overlaps(a, b), Is.True);
    }

    [Test]
    public void Area_AABB2D_KnownBox_ReturnsCorrectArea()
    {
        var box = new AABB2D { MinX = 0, MinY = 0, MaxX = 1e6, MaxY = 2e6 };
        Assert.That(SpatialGeometry.Area(box), Is.EqualTo(2e12));
    }

    [Test]
    public void Enclosing_BSphere2D_CorrectAABB()
    {
        var sphere = new BSphere2D { CenterX = 100.0, CenterY = 200.0, Radius = 50.0 };
        var aabb = SpatialGeometry.Enclosing(sphere);
        Assert.That(aabb.MinX, Is.EqualTo(50.0));
        Assert.That(aabb.MaxY, Is.EqualTo(250.0));
    }

    // ── AABB3D Spot Checks ──────────────────────────────────────────────────

    [Test]
    public void Volume_AABB3D_KnownBox_ReturnsCorrectVolume()
    {
        var box = new AABB3D { MinX = 0, MinY = 0, MinZ = 0, MaxX = 2.0, MaxY = 3.0, MaxZ = 4.0 };
        Assert.That(SpatialGeometry.Volume(box), Is.EqualTo(24.0));
    }

    [Test]
    public void Enclosing_BSphere3D_CorrectAABB()
    {
        var sphere = new BSphere3D { CenterX = 1e8, CenterY = 2e8, CenterZ = 3e8, Radius = 1e6 };
        var aabb = SpatialGeometry.Enclosing(sphere);
        Assert.That(aabb.MinX, Is.EqualTo(1e8 - 1e6));
        Assert.That(aabb.MaxZ, Is.EqualTo(3e8 + 1e6));
    }

    [Test]
    public void IsDegenerate_AABB3D_NaN_ReturnsTrue()
    {
        var box = new AABB3D { MinX = 0, MinY = 0, MinZ = double.NaN, MaxX = 1, MaxY = 1, MaxZ = 1 };
        Assert.That(SpatialGeometry.IsDegenerate(box), Is.True);
    }
}
