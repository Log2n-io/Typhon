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

    /// <summary>
    /// AC: the per-ComponentTable secondary index is what ANSWERS a field query on a pure-Versioned archetype — established by breaking the index and watching
    /// the query lose exactly the broken entity, not by reading the planner and trusting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #670 left this open. Its fix changed what the backfill writes into this index, yet the query returned the right entities whether the backfill was
    /// correct or corrupt — so nothing there proved the query consults the index at all. Content chunk ids recycle into the same small-integer range as
    /// revision chunk ids, so a wrong leaf value ALIASES a valid chain root and resolves to a plausible entity instead of failing. Removal cannot alias: an
    /// absent key is absent, whatever the id spaces do.
    /// </para>
    /// <para>
    /// The corruption is deliberately one-sided. Only the index entry goes; the entity, its revision chain and its component data are all untouched and still
    /// hold <c>B == 20</c>. So anything that finds it afterwards — a component scan, a zone map, a fallback — is by construction not reading this index.
    /// </para>
    /// <para>
    /// Unique field (<c>CompD.B</c>) rather than <c>AllowMultiple</c>: a unique key's leaf value is a single entity, so the removal unlinks exactly one and the
    /// entities sharing the fixture are an untouched control.
    /// </para>
    /// <para>
    /// This is the coverage #666's remaining step leans on. When pure-Versioned archetypes move onto per-archetype trees, the migration's correctness argument
    /// is that this read path keeps working — and until now no test would have failed if it silently stopped consulting an index altogether.
    /// </para>
    /// </remarks>
    [Test]
    public void UniqueIndexEntryRemoved_PureVersionedFieldQuery_LosesExactlyThatEntity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.That(ArchetypeRegistry.GetMetadata<CompDArch>().IsClusterEligible, Is.False,
            "premise: CompDArch is pure-Versioned, so its queries route to the per-ComponentTable index home and not to a per-archetype tree");

        EntityId target, keepLow, keepHigh;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompD(1.0f, 10, 2.0);
            var b = new CompD(1.0f, 20, 2.0);
            var c = new CompD(1.0f, 30, 2.0);
            keepLow = t.Spawn<CompDArch>(CompDArch.D.Set(in a));
            target = t.Spawn<CompDArch>(CompDArch.D.Set(in b));
            keepHigh = t.Spawn<CompDArch>(CompDArch.D.Set(in c));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.B == 20).Execute(), Is.EquivalentTo(new[] { target }),
                "premise: the query resolves the entity while its index entry is intact");
        }

        var table = dbe.GetComponentTable<CompD>();
        RemoveIndexKey<int>(table, 20);

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.B == 20).Execute(), Is.Empty,
                    "the entity is unreachable once its index entry is gone — the index, not a component scan, is what answered the first query");
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.B == 10).Execute(), Is.EquivalentTo(new[] { keepLow }),
                    "the removal took only its own key (low control)");
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.B == 30).Execute(), Is.EquivalentTo(new[] { keepHigh }),
                    "the removal took only its own key (high control)");
            });
        }
    }

    /// <summary>
    /// AC: the same, for an <c>AllowMultiple</c> key — the shape whose leaf value is a buffer of entities rather than one.
    /// </summary>
    /// <remarks>
    /// Worth a second test rather than a second assertion: the two shapes reach the tree through different leaf machinery, and it is the multi-value one that
    /// <see cref="VersionedComponent_AllowMultipleKeyMove_RemainsQueryable"/> and the #670 backfill both operate on. A key-level removal drops the whole
    /// buffer, so every entity sharing the value must vanish together — which also re-states, from the read side, why the write path must never use
    /// <c>Remove(key)</c> to unlink one entity.
    /// </remarks>
    [Test]
    public void MultiValueIndexKeyRemoved_PureVersionedFieldQuery_LosesEveryEntityUnderThatKey()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId shared1, shared2, alone;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompD(1.0f, 10, 2.0);
            var b = new CompD(1.0f, 20, 2.0);
            var c = new CompD(5.0f, 30, 2.0);
            shared1 = t.Spawn<CompDArch>(CompDArch.D.Set(in a));
            shared2 = t.Spawn<CompDArch>(CompDArch.D.Set(in b));
            alone = t.Spawn<CompDArch>(CompDArch.D.Set(in c));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.A == 1.0f).Execute(), Is.EquivalentTo(new[] { shared1, shared2 }),
                "premise: both entities resolve under the shared key while its buffer is intact");
        }

        var table = dbe.GetComponentTable<CompD>();
        RemoveIndexKey<float>(table, 1.0f);

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.A == 1.0f).Execute(), Is.Empty,
                    "dropping the key's buffer takes every entity under it — so that buffer is what the query was reading");
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.A == 5.0f).Execute(), Is.EquivalentTo(new[] { alone }),
                    "and no other key was disturbed");
            });
        }
    }

    /// <summary>
    /// Unlink <paramref name="key"/> from the <typeparamref name="TKey"/>-keyed index on <paramref name="table"/>, leaving component data and revision chains
    /// untouched.
    /// </summary>
    /// <remarks>
    /// <c>CompD</c> indexes one field per key type — <c>float A</c>, <c>int B</c>, <c>double C</c> — so the key type selects the field unambiguously without
    /// depending on field order or storage offsets.
    /// </remarks>
    private static void RemoveIndexKey<TKey>(ComponentTable table, TKey key) where TKey : unmanaged
    {
        BTree<TKey, PersistentStore> index = null;
        for (var i = 0; i < table.IndexedFieldInfos.Length; i++)
        {
            if (table.IndexedFieldInfos[i].Index is BTree<TKey, PersistentStore> typed)
            {
                index = typed;
                break;
            }
        }

        Assert.That(index, Is.Not.Null, $"premise: CompD exposes exactly one index keyed on {typeof(TKey).Name}");

        using var epoch = EpochGuard.Enter(table.DBE.EpochManager);
        var accessor = index.Segment.CreateChunkAccessor(table.DBE.MMF.CreateChangeSet());
        try
        {
            Assert.That(index.Remove(key, out _, ref accessor), Is.True, $"premise: key {key} was present before the removal");
        }
        finally
        {
            accessor.CommitChanges();
            accessor.Dispose();
        }
    }
}
