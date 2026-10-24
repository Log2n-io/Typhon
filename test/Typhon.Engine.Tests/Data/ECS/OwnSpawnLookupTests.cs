using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// A transaction finds its own pending spawns through an EntityId → spawn-list index that is built on the first lookup and then extended by the entries
/// appended since, never rebuilt (#1477). These tests interleave appends and lookups so that every lookup runs against a map that was already built and has
/// to pick up a tail: a tail indexed at the wrong position, or not at all, reads another entity's values or misses the entity.
/// </summary>
class OwnSpawnLookupTests : TestBase<OwnSpawnLookupTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void AssertUnit(Transaction t, EntityId id, float x, float dx)
    {
        var entity = t.Open(id);
        Assert.That(entity.Read(EcsUnit.Position).X, Is.EqualTo(x), $"Position of {id}");
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True, $"Velocity of {id} enabled");
        Assert.That(entity.Read(EcsUnit.Velocity).Dx, Is.EqualTo(dx), $"Velocity of {id}");
    }

    /// <summary>The #1477 shape: spawn without the second component, then open the spawn and enable it, before the next spawn.</summary>
    [Test]
    public void SpawnThenEnable_Interleaved_EveryEntityKeepsItsOwnValues()
    {
        using var dbe = SetupEngine();
        const int count = 300;
        var ids = new EntityId[count];

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var pos = new EcsPosition(i, 0, 0);
                ids[i] = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
                var vel = new EcsVelocity(1000 + i, 0, 0);
                t.OpenMut(ids[i]).Enable(EcsUnit.Velocity, in vel);
            }

            // Read back in reverse, still inside the transaction: the map now serves lookups for entries it indexed one at a time.
            for (int i = count - 1; i >= 0; i--)
            {
                AssertUnit(t, ids[i], i, 1000 + i);
            }
            Assert.That(t.Commit(), Is.True);
        }

        using var r = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            AssertUnit(r, ids[i], i, 1000 + i);
        }
    }

    /// <summary>Each spawn path appends differently (one Add, Adds in a loop, a pre-extended span); a lookup between them must see every tail.</summary>
    [Test]
    public void LookupBetweenAppendPaths_SeesEveryPathsEntries()
    {
        using var dbe = SetupEngine();

        EntityId committed;
        using (var t0 = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(-1, 0, 0);
            var vel = new EcsVelocity(-1, 0, 0);
            committed = t0.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            Assert.That(t0.Commit(), Is.True);
        }

        using var t = dbe.CreateQuickTransaction();

        var p0 = new EcsPosition(0, 0, 0);
        var v0 = new EcsVelocity(100, 0, 0);
        var single = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in p0), EcsUnit.Velocity.Set(in v0));
        AssertUnit(t, single, 0, 100);   // builds the map

        var pShared = new EcsPosition(1, 0, 0);
        var vShared = new EcsVelocity(101, 0, 0);
        var shared = new EntityId[4];
        t.SpawnBatch<EcsUnit>(shared, EcsUnit.Position.Set(in pShared), EcsUnit.Velocity.Set(in vShared));
        foreach (var id in shared)
        {
            AssertUnit(t, id, 1, 101);
        }

        var positions = new EcsPosition[] { new(2, 0, 0), new(3, 0, 0), new(4, 0, 0) };
        var velocities = new EcsVelocity[] { new(102, 0, 0), new(103, 0, 0), new(104, 0, 0) };
        var soa = EcsUnit.SpawnBatch(t, positions, velocities);
        for (int i = 0; i < soa.Length; i++)
        {
            AssertUnit(t, soa[i], 2 + i, 102 + i);
        }

        var p5 = new EcsPosition(5, 0, 0);
        var v5 = new EcsVelocity(105, 0, 0);
        var last = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in p5), EcsUnit.Velocity.Set(in v5));

        // Every earlier entry is still found at its own position, and a committed entity is not mistaken for an own spawn.
        AssertUnit(t, last, 5, 105);
        AssertUnit(t, single, 0, 100);
        AssertUnit(t, shared[3], 1, 101);
        AssertUnit(t, soa[0], 2, 102);
        AssertUnit(t, committed, -1, -1);
    }

    /// <summary>Destroying an own spawn resolves it through the same map; the spawns appended after the map was built must stay distinguishable.</summary>
    [Test]
    public void DestroyOwnSpawnAppendedAfterLookup_OnlyThatEntityDies()
    {
        using var dbe = SetupEngine();
        var ids = new EntityId[3];

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < ids.Length; i++)
            {
                var pos = new EcsPosition(i, 0, 0);
                var vel = new EcsVelocity(10 + i, 0, 0);
                ids[i] = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                if (i == 0)
                {
                    AssertUnit(t, ids[0], 0, 10);   // the map exists before ids[1] and ids[2] are appended
                }
            }

            t.Destroy(ids[1]);
            Assert.That(t.Commit(), Is.True);
        }

        using var r = dbe.CreateQuickTransaction();
        Assert.That(r.IsAlive(ids[1]), Is.False, "the destroyed own spawn");
        AssertUnit(r, ids[0], 0, 10);
        AssertUnit(r, ids[2], 2, 12);
    }

    /// <summary>
    /// A pooled transaction is reset between uses, clearing its spawn list and map together; its next use must index its own spawns from position 0, not
    /// from where the previous use stopped. The pool starts with 16 instances and hands them out first-in first-out, so a returned instance comes back only
    /// after every other one has been used: the rounds go round it twice.
    /// </summary>
    [Test]
    public void ReusedPooledTransaction_IndexesItsSpawnsFromScratch()
    {
        using var dbe = SetupEngine();
        const int rounds = 40;
        var instances = new HashSet<Transaction>(ReferenceEqualityComparer.Instance);
        var previous = Array.Empty<EntityId>();

        for (int round = 0; round < rounds; round++)
        {
            using var t = dbe.CreateQuickTransaction();
            instances.Add(t);

            var ids = new EntityId[3];
            for (int i = 0; i < ids.Length; i++)
            {
                var pos = new EcsPosition(10 * round + i, 0, 0);
                ids[i] = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
                var vel = new EcsVelocity(1000 + 10 * round + i, 0, 0);
                t.OpenMut(ids[i]).Enable(EcsUnit.Velocity, in vel);
            }

            for (int i = 0; i < ids.Length; i++)
            {
                AssertUnit(t, ids[i], 10 * round + i, 1000 + 10 * round + i);
            }
            foreach (var id in previous)
            {
                Assert.That(t.IsAlive(id), Is.False, $"round {round}: a rolled-back spawn is not this transaction's");
            }

            t.Rollback();
            previous = ids;
        }

        Assert.That(instances.Count, Is.LessThan(rounds), "no pooled instance came back, so nothing above exercised a reset");
    }
}
