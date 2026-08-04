using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Secondary-index behaviour for a Versioned component on a pure-Versioned archetype — cluster-backed like every other since #629.
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
    /// AC: on a cluster-backed archetype an unordered field query does NOT read the B+Tree — established by breaking the tree and watching the query answer
    /// correctly anyway, rather than by reading the planner and trusting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion is inverted from what it was, and the inversion is the finding. Before #629 this archetype was flat and the shared index really did answer
    /// the query, so removing a key lost exactly that entity. Now every archetype is cluster-backed and <c>ScanAllArchetypes</c> routes unordered field
    /// predicates to Path B — a zone-map-pruned scan over the cluster SoA — which never touches a tree. The index is still maintained and still load-bearing,
    /// but for ORDERED queries, FK reverse lookup and <c>EnumerateIndex</c>, not for this one.
    /// </para>
    /// <para>
    /// Worth keeping as a test rather than a comment, because it is the only direct evidence for it: the two paths return identical entities, so no ordinary
    /// assertion can tell them apart. Breaking the tree is what makes the routing observable. The same technique will catch the reverse regression — a change
    /// that quietly puts unordered queries back on the tree would start failing here.
    /// </para>
    /// <para>
    /// The corruption is deliberately one-sided. Only the index entry goes; the entity, its revision chain and its cluster slot are untouched and still hold
    /// <c>B == 20</c>. And removal cannot alias: chunk ids recycle into overlapping small-integer ranges, so a wrong leaf value resolves to a plausible entity
    /// instead of failing, whereas an absent key is absent whatever the id spaces do.
    /// </para>
    /// </remarks>
    [Test]
    public void UniqueIndexEntryRemoved_ClusterFieldQuery_StillAnswersFromTheSoaScan()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.That(ArchetypeRegistry.GetMetadata<CompDArch>().IsClusterEligible, Is.True,
            "premise: CompDArch is cluster-backed since #629, so its field queries scan cluster data rather than the index");

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
        RemoveIndexKey<int>(dbe, table, 20);

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.B == 20).Execute(), Is.EquivalentTo(new[] { target }),
                    "the entity survives the loss of its index entry — the cluster SoA scan, not the tree, is what answers an unordered field query");
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.B == 10).Execute(), Is.EquivalentTo(new[] { keepLow }),
                    "and the untouched keys are unaffected either way (low control)");
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.B == 30).Execute(), Is.EquivalentTo(new[] { keepHigh }),
                    "and the untouched keys are unaffected either way (high control)");

                // The other half of the claim: the tree IS what an ORDERED query reads, so the same corruption is visible there. Without this the test would
                // only show that one path ignores the index, not that the index is still doing a job.
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.B > 0).OrderByField<CompD, int>(d => d.B).ExecuteOrdered(),
                    Is.EquivalentTo(new[] { keepLow, keepHigh }),
                    "the ordered path merges the per-archetype B+Trees, so the unlinked entity is genuinely missing there");
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
    public void MultiValueIndexKeyRemoved_ClusterFieldQuery_StillAnswersFromTheSoaScan()
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
        RemoveIndexKey<float>(dbe, table, 1.0f);

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.A == 1.0f).Execute(), Is.EquivalentTo(new[] { shared1, shared2 }),
                    "dropping the whole buffer changes nothing for an unordered query — it reads the cluster SoA, not the tree");
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.A == 5.0f).Execute(), Is.EquivalentTo(new[] { alone }),
                    "and no other key was disturbed");

                // Ordered BY THE CORRUPTED FIELD — the ordered path merges the tree belonging to the OrderBy field, so ordering by B here would read B's intact
                // tree and prove nothing. Both entities under the dropped key vanish together, which is also why the write path must never use Remove(key) to
                // unlink a single entity from an AllowMultiple index.
                Assert.That(t.Query<CompDArch>().WhereField<CompD>(d => d.A > 0.0f).OrderByField<CompD, float>(d => d.A).ExecuteOrdered(),
                    Is.EquivalentTo(new[] { alone }),
                    "the ordered path lost both entities whose shared key buffer was dropped, and kept the untouched key");
            });
        }
    }

    /// <summary>
    /// Unlink <paramref name="key"/> from the <typeparamref name="TKey"/>-keyed index on <paramref name="table"/>, leaving component data and revision chains
    /// untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CompD</c> indexes one field per key type — <c>float A</c>, <c>int B</c>, <c>double C</c> — so the key type selects the field unambiguously without
    /// depending on field order or storage offsets.
    /// </para>
    /// <para>
    /// Searches the archetype's OWN trees, not <c>table.IndexedFieldInfos</c> (#629). Removing from the shared per-ComponentTable tree stopped proving
    /// anything the moment every archetype became cluster-backed: that tree holds no entries, so the removal would find nothing to unlink and the query would
    /// keep answering correctly — the test would fail on its own premise rather than demonstrate what the query reads.
    /// </para>
    /// </remarks>
    private static void RemoveIndexKey<TKey>(DatabaseEngine dbe, ComponentTable table, TKey key) where TKey : unmanaged
    {
        // Resolved through CompDArch by name, not by searching for "whichever archetype indexes CompD". Three archetypes hold this component, each with its own
        // tree, and a search returns the first — which is not the one these tests spawn into, so the removal would land in an empty tree and the premise assert
        // below would fire instead of the behaviour being tested.
        BTree<TKey, PersistentStore> index = null;
        for (var f = 0; f < table.IndexedFieldInfos.Length; f++)
        {
            if (IndexTestHelpers.ArchetypeIndex<CompDArch>(dbe, table, f) is BTree<TKey, PersistentStore> typed)
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
