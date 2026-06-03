using System.Threading;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Coverage for the SWG <see cref="FixtureConfig"/> + the configurable <see cref="FixtureDatabase.CreateOrReuse"/> path.
/// Asserts:
///   - default config produces the expected entity total;
///   - custom counts flow through end-to-end (returned <see cref="FixtureGenerationResult.TotalEntities"/> matches the config's estimate);
///   - edge configs (empty pools, no factories) generate without throwing — the FK-null fallbacks hold;
///   - cache-hash semantics: same config + no force → reuse; different config → regenerate; force → always regenerate;
///   - hash stability: identical configs hash to the same string; field tweaks change the hash;
///   - progress reports fire at the new phase boundaries (incl. the enable/disable + cascade passes);
///   - cancellation between sub-batches throws <see cref="OperationCanceledException"/>;
///   - the database-name validation contract (shared regex with the client).
/// Generating the Default/small configs runs the FULL Populate path — FK wiring, CC accessor, spatial placement,
/// enable/disable seeding, and cascade delete all execute — so these tests double as a generation smoke for every
/// engine feature the schema exercises. Public-API feature ASSERTIONS live in <see cref="SwgFixtureFeatureTests"/>.
/// Uses a per-test temp directory cleaned up in TearDown.
/// </summary>
[TestFixture]
public sealed class FixtureConfigTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-fixture-cfg-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void Default_Config_Produces_Expected_Total()
    {
        // The 2-arg overload (no config) used by E2E specs + the manual NUnit generator. Default total:
        // 60 RT + 50 Guild + 200 Recipe + 2000 Player + 1000 Deposit + 800 Harvester + 200 Factory + 5000 Item = 9310.
        var result = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        Assert.That(result.WasCreated, Is.True);
        Assert.That(result.TotalEntities, Is.EqualTo(FixtureConfig.Default.TotalSpawnEstimate));
        Assert.That(result.TotalEntities, Is.EqualTo(9_310));
    }

    [Test]
    public void Custom_Config_Honors_Requested_Counts_In_Total_Estimate()
    {
        var cfg = FixtureConfig.Default with
        {
            ResourceTypeCount = 10, GuildCount = 2, RecipeCount = 5, PlayerCount = 5,
            DepositCount = 3, HarvesterCount = 2, FactoryCount = 1, ItemCount = 10,
        };
        var result = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);
        Assert.That(result.TotalEntities, Is.EqualTo(cfg.TotalSpawnEstimate));
        Assert.That(result.TotalEntities, Is.EqualTo(38));
    }

    [Test]
    public void Edge_Config_With_Empty_Pools_Generates_Without_Throwing()
    {
        // Regression guard for the FK-null fallbacks: no guilds (Membership.Guild → Null), no recipes
        // (Factory/Item.Recipe → Null), no deposits (Harvester.Target → Null), polymorphism off (no Factories).
        // Structures + Items still reference the (present) players. Must not throw or index into an empty array.
        var cfg = FixtureConfig.Default with
        {
            ResourceTypeCount = 5, GuildCount = 0, RecipeCount = 0, PlayerCount = 10,
            DepositCount = 0, HarvesterCount = 5, FactoryCount = 0, ItemCount = 8,
            IncludePolymorphicStructure = false,
        };
        var result = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);
        Assert.That(result.TotalEntities, Is.EqualTo(cfg.TotalSpawnEstimate));
        Assert.That(result.TotalEntities, Is.EqualTo(28));
    }

    [Test]
    public void Reuse_On_Second_Call_Same_Config_Skips_Generation()
    {
        var cfg = FixtureConfig.Default with { PlayerCount = 7 };
        var first = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);
        Assert.That(first.WasCreated, Is.True);

        var second = FixtureDatabase.CreateOrReuse(_tempDir, force: false, cfg);
        Assert.That(second.WasCreated, Is.False);
        Assert.That(second.TotalEntities, Is.EqualTo(0));
    }

    [Test]
    public void Different_Config_Invalidates_Cache_And_Regenerates()
    {
        var cfgA = FixtureConfig.Default with { PlayerCount = 7 };
        var first = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfgA);
        Assert.That(first.WasCreated, Is.True);

        var cfgB = cfgA with { PlayerCount = 9 };
        var second = FixtureDatabase.CreateOrReuse(_tempDir, force: false, cfgB);
        Assert.That(second.WasCreated, Is.True);
        Assert.That(second.TotalEntities, Is.EqualTo(cfgB.TotalSpawnEstimate));
    }

    [Test]
    public void Force_Regenerates_Even_When_Hash_Matches()
    {
        var cfg = FixtureConfig.Default with { PlayerCount = 7 };
        FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);

        var second = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);
        Assert.That(second.WasCreated, Is.True);
        Assert.That(second.TotalEntities, Is.EqualTo(cfg.TotalSpawnEstimate));
    }

    [Test]
    public void Hash_Is_Stable_For_Equal_Configs()
    {
        var cfgA = FixtureConfig.Default with { Seed = 42, PlayerCount = 7, OnlinePlayerFraction = 0.3 };
        var cfgB = FixtureConfig.Default with { Seed = 42, PlayerCount = 7, OnlinePlayerFraction = 0.3 };
        Assert.That(cfgA.Hash(), Is.EqualTo(cfgB.Hash()));
    }

    [Test]
    public void Hash_Changes_When_Any_Field_Differs()
    {
        var baseline = FixtureConfig.Default;
        Assert.That((baseline with { Seed = 1 }).Hash(), Is.Not.EqualTo(baseline.Hash()));
        Assert.That((baseline with { PlayerCount = 999 }).Hash(), Is.Not.EqualTo(baseline.Hash()));
        Assert.That((baseline with { OnlinePlayerFraction = 0.5 }).Hash(), Is.Not.EqualTo(baseline.Hash()));
        Assert.That((baseline with { PlayersPerGuildShape = PlayersPerGuildShape.Uniform }).Hash(), Is.Not.EqualTo(baseline.Hash()));
        Assert.That((baseline with { IncludePolymorphicStructure = false }).Hash(), Is.Not.EqualTo(baseline.Hash()));
    }

    [Test]
    public void Progress_Reports_Fire_For_Each_Phase()
    {
        var phases = new List<string>();
        var progress = new Progress<FixtureProgressReport>(p =>
        {
            if (phases.Count == 0 || phases[^1] != p.Phase) phases.Add(p.Phase);
        });
        var cfg = FixtureConfig.Default with
        {
            ResourceTypeCount = 3, GuildCount = 3, RecipeCount = 3, PlayerCount = 3,
            DepositCount = 3, HarvesterCount = 3, FactoryCount = 3, ItemCount = 3,
        };
        FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg, progress, CancellationToken.None);

        // Progress<T> dispatches via the threadpool in the absence of a sync context — tiny sleep so tail reports land.
        Thread.Sleep(50);

        Assert.That(phases, Does.Contain("Preparing directory"));
        Assert.That(phases.Any(p => p.StartsWith("Spawning ResourceTypes")), Is.True);
        Assert.That(phases.Any(p => p.StartsWith("Spawning Players")), Is.True);
        Assert.That(phases.Any(p => p.StartsWith("Spawning Items")), Is.True);
        Assert.That(phases.Any(p => p.StartsWith("Disabling offline Sessions")), Is.True);
        Assert.That(phases.Any(p => p.StartsWith("Cascade-deleting players")), Is.True);
        Assert.That(phases, Does.Contain("Checkpointing"));
    }

    [Test]
    public void Cancellation_Before_First_Batch_Throws_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => FixtureDatabase.CreateOrReuse(_tempDir, force: true, FixtureConfig.Default, null, cts.Token));
    }

    [Test]
    public void Custom_DatabaseName_Drives_Output_Filenames()
    {
        var result = FixtureDatabase.CreateOrReuse(_tempDir, force: true, config: null, progress: null,
            ct: CancellationToken.None, databaseName: "swg-v2");
        Assert.That(result.WasCreated, Is.True);
        Assert.That(Path.GetFileName(result.TyphonFilePath), Is.EqualTo("swg-v2.typhon"));
        Assert.That(File.Exists(result.TyphonFilePath), Is.True);
        var fixtureDir = Path.GetDirectoryName(result.TyphonFilePath)!;
        Assert.That(File.Exists(Path.Combine(fixtureDir, "swg-v2.bin")), Is.True);
    }

    [Test]
    public void Invalid_DatabaseName_Throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => FixtureDatabase.CreateOrReuse(_tempDir, force: true, config: null, progress: null,
                ct: CancellationToken.None, databaseName: "has spaces"));
        Assert.Throws<ArgumentException>(
            () => FixtureDatabase.CreateOrReuse(_tempDir, force: true, config: null, progress: null,
                ct: CancellationToken.None, databaseName: "with/slash"));
        Assert.Throws<ArgumentException>(
            () => FixtureDatabase.CreateOrReuse(_tempDir, force: true, config: null, progress: null,
                ct: CancellationToken.None, databaseName: ""));
    }

    [TestCase("base-tests", true)]
    [TestCase("swg_v2", true)]
    [TestCase("A", true)]
    [TestCase("", false)]
    [TestCase("has spaces", false)]
    [TestCase("with/slash", false)]
    [TestCase("with.dots", false)]
    public void TryValidateDatabaseName_Mirrors_Client_Regex(string candidate, bool expectedValid)
    {
        var isValid = FixtureDatabase.TryValidateDatabaseName(candidate, out var _, out var error);
        Assert.That(isValid, Is.EqualTo(expectedValid),
            $"databaseName '{candidate}' expected valid={expectedValid}, error='{error}'");
        if (!expectedValid) Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    /// <summary>
    /// Back-pressure regression gate on the feature-complete (regular, non-bulk) path. The SWG schema is heavier
    /// per entity than the old flat fixture (Player alone = 5 components + indexes), and the enable/disable + cascade
    /// passes add post-spawn Open/Destroy churn — so this exercises the page-cache dirty-counter drain harder than
    /// the prior fixture did (project memory: DC inflation issue #133). MUST complete without
    /// <see cref="PageCacheBackpressureTimeoutException"/>. Tagged <c>Slow</c> — skipped by default; run via
    /// <c>--filter "Category=Slow"</c>. (Huge multi-million-entity scale is covered by the BulkLoad path test.)
    /// </summary>
    [Test]
    [Category("Slow")]
    public void Stress_Config_Completes_Without_Backpressure_Timeout()
    {
        var stress = FixtureConfig.Default with
        {
            ResourceTypeCount = 1_000,
            GuildCount = 500,
            RecipeCount = 2_000,
            PlayerCount = 40_000,
            DepositCount = 10_000,
            HarvesterCount = 10_000,
            FactoryCount = 2_000,
            ItemCount = 40_000,
        };

        FixtureGenerationResult result = default;
        Assert.DoesNotThrow(
            () => result = FixtureDatabase.CreateOrReuse(_tempDir, force: true, stress),
            "Stress config should complete without page-cache back-pressure timeout. If this throws "
            + nameof(PageCacheBackpressureTimeoutException) + ", the per-batch DC drain is insufficient.");

        Assert.That(result.WasCreated, Is.True);
        Assert.That(result.TotalEntities, Is.EqualTo(stress.TotalSpawnEstimate));
        Assert.That(result.TotalEntities, Is.EqualTo(105_500));
    }
}
