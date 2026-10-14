using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.QueryEnabled.SvRanked", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QcSvRanked
{
    [Index(AllowMultiple = true)] public int Tier;
    public int Score;

    public QcSvRanked(int tier, int score)
    {
        Tier = tier;
        Score = score;
    }
}

[Component("Typhon.Test.QueryEnabled.SvTag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QcSvTag
{
    public int Marker;
    public QcSvTag(int marker) => Marker = marker;
}

[Component("Typhon.Test.QueryEnabled.SvCode", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QcSvCode
{
    [Index] public int Code;
    public QcSvCode(int code) => Code = code;
}

[Component("Typhon.Test.QueryEnabled.Spatial", 1)]
[StructLayout(LayoutKind.Sequential)]
struct QcSpatial
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;
}

[Component("Typhon.Test.QueryEnabled.SpRanked", 1)]
[StructLayout(LayoutKind.Sequential)]
struct QcSpRanked
{
    [Index(AllowMultiple = true)] public int Tier;
    public QcSpRanked(int tier) => Tier = tier;
}

[Archetype]
class QcSpArch : Archetype<QcSpArch>
{
    public static readonly Comp<QcSpatial> Spatial = Register<QcSpatial>();
    public static readonly Comp<QcSpRanked> Ranked = Register<QcSpRanked>();
    public static readonly Comp<StatsTag> Tag = Register<StatsTag>();
}

[Archetype]
class QcSvArch : Archetype<QcSvArch>
{
    public static readonly Comp<QcSvRanked> Ranked = Register<QcSvRanked>();
    public static readonly Comp<QcSvTag> Tag = Register<QcSvTag>();
}

[Archetype]
class QcSvCodeArch : Archetype<QcSvCodeArch>
{
    public static readonly Comp<QcSvCode> Code = Register<QcSvCode>();
    public static readonly Comp<QcSvTag> Tag = Register<QcSvTag>();
}

/// <summary>
/// Rule <b>QENABLE-01</b> (<c>rules/ecs.md</c>): a field predicate on component T matches an entity only when T is present and enabled at the query's
/// snapshot — or present and disabled when the query names <c>Disabled&lt;T&gt;()</c> — and every execution path answers the same (#1259).
/// </summary>
/// <remarks>
/// <para>The targeted paths — the selective B+Tree scan, the SoA scan, the ordered merge — never read the enabled bits, and ignored <c>Enabled&lt;T&gt;()</c> /
/// <c>Disabled&lt;T&gt;()</c> once a field predicate was present, while views, opaque <c>Where</c>, spatial queries and pending spawns honoured them: one
/// query answered differently by path, by terminal, and before and after its own commit.</para>
/// <para>A Versioned component the spawn never supplied is absent (STAGE-02) and had an index entry under the slot's bytes, which outlived its first supply
/// next to the real key. A SingleVersion one is present with a zero value and disabled — the record cannot tell it from a disabled one — and a unique
/// index on it let one such entity hold key 0 against every later spawn.</para>
/// </remarks>
[TestFixture]
[NonParallelizable] // QueryPathProbe.Forced is a static every engine reads.
class QueryEnabledContractTests : TestBase<QueryEnabledContractTests>
{
    private static readonly (string Name, ClusterScanPath Path)[] Paths =
        [("the planner's path", ClusterScanPath.Planner), ("the index path", ClusterScanPath.Selective), ("the scan path", ClusterScanPath.FullScan)];

    private DatabaseEngine OpenEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.RegisterComponentFromAccessor<QcSvRanked>();
        dbe.RegisterComponentFromAccessor<QcSvTag>();
        dbe.RegisterComponentFromAccessor<QcSvCode>();
        dbe.RegisterComponentFromAccessor<QcSpatial>();
        dbe.RegisterComponentFromAccessor<QcSpRanked>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(-1000, -1000), worldMax: new Vector2(1000, 1000), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private readonly record struct Answers(int Count, bool Any, HashSet<EntityId> Ids, List<EntityId> Ordered);

    /// <summary>The Versioned archetype's query for Tier = <paramref name="tier"/>, through every terminal.</summary>
    private static Answers VersionedTier(Transaction tx, int tier, Func<EcsQuery<StatsArch>, EcsQuery<StatsArch>> shape = null)
    {
        EcsQuery<StatsArch> Q()
        {
            var q = tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == tier);
            return shape == null ? q : shape(q);
        }

        return new Answers(Q().Count(), Q().Any(), Q().Execute(), Q().OrderByField<StatsRanked, int>(x => x.Tier).ExecuteOrdered());
    }

    /// <summary>The SingleVersion archetype's query for Tier = <paramref name="tier"/>, through every terminal.</summary>
    private static Answers SingleVersionTier(Transaction tx, int tier, Func<EcsQuery<QcSvArch>, EcsQuery<QcSvArch>> shape = null)
    {
        EcsQuery<QcSvArch> Q()
        {
            var q = tx.Query<QcSvArch>().WhereField<QcSvRanked>(x => x.Tier == tier);
            return shape == null ? q : shape(q);
        }

        return new Answers(Q().Count(), Q().Any(), Q().Execute(), Q().OrderByField<QcSvRanked, int>(x => x.Tier).ExecuteOrdered());
    }

    /// <summary>Every path, every terminal: the entity matches exactly when <paramref name="expected"/>, and nothing else does.</summary>
    private static void OnEveryPath(DatabaseEngine dbe, Func<Transaction, Answers> query, EntityId id, bool expected, string what, Transaction inTx = null,
        bool ordered = true, bool scanOnly = false)
    {
        foreach (var (name, path) in Paths)
        {
            if (scanOnly && path != ClusterScanPath.FullScan)
            {
                continue;
            }

            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            try
            {
                Answers a;
                if (inTx != null)
                {
                    a = query(inTx);
                }
                else
                {
                    using var tx = dbe.CreateQuickTransaction();
                    a = query(tx);
                }

                var at = $"{what}, on {name}";
                Assert.That(a.Count, Is.EqualTo(expected ? 1 : 0), $"{at}: Count");
                Assert.That(a.Any, Is.EqualTo(expected), $"{at}: Any");
                Assert.That(a.Ids, expected ? Is.EquivalentTo(new[] { id }) : Is.Empty, $"{at}: Execute");
                if (ordered)
                {
                    Assert.That(a.Ordered, expected ? Is.EqualTo(new[] { id }) : Is.Empty, $"{at}: ExecuteOrdered");
                }
            }
            finally
            {
                QueryPathProbe.Reset();
            }
        }
    }

    private static EntityId SpawnVersioned(DatabaseEngine dbe, int? tier)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tier.HasValue
            ? tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(tier.Value, 1)), StatsArch.Tag.Set(new StatsTag(1)))
            : tx.Spawn<StatsArch>(StatsArch.Tag.Set(new StatsTag(1)));
        tx.Commit();
        return id;
    }

    private EntityId SpawnSingleVersion(DatabaseEngine dbe, int? tier)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tier.HasValue
            ? tx.Spawn<QcSvArch>(QcSvArch.Ranked.Set(new QcSvRanked(tier.Value, 1)), QcSvArch.Tag.Set(new QcSvTag(1)))
            : tx.Spawn<QcSvArch>(QcSvArch.Tag.Set(new QcSvTag(1)));
        tx.Commit();
        return id;
    }

    private long _tick;

    private void Committed(DatabaseEngine dbe, Action<Transaction> change)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            change(tx);
            Assert.That(tx.Commit(), Is.True, "premise: the change commits");
        }

        // A SingleVersion index is maintained at the tick fence.
        dbe.WriteTickFence(++_tick);
    }

    private static List<long> TierEntries<TComp>(DatabaseEngine dbe, int tier) where TComp : unmanaged
    {
        using var tx = dbe.CreateQuickTransaction();
        var found = new List<long>();
        using var e = tx.EnumerateIndex<TComp, int>(dbe.GetIndexRef<TComp, int>(IndexOf<TComp>()), tier, tier);
        while (e.MoveNext())
        {
            found.Add(e.CurrentEntityPK);
        }

        return found;
    }

    private static System.Linq.Expressions.Expression<Func<TComp, int>> IndexOf<TComp>() where TComp : unmanaged
    {
        if (typeof(TComp) == typeof(StatsRanked))
        {
            return (System.Linq.Expressions.Expression<Func<TComp, int>>)(object)(System.Linq.Expressions.Expression<Func<StatsRanked, int>>)(x => x.Tier);
        }

        return (System.Linq.Expressions.Expression<Func<TComp, int>>)(object)(System.Linq.Expressions.Expression<Func<QcSvRanked, int>>)(x => x.Tier);
    }

    // ── The per-entity paths: Where<T>, the transaction's own pending spawns, spatial ──────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Where_OnADisabledComponent_MatchesOnlyWhenDisabledIsNamed()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        var absent = SpawnVersioned(dbe, null);
        Committed(dbe, tx => tx.OpenMut(id).Disable(StatsArch.Ranked));

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<StatsArch>().Where<StatsRanked>(x => x.Tier == 5).Execute(), Is.Empty, "a Where on a disabled component matched");
        Assert.That(tx.Query<StatsArch>().Disabled<StatsRanked>().Where<StatsRanked>(x => x.Tier == 5).Execute(), Is.EquivalentTo(new[] { id }),
            "a Where on a component the query names Disabled did not read it");
        Assert.That(tx.Query<StatsArch>().Disabled<StatsRanked>().Where<StatsRanked>(x => x.Tier == 0).Execute(), Is.Empty,
            "a Where read an absent component as zero");
        Assert.That(absent.IsNull, Is.False);
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void APendingSpawn_HonoursEnabledAndDisabledBesideAFieldPredicate()
    {
        using var dbe = OpenEngine();
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(5, 1)), StatsArch.Tag.Set(new StatsTag(1)));

        EcsQuery<StatsArch> Q() => tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5);
        Assert.That(Q().Execute(), Is.EquivalentTo(new[] { id }), "premise: the pending spawn matches");
        Assert.That(Q().Enabled<StatsTag>().Execute(), Is.EquivalentTo(new[] { id }), "Enabled<U> on an enabled U");
        Assert.That(Q().Disabled<StatsTag>().Execute(), Is.Empty, "Disabled<U> on an enabled U matched the pending spawn");
        Assert.That(Q().Disabled<StatsTag>().Count(), Is.Zero, "Disabled<U> on an enabled U, counted");

        tx.OpenMut(id).Disable(StatsArch.Ranked);
        Assert.That(Q().Execute(), Is.Empty, "a pending spawn's disabled component matched");
        Assert.That(Q().Disabled<StatsRanked>().Execute(), Is.EquivalentTo(new[] { id }), "Disabled<T> on the pending spawn's disabled T");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void ASpatialQuery_HonoursEnabledAndDisabled_AndItsFieldPredicateTheContract()
    {
        using var dbe = OpenEngine();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var bounds = new AABB3F { MinX = 10, MinY = 10, MinZ = 0, MaxX = 20, MaxY = 20, MaxZ = 1 };
            id = tx.Spawn<QcSpArch>(QcSpArch.Spatial.Set(new QcSpatial { Bounds = bounds }),
                QcSpArch.Ranked.Set(new QcSpRanked(5)), QcSpArch.Tag.Set(new StatsTag(1)));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(++_tick);
        EcsQuery<QcSpArch> Box(Transaction tx) => tx.Query<QcSpArch>().WhereInAABB<QcSpatial>(0, 0, -10, 100, 100, 10);

        // The transaction's own first supply: absent to its own field queries, the spatial ones too, until it commits. Out of the box above.
        EcsQuery<QcSpArch> FarBox(Transaction tx) => tx.Query<QcSpArch>().WhereInAABB<QcSpatial>(250, 250, -10, 400, 400, 10);
        EntityId bare;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var far = new AABB3F { MinX = 300, MinY = 300, MinZ = 0, MaxX = 310, MaxY = 310, MaxZ = 1 };
            bare = tx.Spawn<QcSpArch>(QcSpArch.Spatial.Set(new QcSpatial { Bounds = far }), QcSpArch.Tag.Set(new StatsTag(2)));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(++_tick);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(bare).Enable(QcSpArch.Ranked, new QcSpRanked(7));
            Assert.That(FarBox(tx).WhereField<QcSpRanked>(x => x.Tier == 7).Execute(), Is.Empty, "a spatial field query saw its own first supply");
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(FarBox(tx).WhereField<QcSpRanked>(x => x.Tier == 7).Execute(), Is.EquivalentTo(new[] { bare }), "the committed supply");
        }

        // The transaction's own destroy: the spatial index still holds the entity, which no longer exists for it.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            Assert.That(Box(tx).Enabled<StatsTag>().Execute(), Is.Empty, "an own destroyed entity under Enabled<U>");
            Assert.That(Box(tx).Where<StatsTag>(_ => true).Execute(), Is.Empty, "an own destroyed entity under Where<T>");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(Box(tx).Execute(), Is.EquivalentTo(new[] { id }), "premise: the entity is in the box");
            Assert.That(Box(tx).Disabled<StatsTag>().Execute(), Is.Empty, "a spatial query ignored Disabled<U>");
            Assert.That(Box(tx).Disabled<StatsTag>().Count(), Is.Zero, "a spatial query ignored Disabled<U>, counted");
        }

        Committed(dbe, tx => tx.OpenMut(id).Disable(QcSpArch.Tag));
        Committed(dbe, tx => tx.OpenMut(id).Disable(QcSpArch.Ranked));
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(Box(tx).Enabled<StatsTag>().Execute(), Is.Empty, "a spatial query ignored Enabled<U>");
            Assert.That(Box(tx).Disabled<StatsTag>().Execute(), Is.EquivalentTo(new[] { id }), "Disabled<U> on a disabled U");
            Assert.That(Box(tx).WhereField<QcSpRanked>(x => x.Tier == 5).Execute(), Is.Empty, "a spatial field predicate on a disabled component matched");
            Assert.That(Box(tx).WhereField<QcSpRanked>(x => x.Tier == 5).Disabled<QcSpRanked>().Execute(), Is.EquivalentTo(new[] { id }),
                "a spatial field predicate did not read the component the query names Disabled");
        }
    }

    // ── Versioned ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AnAbsentComponent_MatchesNoFieldPredicate_AndNoIndexReadReturnsIt()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        OnEveryPath(dbe, tx => VersionedTier(tx, 0), id, false, "an absent component, at the slot's bytes");
        OnEveryPath(dbe, tx => VersionedTier(tx, 0, q => q.Disabled<StatsRanked>()), id, false, "an absent component, Disabled<T> named");
        Assert.That(TierEntries<StatsRanked>(dbe, 0), Is.Empty, "an index read returns an absent component");
    }

    /// <summary>An absent component in a recycled slot: the slot once held Tier 77, and nothing under 77 or 0 is the new entity.</summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AnAbsentComponentInARecycledSlot_MatchesNeitherTheOldKeyNorZero()
    {
        using var dbe = OpenEngine();
        var old = SpawnVersioned(dbe, 77);
        Committed(dbe, tx => tx.Destroy(old));
        for (var p = 0; p < 3; p++)
        {
            dbe.FlushDeferredCleanups();
        }

        var id = SpawnVersioned(dbe, null);
        OnEveryPath(dbe, tx => VersionedTier(tx, 77), id, false, "an absent component in a recycled slot, at the old key");
        OnEveryPath(dbe, tx => VersionedTier(tx, 0), id, false, "an absent component in a recycled slot, at zero");
    }

    /// <summary>
    /// One cluster, entities in every state side by side: each path counts exactly the enabled ones, so no slot bit is read for its neighbour.
    /// </summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AClusterOfMixedStates_CountsExactlyTheEnabledOnes()
    {
        using var dbe = OpenEngine();
        var enabled = new List<EntityId>();
        var disabled = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 30; i++)
            {
                var id = i % 3 == 2
                    ? tx.Spawn<StatsArch>(StatsArch.Tag.Set(new StatsTag(i)))
                    : tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(5, i)), StatsArch.Tag.Set(new StatsTag(i)));
                (i % 3 == 0 ? enabled : i % 3 == 1 ? disabled : null)?.Add(id);
            }

            tx.Commit();
        }

        Committed(dbe, tx =>
        {
            foreach (var id in disabled)
            {
                tx.OpenMut(id).Disable(StatsArch.Ranked);
            }
        });

        foreach (var (name, path) in Paths)
        {
            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            try
            {
                using var tx = dbe.CreateQuickTransaction();
                Assert.That(tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).Execute(), Is.EquivalentTo(enabled), $"enabled ones, on {name}");
                Assert.That(tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).Disabled<StatsRanked>().Execute(), Is.EquivalentTo(disabled),
                    $"disabled ones, on {name}");
                Assert.That(tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).OrderByField<StatsRanked, int>(x => x.Tier).ExecuteOrdered(),
                    Is.EquivalentTo(enabled), $"enabled ones, ordered, on {name}");
            }
            finally
            {
                QueryPathProbe.Reset();
            }
        }
    }

    /// <summary>
    /// A reader older than a disable and a destroy committed after it: the destroyed slot is hidden from it until the cleanup (VIS-09), decided from the
    /// entity's record — whose bits are the disable's, newer than the reader. The reader still sees the component enabled. On the SoA scan only, the one
    /// path that visits a hidden slot: the selective scan and the ordered merge lose a destroyed entity for an older snapshot with no disable at all
    /// (#1347).
    /// </summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AReaderOlderThanADisableAndADestroy_StillFindsTheEntity()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        using var reader = dbe.CreateQuickTransaction();
        Committed(dbe, tx => tx.OpenMut(id).Disable(StatsArch.Ranked));
        Committed(dbe, tx => tx.Destroy(id));
        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, true, "a snapshot older than a disable and a destroy", reader, ordered: false, scanOnly: true);
    }

    /// <summary>
    /// A spawn whose component is disabled in the same transaction: a view built before it does not take it in, as a view built after does not.
    /// </summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_ASpawnDisabledInItsOwnTransaction_IsNotAddedToAnEarlierView()
    {
        using var dbe = OpenEngine();
        using var builder = dbe.CreateQuickTransaction();
        using var view = builder.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).ToView();
        using (var first = dbe.CreateQuickTransaction())
        {
            view.Refresh(first);
        }

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(5, 1)), StatsArch.Tag.Set(new StatsTag(1)));
            tx.OpenMut(id).Disable(StatsArch.Ranked);
            Assert.That(tx.Commit(), Is.True);
        }

        using (var after = dbe.CreateQuickTransaction())
        {
            view.Refresh(after);
            Assert.That(view.Contains((long)id.RawValue), Is.False, "a view built before the spawn holds a component the spawn left disabled");
        }
    }

    /// <summary>A view that names Disabled&lt;T&gt; beside the field predicate follows the disables, as its query does.</summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AViewNamingDisabled_FollowsTheQuery()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        using var builder = dbe.CreateQuickTransaction();
        using var view = builder.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).Disabled<StatsRanked>().ToView();
        using (var first = dbe.CreateQuickTransaction())
        {
            view.Refresh(first);
            Assert.That(view.Contains((long)id.RawValue), Is.False, "premise: an enabled entity is not in a Disabled<T> view");
        }

        Committed(dbe, tx => tx.OpenMut(id).Disable(StatsArch.Ranked));
        using (var after = dbe.CreateQuickTransaction())
        {
            view.Refresh(after);
            Assert.That(view.Contains((long)id.RawValue), Is.True, "a Disabled<T> view did not take in the disabled entity");
        }

        Committed(dbe, tx => tx.OpenMut(id).Enable(StatsArch.Ranked));
        using (var after = dbe.CreateQuickTransaction())
        {
            view.Refresh(after);
            Assert.That(view.Contains((long)id.RawValue), Is.False, "a Disabled<T> view kept the re-enabled entity");
        }
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_ADisabledComponent_MatchesOnlyWhenDisabledIsNamed()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        Committed(dbe, tx => tx.OpenMut(id).Disable(StatsArch.Ranked));
        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, false, "a disabled component");
        OnEveryPath(dbe, tx => VersionedTier(tx, 5, q => q.Enabled<StatsRanked>()), id, false, "a disabled component, Enabled<T> named");
        OnEveryPath(dbe, tx => VersionedTier(tx, 5, q => q.Disabled<StatsRanked>()), id, true, "a disabled component, Disabled<T> named");
        Assert.That(TierEntries<StatsRanked>(dbe, 5), Is.EqualTo(new[] { (long)id.RawValue }), "a disabled component keeps its index entry");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AnEnabledComponent_MatchesUnlessDisabledIsNamed()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, true, "an enabled component");
        OnEveryPath(dbe, tx => VersionedTier(tx, 5, q => q.Disabled<StatsRanked>()), id, false, "an enabled component, Disabled<T> named");
        OnEveryPath(dbe, tx => VersionedTier(tx, 5, q => q.Disabled<StatsTag>()), id, false, "an enabled component, another component's Disabled named");
        OnEveryPath(dbe, tx => VersionedTier(tx, 5, q => q.Enabled<StatsTag>()), id, true, "an enabled component, another component's Enabled named");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AComponentSuppliedMidLife_IsIndexedUnderItsValueAlone()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        Committed(dbe, tx => tx.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 0)));
        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, true, "a component supplied mid-life, at its value");
        OnEveryPath(dbe, tx => VersionedTier(tx, 0), id, false, "a component supplied mid-life, at the slot's old bytes");
        Assert.That(TierEntries<StatsRanked>(dbe, 0), Is.Empty, "the spawn-time entry outlived the first supply");
        Committed(dbe, tx => tx.Destroy(id));
        Assert.That(TierEntries<StatsRanked>(dbe, 5), Is.Empty, "the destroy left the supplied key");
    }

    /// <summary>The chain root <paramref name="id"/>'s record names for its <see cref="StatsRanked"/>, read from the EntityMap.</summary>
    private static unsafe int RankedChainRoot(DatabaseEngine dbe, EntityId id)
    {
        var meta = dbe.GetMetaByRouting(id.ArchetypeId);
        Assert.That(meta.TryGetSlot(ArchetypeRegistry.GetComponentTypeId<StatsRanked>(), out var slot), Is.True);
        var es = dbe._archetypeStates[meta.ArchetypeId];
        var record = stackalloc byte[meta._entityRecordSize];
        var accessor = es.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            Assert.That(es.EntityMap.TryGet(id.EntityKey, record, ref accessor), Is.True);
            return ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, meta.ClusterLayout.SlotToVersionedIndex[slot]);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Whether the <see cref="StatsRanked"/> chain at <paramref name="root"/> is still allocated, and whether its newest published revision is a
    /// tombstone.
    /// </summary>
    private static (bool Live, bool NewestIsTombstone) RankedChain(DatabaseEngine dbe, int root)
    {
        var segment = dbe.GetComponentTable<StatsRanked>().CompRevTableSegment;
        if (!segment.IsChunkLive(root))
        {
            return (false, false);
        }

        var accessor = segment.CreateChunkAccessor();
        try
        {
            var first = accessor.GetChunk<CompRevStorageHeader>(root).FirstItemIndex;
            var count = accessor.GetChunk<CompRevStorageHeader>(root).ItemCount;
            long newest = -1;
            var newestChunk = -1;
            for (var i = 0; i < count; i++)
            {
                var element = ComponentRevisionManager.GetRevisionElement(ref accessor, root, (short)(first + i)).Element;
                if (!element.IsVoid && !element.IsolationFlag && element.TSN > newest)
                {
                    newest = element.TSN;
                    newestChunk = element.ComponentChunkId;
                }
            }

            return (true, newestChunk == 0);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>After two supplies of one absent component: one value everywhere — the point read, every query path, the index.</summary>
    private static void AssertSuppliedOnce(DatabaseEngine dbe, EntityId id, int value, int lost)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(id).Read(StatsArch.Ranked).Tier, Is.EqualTo(value), "the point read");
        }

        OnEveryPath(dbe, tx => VersionedTier(tx, value), id, true, "the winning supply's key");
        OnEveryPath(dbe, tx => VersionedTier(tx, lost), id, false, "the losing supply's key");
        Assert.That(TierEntries<StatsRanked>(dbe, lost), Is.Empty, "the losing supply's key outlived it");
        Assert.That(TierEntries<StatsRanked>(dbe, value), Is.EqualTo(new[] { (long)id.RawValue }), "the winning supply's key");
        Assert.That(dbe.FirstSupplyClaims, Is.Empty, "a first-supply claim outlived its commit");
    }

    /// <summary>
    /// The losing supply's join fails after it added its revision to the winner's chain: its commit fails, the winner's chain and value stand.
    /// </summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AFirstSupplyWhoseJoinFails_LeavesTheWinnersChain()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        using (var first = dbe.CreateQuickTransaction())
        using (var second = dbe.CreateQuickTransaction())
        {
            first.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 1));
            second.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(7, 1));
            Assert.That(first.Commit(), Is.True, "premise: the first supply commits");
            var root = RankedChainRoot(dbe, id);
            dbe.FirstSupplyJoinProbe = () => throw new InvalidOperationException("staged join failure");
            try
            {
                Assert.Throws<InvalidOperationException>(() => second.Commit(), "premise: the join fails");
            }
            finally
            {
                dbe.FirstSupplyJoinProbe = null;
            }

            Assert.That(RankedChainRoot(dbe, id), Is.EqualTo(root), "the record's root moved");
            Assert.That(RankedChain(dbe, root).Live, Is.True, "the failed join's rollback freed the winner's chain");
        }

        AssertSuppliedOnce(dbe, id, 5, 7);
        Committed(dbe, tx => tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(9, 1)));
        AssertSuppliedOnce(dbe, id, 9, 5);
    }

    /// <summary>A supply the losing transaction then destroyed joins the winner's chain as the destroy: the entity is gone, no value survives it.</summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AFirstSupplyDestroyedInItsTransaction_JoinsAsTheDestroy()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        using (var first = dbe.CreateQuickTransaction())
        using (var second = dbe.CreateQuickTransaction())
        {
            first.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 1));
            second.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(7, 1));
            second.Destroy(id);
            Assert.That(first.Commit(), Is.True, "premise: the first supply commits");
            var root = RankedChainRoot(dbe, id);
            using var between = dbe.CreateQuickTransaction();
            Assert.That(second.Commit(), Is.True, "premise: the destroying supply commits");
            Assert.That(between.Open(id).Read(StatsArch.Ranked).Tier, Is.EqualTo(5), "a snapshot between the two commits");
            Assert.That(RankedChain(dbe, root), Is.EqualTo((true, true)), "the destroy did not reach the winner's chain as its newest revision");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.TryOpen(id, out _), Is.False, "the destroyed entity is still there");
        }

        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, false, "the winner's key after the destroy");
        OnEveryPath(dbe, tx => VersionedTier(tx, 7), id, false, "the destroying supply's key");
        Assert.That(dbe.FirstSupplyClaims, Is.Empty, "a first-supply claim outlived its commit");
    }

    /// <summary>
    /// A commit's first supplies are claimed in one global order, whatever order they were written in: crossed commits cannot wait on each other.
    /// </summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void FirstSupplies_AreClaimedInOneOrder()
    {
        using var dbe = OpenEngine();
        var a = SpawnVersioned(dbe, null);
        var b = SpawnVersioned(dbe, null);
        var claimed = new List<long>();
        dbe.FirstSupplyClaimedProbe = (pk, _) => claimed.Add(pk);
        try
        {
            Committed(dbe, tx =>
            {
                tx.OpenMut(b).Enable(StatsArch.Ranked, new StatsRanked(5, 1));
                tx.OpenMut(a).Enable(StatsArch.Ranked, new StatsRanked(5, 2));
            });
        }
        finally
        {
            dbe.FirstSupplyClaimedProbe = null;
        }

        var expected = new List<long> { (long)a.RawValue, (long)b.RawValue };
        expected.Sort();
        Assert.That(claimed, Is.EqualTo(expected), "the claims were taken in the order the supplies were written");
        Assert.That(dbe.FirstSupplyClaims, Is.Empty);
    }

    /// <summary>The first supply fails before its append while a second waits on its claim: the claim goes back, and the second commits as the first.</summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AFirstSupplyWaitingOnACommitThatFails_CommitsAsTheFirst()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        using var ready = new System.Threading.ManualResetEventSlim();
        using var go = new System.Threading.ManualResetEventSlim();
        using var waiting = new System.Threading.ManualResetEventSlim();
        Exception workerFailure = null;
        var secondCommitted = false;
        var worker = new System.Threading.Thread(() =>
        {
            try
            {
                using var second = dbe.CreateQuickTransaction();
                second.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(7, 1));
                ready.Set();
                go.Wait();
                secondCommitted = second.Commit();
            }
            catch (Exception e)
            {
                workerFailure = e;
                waiting.Set();
            }
        });

        using (var first = dbe.CreateQuickTransaction())
        {
            first.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 1));
            worker.Start();
            Assert.That(ready.Wait(5_000), Is.True, "premise: the second supply is written");
            dbe.FirstSupplyWaitProbe = () => waiting.Set();
            dbe.CommitBeforeAppendProbe = () =>
            {
                dbe.CommitBeforeAppendProbe = null;
                go.Set();
                Assert.That(waiting.Wait(5_000), Is.True, "premise: the second supply waits for the first's claim");
                throw new InvalidOperationException("staged failure before the append");
            };
            try
            {
                Assert.Throws<InvalidOperationException>(() => first.Commit(), "premise: the first supply fails");
            }
            finally
            {
                dbe.CommitBeforeAppendProbe = null;
                go.Set();
            }
        }

        Assert.That(worker.Join(10_000), Is.True, "the second supply never finished");
        dbe.FirstSupplyWaitProbe = null;
        Assert.That(workerFailure, Is.Null, "the second supply failed");
        Assert.That(secondCommitted, Is.True, "premise: the second supply commits");
        AssertSuppliedOnce(dbe, id, 7, 5);
    }

    /// <summary>Rows the enabled gate refuses are not counted against an ordered query's Skip or Take.</summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void AnOrderedPage_SkipsAndTakesOnlyEnabledRows()
    {
        using var dbe = OpenEngine();
        var ids = new List<EntityId>();
        for (var tier = 1; tier <= 10; tier++)
        {
            ids.Add(SpawnVersioned(dbe, tier));
        }

        Committed(dbe, tx =>
        {
            tx.OpenMut(ids[1]).Disable(StatsArch.Ranked);   // tier 2
            tx.OpenMut(ids[2]).Disable(StatsArch.Ranked);   // tier 3
        });

        using var reader = dbe.CreateQuickTransaction();
        Assert.That(reader.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier >= 1).OrderByField<StatsRanked, int>(x => x.Tier).Skip(2).Take(3)
            .ExecuteOrdered(), Is.EqualTo(new[] { ids[4], ids[5], ids[6] }), "ascending: tiers 5, 6, 7");
        Assert.That(reader.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier >= 1).OrderByFieldDescending<StatsRanked, int>(x => x.Tier).Skip(6)
            .Take(3).ExecuteOrdered(), Is.EqualTo(new[] { ids[3], ids[0] }), "descending: tiers 4, 1");
    }


    /// <summary>
    /// Two transactions supply one absent component concurrently: the later commit wins, as for any write-write conflict — its revision joins the chain
    /// the first published, and its value is the one the read, the slot and the index hold.
    /// </summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_TwoConcurrentFirstSupplies_TheLaterCommitWins()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        using (var first = dbe.CreateQuickTransaction())
        using (var second = dbe.CreateQuickTransaction())
        {
            first.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 1));
            second.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(7, 1));

            // The claim goes only once the root is published: a supply waiting for it must find the root, or it commits a chain of its own.
            var rootAtRelease = -1;
            dbe.FirstSupplyReleaseProbe = () => rootAtRelease = RankedChainRoot(dbe, id);
            try
            {
                Assert.That(first.Commit(), Is.True, "premise: the first supply commits");
            }
            finally
            {
                dbe.FirstSupplyReleaseProbe = null;
            }

            Assert.That(rootAtRelease, Is.Not.EqualTo(-1), "premise: the first supply released a claim");
            Assert.That(rootAtRelease, Is.Not.Zero, "the first supply released its claim before publishing its chain root");
            Assert.That(second.Commit(), Is.True, "premise: the second supply commits");
        }

        AssertSuppliedOnce(dbe, id, 7, 5);
    }

    /// <summary>The losing supply's conflict handler sees the winner's value as committed, its own as committing, and no read value.</summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_TwoConcurrentFirstSupplies_TheHandlerDecides()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        int committed = -1, committing = -1;
        var readAbsent = false;
        using (var first = dbe.CreateQuickTransaction())
        using (var second = dbe.CreateQuickTransaction())
        {
            first.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 1));
            second.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(7, 1));
            Assert.That(first.Commit(), Is.True, "premise: the first supply commits");
            Assert.That(second.Commit((ref ConcurrencyConflictSolver s) =>
            {
                committed = s.CommittedData<StatsRanked>().Tier;
                committing = s.CommittingData<StatsRanked>().Tier;
                readAbsent = s.ReadWasAbsent;
                s.TakeCommitted<StatsRanked>();
            }), Is.True, "premise: the second supply commits");
        }

        Assert.That((committed, committing, readAbsent), Is.EqualTo((5, 7, true)), "what the handler was shown");
        AssertSuppliedOnce(dbe, id, 5, 7);
    }

    /// <summary>A first supply that aborts leaves no root: the next supply commits as the first.</summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AFirstSupplyAfterAnAbortedOne_CommitsAsTheFirst()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        using (var aborted = dbe.CreateQuickTransaction())
        {
            aborted.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 1));
            aborted.Rollback();
        }

        Committed(dbe, tx => tx.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(7, 1)));
        AssertSuppliedOnce(dbe, id, 7, 5);
    }

    /// <summary>
    /// The second supply commits while the first is between its append and its publish: it waits for the first's claim, then joins the chain it published.
    /// </summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AFirstSupplyCommittingWhileAnotherPublishes_WaitsAndJoinsItsChain()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        using var ready = new System.Threading.ManualResetEventSlim();
        using var go = new System.Threading.ManualResetEventSlim();
        using var waiting = new System.Threading.ManualResetEventSlim();
        Exception workerFailure = null;
        var secondCommitted = false;
        var worker = new System.Threading.Thread(() =>
        {
            try
            {
                using var second = dbe.CreateQuickTransaction();
                second.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(7, 1));
                ready.Set();
                go.Wait();
                secondCommitted = second.Commit();
            }
            catch (Exception e)
            {
                workerFailure = e;
                waiting.Set();
            }
        });

        using (var first = dbe.CreateQuickTransaction())
        {
            first.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 1));
            worker.Start();
            Assert.That(ready.Wait(5_000), Is.True, "premise: the second supply is written");
            dbe.FirstSupplyWaitProbe = () => waiting.Set();
            dbe.CommitAfterAppendProbe = () =>
            {
                dbe.CommitAfterAppendProbe = null;
                go.Set();
                Assert.That(waiting.Wait(5_000), Is.True, "premise: the second supply waits for the first's claim");
            };
            try
            {
                Assert.That(first.Commit(), Is.True, "premise: the first supply commits");
            }
            finally
            {
                dbe.CommitAfterAppendProbe = null;
                go.Set();
            }
        }

        Assert.That(worker.Join(10_000), Is.True, "the second supply never finished");
        dbe.FirstSupplyWaitProbe = null;
        Assert.That(workerFailure, Is.Null, "the second supply failed");
        Assert.That(secondCommitted, Is.True, "premise: the second supply commits");
        AssertSuppliedOnce(dbe, id, 7, 5);
    }

    /// <summary>
    /// A reader older than a first supply: the component is absent at its snapshot, so <c>Disabled&lt;T&gt;()</c> does not match it — though the record names a
    /// chain now, and the slot holds the supplied value.
    /// </summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AReaderOlderThanAFirstSupply_SeesTheComponentAbsent()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        using var reader = dbe.CreateQuickTransaction();
        Committed(dbe, tx => tx.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 1)));
        Committed(dbe, tx => tx.OpenMut(id).Disable(StatsArch.Ranked));
        OnEveryPath(dbe, tx => VersionedTier(tx, 5, q => q.Disabled<StatsRanked>()), id, false, "Disabled<T> at a snapshot before the first supply",
            reader);
        OnEveryPath(dbe, tx => VersionedTier(tx, 5, q => q.Disabled<StatsRanked>()), id, true, "Disabled<T> after the supply and the disable");
    }

    /// <summary>An entity the transaction destroyed is gone for its own field queries, as for its point reads — on every path and terminal.</summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_ATransactionsOwnDestroy_HidesTheEntityFromItsFieldQueries()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        var other = SpawnVersioned(dbe, 5);
        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(id);
        foreach (var (name, path) in Paths)
        {
            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            try
            {
                var a = VersionedTier(tx, 5);
                Assert.That((a.Count, a.Ids, a.Ordered), Is.EqualTo((1, new HashSet<EntityId> { other }, new List<EntityId> { other }))
                    .Using(new AnswersComparer()), $"own destroy, on {name}");
                EcsQuery<StatsArch> First(Transaction t) =>
                    t.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).OrderByField<StatsRanked, int>(x => x.Tier).Take(1);
                using (var fresh = dbe.CreateQuickTransaction())
                {
                    Assert.That(First(fresh).ExecuteOrdered(), Is.EqualTo(new[] { id }), "premise: the destroyed entity comes first among equal keys");
                }

                Assert.That(First(tx).ExecuteOrdered(), Is.EqualTo(new[] { other }), $"own destroy is not counted against Take, on {name}");
            }
            finally
            {
                QueryPathProbe.Reset();
            }
        }
    }

    private sealed class AnswersComparer : IEqualityComparer<(int, HashSet<EntityId>, List<EntityId>)>
    {
        public bool Equals((int, HashSet<EntityId>, List<EntityId>) a, (int, HashSet<EntityId>, List<EntityId>) b) =>
            a.Item1 == b.Item1 && a.Item2.SetEquals(b.Item2) && a.Item3.SequenceEqual(b.Item3);

        public int GetHashCode((int, HashSet<EntityId>, List<EntityId>) obj) => obj.Item1;
    }

    /// <summary>A component the transaction supplies is absent to its own field queries until it commits: the scans read the committed slot.</summary>
    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_ATransactionsOwnFirstSupply_IsAbsentToItsFieldQueriesUntilItCommits()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, null);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(5, 1));
            Assert.That(tx.Open(id).Read(StatsArch.Ranked).Tier, Is.EqualTo(5), "premise: the point read sees its own supply");
            OnEveryPath(dbe, q => VersionedTier(q, 0), id, false, "its own supply, at the slot's bytes", tx, ordered: true);
            OnEveryPath(dbe, q => VersionedTier(q, 5), id, false, "its own supply, at the supplied value", tx, ordered: true);
            Assert.That(tx.Commit(), Is.True);
        }

        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, true, "after the commit");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_ADestroyOfAnAbsentComponent_LeavesAnotherEntitysEntry()
    {
        using var dbe = OpenEngine();
        var absent = SpawnVersioned(dbe, null);
        var other = SpawnVersioned(dbe, 0);
        Committed(dbe, tx => tx.Destroy(absent));
        OnEveryPath(dbe, tx => VersionedTier(tx, 0), other, true, "another entity under the key an absent one's bytes showed");
        Assert.That(TierEntries<StatsRanked>(dbe, 0), Is.EqualTo(new[] { (long)other.RawValue }), "the other entity's entry is gone");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_ADisableThenReEnable_MatchesAgain()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        Committed(dbe, tx => tx.OpenMut(id).Disable(StatsArch.Ranked));
        Committed(dbe, tx => tx.OpenMut(id).Enable(StatsArch.Ranked));
        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, true, "a component disabled then enabled again");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_ASnapshotBeforeADisable_StillSeesTheComponentEnabled()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        using var reader = dbe.CreateQuickTransaction();
        Committed(dbe, tx => tx.OpenMut(id).Disable(StatsArch.Ranked));
        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, true, "a snapshot taken before the disable", reader);
        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, false, "a snapshot taken after the disable");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_ASnapshotBeforeAnEnable_StillSeesTheComponentDisabled()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        Committed(dbe, tx => tx.OpenMut(id).Disable(StatsArch.Ranked));
        using var reader = dbe.CreateQuickTransaction();
        Committed(dbe, tx => tx.OpenMut(id).Enable(StatsArch.Ranked));
        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, false, "a snapshot taken before the enable", reader);
        OnEveryPath(dbe, tx => VersionedTier(tx, 5), id, true, "a snapshot taken after the enable");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_ATransactionsOwnPendingDisable_HidesTheComponentFromItsQueries()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(id).Disable(StatsArch.Ranked);
        OnEveryPath(dbe, t => VersionedTier(t, 5), id, false, "a component this transaction disabled", tx);
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void Versioned_AViewBuiltBeforeOrAfterADisable_AgreesWithTheQuery()
    {
        using var dbe = OpenEngine();
        var id = SpawnVersioned(dbe, 5);
        foreach (var (name, path) in Paths)
        {
            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            try
            {
                using var builder = dbe.CreateQuickTransaction();
                using var early = builder.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).ToView();
                using (var first = dbe.CreateQuickTransaction())
                {
                    early.Refresh(first);
                }

                Assert.That(early.Contains((long)id.RawValue), Is.True, $"premise on {name}: the view holds the enabled entity");
                using (var tx = dbe.CreateQuickTransaction())
                {
                    tx.OpenMut(id).Disable(StatsArch.Ranked);
                    Assert.That(tx.Commit(), Is.True);
                }

                using (var after = dbe.CreateQuickTransaction())
                {
                    early.Refresh(after);
                    Assert.That(early.Contains((long)id.RawValue), Is.False, $"on {name}: a view built before the disable keeps the entity");
                    using var late = after.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == 5).ToView();
                    late.Refresh(after);
                    Assert.That(late.Contains((long)id.RawValue), Is.False, $"on {name}: a view built after the disable holds the entity");
                }

                using (var tx = dbe.CreateQuickTransaction())
                {
                    tx.OpenMut(id).Enable(StatsArch.Ranked);
                    Assert.That(tx.Commit(), Is.True);
                }
            }
            finally
            {
                QueryPathProbe.Reset();
            }
        }
    }

    // ── SingleVersion ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void SingleVersion_AnUnsuppliedComponent_IsZeroAndDisabled_AndMatchesOnlyWhenDisabledIsNamed()
    {
        using var dbe = OpenEngine();
        var id = SpawnSingleVersion(dbe, null);
        OnEveryPath(dbe, tx => SingleVersionTier(tx, 0), id, false, "an unsupplied SingleVersion component, at zero");
        OnEveryPath(dbe, tx => SingleVersionTier(tx, 0, q => q.Disabled<QcSvRanked>()), id, true, "an unsupplied SingleVersion component, Disabled<T> named");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void SingleVersion_ADisabledComponent_MatchesOnlyWhenDisabledIsNamed()
    {
        using var dbe = OpenEngine();
        var id = SpawnSingleVersion(dbe, 5);
        Committed(dbe, tx => tx.OpenMut(id).Disable(QcSvArch.Ranked));
        OnEveryPath(dbe, tx => SingleVersionTier(tx, 5), id, false, "a disabled SingleVersion component");
        OnEveryPath(dbe, tx => SingleVersionTier(tx, 5, q => q.Disabled<QcSvRanked>()), id, true, "a disabled SingleVersion component, Disabled<T> named");
        Committed(dbe, tx => tx.OpenMut(id).Enable(QcSvArch.Ranked));
        OnEveryPath(dbe, tx => SingleVersionTier(tx, 5), id, true, "a SingleVersion component enabled again");
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    public void SingleVersion_ASpawnWithoutAUniqueIndexedComponent_IsRefused()
    {
        using var dbe = OpenEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<QcSvCodeArch>(QcSvCodeArch.Tag.Set(new QcSvTag(1)));
            Assert.Throws<InvalidOperationException>(() => tx.Commit(), "a spawn leaving a unique-indexed SingleVersion component unsupplied committed");
        }

        Assert.That(dbe.CommitPipelineFailure, Is.Null, "the engine stopped committing");
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<QcSvCodeArch>(QcSvCodeArch.Code.Set(new QcSvCode(0)), QcSvCodeArch.Tag.Set(new QcSvTag(2)));
            Assert.That(tx.Commit(), Is.True, "a spawn supplying key 0 is refused: an unsupplied one holds it");
        }

        // Supplied after the Spawn call, in the same transaction: it publishes enabled with its value, so it is not refused.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var id = tx.Spawn<QcSvCodeArch>(QcSvCodeArch.Tag.Set(new QcSvTag(3)));
            tx.OpenMut(id).Enable(QcSvCodeArch.Code, new QcSvCode(9));
            Assert.That(tx.Commit(), Is.True, "a unique component supplied by Enable(value) on the pending spawn was refused");
        }
    }
}
