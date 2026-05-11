using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="EcsQuery{T}.EcsQueryId"/> — monotonic, unique under concurrent construction.
/// Mirrors the pattern in <c>EcsConcurrencyTests.ParallelSpawn_SameArchetype_AllEntitiesUnique</c>.
/// Part of P0 (issue #333) of the Query Profiling umbrella (#342).
/// </summary>
[TestFixture]
[NonParallelizable]
class EcsQueryIdTests : TestBase<EcsQueryIdTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<EcsUnit>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    [CancelAfter(15000)]
    public void EcsQueryId_Monotonic_SingleThread()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var q1 = tx.Query<EcsUnit>();
        var q2 = tx.Query<EcsUnit>();
        var q3 = tx.QueryExact<EcsUnit>();

        Assert.That(q2.EcsQueryId, Is.GreaterThan(q1.EcsQueryId), "EcsQueryId must be monotonic across constructions");
        Assert.That(q3.EcsQueryId, Is.GreaterThan(q2.EcsQueryId), "QueryExact construction also bumps the counter");
        Assert.That(q1.EcsQueryId, Is.Not.EqualTo(q2.EcsQueryId), "Distinct constructions get distinct IDs even on the same archetype");
    }

    [Test]
    [CancelAfter(15000)]
    public void EcsQueryId_FluentChain_PreservesId()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var q = tx.Query<EcsUnit>();
        var originalId = q.EcsQueryId;

        // Fluent mutation methods modify the struct value and return — the EcsQueryId must propagate through copies.
        var qWith = q.With<EcsPosition>();
        Assert.That(qWith.EcsQueryId, Is.EqualTo(originalId), "Fluent chain (With<T>) must preserve EcsQueryId via struct copy");
    }

    [Test]
    [CancelAfter(15000)]
    public void EcsQueryId_ParallelConstruction_AllUnique()
    {
        using var dbe = SetupEngine();
        const int threadCount = 8;
        const int queriesPerThread = 200;

        var allIds = new ConcurrentBag<int>();
        var barrier = new Barrier(threadCount);

        Parallel.For(0, threadCount, _ =>
        {
            barrier.SignalAndWait();
            using var tx = dbe.CreateQuickTransaction();
            for (int j = 0; j < queriesPerThread; j++)
            {
                var q = tx.Query<EcsUnit>();
                allIds.Add(q.EcsQueryId);
            }
        });

        var uniqueIds = new HashSet<int>(allIds);
        Assert.That(uniqueIds.Count, Is.EqualTo(threadCount * queriesPerThread),
            "All EcsQueryIds must be unique across threads (Interlocked.Increment on the static counter)");
    }
}
