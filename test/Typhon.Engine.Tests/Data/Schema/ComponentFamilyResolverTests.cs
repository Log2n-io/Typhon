using NUnit.Framework;
using Typhon.Engine.Data.Schema;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Pure unit tests for <see cref="ComponentFamilyResolver"/>. The resolver is allocation-free, side-effect-free,
/// and serves both Live (attribute + heuristic) and Trace (heuristic only) projection paths.
/// </summary>
class ComponentFamilyResolverTests
{
    private struct PositionLike { public float X; public float Y; }

    private struct HealthLike { public int Value; }

    [ComponentFamily("Diagnostics")]
    private struct CustomDecorated { public int Counter; }

    private struct Whatever { public byte Tag; }

    [Test]
    public void ResolveByAttribute_ReturnsName_WhenPresent()
    {
        Assert.That(ComponentFamilyResolver.ResolveByAttribute(typeof(CustomDecorated)), Is.EqualTo("Diagnostics"));
    }

    [Test]
    public void ResolveByAttribute_ReturnsNull_WhenAbsent()
    {
        Assert.That(ComponentFamilyResolver.ResolveByAttribute(typeof(PositionLike)), Is.Null);
    }

    [TestCase("Position",   "Spatial")]
    [TestCase("Velocity",   "Spatial")]
    [TestCase("PoseGlobal", "Spatial")]
    [TestCase("Health",     "Combat")]
    [TestCase("HpDelta",    "Combat")]
    [TestCase("HitState",   "Combat")]
    [TestCase("AiTarget",   "AI")]
    [TestCase("Pathfind",   "AI")]
    [TestCase("Inventory",  "Inventory")]
    [TestCase("AmmoCount",  "Inventory")]
    [TestCase("Sprite",     "Rendering")]
    [TestCase("MeshRef",    "Rendering")]
    [TestCase("Replication", "Networking")]
    [TestCase("InputState", "Input")]
    [TestCase("CommandQueue", "Input")]
    [TestCase("Whatever",   "Misc")]
    [TestCase("",           "Misc")]
    [TestCase(null,         "Misc")]
    public void ResolveByHeuristic_ClassifiesByName(string name, string expected)
    {
        Assert.That(ComponentFamilyResolver.ResolveByHeuristic(name), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_AttributeWinsOverHeuristic()
    {
        // CustomDecorated would map to Misc by name, but the attribute pins it to "Diagnostics".
        Assert.That(ComponentFamilyResolver.Resolve(typeof(CustomDecorated)), Is.EqualTo("Diagnostics"));
    }

    [Test]
    public void Resolve_FallsBackToHeuristic_WhenNoAttribute()
    {
        Assert.That(ComponentFamilyResolver.Resolve(typeof(PositionLike)), Is.EqualTo("Spatial"));
        Assert.That(ComponentFamilyResolver.Resolve(typeof(HealthLike)), Is.EqualTo("Combat"));
        Assert.That(ComponentFamilyResolver.Resolve(typeof(Whatever)), Is.EqualTo(ComponentFamilyResolver.Misc));
    }

    [Test]
    public void CanonicalFamilyOrder_HasMiscLast()
    {
        var order = ComponentFamilyResolver.CanonicalFamilyOrder;
        Assert.That(order[^1], Is.EqualTo(ComponentFamilyResolver.Misc));
        // Sanity: every heuristic family appears in the canonical order.
        Assert.That(order, Does.Contain("Spatial"));
        Assert.That(order, Does.Contain("Combat"));
        Assert.That(order, Does.Contain("AI"));
        Assert.That(order, Does.Contain("Inventory"));
        Assert.That(order, Does.Contain("Rendering"));
        Assert.That(order, Does.Contain("Networking"));
        Assert.That(order, Does.Contain("Input"));
    }
}
