using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public partial struct CompE_Eng
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompE";
    
    public int A;
    public ComponentCollection<int> Collection;

    public static CompE_Eng Create(Random rand) => new(rand.Next());

    public CompE_Eng(int a)
    {
        A = a;
        Collection = default;
    }
    
    public void Update(Random rand)
    {
        A = rand.Next();
    }

    public override string ToString() => $"A={A}";
}

class ComponentCollectionTests : TestBase<ComponentCollectionTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompAEArch>.Touch();
    }

    protected override void RegisterComponents(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CompE_Eng>();
        base.RegisterComponents(dbe);
    }

    [Test]
    public void Collection_CreateReadUpdate_Successful()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();

            var a = new CompA(2);
            var e = new CompE_Eng(1);

            {
                using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);

                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }

            entityId = t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
            Assert.That(entityId.IsNull, Is.False, "A valid entity id must not be null");

            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.Open(entityId);
            var e2 = entity.Read(CompAEArch.E);

            using var cca = t.CreateComponentCollectionAccessor(ref e2.Collection);
            Span<int> allItems = stackalloc int[cca.ElementCount];
            cca.GetAllElements(allItems);
            Assert.That(allItems.Length, Is.EqualTo(10), "There should be 10 items");

            for (int i = 0; i < 10; i++)
            {
                var actual = allItems[i];
                Assert.That(allItems.Contains(actual), Is.True);
            }
        }

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.OpenMut(entityId);
            ref var e2 = ref entity.Write(CompAEArch.E);

            {
                using var cca = t.CreateComponentCollectionAccessor(ref e2.Collection);

                for (int i = 10; i < 20; i++)
                {
                    cca.Add(i);
                }
            }

            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.Open(entityId);
            var e2 = entity.Read(CompAEArch.E);

            using var cca = t.CreateComponentCollectionAccessor(ref e2.Collection);
            Span<int> allItems = stackalloc int[cca.ElementCount];
            cca.GetAllElements(allItems);
            for (int i = 0; i < 20; i++)
            {
                Assert.That(allItems.Contains(i), Is.True);
            }
        }
    }

    [Test]
    public void Collection_RefCounter()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();

            var a = new CompA(2);
            var e = new CompE_Eng(1);

            {
                using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);

                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }

            entityId = t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
            Assert.That(entityId.IsNull, Is.False, "A valid entity id must not be null");

            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.OpenMut(entityId);
            ref var e2 = ref entity.Write(CompAEArch.E);

            // Change A to trigger the creation of a new revision during the Write call above
            e2.A = 12;

            {
                Assert.That(t.GetComponentCollectionRefCounter(ref e2.Collection), Is.EqualTo(2), "RefCounter should be 2, because shared by 2 revisions");
            }

            var res = t.Commit();
            Assert.That(res, Is.True, "Transaction commit should be successful");
        }

        // Flush deferred cleanup so the old revision is removed and refcount decremented
        dbe.FlushDeferredCleanups();

        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.Open(entityId);
            var e2 = entity.Read(CompAEArch.E);

            Assert.That(t.GetComponentCollectionRefCounter(ref e2.Collection), Is.EqualTo(1), "RefCounter should be 1, because there is now only one revision for this component");
        }
    }

    /// <summary>
    /// Bug #1 regression: a Versioned update that copies-on-write (AddRef on the shared buffer) AND THEN mutates the
    /// collection (clone to a fresh buffer) must release the old buffer's spurious AddRef. Otherwise the old buffer's
    /// refcount stays inflated and it leaks when the old revision is cleaned up. The existing Collection_RefCounter
    /// test misses this because it only mutates a scalar field, so no clone ever happens.
    /// </summary>
    [Test]
    public void Collection_COWThenClone_ReleasesOldBuffer()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();

            var a = new CompA(2);
            var e = new CompE_Eng(1);
            {
                using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);
                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }

            entityId = t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
            Assert.That(t.Commit(), Is.True, "Spawn commit should be successful");
        }

        // Capture the committed (old) buffer handle and confirm its refcount is 1.
        ComponentCollection<int> oldHandle;
        {
            using var t = dbe.CreateQuickTransaction();
            var e2 = t.Open(entityId).Read(CompAEArch.E);
            oldHandle = e2.Collection;
            Assert.That(t.GetComponentCollectionRefCounter(ref oldHandle), Is.EqualTo(1), "Initial buffer refcount should be 1");
        }

        // Update: Write (COW → AddRef on the old buffer) then MUTATE the collection (→ CloneBuffer to a fresh buffer).
        {
            using var t = dbe.CreateQuickTransaction();

            var entity = t.OpenMut(entityId);
            ref var e2 = ref entity.Write(CompAEArch.E);
            {
                using var cca = t.CreateComponentCollectionAccessor(ref e2.Collection);
                cca.Add(99);
            }

            Assert.That(t.Commit(), Is.True, "Update commit should be successful");
        }

        // BEFORE cleanup: both revisions exist. The new revision cloned to a fresh buffer, so the OLD buffer must be
        // referenced by exactly one revision (the old one). Bug #1 leaves it at 2 (COW AddRef never released on clone).
        {
            using var t = dbe.CreateQuickTransaction();

            var headHandle = t.Open(entityId).Read(CompAEArch.E).Collection;
            Assert.That(t.GetComponentCollectionRefCounter(ref headHandle), Is.EqualTo(1), "New (cloned) buffer should have refcount 1");
            Assert.That(t.GetComponentCollectionRefCounter(ref oldHandle), Is.EqualTo(1),
                "Old buffer should be referenced by exactly 1 (old) revision once the new revision cloned away — Bug #1 leaves it at 2");
        }
    }

    /// <summary>
    /// Diagnostic: does destroying a (non-cluster) Versioned entity free its ComponentCollection buffer?
    /// Used to isolate whether the cluster-destroy CC-leak is cluster-specific or general to Versioned destroy.
    /// </summary>
    [Test]
    public void Collection_Destroy_FreesBuffer()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(2);
            var e = new CompE_Eng(1);
            {
                using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);
                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }
            entityId = t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
            Assert.That(t.Commit(), Is.True);
        }

        ComponentCollection<int> handle;
        {
            using var t = dbe.CreateQuickTransaction();
            handle = t.Open(entityId).Read(CompAEArch.E).Collection;
            Assert.That(t.GetComponentCollectionRefCounter(ref handle), Is.EqualTo(1), "live buffer refcount 1");
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(entityId);
            Assert.That(t.Commit(), Is.True);
        }
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();

        {
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.GetComponentCollectionRefCounter(ref handle), Is.EqualTo(0),
                "destroying a Versioned entity must free its collection buffer");
        }
    }
}