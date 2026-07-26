using System;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;

// Placed UNDER the Shard namespace so bare type names (Wallet, Transform, …) bind to the world-shard Light types via
// the closest enclosing namespace, not the Full tier's same-named types up in Typhon.Samples.Swg.
namespace Typhon.Samples.Swg.Shard.Tests;

/// <summary>
/// Proves the World-Shard Light slice stands alone and its source-generated accessors fire. Registers the six
/// <see cref="Character"/> components, then spawns/reads a character across all three storage modes, runs a spatial
/// query, exercises a Versioned wallet transaction (commit + rollback), and enable/disables a component. If the
/// consumer generator hadn't emitted the accessors + the <c>[ModuleInitializer]</c> barrier, neither
/// <c>Character.Transform.Set(...)</c> nor <c>RegisterComponentFromAccessor&lt;Transform&gt;()</c> would resolve.
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
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-shard-light", Guid.NewGuid().ToString("N"));
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
                opts.DatabaseName = "shard-light";
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

        _engine.RegisterComponentFromAccessor<Transform>();
        _engine.RegisterComponentFromAccessor<Bounds>();
        _engine.RegisterComponentFromAccessor<Ham>();
        _engine.RegisterComponentFromAccessor<Faction>();
        _engine.RegisterComponentFromAccessor<Wallet>();
        _engine.RegisterComponentFromAccessor<Intent>();

        // Bounds is SingleVersion + spatial ⇒ Character is cluster-eligible ⇒ a grid is required before init (#230 Option B).
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

    private EntityId SpawnCharacter(Transaction tx, float x, float y, int faction, long credits)
        => tx.Spawn<Character>(
            Character.Transform.Set(new Transform { Pos = new Point2F { X = x, Y = y }, Vel = new Point2F { X = 1f, Y = 0f } }),
            Character.Bounds.Set(new Bounds { Box = Boxed(x, y) }),
            Character.Ham.Set(new Ham { Health = 80, MaxHealth = 100, Action = 70, MaxAction = 100, Mind = 60, MaxMind = 100 }),
            Character.Faction.Set(new Faction { Value = faction }),
            Character.Wallet.Set(new Wallet { Credits = credits }),
            Character.Intent.Set(new Intent()));

    [Test]
    public void Character_RoundTrips_SV_Versioned_Transient()
    {
        EntityId id;
        using (var tx = _engine.CreateQuickTransaction())
        {
            id = SpawnCharacter(tx, 50f, 60f, faction: Factions.Hutt, credits: 123);
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            var e = tx.Open(id);
            Assert.That(e.Read(Character.Transform).Pos.X, Is.EqualTo(50f), "SingleVersion pose reads back");
            Assert.That(e.Read(Character.Bounds).Box.MinX, Is.EqualTo(49f), "SingleVersion spatial component reads back");
            var ham = e.Read(Character.Ham);
            Assert.That((ham.Health, ham.Action, ham.Mind), Is.EqualTo((80, 70, 60)), "SingleVersion HAM pools read back");
            Assert.That(e.Read(Character.Faction).Value, Is.EqualTo(Factions.Hutt), "SingleVersion indexed component reads back");
            Assert.That(e.Read(Character.Wallet).Credits, Is.EqualTo(123), "Versioned wallet reads back");
            Assert.That(e.Read(Character.Intent).Target.X, Is.EqualTo(0f), "Transient component reads back in-session");
        }
    }

    [Test]
    public void Spatial_Query_Returns_Positioned_Characters()
    {
        using (var tx = _engine.CreateQuickTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                SpawnCharacter(tx, 100f + i * 10f, 100f, faction: Factions.Rebel, credits: 0);
            }
            Assert.That(tx.Commit(), Is.True);
        }

        // Dynamic spatial entities enter the grid at the tick fence, not on commit.
        _engine.WriteTickFence(1);

        using (var tx = _engine.CreateQuickTransaction())
        {
            var all = tx.Query<Character>().WhereInAABB<Bounds>(0, 0, WorldSize, WorldSize, 0, 0).Execute();
            Assert.That(all.Count, Is.EqualTo(3), "world-covering AABB query returns all 3 positioned characters");
        }
    }

    [Test]
    public void Wallet_Versioned_Commit_And_Rollback()
    {
        EntityId id;
        using (var tx = _engine.CreateQuickTransaction())
        {
            id = SpawnCharacter(tx, 5f, 5f, faction: Factions.Rebel, credits: 100);
            Assert.That(tx.Commit(), Is.True);
        }

        // A committed write sticks.
        using (var tx = _engine.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(Character.Wallet).Credits += 40;
            Assert.That(tx.Commit(), Is.True);
        }
        using (var tx = _engine.CreateQuickTransaction())
        {
            Assert.That(tx.Open(id).Read(Character.Wallet).Credits, Is.EqualTo(140), "committed Versioned write persists");
        }

        // A rolled-back write does not.
        using (var tx = _engine.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(Character.Wallet).Credits += 5000;
            tx.Rollback();
        }
        using (var tx = _engine.CreateQuickTransaction())
        {
            Assert.That(tx.Open(id).Read(Character.Wallet).Credits, Is.EqualTo(140), "rolled-back Versioned write is discarded");
        }
    }

    [Test]
    public void EnableDisable_Partitions_Characters_By_Intent()
    {
        var ids = new EntityId[6];
        using (var tx = _engine.CreateQuickTransaction())
        {
            for (int i = 0; i < 6; i++)
            {
                ids[i] = SpawnCharacter(tx, i, i, faction: Factions.Imperial, credits: 10);
            }
            Assert.That(tx.Commit(), Is.True);
        }

        // Disable Intent on the first two (idle); leave four enabled (active).
        using (var tx = _engine.CreateQuickTransaction())
        {
            for (int i = 0; i < 2; i++)
            {
                tx.OpenMut(ids[i]).Disable(Character.Intent);
            }
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            var active = tx.Query<Character>().Enabled<Intent>().Execute();
            var idle = tx.Query<Character>().Disabled<Intent>().Execute();
            Assert.That(active.Count, Is.EqualTo(4), "4 characters should have Intent ENABLED");
            Assert.That(idle.Count, Is.EqualTo(2), "2 characters should have Intent DISABLED");
        }
    }
}
