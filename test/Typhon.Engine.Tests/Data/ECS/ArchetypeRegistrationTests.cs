using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Test component structs
// ═══════════════════════════════════════════════════════════════════════

[StructLayout(LayoutKind.Sequential)]
struct Placement
{
    public float X, Y, Z;
}

[StructLayout(LayoutKind.Sequential)]
struct BuildingData
{
    public int Level;
    public float Health;
}

[StructLayout(LayoutKind.Sequential)]
struct HouseData
{
    public int Residents;
}

[StructLayout(LayoutKind.Sequential)]
struct FactoryData
{
    public int ProductionRate;
}

// ═══════════════════════════════════════════════════════════════════════
// Test archetypes
// ═══════════════════════════════════════════════════════════════════════

[Archetype]
class TestBuilding : Archetype<TestBuilding>
{
    public static readonly Comp<Placement> PlacementComp = Register<Placement>();
    public static readonly Comp<BuildingData> BuildingComp = Register<BuildingData>();
}

[Archetype]
class TestHouse : Archetype<TestHouse, TestBuilding>
{
    public static readonly Comp<HouseData> HouseComp = Register<HouseData>();
}

[Archetype]
class TestFactory : Archetype<TestFactory, TestBuilding>
{
    public static readonly Comp<FactoryData> FactoryComp = Register<FactoryData>();
}

// Standalone (no parent)
[Archetype]
class TestVehicle : Archetype<TestVehicle>
{
    public static readonly Comp<Placement> VehiclePlacement = Register<Placement>();
}

