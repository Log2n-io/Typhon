using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Components

/// <summary>SingleVersion: its indexes are maintained at the tick fence, through the batched moves.</summary>
[Component("Typhon.Test.Sparse.Member", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpMember
{
    [Index(AllowMultiple = true, Sparse = true)] public int Group;
    [Index(Sparse = true)] public int Tag;

    public SpMember(int group, int tag)
    {
        Group = group;
        Tag = tag;
    }
}

[Archetype]
class SpUnit : Archetype<SpUnit>
{
    public static readonly Comp<SpMember> Member = Register<SpMember>();
}

/// <summary>Versioned: its indexes are maintained at commit, through the single-entry moves.</summary>
[Component("Typhon.Test.Sparse.VMember", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SpVMember
{
    [Index(AllowMultiple = true, Sparse = true)] public int Group;
    [Index(Sparse = true)] public int Tag;

    public SpVMember(int group, int tag)
    {
        Group = group;
        Tag = tag;
    }
}

[Archetype]
class SpVUnit : Archetype<SpVUnit>
{
    public static readonly Comp<SpVMember> Member = Register<SpVMember>();
}

/// <summary>A float has two zeros that compare equal, so a sparse index over it is refused.</summary>
[Component("Typhon.Test.Sparse.Float", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpFloat
{
    [Index(AllowMultiple = true, Sparse = true)] public float Weight;
    public int Padding;
}

#endregion

/// <summary>
/// <c>[Index(Sparse = true)]</c> (IX-09): an entity whose key is 0 — "no data" — has no index entry, on every write path, and every reader that would scan a
/// range admitting 0 over such a tree reads the data instead. Each case runs on the SingleVersion archetype (fence-maintained, batched moves) and on the
/// Versioned one (commit-maintained, single-entry moves).
/// </summary>
[TestFixture]
class SparseIndexTests : TestBase<SparseIndexTests>
{
    private const int Rows = 256;

    [TearDown]
    public void ResetProbes() => QueryPathProbe.Reset();

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpMember>();
        dbe.RegisterComponentFromAccessor<SpVMember>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>One in four entities in a group (1..3), one in five tagged with a unique key; every other value 0.</summary>
    private static int GroupOf(int i) => i % 4 == 0 ? 1 + i / 4 % 3 : 0;

    private static int TagOf(int i) => i % 5 == 0 ? 1_000 + i : 0;

    private static EntityId[] Spawn(DatabaseEngine dbe, bool versioned)
    {
        var ids = new EntityId[Rows];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Rows; i++)
            {
                if (versioned)
                {
                    var v = new SpVMember(GroupOf(i), TagOf(i));
                    ids[i] = tx.Spawn<SpVUnit>(SpVUnit.Member.Set(in v));
                }
                else
                {
                    var m = new SpMember(GroupOf(i), TagOf(i));
                    ids[i] = tx.Spawn<SpUnit>(SpUnit.Member.Set(in m));
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(0);
        return ids;
    }

    private static void Write(DatabaseEngine dbe, bool versioned, EntityId id, int group, int tag)
    {
        using var tx = dbe.CreateQuickTransaction();
        var e = tx.OpenMut(id);
        if (versioned)
        {
            e.Set(SpVUnit.Member, new SpVMember(group, tag));
        }
        else
        {
            e.Set(SpUnit.Member, new SpMember(group, tag));
        }

        tx.Commit();
    }

    private static ArchetypeMetadata Meta(bool versioned) => versioned ? Archetype<SpVUnit>.Metadata : Archetype<SpUnit>.Metadata;

    private static ref ClusterIndexField<PersistentStore> Field(DatabaseEngine dbe, bool versioned, int field)
        => ref dbe._archetypeStates[Meta(versioned).ArchetypeId].ClusterState.IndexSlots[0].Fields[field];

    private static HashSet<EntityId> Where(DatabaseEngine dbe, bool versioned, ClusterScanPath? path, int? equals = null,
        int? tagEquals = null)
    {
        if (path.HasValue)
        {
            QueryPathProbe.Forced = path.Value;
        }

        try
        {
            using var tx = dbe.CreateQuickTransaction();
            if (versioned)
            {
                var q = tagEquals.HasValue
                    ? tx.Query<SpVUnit>().WhereField<SpVMember>(m => m.Tag == tagEquals.Value)
                    : tx.Query<SpVUnit>().WhereField<SpVMember>(m => m.Group == equals.Value);
                return [.. q.Execute()];
            }

            var s = tagEquals.HasValue
                ? tx.Query<SpUnit>().WhereField<SpMember>(m => m.Tag == tagEquals.Value)
                : tx.Query<SpUnit>().WhereField<SpMember>(m => m.Group == equals.Value);
            return [.. s.Execute()];
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    private static HashSet<EntityId> Expected(EntityId[] ids, int[] values, int value)
    {
        var set = new HashSet<EntityId>();
        for (var i = 0; i < ids.Length; i++)
        {
            if (values[i] == value)
            {
                set.Add(ids[i]);
            }
        }

        return set;
    }

    private static void AssertQueries(DatabaseEngine dbe, bool versioned, EntityId[] ids, int[] groups, int[] tags, string when)
    {
        foreach (ClusterScanPath? path in new ClusterScanPath?[] { null, ClusterScanPath.Selective, ClusterScanPath.FullScan })
        {
            foreach (var g in new[] { 0, 1, 2, 3, 5 })
            {
                Assert.That(Where(dbe, versioned, path, equals: g), Is.EquivalentTo(Expected(ids, groups, g)), $"{when}: Group == {g}, path {path}");
            }

            Assert.That(Where(dbe, versioned, path, tagEquals: 0), Is.EquivalentTo(Expected(ids, tags, 0)), $"{when}: Tag == 0, path {path}");
            Assert.That(Where(dbe, versioned, path, tagEquals: 1_000), Is.EquivalentTo(Expected(ids, tags, 1_000)), $"{when}: Tag == 1000, path {path}");
        }

        IndexDataOracle.AssertIndexAgreesWithData(dbe, Meta(versioned), when);
    }

    /// <summary>
    /// Spawned entities holding 0 have no entry — the group tree holds the three groups, the tag tree only the tagged rows — and every query, forced down
    /// either scan path, still finds them: the selective path refuses a range admitting 0 over a sparse tree.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("IX-09")]
    public void EntitiesHoldingZero_AreNotIndexed_AndEveryQueryStillFindsThem(bool versioned)
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe, versioned);
        var groups = Enumerable.Range(0, Rows).Select(GroupOf).ToArray();
        var tags = Enumerable.Range(0, Rows).Select(TagOf).ToArray();

        Assert.That(Field(dbe, versioned, 0).Index.Sparse, Is.True, "the schema's flag reached the tree");
        Assert.That(Field(dbe, versioned, 0).Index.EntryCount, Is.EqualTo(3), "groups 1..3, not 0");
        Assert.That(Field(dbe, versioned, 1).Index.EntryCount, Is.EqualTo(tags.Count(t => t != 0)), "one entry per tagged row");
        AssertQueries(dbe, versioned, ids, groups, tags, "after spawn");
    }

    /// <summary>
    /// Keys moving onto 0, off 0, and from 0 to 0, over several ticks: a move onto 0 removes the entity's entry, a move off 0 inserts it with a real element
    /// id — every later move and removal reads that id back — and the queries agree with the data after each one.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("IX-09")]
    public void KeysMovingOnAndOffZero_KeepTheIndexInAgreementWithTheData(bool versioned)
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe, versioned);
        var groups = Enumerable.Range(0, Rows).Select(GroupOf).ToArray();
        var tags = Enumerable.Range(0, Rows).Select(TagOf).ToArray();

        for (var round = 0; round < 3; round++)
        {
            for (var i = round; i < Rows; i += 7)
            {
                // Off 0 into group 5, onto 0, and 0 to 0 (a write that leaves the key alone); tags likewise, each to a key no other row holds.
                groups[i] = groups[i] == 0 ? (i % 3 == 0 ? 0 : 5) : 0;
                tags[i] = tags[i] == 0 ? 2_000 + 10 * i + round : 0;
                Write(dbe, versioned, ids[i], groups[i], tags[i]);
            }

            dbe.WriteTickFence(1 + round);
            AssertQueries(dbe, versioned, ids, groups, tags, $"after round {round}");
        }

        // Back to 0, every one of them: the trees end empty, and nothing is left behind by an element id stored for a key-0 entity.
        for (var i = 0; i < Rows; i++)
        {
            if (groups[i] != 0 || tags[i] != 0)
            {
                groups[i] = 0;
                tags[i] = 0;
                Write(dbe, versioned, ids[i], 0, 0);
            }
        }

        dbe.WriteTickFence(10);
        Assert.That(Field(dbe, versioned, 0).Index.EntryCount, Is.Zero, "every group emptied");
        Assert.That(Field(dbe, versioned, 1).Index.EntryCount, Is.Zero, "every tag removed");
        AssertQueries(dbe, versioned, ids, groups, tags, "after every key returned to 0");
    }

    /// <summary>Destroying an entity that holds 0 removes nothing and fails nothing; destroying one that holds a key removes its entry.</summary>
    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("IX-09")]
    public void DestroyingEntitiesHoldingZeroOrAKey_LeavesTheIndexInAgreement(bool versioned)
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe, versioned);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Rows; i += 3)
            {
                tx.Destroy(ids[i]);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        IndexDataOracle.AssertIndexAgreesWithData(dbe, Meta(versioned), "after the destroys");
    }

    /// <summary>
    /// A unique sparse index lets any number of entities of one commit hold 0, and still refuses two of them one real key: 0 is no key, not a shared one.
    /// </summary>
    [Test]
    [VerifiesRule("IX-09")]
    public void AUniqueSparseIndex_LetsAnyNumberHoldZero_AndStillRefusesADuplicate()
    {
        using var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 100; i++)
            {
                var v = new SpVMember(0, 0);
                tx.Spawn<SpVUnit>(SpVUnit.Member.Set(in v));
            }

            Assert.DoesNotThrow(() => tx.Commit(), "a hundred entities with no tag");
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var a = new SpVMember(0, 7);
            var b = new SpVMember(0, 7);
            tx.Spawn<SpVUnit>(SpVUnit.Member.Set(in a));
            tx.Spawn<SpVUnit>(SpVUnit.Member.Set(in b));
            Assert.Throws<UniqueConstraintViolationException>(() => tx.Commit(), "two entities tagged 7");
        }
    }

    /// <summary>An ordered query over a sparse field whose range admits 0 returns the 0s too, in order: it reads the data rather than merging the trees.</summary>
    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("IX-09")]
    public void AnOrderedQueryOverASparseField_ReturnsTheZerosInOrder(bool versioned)
    {
        using var dbe = SetupEngine();
        Spawn(dbe, versioned);
        var expected = Enumerable.Range(0, Rows).Select(GroupOf).Order().ToArray();

        using var tx = dbe.CreateQuickTransaction();
        var ordered = versioned
            ? tx.Query<SpVUnit>().WhereField<SpVMember>(m => m.Group >= 0).OrderByField<SpVMember, int>(m => m.Group).ExecuteOrdered()
            : tx.Query<SpUnit>().WhereField<SpMember>(m => m.Group >= 0).OrderByField<SpMember, int>(m => m.Group).ExecuteOrdered();
        var got = ordered.Select(id => versioned ? tx.Open(id).Read(SpVUnit.Member).Group : tx.Open(id).Read(SpUnit.Member).Group).ToArray();
        Assert.That(got, Is.EqualTo(expected), "every row, ascending by group, the 192 rows in no group first");

        // A range that excludes 0 still streams from the tree, and answers the same as the data.
        var positive = versioned
            ? tx.Query<SpVUnit>().WhereField<SpVMember>(m => m.Group >= 2).OrderByField<SpVMember, int>(m => m.Group).ExecuteOrdered()
            : tx.Query<SpUnit>().WhereField<SpMember>(m => m.Group >= 2).OrderByField<SpMember, int>(m => m.Group).ExecuteOrdered();
        Assert.That(positive.Count, Is.EqualTo(expected.Count(g => g >= 2)));
    }

    [Test]
    public void ASparseIndex_OnAFloatField_IsRefused()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var e = Assert.Throws<InvalidOperationException>(() => dbe.RegisterComponentFromAccessor<SpFloat>());
        Assert.That(e.Message, Does.Contain("Sparse").And.Contain("integer"));
    }

    /// <summary>
    /// The oracle sees a key-0 entry: filed by a tree whose sparse flag was hidden for the insert, it is reported as an over-population.
    /// </summary>
    [Test]
    [RuleMutant("IX-09")]
    public unsafe void Mutant_AKeyZeroEntry_IsReportedByTheOracle()
    {
        using var dbe = SetupEngine();
        Spawn(dbe, versioned: false);
        var tree = Field(dbe, false, 0).Index;

        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var accessor = tree.Segment.CreateChunkAccessor();
            try
            {
                var zero = 0;
                tree.Sparse = false;
                tree.Add(&zero, 0, ref accessor);
            }
            finally
            {
                tree.Sparse = true;
                accessor.Dispose();
            }
        }

        var problems = IndexDataOracle.Check(dbe, Meta(false), out _);
        Assert.That(problems, Has.Some.Contains("holds key 0"), "the planted key-0 entry");
    }

    /// <summary>
    /// The planner's refusal is what makes the selective path right: with the tree's sparse flag hidden from it, the forced selective scan of
    /// <c>Group == 0</c> answers from the tree, which holds no 0, and misses every one of the 192 entities.
    /// </summary>
    [Test]
    [RuleMutant("IX-09")]
    public void Mutant_APlannerBlindToSparse_MissesTheZeros()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe, versioned: false);
        var groups = Enumerable.Range(0, Rows).Select(GroupOf).ToArray();
        var tree = Field(dbe, false, 0).Index;

        tree.Sparse = false;
        try
        {
            var got = Where(dbe, false, ClusterScanPath.Selective, equals: 0);
            Assert.That(got.Count, Is.LessThan(Expected(ids, groups, 0).Count), "the tree's answer drops the 0s");
        }
        finally
        {
            tree.Sparse = true;
        }

        Assert.That(Where(dbe, false, ClusterScanPath.Selective, equals: 0), Is.EquivalentTo(Expected(ids, groups, 0)), "and with it seen, finds them");
    }
}
