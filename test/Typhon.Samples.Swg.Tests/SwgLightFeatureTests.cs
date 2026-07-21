using System;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Samples.Swg;
using Typhon.Schema.Definition;

namespace Typhon.Samples.Swg.Tests;

/// <summary>
/// Proves SWG Light stands alone and its source-generated accessors fire. Registers ONLY the Light components (the Full
/// tier's types are present in the same assembly but dormant — <see cref="DatabaseEngine.InitializeArchetypes"/> skips
/// archetypes whose components weren't registered), then spawns/reads a <see cref="Harvester"/> across all three
/// storage modes and runs a spatial query. If the consumer generator hadn't emitted the accessors + the
/// <c>[ModuleInitializer]</c> barrier, neither <c>Harvester.Position.Set(...)</c> nor
/// <c>RegisterComponentFromAccessor&lt;Position&gt;()</c> would resolve.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SwgLightFeatureTests
{
    private const float WorldSize = 10_000f;

    private string _tempDir;
    private ServiceProvider _sp;
    private DatabaseEngine _engine;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-swg-light", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var walDir = Path.Combine(_tempDir, "wal");
        Directory.CreateDirectory(walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = "swg-light";
                opts.DatabaseDirectory = _tempDir;
                opts.DatabaseCacheSize = 8192UL * 8192;
                opts.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions { WalDirectory = walDir, UseFUA = false };
            });
        _sp = services.BuildServiceProvider();
        _engine = _sp.GetRequiredService<DatabaseEngine>();

        // Register ONLY the SWG Light components — the Full tier stays dormant.
        _engine.RegisterComponentFromAccessor<Position>();
        _engine.RegisterComponentFromAccessor<Footprint>();
        _engine.RegisterComponentFromAccessor<Cargo>();
        _engine.RegisterComponentFromAccessor<Drift>();
        _engine.RegisterComponentFromAccessor<Extractor>();

        // Footprint is SingleVersion + spatial ⇒ Harvester is cluster-eligible ⇒ a grid is required before init (#230 Option B).
        _engine.ConfigureSpatialGrid(new SpatialGridConfig(new Vector2(0f, 0f), new Vector2(WorldSize, WorldSize), cellSize: 100f));
        _engine.InitializeArchetypes();
    }

    [TearDown]
    public void TearDown()
    {
        _engine?.Dispose();
        _sp?.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static AABB2F Boxed(float x, float y)
        => new() { MinX = x - 1f, MinY = y - 1f, MaxX = x + 1f, MaxY = y + 1f };

    [Test]
    public void Harvester_RoundTrips_V_SV_Transient()
    {
        EntityId id;
        using (var tx = _engine.CreateQuickTransaction())
        {
            var position = new Position { P = new Point2F { X = 50f, Y = 60f } };
            var footprint = new Footprint { Box = Boxed(50f, 60f) };
            var cargo = new Cargo { Amount = 123, Capacity = 500 };
            var drift = new Drift { Dx = 1.5f, Dy = -0.5f };
            var extractor = new Extractor { ResourceKind = 7, Rate = 42 };
            id = tx.Spawn<Harvester>(
                Harvester.Position.Set(in position),
                Harvester.Footprint.Set(in footprint),
                Harvester.Cargo.Set(in cargo),
                Harvester.Drift.Set(in drift),
                Harvester.Extractor.Set(in extractor));
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            var e = tx.Open(id);
            Assert.That(e.Read(Harvester.Position).P.X, Is.EqualTo(50f), "SingleVersion component reads back");
            Assert.That(e.Read(Harvester.Footprint).Box.MinX, Is.EqualTo(49f), "SingleVersion spatial component reads back");
            Assert.That(e.Read(Harvester.Cargo).Amount, Is.EqualTo(123), "Versioned component reads back");
            Assert.That(e.Read(Harvester.Extractor).Rate, Is.EqualTo(42), "Versioned indexed component reads back");
            Assert.That(e.Read(Harvester.Drift).Dx, Is.EqualTo(1.5f), "Transient component reads back in-session");
        }
    }

    [Test]
    public void Spatial_Query_Returns_Positioned_Harvesters()
    {
        using (var tx = _engine.CreateQuickTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                var position = new Position { P = new Point2F { X = 100f + i * 10f, Y = 100f } };
                var footprint = new Footprint { Box = Boxed(100f + i * 10f, 100f) };
                var cargo = new Cargo { Amount = 0, Capacity = 100 };
                var drift = new Drift { Dx = 0f, Dy = 0f };
                var extractor = new Extractor { ResourceKind = 1, Rate = 1 };
                tx.Spawn<Harvester>(
                    Harvester.Position.Set(in position),
                    Harvester.Footprint.Set(in footprint),
                    Harvester.Cargo.Set(in cargo),
                    Harvester.Drift.Set(in drift),
                    Harvester.Extractor.Set(in extractor));
            }
            Assert.That(tx.Commit(), Is.True);
        }

        // Dynamic spatial entities enter the grid at the tick fence, not on commit.
        _engine.WriteTickFence(1);

        using (var tx = _engine.CreateQuickTransaction())
        {
            var all = tx.Query<Harvester>().WhereInAABB<Footprint>(0, 0, WorldSize, WorldSize, 0, 0).Execute();
            Assert.That(all.Count, Is.EqualTo(3), "world-covering AABB query returns all 3 positioned drones");

            var near = tx.Query<Harvester>().WhereInAABB<Footprint>(95, 95, 115, 105, 0, 0).Execute();
            Assert.That(near.Count, Is.GreaterThanOrEqualTo(1).And.LessThanOrEqualTo(3),
                "a narrow AABB returns the drones inside it (the SingleVersion spatial index is queryable)");
        }
    }

    [Test]
    public void EnableDisable_Partitions_Harvesters_By_Drift()
    {
        var ids = new EntityId[6];
        using (var tx = _engine.CreateQuickTransaction())
        {
            for (int i = 0; i < 6; i++)
            {
                var position = new Position { P = new Point2F { X = i, Y = i } };
                var footprint = new Footprint { Box = Boxed(i, i) };
                var cargo = new Cargo { Amount = 0, Capacity = 100 };
                var drift = new Drift { Dx = 1f, Dy = 0f };
                var extractor = new Extractor { ResourceKind = 2, Rate = 1 };
                ids[i] = tx.Spawn<Harvester>(
                    Harvester.Position.Set(in position),
                    Harvester.Footprint.Set(in footprint),
                    Harvester.Cargo.Set(in cargo),
                    Harvester.Drift.Set(in drift),
                    Harvester.Extractor.Set(in extractor));
            }
            Assert.That(tx.Commit(), Is.True);
        }

        // Disable Drift on the first two (parked); leave four enabled (roaming).
        using (var tx = _engine.CreateQuickTransaction())
        {
            for (int i = 0; i < 2; i++)
            {
                tx.OpenMut(ids[i]).Disable(Harvester.Drift);
            }
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            var roaming = tx.Query<Harvester>().Enabled<Drift>().Execute();
            var parked = tx.Query<Harvester>().Disabled<Drift>().Execute();
            Assert.That(roaming.Count, Is.EqualTo(4), "4 drones should have Drift ENABLED (roaming)");
            Assert.That(parked.Count, Is.EqualTo(2), "2 drones should have Drift DISABLED (parked)");
        }
    }
}
