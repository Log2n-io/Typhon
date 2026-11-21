using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.QueryClause.Levels", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct QcLevels
{
    [Index(AllowMultiple = true)] public int I;
    [Index(AllowMultiple = true)] public short S;
    [Index(AllowMultiple = true)] public byte B;
    [Index(AllowMultiple = true)] public float F;
    [Index(AllowMultiple = true)] public ushort U;
    [Index(AllowMultiple = true)] public uint UI;
    [Index(AllowMultiple = true)] public long L;
    [Index(AllowMultiple = true)] public ulong UL;
    [Index(AllowMultiple = true)] public sbyte SB;
    [Index(AllowMultiple = true)] public double D;
    public int Plain;

    /// <summary>Not a schema field: a computed property.</summary>
    public readonly int Twice => Plain * 2;
}

[Component("Typhon.Test.QueryClause.Other", 1)]
[StructLayout(LayoutKind.Sequential)]
struct QcOther
{
    [Index(AllowMultiple = true)] public int Value;
}

/// <summary>A field whose schema name differs from its C# name.</summary>
[Component("Typhon.Test.QueryClause.Renamed", 1)]
[StructLayout(LayoutKind.Sequential)]
struct QcRenamed
{
    [Field(Name = "Lvl")]
    [Index(AllowMultiple = true)]
    public int Level;
}

[Archetype]
class QcRenamedArch : Archetype<QcRenamedArch>
{
    public static readonly Comp<QcRenamed> Renamed = Register<QcRenamed>();
}

/// <summary>Never registered.</summary>
[Component("Typhon.Test.QueryClause.Unregistered", 1)]
[StructLayout(LayoutKind.Sequential)]
struct QcUnregistered
{
    public int Value;
}

[Archetype]
class QcArch : Archetype<QcArch>
{
    public static readonly Comp<QcLevels> Levels = Register<QcLevels>();
}

[Archetype]
class QcOtherArch : Archetype<QcOtherArch>
{
    public static readonly Comp<QcOther> Other = Register<QcOther>();
}

/// <summary>A subtree where only the child carries <see cref="QcOther"/>: a polymorphic predicate on it is legal.</summary>
[Archetype]
class QcBase : Archetype<QcBase>
{
    public static readonly Comp<QcLevels> Levels = Register<QcLevels>();
}

[Archetype]
class QcChild : Archetype<QcChild, QcBase>
{
    public static readonly Comp<QcOther> Other = Register<QcOther>();
}

/// <summary>
/// A query clause that cannot be answered raises at the call instead of returning an empty or rounded result (#1654).
/// </summary>
[TestFixture]
class QueryClauseValidationTests : TestBase<QueryClauseValidationTests>
{
    private const int RowCount = 10;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QcLevels>();
        dbe.RegisterComponentFromAccessor<QcOther>();
        dbe.RegisterComponentFromAccessor<QcRenamed>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static QcLevels Row(int i) => new()
    {
        I = i, S = (short)i, B = (byte)i, F = i / 10f, U = (ushort)i, UI = (uint)i, L = i, UL = (ulong)i, SB = (sbyte)i, D = i / 10.0, Plain = i
    };

    /// <summary>Every field at its type's maximum (or minimum): where a Never encoded one step off, or a saturated range, would show.</summary>
    private static QcLevels Extreme(bool max) => max
        ? new QcLevels
        {
            I = int.MaxValue, S = short.MaxValue, B = byte.MaxValue, F = float.MaxValue, U = ushort.MaxValue, UI = uint.MaxValue, L = long.MaxValue,
            UL = ulong.MaxValue, SB = sbyte.MaxValue, D = double.MaxValue
        }
        : new QcLevels
        {
            I = int.MinValue, S = short.MinValue, B = byte.MinValue, F = float.MinValue, U = ushort.MinValue, UI = uint.MinValue, L = long.MinValue,
            UL = ulong.MinValue, SB = sbyte.MinValue, D = double.MinValue
        };

