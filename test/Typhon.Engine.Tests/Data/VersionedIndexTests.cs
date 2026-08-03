using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Secondary-index behaviour for a Versioned component on a NON-cluster (pure-Versioned) archetype — the population that still lives on the
/// per-ComponentTable index home.
/// </summary>
/// <remarks>
/// <para>
/// This fixture used to be almost entirely about the TAIL version-history machinery: temporal queries, tombstones, backfill, GC pruning. #666 deleted all of
/// it. <c>TemporalIndexQuery</c> and <c>TailGarbageCollector</c> had zero production callers, no public temporal API ever existed to reach them, and nothing
/// pruned the TAIL — so it was an unbounded write amplifier paid for on every AllowMultiple Versioned mutation. Its tests went with it: they were its only
/// callers, which is exactly what made the machinery removable.
/// </para>
/// <para>
/// What survives is the HEAD path, kept rather than folded into a broader fixture because it is the coverage #666's remaining step leans on — when
/// pure-Versioned archetypes move onto per-archetype trees, this is the behaviour that must not change.
/// </para>
/// </remarks>
class VersionedIndexTests : TestBase<VersionedIndexTests>
{
    /// <summary>
    /// Spawn, read, update, re-read a Versioned component carrying AllowMultiple indexed fields — the current-set path, untouched by the TAIL removal.
    /// </summary>
    [Test]
    public void VersionedComponent_SpawnUpdateRead_HeadPathUnchanged()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId e1Id;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            e1Id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        {
            using var t = dbe.CreateQuickTransaction();
            var read = t.Open(e1Id).Read(CompDArch.D);
            Assert.Multiple(() =>
            {
                Assert.That(read.A, Is.EqualTo(1.0f));
                Assert.That(read.B, Is.EqualTo(10));
                Assert.That(read.C, Is.EqualTo(2.0));
            });
        }

        {
            using var t = dbe.CreateQuickTransaction();
            ref var d = ref t.OpenMut(e1Id).Write(CompDArch.D);
            d = new CompD(5.0f, 20, 6.0);
            t.Commit();
        }

        {
            using var t = dbe.CreateQuickTransaction();
            var read = t.Open(e1Id).Read(CompDArch.D);
            Assert.Multiple(() =>
            {
                Assert.That(read.A, Is.EqualTo(5.0f));
                Assert.That(read.B, Is.EqualTo(20));
                Assert.That(read.C, Is.EqualTo(6.0));
            });
        }
    }

    /// <summary>
    /// AC: an indexed AllowMultiple field is still queryable after an update moves its key — the behaviour the TAIL was NOT providing.
    /// </summary>
    /// <remarks>
    /// Worth stating explicitly, because it is what makes the deletion safe rather than merely unused: the HEAD buffer alone answers every query the engine
    /// can express. The TAIL only ever served as-of-TSN queries, and no API existed to ask one.
    /// </remarks>
    [Test]
    public void VersionedComponent_AllowMultipleKeyMove_RemainsQueryable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId moved, sibling;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompD(1.0f, 10, 2.0);
            var b = new CompD(1.0f, 11, 2.0);
            moved = t.Spawn<CompDArch>(CompDArch.D.Set(in a));
            sibling = t.Spawn<CompDArch>(CompDArch.D.Set(in b));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.A == 1.0f).Count(), Is.EqualTo(2), "premise: both entities share key 1.0");
        }

        {
            using var t = dbe.CreateQuickTransaction();
            ref var d = ref t.OpenMut(moved).Write(CompDArch.D);
            d = new CompD(5.0f, 10, 2.0);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.A == 5.0f).Execute(), Is.EquivalentTo(new[] { moved }),
                    "the moved entity answers to its new key");
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.A == 1.0f).Execute(), Is.EquivalentTo(new[] { sibling }),
                    "and left the old one without taking its sibling — a plain Remove(key) would have dropped both");
            });
        }
    }

    /// <summary>
    /// AC: the AllowMultiple shape that used to allocate a version-history segment on every open no longer does.
    /// </summary>
    /// <remarks>
    /// Pins the removal rather than a behaviour. <c>CompD</c> is exactly the component that drove the TAIL allocation, so a reintroduced segment without a
    /// consumer shows up here first.
    /// </remarks>
    [Test]
    public void VersionedComponent_WithAllowMultipleIndex_HasNoVersionHistorySegment()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();
        Assert.That(ct.Definition.MultipleIndicesCount, Is.GreaterThan(0),
            "premise: CompD is the AllowMultiple shape that used to allocate a TAIL segment and its VSBS");
    }
}
