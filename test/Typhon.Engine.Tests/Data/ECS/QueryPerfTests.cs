using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Profiling tests for EcsQuery/View paths. Measures avg time per operation with Stopwatch.
/// Run with: dotnet test --filter "FullyQualifiedName~QueryPerfTests" -v n
/// </summary>
class QueryPerfTests : TestBase<QueryPerfTests>
{
    private const int EntityCount = 1000;
    private const int WarmupRuns = 3;
    private const int MeasuredRuns = 10;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupWithEntities()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();

        // Populate 1000 entities with unique B values (0-999), B has unique [Index]
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < EntityCount; i++)
        {
            var d = new CompD(i * 0.1f, i, i * 0.01);
            tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        }
        tx.Commit();

        return dbe;
    }

    [Test]
    public void Profile_Execute_SinglePredicate()
    {
        using var dbe = SetupWithEntities();

        for (int w = 0; w < WarmupRuns; w++)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).Execute();
        }

        var sw = new Stopwatch();
        int totalCount = 0;
        for (int i = 0; i < MeasuredRuns; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            sw.Start();
            var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).Execute();
            sw.Stop();
            totalCount += result.Count;
        }

        double avgUs = sw.Elapsed.TotalMicroseconds / MeasuredRuns;
        int avgCount = totalCount / MeasuredRuns;
        TestContext.Out.WriteLine($"Execute_SinglePredicate: {avgUs:F1}us/query, {avgCount} entities matched");
        Assert.That(avgCount, Is.EqualTo(500));
    }

    [Test]
    public void Profile_Count_SinglePredicate()
    {
        using var dbe = SetupWithEntities();

        for (int w = 0; w < WarmupRuns; w++)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).Count();
        }

        var sw = new Stopwatch();
        int totalCount = 0;
        for (int i = 0; i < MeasuredRuns; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            sw.Start();
            totalCount += tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).Count();
            sw.Stop();
        }

        double avgUs = sw.Elapsed.TotalMicroseconds / MeasuredRuns;
        int avgCount = totalCount / MeasuredRuns;
        TestContext.Out.WriteLine($"Count_SinglePredicate: {avgUs:F1}us/query, count={avgCount}");
        Assert.That(avgCount, Is.EqualTo(500));
    }

    [Test]
    public void Profile_View_InitialPopulation()
    {
        using var dbe = SetupWithEntities();

        for (int w = 0; w < WarmupRuns; w++)
        {
            using var tx = dbe.CreateQuickTransaction();
            using var v = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).ToView();
        }

        var sw = new Stopwatch();
        int totalCount = 0;
        for (int i = 0; i < MeasuredRuns; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            sw.Start();
            using var v = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).ToView();
            sw.Stop();
            totalCount += v.Count;
        }

        double avgUs = sw.Elapsed.TotalMicroseconds / MeasuredRuns;
        int avgCount = totalCount / MeasuredRuns;
        TestContext.Out.WriteLine($"View_InitialPopulation: {avgUs:F1}us/create, {avgCount} entities in view");
        Assert.That(avgCount, Is.EqualTo(500));
    }

    [Test]
    public void Profile_ExecuteOrdered_SinglePredicate()
    {
        using var dbe = SetupWithEntities();

        for (int w = 0; w < WarmupRuns; w++)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).OrderByField<CompD, int>(d => d.B).ExecuteOrdered();
        }

        var sw = new Stopwatch();
        int totalCount = 0;
        for (int i = 0; i < MeasuredRuns; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            sw.Start();
            var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).OrderByField<CompD, int>(d => d.B).ExecuteOrdered();
            sw.Stop();
            totalCount += result.Count;
        }

        double avgUs = sw.Elapsed.TotalMicroseconds / MeasuredRuns;
        int avgCount = totalCount / MeasuredRuns;
        TestContext.Out.WriteLine($"ExecuteOrdered_SinglePredicate: {avgUs:F1}us/query, {avgCount} entities matched");
        Assert.That(avgCount, Is.EqualTo(500));
    }

    [Test]
    public void Profile_ExecuteOrdered_SkipTake()
    {
        using var dbe = SetupWithEntities();

        for (int w = 0; w < WarmupRuns; w++)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).OrderByField<CompD, int>(d => d.B).Skip(100).Take(50).ExecuteOrdered();
        }

        var sw = new Stopwatch();
        int totalCount = 0;
        for (int i = 0; i < MeasuredRuns; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            sw.Start();
            var result = tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).OrderByField<CompD, int>(d => d.B).Skip(100).Take(50).ExecuteOrdered();
            sw.Stop();
            totalCount += result.Count;
        }

        double avgUs = sw.Elapsed.TotalMicroseconds / MeasuredRuns;
        int avgCount = totalCount / MeasuredRuns;
        TestContext.Out.WriteLine($"ExecuteOrdered_SkipTake: {avgUs:F1}us/query, {avgCount} entities matched");
        Assert.That(avgCount, Is.EqualTo(50));
    }
}
