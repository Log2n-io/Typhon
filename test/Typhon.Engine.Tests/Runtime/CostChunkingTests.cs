using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The cost rule for parallel QuerySystem chunk counts (<see cref="RuntimeOptions.CostBasedChunking"/>): chunks sized by the previous dispatch's measured
/// worker time, not by entity count.
/// </summary>
[TestFixture]
class CostChunkingTests : TestBase<CostChunkingTests>
{
    private const int Workers = 4;
    private const int Entities = 640;
    private const double EntityCostUs = 5;

    /// <summary>
    /// ~3.2 ms of work over 640 entities: the entity rule gives min(4 workers, ceil(640 / 64)) = 4 chunks, one per worker, whatever they cost. The cost rule,
    /// from the second dispatch on, splits it past the width — twice it here, 8, since each of 4 chunks would carry ~800 µs.
    /// </summary>
    [Test]
    public void AnExpensiveSystem_IsSplitByItsCost()
    {
        var (chunks, workUs) = Run(costBased: true, systemMinChunk: 0);
        Assert.Multiple(() =>
        {
            Assert.That(chunks, Is.GreaterThan(Workers), "the cost rule should split past the worker count");
            Assert.That(workUs, Is.GreaterThanOrEqualTo(Entities * EntityCostUs), "WorkUs must sum every chunk's time: each entity spins 5 µs");
        });
    }

    [Test]
    public void WithTheCostRuleOff_TheEntityRuleStays()
    {
        var (chunks, _) = Run(costBased: false, systemMinChunk: 0);
        Assert.That(chunks, Is.EqualTo(Workers));
    }

    /// <summary>A system's own MinChunkSize is an explicit entity floor, and the cost rule leaves it alone.</summary>
    [Test]
    public void ASystemWithItsOwnMinChunkSize_KeepsTheEntityRule()
    {
        var (chunks, _) = Run(costBased: true, systemMinChunk: 64);
        Assert.That(chunks, Is.EqualTo(Workers));
    }

    /// <summary>With one worker the scheduler runs the chunks in its own loop, which must sum their time into WorkUs as the pool's path does.</summary>
    [Test]
    public void AtOneWorker_WorkUsStillSumsEveryChunk()
    {
        var (_, workUs) = Run(costBased: true, systemMinChunk: 0, workers: 1);
        Assert.That(workUs, Is.GreaterThanOrEqualTo(Entities * EntityCostUs), "each entity spins 5 µs, so WorkUs cannot be less than their sum");
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Runs a system that spins <see cref="EntityCostUs"/> per entity for at least three dispatches; returns its last chunk count and largest WorkUs.
    /// </summary>
    private (int Chunks, float WorkUs) Run(bool costBased, int systemMinChunk, int workers = Workers)
    {
        using var dbe = SetupEngine();
        using (var seedTx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(0, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            for (var i = 0; i < Entities; i++)
            {
                seedTx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            }

            seedTx.Commit();
        }

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EcsUnit>().ToView();
        var spin = (long)(EntityCostUs * Stopwatch.Frequency / 1_000_000.0);
        var ticksSeen = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Heavy", ctx =>
            {
                foreach (var _ in ctx.Entities)
                {
                    var end = Stopwatch.GetTimestamp() + spin;
                    while (Stopwatch.GetTimestamp() < end)
                    {
                    }
                }
            }, input: () => view, parallel: true, minChunkSize: systemMinChunk, after: "Tick");
        }, new RuntimeOptions { WorkerCount = workers, BaseTickRate = 100, CostBasedChunking = costBased });

        // Tick 4 has started, so Heavy has completed three dispatches: the first on the entity rule, the next two on whichever rule applies.
        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= 4, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        Assert.That(Volatile.Read(ref ticksSeen), Is.GreaterThanOrEqualTo(4), "precondition: the runtime did not reach tick 4");

        var idx = -1;
        for (var i = 0; i < runtime.Scheduler.AllSystemCount; i++)
        {
            if (runtime.Scheduler.Systems[i].Name == "Heavy")
            {
                idx = i;
                break;
            }
        }

        Assert.That(idx, Is.Not.EqualTo(-1), "Heavy should be found in the scheduler");
        var ring = runtime.Telemetry;
        var workUs = 0f;
        for (var t = ring.OldestAvailableTick; t <= ring.NewestTick; t++)
        {
            workUs = Math.Max(workUs, ring.GetSystemMetrics(t)[idx].WorkUs);
        }

        var chunks = runtime.Scheduler.Systems[idx].TotalChunks;
        view.Dispose();
        return (chunks, workUs);
    }
}

/// <summary>The cost rule's arithmetic alone, without an engine (TestBase names its database after the test, and these names do not make valid ones).</summary>
[TestFixture]
public class CostChunkCountTests
{
    /// <summary>
    /// The grain: the width inside 25–100 µs per chunk, fewer chunks below it, more above it up to twice the width, never zero, never past the units.
    /// </summary>
    [TestCase(10.0, 8, 1000, 1)]    // under one floor's worth: one chunk
    [TestCase(100.0, 8, 1000, 4)]   // below the band: chunks of the floor
    [TestCase(400.0, 8, 1000, 8)]   // inside the band: the width
    [TestCase(1200.0, 8, 1000, 12)] // above the band: chunks of the ceiling
    [TestCase(6400.0, 8, 1000, 16)] // far above it: twice the width, not 64
    [TestCase(1600.0, 8, 10, 10)]   // capped by the units
    [TestCase(0.0, 8, 1000, 1)]     // never zero
    public void FollowsTheGrain(double costUs, int width, int units, int expected) =>
        Assert.That(TyphonRuntime.CostChunkCount(costUs, width, units), Is.EqualTo(expected));
}
