using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

// ReSharper disable AccessToDisposedClosure

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only archetypes: 3-level hierarchy (300–302)
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.H.VehicleData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HVehicleData
{
    [Index(AllowMultiple = true)]
    public float Speed;
    public int _pad;
    public HVehicleData(float speed) { Speed = speed; _pad = 0; }
}

[Component("Typhon.Test.ECS.H.CarData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HCarData
{
    public int Doors;
    public int _pad;
    public HCarData(int doors) { Doors = doors; _pad = 0; }
}

[Component("Typhon.Test.ECS.H.SportsData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HSportsData
{
    public float Turbo;
    public int _pad;
    public HSportsData(float turbo) { Turbo = turbo; _pad = 0; }
}

[Archetype]
class HVehicle : Archetype<HVehicle>
{
    public static readonly Comp<HVehicleData> Vehicle = Register<HVehicleData>();
}

[Archetype]
class HCar : Archetype<HCar, HVehicle>
{
    public static readonly Comp<HCarData> Car = Register<HCarData>();
}

[Archetype]
class HSportsCar : Archetype<HSportsCar, HCar>
{
    public static readonly Comp<HSportsData> Sports = Register<HSportsData>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only archetypes: multi-level cascade (310–312)
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.H.RegionData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HRegionData
{
    public int Population;
    public int _pad;
    public HRegionData(int pop) { Population = pop; _pad = 0; }
}

[Component("Typhon.Test.ECS.H.CityData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HCityData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<HRegion> Region;
    public int Size;
}

[Component("Typhon.Test.ECS.H.DistrictData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HDistrictData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<HCity> City;
    public int Area;
}

[Archetype]
class HRegion : Archetype<HRegion>
{
    public static readonly Comp<HRegionData> Data = Register<HRegionData>();
}

[Archetype]
class HCity : Archetype<HCity>
{
    public static readonly Comp<HCityData> Data = Register<HCityData>();
}

[Archetype]
class HDistrict : Archetype<HDistrict>
{
    public static readonly Comp<HDistrictData> Data = Register<HDistrictData>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class EcsHardeningTests : TestBase<EcsHardeningTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        // 3-level hierarchy

        // Multi-level cascade

        // Also touch EcsUnit/EcsSoldier for enable/disable and error tests

        // Cascade bag/item for EntityLink tests
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<HVehicleData>();
        dbe.RegisterComponentFromAccessor<HCarData>();
        dbe.RegisterComponentFromAccessor<HSportsData>();
        dbe.RegisterComponentFromAccessor<HRegionData>();
        dbe.RegisterComponentFromAccessor<HCityData>();
        dbe.RegisterComponentFromAccessor<HDistrictData>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.RegisterComponentFromAccessor<BagData>();
        dbe.RegisterComponentFromAccessor<ItemData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.1 — Enable/Disable API
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Enable_PreviouslyDisabledComponent_ReadSucceeds()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        // Spawn with only Position provided → Velocity is disabled
        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        // Verify Velocity is disabled
        var entity = t.Open(id);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
        Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);

        // Enable Velocity
        var mut = t.OpenMut(id);
        mut.Enable(EcsUnit.Velocity);

        // Now read succeeds (zero-initialized)
        var entity2 = t.Open(id);
        Assert.That(entity2.IsEnabled(EcsUnit.Velocity), Is.True);
        ref readonly var vel = ref entity2.Read(EcsUnit.Velocity);
        Assert.That(vel.Dx, Is.EqualTo(0f));
    }

    [Test]
    public void Disable_EnabledComponent_IsEnabledReturnsFalse()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var mut = t.OpenMut(id);
        Assert.That(mut.IsEnabled(EcsUnit.Velocity), Is.True);

        mut.Disable(EcsUnit.Velocity);
        Assert.That(mut.IsEnabled(EcsUnit.Velocity), Is.False);
        Assert.That(mut.IsEnabled(EcsUnit.Position), Is.True);
    }

    [Test]
    public void EnableDisable_CommitPersists_NewTransactionSeesChange()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Disable Velocity and commit
        using (var t = dbe.CreateQuickTransaction())
        {
            var mut = t.OpenMut(id);
            mut.Disable(EcsUnit.Velocity);
            t.Commit();
        }

        // New transaction sees disabled
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
            Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
        }
    }

    [Test]
    public void EnableDisable_Toggle_RestoresOriginalState()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var mut = t.OpenMut(id);
        mut.Disable(EcsUnit.Velocity);
        Assert.That(mut.IsEnabled(EcsUnit.Velocity), Is.False);

        mut.Enable(EcsUnit.Velocity);
        Assert.That(mut.IsEnabled(EcsUnit.Velocity), Is.True);

        // Data still intact after disable/enable cycle
        var entity = t.Open(id);
        ref readonly var v = ref entity.Read(EcsUnit.Velocity);
        Assert.That(v.Dx, Is.EqualTo(4f));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.1b — Query read-your-own-writes (pending spawns visible to query)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Query_SeesPendingSpawns_InSameTransaction()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id1 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        var id2 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        // Query should see both pending spawns
        var results = t.Query<EcsUnit>().Execute();
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results, Does.Contain(id1));
        Assert.That(results, Does.Contain(id2));
    }

    [Test]
    public void Query_Count_IncludesPendingSpawns()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
        t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
        t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        Assert.That(t.Query<EcsUnit>().Count(), Is.EqualTo(3));
    }

    [Test]
    public void Query_Any_FindsPendingSpawn()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        Assert.That(t.Query<EcsUnit>().Any(), Is.False);

        var pos = new EcsPosition(1, 2, 3);
        t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        Assert.That(t.Query<EcsUnit>().Any(), Is.True);
    }

    [Test]
    public void Query_PendingSpawn_ExcludesDestroyedPending()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var id1 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
        var id2 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        t.Destroy(id1);

        var results = t.Query<EcsUnit>().Execute();
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results, Does.Contain(id2));
    }

    [Test]
    public void Query_PendingSpawn_RespectsEnabledFilter()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        // id1: both enabled
        var id1 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        // id2: only Position enabled
        var id2 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        // Query with Enabled<Velocity> should only find id1
        var results = t.Query<EcsUnit>().Enabled<EcsVelocity>().Execute();
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results, Does.Contain(id1));
    }

    [Test]
    public void Query_Foreach_IncludesPendingSpawns()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(10, 20, 30);
        var vel = new EcsVelocity(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        int count = 0;
        foreach (var entity in t.Query<EcsUnit>())
        {
            Assert.That(entity.Id, Is.EqualTo(id));
            ref readonly var p = ref entity.Read(EcsUnit.Position);
            Assert.That(p.X, Is.EqualTo(10f));
            count++;
        }
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void Query_MixedCommittedAndPending_SeesAll()
    {
        using var dbe = SetupEngine();

        EntityId committedId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            committedId = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(4, 5, 6);
            var pendingId = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

            var results = t.Query<EcsUnit>().Execute();
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results, Does.Contain(committedId));
            Assert.That(results, Does.Contain(pendingId));
        }
    }

    [Test]
    public void Query_PolymorphicWithPendingSpawns_SeesChildArchetypes()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var v = new HVehicleData(100f);
        var c = new HCarData(4);
        var vehicleId = t.Spawn<HVehicle>(HVehicle.Vehicle.Set(in v));
        var carId = t.Spawn<HCar>(HVehicle.Vehicle.Set(in v), HCar.Car.Set(in c));

        // Polymorphic query on base should find both
        var results = t.Query<HVehicle>().Execute();
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results, Does.Contain(vehicleId));
        Assert.That(results, Does.Contain(carId));
    }

    [Test]
    public void Query_WhereField_SeesPendingSpawns()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var fast = new HVehicleData(200f);
        var slow = new HVehicleData(10f);
        var id1 = t.Spawn<HVehicle>(HVehicle.Vehicle.Set(in fast));
        var id2 = t.Spawn<HVehicle>(HVehicle.Vehicle.Set(in slow));

        // WhereField with indexed predicate — should find pending spawn with Speed > 50
        var results = t.Query<HVehicle>().WhereField<HVehicleData>(v => v.Speed > 50f).Execute();
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results, Does.Contain(id1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.11 — Mixed storage mode in queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Query_MixedModeArchetype_ResolvesAllComponentsCorrectly()
    {
        using var dbe = SetupMixedModeEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new CompSmVersionedMix(10);
            var sv = new CompSmSingleVersion(20);
            var tr = new CompSmTransient(30);
            id = t.Spawn<MixedModeArchetype>(
                MixedModeArchetype.Versioned.Set(in v),
                MixedModeArchetype.SV.Set(in sv),
                MixedModeArchetype.Trans.Set(in tr));
            t.Commit();
        }

        // Query should find the entity, and all three components should be readable
        using (var t = dbe.CreateQuickTransaction())
        {
            var results = t.Query<MixedModeArchetype>().Execute();
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results, Does.Contain(id));

            var entity = t.Open(id);

            // Versioned component — MVCC read
            Assert.That(entity.TryRead<CompSmVersionedMix>(out var vr), Is.True);
            Assert.That(vr.Value, Is.EqualTo(10));

            // SingleVersion component — direct read
            Assert.That(entity.TryRead<CompSmSingleVersion>(out var svr), Is.True);
            Assert.That(svr.Value, Is.EqualTo(20));

            // Transient component — heap-backed read
            Assert.That(entity.TryRead<CompSmTransient>(out var tr), Is.True);
            Assert.That(tr.Value, Is.EqualTo(30));
        }
    }

    [Test]
    public void Query_MixedMode_WriteVersioned_SvUntouched()
    {
        using var dbe = SetupMixedModeEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new CompSmVersionedMix(10);
            var sv = new CompSmSingleVersion(20);
            id = t.Spawn<MixedModeArchetype>(
                MixedModeArchetype.Versioned.Set(in v),
                MixedModeArchetype.SV.Set(in sv));
            t.Commit();
        }

        // Update only Versioned, then read both
        using (var t = dbe.CreateQuickTransaction())
        {
            var mut = t.OpenMut(id);
            mut.Write(MixedModeArchetype.Versioned).Value = 999;
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(id);
            Assert.That(entity.TryRead<CompSmVersionedMix>(out var vr), Is.True);
            Assert.That(vr.Value, Is.EqualTo(999), "Versioned should reflect the write");

            Assert.That(entity.TryRead<CompSmSingleVersion>(out var svr), Is.True);
            Assert.That(svr.Value, Is.EqualTo(20), "SV should be untouched");
        }
    }

    private DatabaseEngine SetupMixedModeEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmVersionedMix>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.2 — Cascade delete: multi-level (Region → City → District)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void CascadeDelete_ThreeLevels_AllDescendantsDestroyed()
    {
        using var dbe = SetupEngine();

        EntityId regionId, city1Id, city2Id, dist1Id, dist2Id, dist3Id;

        using (var t = dbe.CreateQuickTransaction())
        {
            var regionData = new HRegionData(1000);
            regionId = t.Spawn<HRegion>(HRegion.Data.Set(in regionData));

            var cityData1 = new HCityData { Region = regionId, Size = 100 };
            var cityData2 = new HCityData { Region = regionId, Size = 200 };
            city1Id = t.Spawn<HCity>(HCity.Data.Set(in cityData1));
            city2Id = t.Spawn<HCity>(HCity.Data.Set(in cityData2));

            var d1 = new HDistrictData { City = city1Id, Area = 10 };
            var d2 = new HDistrictData { City = city1Id, Area = 20 };
            var d3 = new HDistrictData { City = city2Id, Area = 30 };
            dist1Id = t.Spawn<HDistrict>(HDistrict.Data.Set(in d1));
            dist2Id = t.Spawn<HDistrict>(HDistrict.Data.Set(in d2));
            dist3Id = t.Spawn<HDistrict>(HDistrict.Data.Set(in d3));

            t.Commit();
        }

        // Destroy the region → should cascade to cities → cascade to districts
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(regionId);
            t.Commit();
        }

        // Verify everything is dead
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(regionId), Is.False);
            Assert.That(t.IsAlive(city1Id), Is.False);
            Assert.That(t.IsAlive(city2Id), Is.False);
            Assert.That(t.IsAlive(dist1Id), Is.False);
            Assert.That(t.IsAlive(dist2Id), Is.False);
            Assert.That(t.IsAlive(dist3Id), Is.False);
        }
    }

    [Test]
    public void CascadeDelete_OnPendingEntities_SpawnedChildrenAlsoDestroyed()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        // Spawn parent and children in same transaction (not yet committed)
        var regionData = new HRegionData(500);
        var regionId = t.Spawn<HRegion>(HRegion.Data.Set(in regionData));

        var cityData = new HCityData { Region = regionId, Size = 50 };
        var cityId = t.Spawn<HCity>(HCity.Data.Set(in cityData));

        var distData = new HDistrictData { City = cityId, Area = 5 };
        var distId = t.Spawn<HDistrict>(HDistrict.Data.Set(in distData));

        // All alive before destroy
        Assert.That(t.IsAlive(regionId), Is.True);
        Assert.That(t.IsAlive(cityId), Is.True);
        Assert.That(t.IsAlive(distId), Is.True);

        // Destroy region — cascade should find pending children
        t.Destroy(regionId);

        Assert.That(t.IsAlive(regionId), Is.False);
        Assert.That(t.IsAlive(cityId), Is.False);
        Assert.That(t.IsAlive(distId), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.3 — Error cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Open_NonExistentEntity_Throws()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var fakeId = new EntityId(999999, 100); // EcsUnit archetype, non-existent key
        Assert.Throws<InvalidOperationException>(() => t.Open(fakeId));
    }

    [Test]
    public void TryOpen_NonExistentEntity_ReturnsFalse()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var fakeId = new EntityId(999999, 100);
        bool found = t.TryOpen(fakeId, out var entity);
        Assert.That(found, Is.False);
    }

    [Test]
    public void Open_DestroyedEntity_Throws()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            Assert.Throws<InvalidOperationException>(() => t.Open(id));
        }
    }

    [Test]
    public void TryRead_DisabledComponent_ReturnsFalse()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        // Spawn with only Position → Velocity disabled
        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        var entity = t.Open(id);
        bool found = entity.TryRead<EcsVelocity>(out _);
        Assert.That(found, Is.False);
    }

    [Test]
    public void TryRead_ComponentNotInArchetype_ReturnsFalse()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        // EcsUnit has Position + Velocity, NOT Health
        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        var entity = t.Open(id);
        bool found = entity.TryRead<EcsHealth>(out _);
        Assert.That(found, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.4 — EntityLink<T> comprehensive
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void IsAlive_ViaEntityLink_ReturnsCorrectState()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var bagData = new BagData { Capacity = 10 };
        var bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

        EntityLink<CascadeBag> link = bagId;
        Assert.That(t.IsAlive(link), Is.True);

        t.Destroy(bagId);
        Assert.That(t.IsAlive(link), Is.False);
    }

    [Test]
    public void EntityLink_ImplicitConversion_RoundTrips()
    {
        var id = new EntityId(42, 700); // CascadeBag archetype
        EntityLink<CascadeBag> link = id;
        EntityId backToId = link;

        Assert.That(backToId, Is.EqualTo(id));
        Assert.That(link.Id, Is.EqualTo(id));
    }

    [Test]
    public void EntityLink_Null_IsDetected()
    {
        EntityLink<CascadeBag> link = EntityLink<CascadeBag>.Null;
        Assert.That(link.IsNull, Is.True);

        EntityLink<CascadeBag> fromNullId = EntityId.Null;
        Assert.That(fromNullId.IsNull, Is.True);
    }

    [Test]
    public void Open_ViaEntityLink_ReadsComponentData()
    {
        using var dbe = SetupEngine();

        EntityId bagId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var bagData = new BagData { Capacity = 42 };
            bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            EntityLink<CascadeBag> link = bagId;
            // Open via implicit EntityLink → EntityId conversion
            var entity = t.Open(link);
            Assert.That(entity.IsValid, Is.True);
            ref readonly var bag = ref entity.Read(CascadeBag.Bag);
            Assert.That(bag.Capacity, Is.EqualTo(42));
        }
    }

    [Test]
    public void OpenMut_ViaEntityLink_WritesComponentData()
    {
        using var dbe = SetupEngine();

        EntityId bagId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var bagData = new BagData { Capacity = 10 };
            bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));
            t.Commit();
        }

        EntityLink<CascadeBag> link = bagId;

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(link);
            ref var bag = ref entity.Write(CascadeBag.Bag);
            bag.Capacity = 99;
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(link);
            ref readonly var bag = ref entity.Read(CascadeBag.Bag);
            Assert.That(bag.Capacity, Is.EqualTo(99));
        }
    }

    [Test]
    public void TryOpen_ViaEntityLink_ReturnsTrueWhenAlive()
    {
        using var dbe = SetupEngine();

        EntityId bagId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var bagData = new BagData { Capacity = 5 };
            bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            EntityLink<CascadeBag> link = bagId;
            bool found = t.TryOpen(link, out var entity);
            Assert.That(found, Is.True);
            Assert.That(entity.IsValid, Is.True);
        }
    }

    [Test]
    public void TryOpen_ViaEntityLink_ReturnsFalseAfterDestroy()
    {
        using var dbe = SetupEngine();

        EntityId bagId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var bagData = new BagData { Capacity = 5 };
            bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(bagId);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            EntityLink<CascadeBag> link = bagId;
            bool found = t.TryOpen(link, out _);
            Assert.That(found, Is.False);
            Assert.That(t.IsAlive(link), Is.False);
        }
    }

    [Test]
    public void PolymorphicLink_BaseTypeHoldsDerivedEntity()
    {
        using var dbe = SetupEngine();

        // Spawn a HSportsCar (grandchild of HVehicle)
        EntityId sportsCarId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new HVehicleData(250f);
            var c = new HCarData(2);
            var s = new HSportsData(1.5f);
            sportsCarId = t.Spawn<HSportsCar>(HVehicle.Vehicle.Set(in v), HCar.Car.Set(in c), HSportsCar.Sports.Set(in s));
            t.Commit();
        }

        // Create an EntityLink<HVehicle> pointing to the HSportsCar — polymorphic acceptance
        EntityLink<HVehicle> baseLink = sportsCarId;

        using (var t = dbe.CreateQuickTransaction())
        {
            // IsAlive works through polymorphic link
            Assert.That(t.IsAlive(baseLink), Is.True);

            // Open works through polymorphic link
            var entity = t.Open(baseLink);
            Assert.That(entity.IsValid, Is.True);

            // Can read the base component through the entity
            ref readonly var vehicleData = ref entity.Read(HVehicle.Vehicle);
            Assert.That(vehicleData.Speed, Is.EqualTo(250f));

            // Can also read derived components (entity is actually a HSportsCar)
            ref readonly var sportsData = ref entity.Read(HSportsCar.Sports);
            Assert.That(sportsData.Turbo, Is.EqualTo(1.5f));
        }
    }

    [Test]
    public void PolymorphicLink_MidLevelHoldsGrandchild()
    {
        using var dbe = SetupEngine();

        // Spawn HSportsCar, reference via EntityLink<HCar> (mid-level)
        EntityId sportsCarId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new HVehicleData(300f);
            var c = new HCarData(4);
            var s = new HSportsData(2.0f);
            sportsCarId = t.Spawn<HSportsCar>(HVehicle.Vehicle.Set(in v), HCar.Car.Set(in c), HSportsCar.Sports.Set(in s));
            t.Commit();
        }

        EntityLink<HCar> midLink = sportsCarId;

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(midLink), Is.True);
            var entity = t.Open(midLink);
            ref readonly var carData = ref entity.Read(HCar.Car);
            Assert.That(carData.Doors, Is.EqualTo(4));
        }
    }

    [Test]
    public void Destroy_ViaPolymorphicLink_EntityDies()
    {
        using var dbe = SetupEngine();

        EntityId carId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new HVehicleData(120f);
            var c = new HCarData(4);
            carId = t.Spawn<HCar>(HVehicle.Vehicle.Set(in v), HCar.Car.Set(in c));
            t.Commit();
        }

        // Destroy via EntityLink<HVehicle> (base type)
        EntityLink<HVehicle> baseLink = carId;
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(baseLink);
            Assert.That(t.IsAlive(baseLink), Is.False);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(carId), Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.5 — Double-destroy
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_AlreadyPendingDestroy_NoOp()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            // Second destroy should not throw — _pendingDestroys prevents double-processing
            Assert.DoesNotThrow(() => t.Destroy(id));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.False);
        }
    }

    [Test]
    public void Destroy_PendingSpawnedEntity_ThenDoubleDestroy_NoOp()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        t.Destroy(id);
        Assert.DoesNotThrow(() => t.Destroy(id));
        Assert.That(t.IsAlive(id), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.6 — 3+ level archetype hierarchy
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ThreeLevelHierarchy_SlotOrdering_ParentFirst()
    {
        using var dbe = SetupEngine();

        var meta = Archetype<HSportsCar>.Metadata;
        Assert.That(meta.ComponentCount, Is.EqualTo(3));
        // Slot 0 = HVehicleData (from HVehicle grandparent)
        // Slot 1 = HCarData (from HCar parent)
        // Slot 2 = HSportsData (from HSportsCar self)
    }

    [Test]
    public void ThreeLevelHierarchy_SpawnAndReadAllLevels()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var v = new HVehicleData(200f);
        var c = new HCarData(4);
        var s = new HSportsData(1.5f);
        var id = t.Spawn<HSportsCar>(HVehicle.Vehicle.Set(in v), HCar.Car.Set(in c), HSportsCar.Sports.Set(in s));

        var entity = t.Open(id);

        // Read using each level's Comp<T> handle
        ref readonly var vr = ref entity.Read(HVehicle.Vehicle);
        Assert.That(vr.Speed, Is.EqualTo(200f));

        ref readonly var cr = ref entity.Read(HCar.Car);
        Assert.That(cr.Doors, Is.EqualTo(4));

        ref readonly var sr = ref entity.Read(HSportsCar.Sports);
        Assert.That(sr.Turbo, Is.EqualTo(1.5f));
    }

    [Test]
    public void ThreeLevelHierarchy_PolymorphicQuery_FindsAllDescendants()
    {
        using var dbe = SetupEngine();

        EntityId vehicleId, carId, sportsId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var v = new HVehicleData(100f);
            vehicleId = t.Spawn<HVehicle>(HVehicle.Vehicle.Set(in v));

            var c = new HCarData(4);
            carId = t.Spawn<HCar>(HVehicle.Vehicle.Set(in v), HCar.Car.Set(in c));

            var s = new HSportsData(2f);
            sportsId = t.Spawn<HSportsCar>(HVehicle.Vehicle.Set(in v), HCar.Car.Set(in c), HSportsCar.Sports.Set(in s));

            t.Commit();
        }

        // Query on a new transaction (sees committed entities in EntityMap)
        using (var t = dbe.CreateQuickTransaction())
        {
            // Query<HVehicle> should find all three
            var all = t.Query<HVehicle>().Execute();
            Assert.That(all, Has.Count.EqualTo(3));
            Assert.That(all, Does.Contain(vehicleId));
            Assert.That(all, Does.Contain(carId));
            Assert.That(all, Does.Contain(sportsId));

            // Query<HCar> should find car + sports car
            var cars = t.Query<HCar>().Execute();
            Assert.That(cars, Has.Count.EqualTo(2));
            Assert.That(cars, Does.Contain(carId));
            Assert.That(cars, Does.Contain(sportsId));

            // QueryExact<HCar> should find only car
            var exactCars = t.QueryExact<HCar>().Execute();
            Assert.That(exactCars, Has.Count.EqualTo(1));
            Assert.That(exactCars, Does.Contain(carId));
        }
    }

    [Test]
    public void ThreeLevelHierarchy_SubtreeArchetypeIds_IncludesSelfAndAllDescendants()
    {
        var meta = Archetype<HVehicle>.Metadata;
        Assert.That(meta.SubtreeArchetypeIds, Does.Contain(meta.ArchetypeId));
        Assert.That(meta.SubtreeArchetypeIds, Does.Contain(Archetype<HCar>.Metadata.ArchetypeId));
        Assert.That(meta.SubtreeArchetypeIds, Does.Contain(Archetype<HSportsCar>.Metadata.ArchetypeId));
        Assert.That(meta.SubtreeArchetypeIds, Has.Length.EqualTo(3));
    }

    [Test]
    public void ThreeLevelHierarchy_MidLevelSubtree_IncludesSelfAndChild()
    {
        using var dbe = SetupEngine(); // Triggers Freeze() which populates SubtreeArchetypeIds
        var carMeta = Archetype<HCar>.Metadata;
        Assert.That(carMeta.SubtreeArchetypeIds, Does.Contain(carMeta.ArchetypeId));
        Assert.That(carMeta.SubtreeArchetypeIds, Does.Contain(Archetype<HSportsCar>.Metadata.ArchetypeId));
        Assert.That(carMeta.SubtreeArchetypeIds, Has.Length.EqualTo(2));

        // Leaf has only itself
        var sportsMeta = Archetype<HSportsCar>.Metadata;
        Assert.That(sportsMeta.SubtreeArchetypeIds, Does.Contain(sportsMeta.ArchetypeId));
        Assert.That(sportsMeta.SubtreeArchetypeIds, Has.Length.EqualTo(1));
    }

    [Test]
    public void ThreeLevelHierarchy_WhereField_FindsAcrossAllLevels()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        // Spawn one entity at each hierarchy level, all with Speed > 50
        var fast = new HVehicleData(100f);
        var slow = new HVehicleData(30f);
        var carData = new HCarData(4);
        var sportsData = new HSportsData(1.5f);

        var vehicleId = t.Spawn<HVehicle>(HVehicle.Vehicle.Set(in fast));
        var carId = t.Spawn<HCar>(HVehicle.Vehicle.Set(in slow), HCar.Car.Set(in carData));
        var sportsId = t.Spawn<HSportsCar>(HVehicle.Vehicle.Set(in fast), HCar.Car.Set(in carData), HSportsCar.Sports.Set(in sportsData));

        // Query<HVehicle> with WhereField Speed > 50 — should find vehicle + sportsCar, not car (speed=30)
        var results = t.Query<HVehicle>().WhereField<HVehicleData>(v => v.Speed > 50f).Execute();
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Contains(vehicleId), Is.True, "HVehicle with speed=100 should match");
        Assert.That(results.Contains(sportsId), Is.True, "HSportsCar with speed=100 should match");
        Assert.That(results.Contains(carId), Is.False, "HCar with speed=30 should not match");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.7 — Spawn-then-destroy same transaction
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnThenDestroy_SameTransaction_EntityNotAlive()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
        Assert.That(t.IsAlive(id), Is.True);

        t.Destroy(id);
        Assert.That(t.IsAlive(id), Is.False);

        // TryOpen should also fail
        bool found = t.TryOpen(id, out _);
        Assert.That(found, Is.False);
    }

    [Test]
    public void SpawnThenDestroy_CommitThenNewTransaction_EntityInvisible()
    {
        using var dbe = SetupEngine();
        EntityId id;

        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Destroy(id);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A.8 — Batch operation edge cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnBatch_ZeroLength_NoOp()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var empty = new EntityId[0].AsSpan();
        t.SpawnBatch<EcsUnit>(empty);
    }

    [Test]
    public void DestroyBatch_EmptySpan_NoOp()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        Assert.DoesNotThrow(() => t.DestroyBatch(ReadOnlySpan<EntityId>.Empty));
    }

    [Test]
    public void SpawnBatch_WithSharedValues_AllEntitiesGetSameData()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(10, 20, 30);
        Span<EntityId> ids = stackalloc EntityId[5];
        t.SpawnBatch<EcsUnit>(ids, EcsUnit.Position.Set(in pos));

        foreach (var id in ids)
        {
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
            ref readonly var p = ref entity.Read(EcsUnit.Position);
            Assert.That(p.X, Is.EqualTo(10f));
            Assert.That(p.Y, Is.EqualTo(20f));
            Assert.That(p.Z, Is.EqualTo(30f));
        }
    }
}
