using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>The market item's shape: a non-unique owner, and a unique 64-bit sequence every trade re-keys.</summary>
[Component("Typhon.Test.OrderedQuery.Keyed", 1)]
struct OqKeyed
{
    [Index(AllowMultiple = true)] public long Owner;
    [Index] public long Seq;
}

[Archetype]
class OqArch : Archetype<OqArch>
{
    public static readonly Comp<OqKeyed> Keyed = Register<OqKeyed>();
    public static readonly Comp<StatsTag> Tag = Register<StatsTag>();
}

/// <summary>
/// An ordered query whose predicates name another field than the one it orders by: <c>WhereField(Tier == t).OrderByField(Score)</c>. Found by the market
/// storm's query auditor, which asks for a player's items ordered by their last trade.
/// </summary>
[TestFixture]
class OrderedQueryByAnotherFieldTests : TestBase<OrderedQueryByAnotherFieldTests>
{
    private DatabaseEngine OpenEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.RegisterComponentFromAccessor<QipTwoIndexed>();
        dbe.RegisterComponentFromAccessor<OqKeyed>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // Spawned in this order, so the entity keys' order is not the scores'.
    private static readonly (int Tier, int Score)[] Rows = [(1, 40), (2, 15), (1, 10), (1, 30), (2, 5), (1, 20), (1, 50)];

    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("QORD-01")]
    public void AQueryOrderedByAFieldNoPredicateNames_ReturnsItsRowsInThatFieldsOrder(bool descending)
    {
        using var dbe = OpenEngine();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var (tier, score) in Rows)
            {
                ids.Add(tx.Spawn<QipArch>(QipArch.Ranked.Set(new QipTwoIndexed(tier, score)), QipArch.Tag.Set(new StatsTag(score))));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var tierOne = Enumerable.Range(0, Rows.Length).Where(i => Rows[i].Tier == 1).OrderBy(i => Rows[i].Score).Select(i => ids[i]).ToList();
        if (descending)
        {
            tierOne.Reverse();
        }

        using var reader = dbe.CreateQuickTransaction();
        EcsQuery<QipArch> Q()
        {
            var q = reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Tier == 1);
            return descending ? q.OrderByFieldDescending<QipTwoIndexed, int>(x => x.Score) : q.OrderByField<QipTwoIndexed, int>(x => x.Score);
        }

        Assert.That(Q().ExecuteOrdered(), Is.EqualTo(tierOne), "every row, by score");
        Assert.That(Q().Skip(1).Take(2).ExecuteOrdered(), Is.EqualTo(tierOne.Skip(1).Take(2).ToList()), "a page, by score");
    }

    /// <summary>A not-equal predicate on another field, alone or beside a range on the ordered one: every row it admits, in the ordered field's order.</summary>
    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("QORD-01")]
    public void ANotEqualPredicateOnAnotherField_FiltersAndKeepsTheOrder(bool withRangeOnTheOrderedField)
    {
        using var dbe = OpenEngine();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var (tier, score) in Rows)
            {
                ids.Add(tx.Spawn<QipArch>(QipArch.Ranked.Set(new QipTwoIndexed(tier, score)), QipArch.Tag.Set(new StatsTag(score))));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var expected = Enumerable.Range(0, Rows.Length).Where(i => Rows[i].Tier != 2 && (!withRangeOnTheOrderedField || Rows[i].Score >= 20))
            .OrderBy(i => Rows[i].Score).Select(i => ids[i]).ToList();
        using var reader = dbe.CreateQuickTransaction();
        var q = withRangeOnTheOrderedField
            ? reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Tier != 2 && x.Score >= 20)
            : reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Tier != 2);
        Assert.That(q.OrderByField<QipTwoIndexed, int>(x => x.Score).ExecuteOrdered(), Is.EqualTo(expected));
    }

    /// <summary>
    /// The market's query: an owner's rows ordered by a unique 64-bit sequence that other rows' updates and a destroy re-key around them, ascending and
    /// descending, with negative and positive keys.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("QORD-01")]
    public void AnOwnersRowsOrderedByAUniqueLongSequence_ComeInSequenceOrder(bool descending)
    {
        using var dbe = OpenEngine();
        var seqs = new long[] { 7_000_000_000L, -5, 42, -9_000_000_000L, 3, 1_000 };
        var mine = new List<(long Seq, EntityId Id)>();
        var others = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < seqs.Length; i++)
            {
                mine.Add((seqs[i], tx.Spawn<OqArch>(OqArch.Keyed.Set(new OqKeyed { Owner = 7, Seq = seqs[i] }), OqArch.Tag.Set(new StatsTag(i)))));
                others.Add(tx.Spawn<OqArch>(OqArch.Keyed.Set(new OqKeyed { Owner = 8, Seq = seqs[i] + 1 }), OqArch.Tag.Set(new StatsTag(i))));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(others[0]).Set(OqArch.Keyed, new OqKeyed { Owner = 8, Seq = 41 });
            tx.Destroy(others[1]);
            Assert.That(tx.Commit(), Is.True);
        }

        var expected = (descending ? mine.OrderByDescending(x => x.Seq) : mine.OrderBy(x => x.Seq)).Select(x => x.Id).ToList();
        using var reader = dbe.CreateQuickTransaction();
        var q = reader.Query<OqArch>().WhereField<OqKeyed>(x => x.Owner == 7);
        Assert.That((descending ? q.OrderByFieldDescending<OqKeyed, long>(x => x.Seq) : q.OrderByField<OqKeyed, long>(x => x.Seq)).ExecuteOrdered(),
            Is.EqualTo(expected));
    }

    /// <summary>A field no ordered key encodes is refused as an order, rather than sorting by entity key without a word.</summary>
    [Test]
    [VerifiesRule("QORD-01")]
    public void OrderingByAStringField_IsRefused()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClusterS64Named>();
        dbe.RegisterComponentFromAccessor<ClusterS64Tag>();
        dbe.InitializeArchetypes();
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(() => tx.Query<ClusterS64Arch>().WhereField<ClusterS64Named>(x => x.Score >= 0).OrderByField<ClusterS64Named, String64>(x => x.Name),
            Throws.InvalidOperationException.With.Message.Contains("cannot order a query"));
    }

    /// <summary>A not-equal predicate on the ordered field itself: the excluded value's rows are not returned, the rest are, in order.</summary>
    [Test]
    [VerifiesRule("QORD-01")]
    public void ANotEqualPredicateOnTheOrderedField_ExcludesItsRowsAndKeepsTheOrder()
    {
        using var dbe = OpenEngine();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var (tier, score) in Rows)
            {
                ids.Add(tx.Spawn<QipArch>(QipArch.Ranked.Set(new QipTwoIndexed(tier, score)), QipArch.Tag.Set(new StatsTag(score))));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var expected = Enumerable.Range(0, Rows.Length).Where(i => Rows[i].Score != 20).OrderBy(i => Rows[i].Score).Select(i => ids[i]).ToList();
        using var reader = dbe.CreateQuickTransaction();
        Assert.That(reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Score != 20).OrderByField<QipTwoIndexed, int>(x => x.Score).ExecuteOrdered(),
            Is.EqualTo(expected));
    }

    /// <summary>
    /// A reader older than an update of the ordered field orders by the value it sees, not by the one the update wrote: filtered on another field, each row's
    /// key is read at the snapshot. It was read from the ordered field's tree, which holds the committed HEAD's keys.
    /// </summary>
    [Test]
    [VerifiesRule("QORD-01")]
    public void AReaderOlderThanAnUpdateOfTheOrderedField_OrdersByTheValueItSees() => OrdersAnOlderReaderByTheValueItSees(filterOnTheOrderedField: false);

    /// <summary>
    /// The same, filtered on the ordered field itself, so the merge streams its tree. Red: the tree holds the committed HEAD's keys, so the row comes at its
    /// newer value — the ordered face of #674, where the predicates evaluate the HEAD too.
    /// </summary>
    [Test]
    [Category("Quarantine")]
    public void AReaderOlderThanAnUpdateOfTheOrderedField_OrdersByTheValueItSees_OnTheMerge() => OrdersAnOlderReaderByTheValueItSees(filterOnTheOrderedField: true);

    private void OrdersAnOlderReaderByTheValueItSees(bool filterOnTheOrderedField)
    {
        using var dbe = OpenEngine();
        EntityId a, b;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a = tx.Spawn<QipArch>(QipArch.Ranked.Set(new QipTwoIndexed(1, 10)), QipArch.Tag.Set(new StatsTag(1)));
            b = tx.Spawn<QipArch>(QipArch.Ranked.Set(new QipTwoIndexed(1, 20)), QipArch.Tag.Set(new StatsTag(2)));
            Assert.That(tx.Commit(), Is.True);
        }

        using var reader = dbe.CreateQuickTransaction();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(a).Set(QipArch.Ranked, new QipTwoIndexed(1, 30));
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.That(reader.Open(a).Read(QipArch.Ranked).Score, Is.EqualTo(10), "premise: the reader sees the old score");
        var q = filterOnTheOrderedField
            ? reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Score >= 0)
            : reader.Query<QipArch>().WhereField<QipTwoIndexed>(x => x.Tier == 1);
        Assert.That(q.OrderByField<QipTwoIndexed, int>(x => x.Score).ExecuteOrdered(), Is.EqualTo(new[] { a, b }), "by the scores the reader sees: 10, 20");
    }
}
