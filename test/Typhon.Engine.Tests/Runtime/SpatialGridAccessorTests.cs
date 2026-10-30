using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Numerics;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// <see cref="SpatialGridAccessor"/> as a system sees it (issue #232): the metadata, the coordinate conversions and the tier setters, on the sparse grid.
/// </summary>
/// <remarks>
/// The grid holds a cell only once something occupies it (#872), and the tier setters touch existing cells only (VG-02), so each test spawns into every cell
/// it reads. The assertions read through <c>TryGetCellKey</c>: <c>WorldToCellKey</c> creates the cell it is asked about, which turns a missing cell into a
/// wrong tier. What a callback sees is copied out and asserted on the test thread, because an assertion on a worker proves nothing unless the throw reaches
/// the test.
/// </remarks>
[TestFixture]
[NonParallelizable] // runs the real scheduler
class SpatialGridAccessorTests : TestBase<SpatialGridAccessorTests>
{
    private static readonly TimeSpan TickWait = TimeSpan.FromSeconds(5);

    private static TierPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Data = 1.0f };

    private DatabaseEngine SetupEngineWithUnitsAt(params (float X, float Y)[] points)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(100, 100), cellSize: 10f));
        dbe.InitializeArchetypes();

        using var tx = dbe.CreateQuickTransaction();
        foreach (var (x, y) in points)
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(x, y)));
        }
        tx.Commit();
        return dbe;
    }

    /// <summary>Runs <paramref name="body"/> once, in a system, and returns once the runtime has stopped.</summary>
    private static void RunOnceInATick(DatabaseEngine dbe, Action<TickContext> body)
    {
        var ran = 0;
        using var done = new ManualResetEventSlim();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Grid", ctx =>
            {
                if (Interlocked.Increment(ref ran) == 1)
                {
                    body(ctx);
                    done.Set();
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        Assert.That(done.Wait(TickWait), Is.True, "the system did not finish its tick");
        runtime.Shutdown();
    }

    private static SimTier TierOfCellAt(DatabaseEngine dbe, float x, float y)
    {
        var grid = dbe.Realm0Grid;
        Assert.That(grid.TryGetCellKeyAt(x, y, 0f, out var cellKey), Is.True, $"no cell at ({x},{y}): the test must spawn into every cell it reads");
        return (SimTier)grid.GetCell(cellKey).Tier;
    }

    [Test]
    [CancelAfter(15_000)]
    public void AccessibleFromTickContext()
    {
        using var dbe = SetupEngineWithUnitsAt((5f, 5f), (15f, 5f));

        var valid = false;
        int cellCount = 0, width = 0, height = 0, cellX = -1, cellY = -1;
        var cellSize = 0.0;
        RunOnceInATick(dbe, ctx =>
        {
            var grid = ctx.SpatialGrid;
            valid = grid.IsValid;
            cellCount = grid.CellCount;
            width = grid.GridWidth;
            height = grid.GridHeight;
            cellSize = grid.CellSize;
            (cellX, cellY, _) = grid.GetCellCoords(grid.WorldToCell(5f, 5f, 0f));

            grid.SetCellTier(0, 0, 0, SimTier.Tier0);
            grid.SetCellTier(1, 0, 0, SimTier.Tier1);
        });

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True);
            Assert.That(cellCount, Is.EqualTo(2), "the two cells spawned into, and only those");
            Assert.That(width, Is.EqualTo(10));
            Assert.That(height, Is.EqualTo(10));
            Assert.That(cellSize, Is.EqualTo(10.0));
            Assert.That((cellX, cellY), Is.EqualTo((0, 0)));
            Assert.That(TierOfCellAt(dbe, 5f, 5f), Is.EqualTo(SimTier.Tier0));
            Assert.That(TierOfCellAt(dbe, 15f, 5f), Is.EqualTo(SimTier.Tier1));
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void MultiObserver_Union()
    {
        using var dbe = SetupEngineWithUnitsAt((5f, 5f), (25f, 25f), (45f, 45f), (85f, 85f));

        RunOnceInATick(dbe, ctx =>
        {
            var grid = ctx.SpatialGrid;

            // Multi-observer pattern: reset, then one SetTierInAABB per observer.
            grid.ResetAllTiers(SimTier.Tier3);
            grid.SetTierInAABB(0f, 0f, 0f, 30f, 30f, 0f, SimTier.Tier0);     // observer A: cells (0,0) to (2,2)
            grid.SetTierInAABB(20f, 20f, 0f, 60f, 60f, 0f, SimTier.Tier1);   // observer B: cells (2,2) to (5,5)
        });

        Assert.Multiple(() =>
        {
            Assert.That(TierOfCellAt(dbe, 5f, 5f), Is.EqualTo(SimTier.Tier0), "observer A only");
            Assert.That(TierOfCellAt(dbe, 25f, 25f), Is.EqualTo(SimTier.Tier0), "both observers: the higher priority wins");
            Assert.That(TierOfCellAt(dbe, 45f, 45f), Is.EqualTo(SimTier.Tier1), "observer B only");
            Assert.That(TierOfCellAt(dbe, 85f, 85f), Is.EqualTo(SimTier.Tier3), "neither observer");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("VG-02")]
    public void TierAssignment_OverEmptyCells_CreatesNothing()
    {
        using var dbe = SetupEngineWithUnitsAt((5f, 5f));

        var key = 0;
        var before = 0;
        var after = 0;
        RunOnceInATick(dbe, ctx =>
        {
            var grid = ctx.SpatialGrid;
            before = grid.CellCount;
            grid.SetCellTier(3, 3, 0, SimTier.Tier0);
            grid.SetCellTierMin(4, 4, 0, SimTier.Tier0);
            grid.SetTierInAABB(0f, 0f, 0f, 100f, 100f, 0f, SimTier.Tier0);
            key = grid.WorldToCell(35f, 35f, 0f);
            after = grid.CellCount;
        });

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo(1));
            Assert.That(after, Is.EqualTo(before), "a sweep of the whole world must create nothing");
            Assert.That(key, Is.EqualTo(-1), "WorldToCell reports an empty region, it does not fill it");
            Assert.That(dbe.Realm0Grid.TryGetCellKey(3, 3, 0, out _), Is.False);
            Assert.That(TierOfCellAt(dbe, 5f, 5f), Is.EqualTo(SimTier.Tier0), "the sweep did reach the cell that exists");
        });
    }
}
