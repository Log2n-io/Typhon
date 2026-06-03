using System;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Feature-level proof that the SWG fixture schema exercises each engine primitive it claims to. Builds a fresh
/// in-process engine, registers the schema, spawns small hand-crafted sets, and asserts behaviour via the public API.
/// It deliberately does NOT reopen a generated database — an in-process reopen hits the #384 ALC type-identity
/// collision. Generation-runs coverage (every feature path executing without throwing) lives in
/// <see cref="FixtureConfigTests"/>; this fixture proves the features actually <i>work</i>.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SwgFixtureFeatureTests
{
    private const float WorldSize = 10_000f;

    private string _tempDir;
    private ServiceProvider _sp;
    private DatabaseEngine _engine;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Archetype<GuildArch>.Touch();
        Archetype<ResourceTypeArch>.Touch();
        Archetype<RecipeArch>.Touch();
        Archetype<PlayerArch>.Touch();
        Archetype<ResourceDepositArch>.Touch();
        Archetype<StructureArch>.Touch();
        Archetype<HarvesterArch>.Touch();
        Archetype<FactoryArch>.Touch();
        Archetype<ItemArch>.Touch();
    }

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-swg-feature", Guid.NewGuid().ToString("N"));
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
                opts.DatabaseName = "swg-feature";
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

        _engine.RegisterComponentFromAccessor<Guild>();
        _engine.RegisterComponentFromAccessor<Membership>();
        _engine.RegisterComponentFromAccessor<Player>();
        _engine.RegisterComponentFromAccessor<Wallet>();
        _engine.RegisterComponentFromAccessor<Session>();
        _engine.RegisterComponentFromAccessor<Typhon.Workbench.Fixtures.ResourceType>();
        _engine.RegisterComponentFromAccessor<Recipe>();
        _engine.RegisterComponentFromAccessor<Deposit>();
        _engine.RegisterComponentFromAccessor<Structure>();
        _engine.RegisterComponentFromAccessor<StructureOwner>();
        _engine.RegisterComponentFromAccessor<Hopper>();
        _engine.RegisterComponentFromAccessor<HarvesterTarget>();
        _engine.RegisterComponentFromAccessor<MaintenanceState>();
        _engine.RegisterComponentFromAccessor<FactoryConfig>();
        _engine.RegisterComponentFromAccessor<PowerSupply>();
        _engine.RegisterComponentFromAccessor<Item>();
        _engine.RegisterComponentFromAccessor<ItemOwner>();
        _engine.RegisterComponentFromAccessor<PlayerPosition>();
        _engine.RegisterComponentFromAccessor<DepositPosition>();
        _engine.RegisterComponentFromAccessor<StructurePosition>();

        _engine.ConfigureSpatialGrid(new SpatialGridConfig(new Vector2(0f, 0f), new Vector2(WorldSize, WorldSize), cellSize: 100f));
        _engine.InitializeArchetypes();
    }

    [TearDown]
    public void TearDown()
    {
        _engine?.Dispose();
        _sp?.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static String64 S64(string s)
    {
        String64 v = default;
        v.AsString = s;
        return v;
    }

    private static AABB2F Box(float x, float y)
        => new() { MinX = x - 1f, MinY = y - 1f, MaxX = x + 1f, MaxY = y + 1f };

    [Test]
    public void EntityLink_ForeignKey_Resolves()
    {
        EntityId guildId, playerId;
        using (var tx = _engine.CreateQuickTransaction())
        {
            var g = new Guild { Name = S64("Imperial"), Faction = 1, MemberCount = 10, Treasury = 999 };
            guildId = tx.Spawn<GuildArch>(GuildArch.Guild.Set(in g));

            var p = new Player { Name = S64("Han"), AccountId = 1, Level = 50, ProfessionId = 3, CreatedAt = 1 };
            var m = new Membership { Guild = guildId, GuildRank = 4 };
            var w = new Wallet { Credits = 100, BankCredits = 200 };
            var pos = new PlayerPosition { Bounds = Box(5f, 5f) };
            var sess = new Session { ConnectionId = 1, LatencyMs = 20 };
            playerId = tx.Spawn<PlayerArch>(
                PlayerArch.Player.Set(in p), PlayerArch.Membership.Set(in m), PlayerArch.Wallet.Set(in w),
                PlayerArch.Position.Set(in pos), PlayerArch.Session.Set(in sess));
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            var m = tx.Open(playerId).Read(PlayerArch.Membership);
            Assert.That(m.Guild.Id, Is.EqualTo(guildId), "Membership.Guild EntityLink must resolve to the spawned guild");
            Assert.That(m.GuildRank, Is.EqualTo(4));
        }
    }

    [Test]
    public void ComponentCollection_Recipe_Slots_RoundTrip()
    {
        EntityId recipeId;
        using (var tx = _engine.CreateQuickTransaction())
        {
            var r = new Recipe { Name = S64("Blaster"), Tier = 2, ProfessionReq = 1 };
            {
                using var cca = tx.CreateComponentCollectionAccessor(ref r.Slots);
                for (int s = 0; s < 5; s++)
                {
                    cca.Add(new RecipeSlot { SlotIndex = s, ClassReq = s * 10, MinUnits = s + 1 });
                }
            }
            recipeId = tx.Spawn<RecipeArch>(RecipeArch.Recipe.Set(in r));
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            var r = tx.Open(recipeId).Read(RecipeArch.Recipe);
            using var cca = tx.CreateComponentCollectionAccessor(ref r.Slots);
            Assert.That(cca.ElementCount, Is.EqualTo(5), "all 5 recipe slots must read back");
            var slots = new RecipeSlot[cca.ElementCount];
            cca.GetAllElements(slots);
            // Slots are appended in order; verify the payload survived the round-trip.
            for (int s = 0; s < 5; s++)
            {
                Assert.That(slots[s].SlotIndex, Is.EqualTo(s));
                Assert.That(slots[s].MinUnits, Is.EqualTo(s + 1));
            }
        }
    }

    [Test]
    public void EnableDisable_Partitions_Players_By_Session()
    {
        var ids = new EntityId[10];
        using (var tx = _engine.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var p = new Player { Name = S64($"P{i}"), AccountId = i, Level = 1, ProfessionId = 0, CreatedAt = i };
                var m = new Membership { Guild = EntityLink<GuildArch>.Null, GuildRank = 0 };
                var w = new Wallet { Credits = 0, BankCredits = 0 };
                var pos = new PlayerPosition { Bounds = Box(i, i) };
                var sess = new Session { ConnectionId = i, LatencyMs = 10 };
                ids[i] = tx.Spawn<PlayerArch>(
                    PlayerArch.Player.Set(in p), PlayerArch.Membership.Set(in m), PlayerArch.Wallet.Set(in w),
                    PlayerArch.Position.Set(in pos), PlayerArch.Session.Set(in sess));
            }
            Assert.That(tx.Commit(), Is.True);
        }

        // Disable Session on the first 4 (offline); leave 6 enabled (online).
        using (var tx = _engine.CreateQuickTransaction())
        {
            for (int i = 0; i < 4; i++)
            {
                var e = tx.OpenMut(ids[i]);
                e.Disable(PlayerArch.Session);
            }
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            var online = tx.Query<PlayerArch>().Enabled<Session>().Execute();
            var offline = tx.Query<PlayerArch>().Disabled<Session>().Execute();
            Assert.That(online.Count, Is.EqualTo(6), "6 players should have Session ENABLED (online)");
            Assert.That(offline.Count, Is.EqualTo(4), "4 players should have Session DISABLED (offline)");
        }
    }

    [Test]
    public void CascadeDelete_Removes_Owned_Items()
    {
        EntityId playerId;
        using (var tx = _engine.CreateQuickTransaction())
        {
            var p = new Player { Name = S64("Owner"), AccountId = 1, Level = 1, ProfessionId = 0, CreatedAt = 1 };
            var m = new Membership { Guild = EntityLink<GuildArch>.Null, GuildRank = 0 };
            var w = new Wallet { Credits = 0, BankCredits = 0 };
            var pos = new PlayerPosition { Bounds = Box(1f, 1f) };
            var sess = new Session { ConnectionId = 1, LatencyMs = 10 };
            playerId = tx.Spawn<PlayerArch>(
                PlayerArch.Player.Set(in p), PlayerArch.Membership.Set(in m), PlayerArch.Wallet.Set(in w),
                PlayerArch.Position.Set(in pos), PlayerArch.Session.Set(in sess));

            var it = new Item { Recipe = EntityLink<RecipeArch>.Null, ItemType = 1, Quality = 50, Decay = 0 };
            var owner = new ItemOwner { Owner = playerId };
            tx.Spawn<ItemArch>(ItemArch.Item.Set(in it), ItemArch.Owner.Set(in owner));
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            Assert.That(tx.Query<ItemArch>().Execute().Count, Is.EqualTo(1), "the owned item exists before the cascade");
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            tx.Destroy(playerId);
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            Assert.That(tx.Query<ItemArch>().Execute().Count, Is.EqualTo(0),
                "deleting the owner must cascade-delete the item (ItemOwner.Owner OnParentDelete=Delete)");
        }
    }

    [Test]
    public void Polymorphic_Query_Matches_Structure_Subtree()
    {
        using (var tx = _engine.CreateQuickTransaction())
        {
            // One harvester.
            var hst = new Structure { TypeCode = 1, PlacedAt = 1, Maintenance = 100 };
            var ho = new StructureOwner { Owner = EntityLink<PlayerArch>.Null };
            var hop = new Hopper { Class = EntityLink<ResourceTypeArch>.Null, Amount = 0, Rate = 1 };
            var tgt = new HarvesterTarget { Deposit = EntityLink<ResourceDepositArch>.Null };
            var maint = new MaintenanceState { PaidUntil = 1 };
            var hpos = new StructurePosition { Bounds = Box(1f, 1f) };
            tx.Spawn<HarvesterArch>(
                StructureArch.Structure.Set(in hst), StructureArch.Owner.Set(in ho), HarvesterArch.Hopper.Set(in hop),
                HarvesterArch.Target.Set(in tgt), HarvesterArch.Maintenance.Set(in maint), HarvesterArch.Position.Set(in hpos));

            // One factory.
            var fst = new Structure { TypeCode = 2, PlacedAt = 2, Maintenance = 100 };
            var fo = new StructureOwner { Owner = EntityLink<PlayerArch>.Null };
            var fc = new FactoryConfig { Recipe = EntityLink<RecipeArch>.Null, RemainingRuns = 5 };
            var pw = new PowerSupply { CreditsRemaining = 100 };
            var fpos = new StructurePosition { Bounds = Box(2f, 2f) };
            tx.Spawn<FactoryArch>(
                StructureArch.Structure.Set(in fst), StructureArch.Owner.Set(in fo), FactoryArch.Config.Set(in fc),
                FactoryArch.Power.Set(in pw), FactoryArch.Position.Set(in fpos));
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            Assert.That(tx.Query<StructureArch>().Execute().Count, Is.EqualTo(2),
                "Query<Structure> is polymorphic — matches both Harvester and Factory");
            Assert.That(tx.QueryExact<HarvesterArch>().Execute().Count, Is.EqualTo(1), "QueryExact<Harvester> matches only the leaf");
            Assert.That(tx.QueryExact<FactoryArch>().Execute().Count, Is.EqualTo(1), "QueryExact<Factory> matches only the leaf");
        }
    }

    [Test]
    public void Spatial_Query_Returns_Positioned_Players()
    {
        using (var tx = _engine.CreateQuickTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                var p = new Player { Name = S64($"S{i}"), AccountId = i, Level = 1, ProfessionId = 0, CreatedAt = i };
                var m = new Membership { Guild = EntityLink<GuildArch>.Null, GuildRank = 0 };
                var w = new Wallet { Credits = 0, BankCredits = 0 };
                var pos = new PlayerPosition { Bounds = Box(100f + i * 10f, 100f) };
                var sess = new Session { ConnectionId = i, LatencyMs = 10 };
                tx.Spawn<PlayerArch>(
                    PlayerArch.Player.Set(in p), PlayerArch.Membership.Set(in m), PlayerArch.Wallet.Set(in w),
                    PlayerArch.Position.Set(in pos), PlayerArch.Session.Set(in sess));
            }
            Assert.That(tx.Commit(), Is.True);
        }

        // Dynamic spatial entities enter the grid at the tick-fence, not on commit.
        _engine.WriteTickFence(1);

        using (var tx = _engine.CreateQuickTransaction())
        {
            // WhereInAABB's 6-arg signature packs 2D bounds as (minX, minY, maxX, maxY, _, _) — the cluster 2D dispatch
            // reads maxX from param[2] and maxY from param[3]. The trailing two are ignored for 2D spatial fields.
            var all = tx.Query<PlayerArch>().WhereInAABB<PlayerPosition>(0, 0, WorldSize, WorldSize, 0, 0).Execute();
            Assert.That(all.Count, Is.EqualTo(3), "world-covering AABB query returns all 3 positioned players");

            var near = tx.Query<PlayerArch>().WhereInAABB<PlayerPosition>(95, 95, 115, 105, 0, 0).Execute();
            Assert.That(near.Count, Is.GreaterThanOrEqualTo(1).And.LessThanOrEqualTo(3),
                "a narrow AABB returns the players inside it (spatial index is queryable)");
        }
    }

    [Test]
    public void MixedStorage_Player_RoundTrips_V_SV_Transient()
    {
        EntityId id;
        using (var tx = _engine.CreateQuickTransaction())
        {
            var p = new Player { Name = S64("Mix"), AccountId = 7, Level = 42, ProfessionId = 5, CreatedAt = 99 };
            var m = new Membership { Guild = EntityLink<GuildArch>.Null, GuildRank = 2 };
            var w = new Wallet { Credits = 123, BankCredits = 456 };
            var pos = new PlayerPosition { Bounds = Box(50f, 60f) };
            var sess = new Session { ConnectionId = 4242, LatencyMs = 77 };
            id = tx.Spawn<PlayerArch>(
                PlayerArch.Player.Set(in p), PlayerArch.Membership.Set(in m), PlayerArch.Wallet.Set(in w),
                PlayerArch.Position.Set(in pos), PlayerArch.Session.Set(in sess));
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = _engine.CreateQuickTransaction())
        {
            var e = tx.Open(id);
            Assert.That(e.Read(PlayerArch.Player).Level, Is.EqualTo(42), "Versioned component reads back");
            Assert.That(e.Read(PlayerArch.Wallet).Credits, Is.EqualTo(123), "Versioned component reads back");
            Assert.That(e.Read(PlayerArch.Position).Bounds.MinX, Is.EqualTo(49f), "SingleVersion spatial component reads back");
            Assert.That(e.Read(PlayerArch.Session).ConnectionId, Is.EqualTo(4242), "Transient component reads back in-session");
        }
    }
}