    [Test]
    [VerifiesRule("QCLAUSE-02")]
    public void With_UnregisteredComponent_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        Assert.That(() => tx.Query<QcArch>().With<QcUnregistered>(),
            Throws.InvalidOperationException.With.Message.Contains("not registered"), "an empty mask used to answer 'no rows'");
    }

    [Test]
    [VerifiesRule("QCLAUSE-02")]
    public void EnabledAndDisabled_UnregisteredComponent_Throw()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        Assert.That(() => tx.Query<QcArch>().Enabled<QcUnregistered>(), Throws.InvalidOperationException.With.Message.Contains("not registered"));
        Assert.That(() => tx.Query<QcArch>().Disabled<QcUnregistered>(), Throws.InvalidOperationException.With.Message.Contains("not registered"));
    }

    [Test]
    [VerifiesRule("QCLAUSE-02")]
    public void WhereAndWhereField_OnAComponentNoSelectedArchetypeCarries_Throw()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        Assert.That(() => tx.Query<QcArch>().WhereField<QcOther>(o => o.Value > 1),
            Throws.InvalidOperationException.With.Message.Contains("carries QcOther"));
        Assert.That(() => tx.Query<QcArch>().Where<QcOther>(o => o.Value > 1),
            Throws.InvalidOperationException.With.Message.Contains("carries QcOther"));
        Assert.That(() => tx.Query<QcArch>().Where<QcUnregistered>(o => o.Value > 1),
            Throws.InvalidOperationException.With.Message.Contains("not registered"));
    }

    [Test]
    [VerifiesRule("QCLAUSE-02")]
    public void WhereField_OnAComponentPartOfTheSubtreeCarries_IsLegal()
    {
        using var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<QcBase>(QcBase.Levels.Set(Row(1)));
            tx.Spawn<QcChild>(QcBase.Levels.Set(Row(2)), QcChild.Other.Set(new QcOther { Value = 7 }));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<QcBase>().WhereField<QcOther>(o => o.Value == 7).Count(), Is.EqualTo(1));
            Assert.That(tx.Query<QcBase>().Where<QcOther>(o => o.Value == 7).Count(), Is.EqualTo(1));
        }
    }

    [Test]
    [VerifiesRule("QCLAUSE-02")]
    public void WhereField_OnANonIndexedField_ThrowsAtTheTerminal()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        // Legal at the clause: a spatial clause, which may still follow, evaluates the predicate per candidate and accepts a plain field
        // (SpatialEcsIntegrationTests.SpatialQuery_ComposesWithFieldAndWherePredicates). Every index-driven terminal refuses it.
        var query = tx.Query<QcArch>().WhereField<QcLevels>(l => l.Plain > 1);
        Assert.That(() => query.Execute(), Throws.InvalidOperationException.With.Message.Contains("not indexed"));
        Assert.That(() => query.Count(), Throws.InvalidOperationException.With.Message.Contains("not indexed"));
        Assert.That(() => query.ToView(), Throws.InvalidOperationException.With.Message.Contains("not indexed"));
    }

    [Test]
    [VerifiesRule("QCLAUSE-02")]
    public void WhereField_OnAFieldTheComponentLacks_ThrowsAtTheClause()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        // A property of the struct that is not a schema field: no terminal can resolve it, so the clause raises.
        Assert.That(() => tx.Query<QcArch>().WhereField<QcLevels>(l => l.Twice > 1),
            Throws.InvalidOperationException.With.Message.Contains("not found"));
    }

    public enum QcField { I, S, B, F, U, UI, L, UL, SB, D }

    private static readonly double[] Constants =
    [
        3.5, 3.7, -0.5, 9.5, 300.0, -1.0, 4.0, 0.3, 0.25, double.NaN, 1e300, -1e300, -0.0, double.PositiveInfinity, double.NegativeInfinity,
        // One ulp either side of an integer: decimal rounded both to 3 and answered 3 for a comparison C# rejects.
        3.0000000000000004, 2.9999999999999996
    ];

    private static readonly float[] FloatConstants = [3.5f, 0.1f, -0.5f, 9.5f, 300f, 4f, float.NaN, 1e30f, -1e30f];

    private static readonly ExpressionType[] Ops =
    [
        ExpressionType.GreaterThan, ExpressionType.GreaterThanOrEqual, ExpressionType.LessThan, ExpressionType.LessThanOrEqual,
        ExpressionType.Equal, ExpressionType.NotEqual
    ];

    /// <summary>
    /// <c>p =&gt; p.I &gt; 3.7</c> is <c>(double)p.I &gt; 3.7</c>: every operator, on int/short/byte/float fields, against non-integral, out-of-range and
    /// float-inexact constants. Committed rows (through the index) and the transaction's own pending spawns (through the compiled lambda) must both match
    /// exactly the rows the C# predicate selects.
    /// </summary>
    [Test]
    [VerifiesRule("QCLAUSE-01")]
    public void WhereField_NonIntegralOrOutOfRangeConstant_SelectsWhatTheLambdaSelects([Values] QcField field)
    {
        using var dbe = SetupEngine();
        var committed = new Dictionary<EntityId, QcLevels>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < RowCount; i++)
            {
                committed[tx.Spawn<QcArch>(QcArch.Levels.Set(Row(i)))] = Row(i);
            }
            committed[tx.Spawn<QcArch>(QcArch.Levels.Set(Extreme(true)))] = Extreme(true);
            committed[tx.Spawn<QcArch>(QcArch.Levels.Set(Extreme(false)))] = Extreme(false);

            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var pending = new Dictionary<EntityId, QcLevels>();
        for (var i = 0; i < RowCount; i++)
        {
            pending[tx2.Spawn<QcArch>(QcArch.Levels.Set(Row(i)))] = Row(i);
        }
        pending[tx2.Spawn<QcArch>(QcArch.Levels.Set(Extreme(true)))] = Extreme(true);

        // Every path the planner can take: the tree range built from the normalised threshold (selective) and the zone-mapped cluster scan.
        foreach (var path in new[] { ClusterScanPath.Planner, ClusterScanPath.Selective, ClusterScanPath.FullScan })
        {
            QueryPathProbe.Forced = path;
            try
            {
                RunMatrix(field, tx2, committed, pending, path);
            }
            finally
            {
                QueryPathProbe.Reset();
            }
        }
    }

    private static void RunMatrix(QcField field, Transaction tx2, Dictionary<EntityId, QcLevels> committed, Dictionary<EntityId, QcLevels> pending,
        ClusterScanPath path)
    {
        foreach (var constant in Constants)
        foreach (var op in Ops)
        {
            var predicate = Build(field.ToString(), op, constant, typeof(double));
            var oracle = predicate.Compile();
            var expected = new HashSet<EntityId>();
            foreach (var (id, row) in committed)
            {
                if (oracle(row))
                {
                    expected.Add(id);
                }
            }
            foreach (var (id, row) in pending)
            {
                if (oracle(row))
                {
                    expected.Add(id);
                }
            }

            var actual = tx2.Query<QcArch>().WhereField(predicate).Execute();
            Assert.That(actual, Is.EquivalentTo(expected), $"{field} {op} {constant} ({path})");
        }

        // A float constant: C# widens an integral field to float (lossy past 2^24) and compares in float. A float or double field instead converts the
        // constant, which the double matrix above already covers.
        if (field is QcField.F or QcField.D)
        {
            return;
        }
        foreach (var constant in FloatConstants)
        foreach (var op in Ops)
        {
            var predicate = Build(field.ToString(), op, constant, typeof(float));
            AssertSelectsAsTheLambda(tx2, committed, pending, predicate, $"{field} {op} {constant}f ({path})");
        }
    }

    /// <summary>
    /// Constants whose comparison C# evaluates with a lossy widening of the field, or that sit one ulp off an integer: the bounds come from the widening
    /// C# applies, not from the constant rounded to the key type.
    /// </summary>
    [Test]
    [VerifiesRule("QCLAUSE-01")]
    public void WhereField_LargeConstants_FollowTheWideningCSharpApplies()
    {
        using var dbe = SetupEngine();
        long[] longs = [long.MaxValue, long.MaxValue - 1, 9007199254740992, 9007199254740993, 1234567890123456768, 1234567890123456000, 3, -3, long.MinValue];
        int[] ints = [3, 1000000000, 16777216, 16777217, int.MaxValue, int.MinValue];
        var committed = new Dictionary<EntityId, QcLevels>();
        var pending = new Dictionary<EntityId, QcLevels>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < longs.Length; i++)
            {
                var row = new QcLevels { L = longs[i], UL = unchecked((ulong)longs[i]), I = ints[i % ints.Length] };
                committed[tx.Spawn<QcArch>(QcArch.Levels.Set(row))] = row;
            }
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        for (var i = 0; i < longs.Length; i++)
        {
            var row = new QcLevels { L = longs[i], UL = unchecked((ulong)longs[i]), I = ints[(i + 1) % ints.Length] };
            pending[tx2.Spawn<QcArch>(QcArch.Levels.Set(row))] = row;
        }

        Expression<Func<QcLevels, bool>>[] predicates =
        [
            l => l.L < 9.2233720368547758E18,
            l => l.L >= 9.2233720368547758E18,
            l => l.L > 9007199254740992.0,
            l => l.L <= 9007199254740992.0,
            l => l.L >= 1.2345678901234568E18,
            l => l.UL > 9.2233720368547758E18,
            l => l.UL < 1.8446744073709552E19,
            l => l.I >= 3.0000000000000004,
            l => l.I == 1000000000.000001,
            l => l.I > 16777216f,
            l => l.I <= 2.147483647E9f
        ];
        foreach (var predicate in predicates)
        {
            AssertSelectsAsTheLambda(tx2, committed, pending, predicate, predicate.ToString());
        }

        // An equality the widening makes true for several integers cannot be one evaluator: refused, not answered for one of them.
        Assert.That(() => tx2.Query<QcArch>().WhereField<QcLevels>(l => l.L == 9.2233720368547758E18).Execute(),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("distinct values"));
        // (float)16777217 == 16777216f: C# matches both 16777216 and 16777217.
        Assert.That(() => tx2.Query<QcArch>().WhereField<QcLevels>(l => l.I == 16777216f).Execute(),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("distinct values"));
    }

    /// <summary>
    /// A narrowing cast on a captured value is part of the comparison: <c>(float)d</c> compares against the float nearest d, <c>(int)d</c> against the
    /// truncation, and a <c>char</c> promoted for a ushort field against its code. The parser's closure fast path used to drop the cast.
    /// </summary>
    [Test]
    [VerifiesRule("QCLAUSE-01")]
    public void WhereField_CastOnACapturedValue_IsApplied()
    {
        using var dbe = SetupEngine();
        var committed = new Dictionary<EntityId, QcLevels>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < RowCount; i++)
            {
                committed[tx.Spawn<QcArch>(QcArch.Levels.Set(Row(i)))] = Row(i);
            }
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var pending = new Dictionary<EntityId, QcLevels>();
        for (var i = 0; i < RowCount; i++)
        {
            pending[tx2.Spawn<QcArch>(QcArch.Levels.Set(Row(i)))] = Row(i);
        }

        var d = 0.1;
        var d37 = 3.7;
        var ch = (char)3;
        var big = 300L;
        Expression<Func<QcLevels, bool>>[] predicates =
        [
            l => l.F == (float)d,
            l => l.I == (int)d37,
            l => l.B < (byte)big,
            l => l.U == ch,
            l => l.I < big
        ];
        foreach (var predicate in predicates)
        {
            AssertSelectsAsTheLambda(tx2, committed, pending, predicate, predicate.ToString());
            Assert.That(tx2.Query<QcArch>().WhereField(predicate).Execute(), Is.Not.Empty, predicate.ToString());
        }
    }

    /// <summary>
    /// A captured variable is read at the <c>WhereField</c> call, for committed and pending rows alike: the pending filter used to compile the caller's
    /// lambda at the terminal, and so read the variable's later value.
    /// </summary>
    [Test]
    [VerifiesRule("QCLAUSE-01")]
    public void WhereField_CapturedVariableChangedBeforeTheTerminal_CommittedAndPendingAgree()
    {
        using var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<QcArch>(QcArch.Levels.Set(Row(5)));
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var pendingId = tx2.Spawn<QcArch>(QcArch.Levels.Set(Row(5)));
        var t = 3;
        var query = tx2.Query<QcArch>().WhereField<QcLevels>(l => l.I > t);
        t = 100;
        var result = query.Execute();
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(pendingId));
    }

    /// <summary>
    /// Unsigned keys on both sides of their midpoint: the zone maps and the ordered merge encoded ushort/uint by flipping bit 15/31 of the zero-extended
    /// value, which sorted 65535 below 0 — a cluster holding 0..9 and 65535 was pruned for <c>U &gt;= 10</c>, and an ordered query came out of order.
    /// </summary>
    [Test]
    [VerifiesRule("QCLAUSE-01")]
    public void UnsignedKeysAcrossTheirMidpoint_ArePrunedAndOrderedByValue()
    {
        using var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            // One cluster of 0..9 plus each type's maximum: the broken encoding recorded its bounds around the maximum's neighbourhood and pruned it.
            for (var i = 0; i < 10; i++)
            {
                tx.Spawn<QcArch>(QcArch.Levels.Set(new QcLevels { U = (ushort)i, UI = (uint)i }));
            }
            tx.Spawn<QcArch>(QcArch.Levels.Set(new QcLevels { U = ushort.MaxValue, UI = uint.MaxValue }));

            // Two archetypes, so an ordered query merges two streams by the encoded key.
            ushort[] mixed = [1, 40000, 65535, 100, 32768, 32767, 0, 50000];
            for (var i = 0; i < mixed.Length; i++)
            {
                var row = QcBase.Levels.Set(new QcLevels { U = mixed[i] });
                _ = i % 2 == 0 ? tx.Spawn<QcBase>(row) : tx.Spawn<QcChild>(row, QcChild.Other.Set(new QcOther { Value = i }));
            }
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        QueryPathProbe.Forced = ClusterScanPath.FullScan;
        try
        {
            Assert.That(tx2.Query<QcArch>().WhereField<QcLevels>(l => l.U >= 10).Count(), Is.EqualTo(1), "U: the zone map must not prune the 65535 row");
            Assert.That(tx2.Query<QcArch>().WhereField<QcLevels>(l => l.UI >= 10u).Count(), Is.EqualTo(1), "UI");
        }
        finally
        {
            QueryPathProbe.Reset();
        }

        var ordered = tx2.Query<QcBase>().WhereField<QcLevels>(l => l.U >= 0).OrderByField<QcLevels, ushort>(l => l.U).ExecuteOrdered();
        var read = new List<ushort>();
        foreach (var id in ordered)
        {
            read.Add(tx2.Open(id).Read(QcBase.Levels).U);
        }
        Assert.That(read, Has.Count.EqualTo(8));
        Assert.That(read, Is.Ordered, string.Join(", ", read));
    }

    [Test]
    [VerifiesRule("QCLAUSE-01")]
    public void WhereField_ANarrowingCastOnTheField_IsRefused()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        // (int)F truncates before comparing; neither the index nor the evaluator can express it, and answering F > 3 would return F = 3.5.
        Assert.That(() => tx.Query<QcArch>().WhereField<QcLevels>(l => (int)l.F > 3), Throws.TypeOf<NotSupportedException>().With.Message.Contains("cast"));
        Assert.That(() => tx.Query<QcArch>().WhereField<QcLevels>(l => (byte)l.I == 5), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    [VerifiesRule("QCLAUSE-01")]
    public void WhereField_ANullConstant_ComparesAsCSharpsLiftedOperators()
    {
        using var dbe = SetupEngine();
        var committed = new Dictionary<EntityId, QcLevels>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < RowCount; i++)
            {
                committed[tx.Spawn<QcArch>(QcArch.Levels.Set(Row(i)))] = Row(i);
            }
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var pending = new Dictionary<EntityId, QcLevels> { [tx2.Spawn<QcArch>(QcArch.Levels.Set(Row(4)))] = Row(4) };
        int? none = null;
        Expression<Func<QcLevels, bool>>[] predicates = [l => l.I > none, l => l.I == none, l => l.I <= none, l => l.I != none];
        foreach (var predicate in predicates)
        {
            AssertSelectsAsTheLambda(tx2, committed, pending, predicate, predicate.ToString());
        }
    }

    [Test]
    [VerifiesRule("QCLAUSE-02")]
    public void WhereField_OnAFieldRenamedInTheSchema_ResolvesByItsCSharpName()
    {
        using var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < RowCount; i++)
            {
                tx.Spawn<QcRenamedArch>(QcRenamedArch.Renamed.Set(new QcRenamed { Level = i }));
            }
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        tx2.Spawn<QcRenamedArch>(QcRenamedArch.Renamed.Set(new QcRenamed { Level = 7 }));
        Assert.That(tx2.Query<QcRenamedArch>().WhereField<QcRenamed>(r => r.Level > 5).Count(), Is.EqualTo(5), "committed 6..9 plus the pending 7");
    }

    private static void AssertSelectsAsTheLambda(Transaction tx, Dictionary<EntityId, QcLevels> committed, Dictionary<EntityId, QcLevels> pending,
        Expression<Func<QcLevels, bool>> predicate, string label)
    {
        var oracle = predicate.Compile();
        var expected = new HashSet<EntityId>();
        foreach (var (id, row) in committed)
        {
            if (oracle(row))
            {
                expected.Add(id);
            }
        }
        foreach (var (id, row) in pending)
        {
            if (oracle(row))
            {
                expected.Add(id);
            }
        }

        Assert.That(tx.Query<QcArch>().WhereField(predicate).Execute(), Is.EquivalentTo(expected), label);
    }

    /// <summary><c>l =&gt; (W)l.{field} {op} constant</c>, the tree C# emits for a field compared to a constant of the wider type W.</summary>
    private static Expression<Func<QcLevels, bool>> Build(string field, ExpressionType op, object constant, Type wide)
    {
        var p = Expression.Parameter(typeof(QcLevels), "l");
        var left = Expression.Convert(Expression.Field(p, field), wide);
        return Expression.Lambda<Func<QcLevels, bool>>(Expression.MakeBinary(op, left, Expression.Constant(constant, wide)), p);
    }
}
