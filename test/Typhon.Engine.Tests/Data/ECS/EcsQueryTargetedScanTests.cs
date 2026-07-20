using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for EcsQuery targeted scan (secondary index), OrderBy, Skip, Take.
/// Uses CompD (indexed fields: [Index] int B, [Index(AllowMultiple)] float A) and CompDArch.
/// </summary>
class EcsQueryTargetedScanTests : TestBase<EcsQueryTargetedScanTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.RegisterComponentFromAccessor<CompF>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Targeted scan via WhereField
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WhereField_Execute_UsesTargetedScan()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < 20; i++)
        {
            var d = new CompD(1.0f, i * 10, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        }
        tx.Commit();

        // Query with B >= 100 (should match 11 entities: B=100,110,...,190)
        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<CompDArch>().WhereField<CompD>(d => d.B >= 100).Execute();

        Assert.That(result.Count, Is.EqualTo(10));
    }

    [Test]
    public void WhereField_Execute_MatchesBroadScan()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < 20; i++)
        {
            var d = new CompD(1.0f, i * 5, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        }
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();

        // Targeted scan
        var targeted = tx2.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).Execute();

        // Broad scan with opaque WHERE
        var broad = tx2.Query<CompDArch>().Where<CompD>(d => d.B >= 50).Execute();

        Assert.That(targeted.Count, Is.EqualTo(broad.Count),
            "Targeted scan and broad scan must return same count");
        Assert.That(targeted.SetEquals(broad), Is.True,
            "Targeted scan and broad scan must return same entities");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // OrderBy + ExecuteOrdered
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ExecuteOrdered_AscendingByIndexedField()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var values = new[] { 50, 30, 70, 10, 90 };
        foreach (var v in values)
        {
            var d = new CompD(1.0f, v, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        }
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<CompDArch>()
            .WhereField<CompD>(d => d.B >= 10)
            .OrderByField<CompD, int>(d => d.B)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(5));

        // Verify ascending order by reading B values
        var bValues = result.Select(id => tx2.Open(id).Read(CompDArch.D).B).ToList();
        Assert.That(bValues, Is.EqualTo(new[] { 10, 30, 50, 70, 90 }));
    }

    [Test]
    public void ExecuteOrdered_DescendingByIndexedField()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var values = new[] { 50, 30, 70, 10, 90 };
        foreach (var v in values)
        {
            var d = new CompD(1.0f, v, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        }
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<CompDArch>()
            .WhereField<CompD>(d => d.B >= 10)
            .OrderByFieldDescending<CompD, int>(d => d.B)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(5));

        var bValues = result.Select(id => tx2.Open(id).Read(CompDArch.D).B).ToList();
        Assert.That(bValues, Is.EqualTo(new[] { 90, 70, 50, 30, 10 }));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Skip / Take
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ExecuteOrdered_SkipTake()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < 10; i++)
        {
            var d = new CompD(1.0f, (i + 1) * 10, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        }
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<CompDArch>()
            .WhereField<CompD>(d => d.B >= 10)
            .OrderByField<CompD, int>(d => d.B)
            .Skip(3)
            .Take(4)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(4));

        var bValues = result.Select(id => tx2.Open(id).Read(CompDArch.D).B).ToList();
        Assert.That(bValues, Is.EqualTo(new[] { 40, 50, 60, 70 }));
    }

    [Test]
    public void ExecuteOrdered_TakeMoreThanAvailable()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < 3; i++)
        {
            var d = new CompD(1.0f, (i + 1) * 10, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        }
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<CompDArch>()
            .WhereField<CompD>(d => d.B >= 10)
            .OrderByField<CompD, int>(d => d.B)
            .Take(100)
            .ExecuteOrdered();

        Assert.That(result, Has.Count.EqualTo(3));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Count / Any with targeted scan
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WhereField_Count()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < 10; i++)
        {
            var d = new CompD(1.0f, i * 10, 2.0);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        }
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var count = tx2.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).Count();

        Assert.That(count, Is.EqualTo(5));
    }

    [Test]
    public void WhereField_Any_True()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var d = new CompD(1.0f, 100, 2.0);
        tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        Assert.That(tx2.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).Any(), Is.True);
    }

    [Test]
    public void WhereField_Any_False()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var d = new CompD(1.0f, 10, 2.0);
        tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        Assert.That(tx2.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).Any(), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Error cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void OrderBy_WithoutWhereField_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        Assert.Throws<InvalidOperationException>(() =>
            tx.Query<CompDArch>().OrderByField<CompD, int>(d => d.B));
    }

    [Test]
    public void Skip_WithoutOrderBy_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        Assert.Throws<InvalidOperationException>(() =>
            tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).Skip(5));
    }

    [Test]
    public void ExecuteOrdered_WithoutOrderBy_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        Assert.Throws<InvalidOperationException>(() =>
            tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).ExecuteOrdered());
    }
}