[NonParallelizable]
class ArchetypeRegistrationTests
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        // Touch ensures: 1) CLR runs field initializers (DeclareComponent), 2) lazy finalization runs
    }

    [Test]
    public void RootArchetype_RegistersWithCorrectId()
    {
        // #514 D1: catalog ids are engine-assigned, so we resolve archetypes by type, not by a literal id.
        var meta = ArchetypeRegistry.GetMetadata<TestBuilding>();
        Assert.That(meta, Is.Not.Null);
        Assert.That(meta.ComponentCount, Is.EqualTo(2));
        Assert.That(meta.ParentArchetypeId, Is.EqualTo(ArchetypeMetadata.NoParent));
    }

    [Test]
    public void RootArchetype_SlotIndices_Sequential()
    {
        _ = TestBuilding.PlacementComp;

        var meta = ArchetypeRegistry.GetMetadata<TestBuilding>();
        Assert.That(meta.ComponentCount, Is.EqualTo(2));
        Assert.That(meta._componentTypeIds.Length, Is.EqualTo(2));

        // Slot 0 = Placement, Slot 1 = BuildingData
        int placementTypeId = ArchetypeRegistry.GetComponentTypeId<Placement>();
        int buildingTypeId = ArchetypeRegistry.GetComponentTypeId<BuildingData>();

        Assert.That(meta.GetSlot(placementTypeId), Is.EqualTo(0));
        Assert.That(meta.GetSlot(buildingTypeId), Is.EqualTo(1));
    }

    [Test]
    public void ChildArchetype_InheritsParentSlots_ParentFirst()
    {
        _ = TestHouse.HouseComp;

        var meta = ArchetypeRegistry.GetMetadata<TestHouse>();
        Assert.That(meta.ComponentCount, Is.EqualTo(3)); // 2 from Building + 1 own
        Assert.That(meta.ParentArchetypeId, Is.EqualTo(ArchetypeRegistry.GetMetadata<TestBuilding>().ArchetypeId));

        int placementTypeId = ArchetypeRegistry.GetComponentTypeId<Placement>();
        int buildingTypeId = ArchetypeRegistry.GetComponentTypeId<BuildingData>();
        int houseTypeId = ArchetypeRegistry.GetComponentTypeId<HouseData>();

        // Parent slots first (0, 1), then own (2)
        Assert.That(meta.GetSlot(placementTypeId), Is.EqualTo(0));
        Assert.That(meta.GetSlot(buildingTypeId), Is.EqualTo(1));
        Assert.That(meta.GetSlot(houseTypeId), Is.EqualTo(2));
    }

    [Test]
    public void ComponentTypeId_DedupAcrossArchetypes()
    {
        // Both TestBuilding and TestVehicle use Placement
        _ = TestBuilding.PlacementComp;
        _ = TestVehicle.VehiclePlacement;

        int typeId = ArchetypeRegistry.GetComponentTypeId<Placement>();
        Assert.That(typeId, Is.GreaterThanOrEqualTo(0));

        // Both should share the same ComponentTypeId
        var buildingMeta = ArchetypeRegistry.GetMetadata<TestBuilding>();
        var vehicleMeta = ArchetypeRegistry.GetMetadata<TestVehicle>();

        Assert.That(buildingMeta._componentTypeIds[0], Is.EqualTo(typeId));
        Assert.That(vehicleMeta._componentTypeIds[0], Is.EqualTo(typeId));
    }

    [Test]
    public void ParentChild_Graph_ChildRegistered()
    {
        _ = TestHouse.HouseComp;
        _ = TestFactory.FactoryComp;

        var parentMeta = ArchetypeRegistry.GetMetadata<TestBuilding>();
        Assert.That(parentMeta.ChildArchetypeIds, Does.Contain(ArchetypeRegistry.GetMetadata<TestHouse>().ArchetypeId));
        Assert.That(parentMeta.ChildArchetypeIds, Does.Contain(ArchetypeRegistry.GetMetadata<TestFactory>().ArchetypeId));
    }

    [Test]
    public void Freeze_BuildsSubtreeIds()
    {
        _ = TestHouse.HouseComp;
        _ = TestFactory.FactoryComp;

        ArchetypeRegistry.Freeze();

        var parentMeta = ArchetypeRegistry.GetMetadata<TestBuilding>();
        var houseId = ArchetypeRegistry.GetMetadata<TestHouse>().ArchetypeId;
        var factoryId = ArchetypeRegistry.GetMetadata<TestFactory>().ArchetypeId;
        // Subtree of Building: self + House + Factory
        Assert.That(parentMeta.SubtreeArchetypeIds, Does.Contain(parentMeta.ArchetypeId));
        Assert.That(parentMeta.SubtreeArchetypeIds, Does.Contain(houseId));
        Assert.That(parentMeta.SubtreeArchetypeIds, Does.Contain(factoryId));
        Assert.That(parentMeta.SubtreeArchetypeIds.Length, Is.EqualTo(3));

        // Subtree of House: just self
        var houseMeta = ArchetypeRegistry.GetMetadata<TestHouse>();
        Assert.That(houseMeta.SubtreeArchetypeIds, Is.EqualTo(new[] { houseMeta.ArchetypeId }));
    }

    [Test]
    public void EntityRecordSize_MatchesComponentCount()
    {
        _ = TestBuilding.PlacementComp;
        _ = TestHouse.HouseComp;

        var buildingMeta = ArchetypeRegistry.GetMetadata<TestBuilding>();
        var houseMeta = ArchetypeRegistry.GetMetadata<TestHouse>();

        // Building: 2 components → 14 + 2*4 = 22 bytes
        Assert.That(buildingMeta._entityRecordSize, Is.EqualTo(22));

        // House: 3 components → 14 + 3*4 = 26 bytes
        Assert.That(houseMeta._entityRecordSize, Is.EqualTo(26));
    }

    [Test]
    public void RegistrationCount_Correct()
    {
        _ = TestBuilding.PlacementComp;
        _ = TestVehicle.VehiclePlacement;

        Assert.That(ArchetypeRegistry.Count, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void CompHandle_Set_RoundTrip()
    {
        _ = TestBuilding.PlacementComp;

        var value = new Placement { X = 1.0f, Y = 2.0f, Z = 3.0f };
        var cv = TestBuilding.PlacementComp.Set(in value);

        var read = cv.Read<Placement>();
        Assert.That(read.X, Is.EqualTo(1.0f));
        Assert.That(read.Y, Is.EqualTo(2.0f));
        Assert.That(read.Z, Is.EqualTo(3.0f));
    }
}
