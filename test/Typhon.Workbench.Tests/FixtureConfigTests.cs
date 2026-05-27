using System.Threading;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Coverage for <see cref="FixtureConfig"/> + the configurable <see cref="FixtureDatabase.CreateOrReuse"/> path
/// landed alongside the Dev Fixture configurability work. Asserts:
///   - default config produces today's entity counts (back-compat with E2E specs + the manual NUnit generator);
///   - custom counts flow through end-to-end (returned <see cref="FixtureGenerationResult.TotalEntities"/> matches the config's estimate);
///   - cache-hash semantics: same config + no force → reuse; different config → regenerate; force → always regenerate;
///   - hash stability: identical configs hash to the same string across calls; field tweaks change the hash;
///   - progress reports fire at phase boundaries;
///   - cancellation between sub-batches throws <see cref="OperationCanceledException"/>.
/// Uses a per-test temp directory cleaned up in TearDown — mirrors the EngineLifecycleManifestTests pattern.
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
    public void Default_Config_Preserves_Baseline_Counts()
    {
        // The 2-arg overload (no config) used by existing E2E specs + the manual NUnit generator must continue to
        // produce the same total entity count. Defaults are: 1000 + 500 + 500 + 200 + 50 + 300 + 2000 = 4550.
        var result = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        Assert.That(result.WasCreated, Is.True);
        Assert.That(result.TotalEntities, Is.EqualTo(FixtureConfig.Default.TotalSpawnEstimate));
        Assert.That(result.TotalEntities, Is.EqualTo(4_550));
    }

    [Test]
    public void Custom_Config_Honors_Requested_Counts_In_Total_Estimate()
    {
        // Picking a non-default shape: shrink everything except Particles, which we crank up for fragmentation tests.
        var cfg = FixtureConfig.Default with
        {
            CompAArchCount = 10,
            CompABArchCount = 10,
            CompABCArchCount = 10,
            CompDArchCount = 5,
            GuildArchCount = 2,
            PlayerArchCount = 5,
            ParticleArchCount = 100,
        };
        var result = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);
        Assert.That(result.TotalEntities, Is.EqualTo(cfg.TotalSpawnEstimate));
        Assert.That(result.TotalEntities, Is.EqualTo(142));
    }

    [Test]
    public void Empty_Cores_Config_Generates_Without_Crashing_When_All_Counts_Zero()
    {
        // The Empty-cores preset variant — Players reference Guilds via id; with GuildArchCount=0 the code must fall
        // back to a sentinel id rather than indexing into an empty array. Regression guard for that branch.
        var cfg = FixtureConfig.Default with
        {
            CompAArchCount = 100,
            CompABArchCount = 0,
            CompABCArchCount = 0,
            CompDArchCount = 0,
            GuildArchCount = 0,
            PlayerArchCount = 50,    // referencing non-existent guilds — must not throw
            ParticleArchCount = 0,
            ParticleFragmentation = 0.0,
        };
        var result = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);
        Assert.That(result.TotalEntities, Is.EqualTo(150));
    }

    [Test]
    public void Reuse_On_Second_Call_Same_Config_Skips_Generation()
    {
        var cfg = FixtureConfig.Default with { CompAArchCount = 7 };
        var first = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);
        Assert.That(first.WasCreated, Is.True);

        // Same config + force=false → cache hit. The hash file written by the first call matches the requested hash,
        // so generation is skipped and TotalEntities = 0 (the "reused" signal).
        var second = FixtureDatabase.CreateOrReuse(_tempDir, force: false, cfg);
        Assert.That(second.WasCreated, Is.False);
        Assert.That(second.TotalEntities, Is.EqualTo(0));
    }

    [Test]
    public void Different_Config_Invalidates_Cache_And_Regenerates()
    {
        var cfgA = FixtureConfig.Default with { CompAArchCount = 7 };
        var first = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfgA);
        Assert.That(first.WasCreated, Is.True);

        // Switching config (different hash on disk vs requested) → the cache check fails even though the DB files
        // exist, so we regenerate. This is the contract the Dev Fixture dialog relies on: changing presets / tweaking
        // counts always reflects in the next "Generate & Open".
        var cfgB = cfgA with { CompAArchCount = 9 };
        var second = FixtureDatabase.CreateOrReuse(_tempDir, force: false, cfgB);
        Assert.That(second.WasCreated, Is.True);
        Assert.That(second.TotalEntities, Is.EqualTo(cfgB.TotalSpawnEstimate));
    }

    [Test]
    public void Force_Regenerates_Even_When_Hash_Matches()
    {
        var cfg = FixtureConfig.Default with { CompAArchCount = 7 };
        FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);

        // Same config but force=true → wipe + regenerate regardless. The dialog's "Force recreation" checkbox flows
        // through here; users hit it when they want a fresh seed materialisation despite no config change.
        var second = FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg);
        Assert.That(second.WasCreated, Is.True);
        Assert.That(second.TotalEntities, Is.EqualTo(cfg.TotalSpawnEstimate));
    }

    [Test]
    public void Hash_Is_Stable_For_Equal_Configs()
    {
        var cfgA = new FixtureConfig(
            CompAArchCount: 10, CompABArchCount: 5, CompABCArchCount: 3, CompDArchCount: 2,
            GuildArchCount: 1, PlayerArchCount: 4, ParticleArchCount: 50,
            ParticleFragmentation: 0.4, Seed: 42);
        var cfgB = new FixtureConfig(
            CompAArchCount: 10, CompABArchCount: 5, CompABCArchCount: 3, CompDArchCount: 2,
            GuildArchCount: 1, PlayerArchCount: 4, ParticleArchCount: 50,
            ParticleFragmentation: 0.4, Seed: 42);
        Assert.That(cfgA.Hash(), Is.EqualTo(cfgB.Hash()));
    }

    [Test]
    public void Hash_Changes_When_Any_Field_Differs()
    {
        var baseline = FixtureConfig.Default;
        Assert.That((baseline with { Seed = 1 }).Hash(), Is.Not.EqualTo(baseline.Hash()));
        Assert.That((baseline with { CompAArchCount = 999 }).Hash(), Is.Not.EqualTo(baseline.Hash()));
        Assert.That((baseline with { ParticleFragmentation = 0.5 }).Hash(), Is.Not.EqualTo(baseline.Hash()));
    }

    [Test]
    public void Progress_Reports_Fire_For_Each_Phase()
    {
        // Small counts so the test runs fast; the assertions are on phase coverage (which labels we see), not on
        // sub-batch granularity. With ProgressChunk = 5000 and counts well under that, only the per-phase boundary
        // reports fire (zero sub-batch reports for these counts).
        var phases = new List<string>();
        var progress = new Progress<FixtureProgressReport>(p =>
        {
            // Coalesce repeated phase labels — we only care about distinct phase transitions, not the firing rate.
            if (phases.Count == 0 || phases[^1] != p.Phase) phases.Add(p.Phase);
        });
        var cfg = FixtureConfig.Default with
        {
            CompAArchCount = 5, CompABArchCount = 5, CompABCArchCount = 5, CompDArchCount = 5,
            GuildArchCount = 5, PlayerArchCount = 5, ParticleArchCount = 5,
            ParticleFragmentation = 0.5,
        };
        FixtureDatabase.CreateOrReuse(_tempDir, force: true, cfg, progress, CancellationToken.None);

        // Progress<T> dispatches via the synchronization context; in the absence of one (NUnit test thread), the
        // callback runs on the threadpool. Tiny sleep so any tail dispatches land before we assert the list contents.
        Thread.Sleep(50);

        // We expect to see (at least) the major phase boundaries: directory prep, each archetype's spawn label, the
        // particle destroy, the checkpoint, and the marker write.
        Assert.That(phases, Does.Contain("Preparing directory"));
        Assert.That(phases.Any(p => p.StartsWith("Spawning Guilds")), Is.True);
        Assert.That(phases.Any(p => p.StartsWith("Spawning CompA archetypes")), Is.True);
        Assert.That(phases.Any(p => p.StartsWith("Spawning Particles")), Is.True);
        Assert.That(phases.Any(p => p.StartsWith("Destroying Particle subset")), Is.True);
        Assert.That(phases, Does.Contain("Checkpointing"));
    }

    [Test]
    public void Cancellation_Before_First_Batch_Throws_OperationCanceledException()
    {
        // Pre-cancelled token → the directory-prep ThrowIfCancellationRequested fires before any Spawn loop runs.
        // The background-task wrapper in the Controller catches this and surfaces "cancelled" terminal state to the
        // client; the underlying contract is just that the exception propagates from CreateOrReuse.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => FixtureDatabase.CreateOrReuse(_tempDir, force: true, FixtureConfig.Default, null, cts.Token));
    }

    [Test]
    public void Custom_DatabaseName_Drives_Output_Filenames()
    {
        // The on-disk filenames must reflect the requested database name — same shape as DefaultDatabaseName but
        // arbitrary. Used by the Dev Fixture dialog so users can generate sibling "stress-tests" / "fragmented" DBs
        // alongside the default without naming collisions.
        var result = FixtureDatabase.CreateOrReuse(_tempDir, force: true, config: null, progress: null,
            ct: CancellationToken.None, databaseName: "stress-v2");
        Assert.That(result.WasCreated, Is.True);
        Assert.That(Path.GetFileName(result.TyphonFilePath), Is.EqualTo("stress-v2.typhon"));
        Assert.That(File.Exists(result.TyphonFilePath), Is.True);
        // CreateOrReuse composes `{outputDir}/{databaseName}/` for the per-database working dir, so the `.bin`
        // lives alongside the `.typhon` inside that sub-folder, not directly in `_tempDir`.
        var fixtureDir = Path.GetDirectoryName(result.TyphonFilePath)!;
        Assert.That(File.Exists(Path.Combine(fixtureDir, "stress-v2.bin")), Is.True);
    }

    [Test]
    public void Invalid_DatabaseName_Throws_ArgumentException()
    {
        // Defence-in-depth: even though the controller filters bad names before calling, CreateOrReuse rejects them
        // too so a programmatic caller (or a test) can't sneak path traversal / disallowed chars in.
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
    [TestCase("stress_v2", true)]
    [TestCase("A", true)]
    [TestCase("", false)]
    [TestCase("has spaces", false)]
    [TestCase("with/slash", false)]
    [TestCase("with.dots", false)]
    public void TryValidateDatabaseName_Mirrors_Client_Regex(string candidate, bool expectedValid)
    {
        // The client and server share the same 1-64 / [a-zA-Z0-9_-] regex — keep them aligned with a parametrised
        // test on both sides so a future change must update both or the test will fail.
        var isValid = FixtureDatabase.TryValidateDatabaseName(candidate, out var _, out var error);
        Assert.That(isValid, Is.EqualTo(expectedValid),
            $"databaseName '{candidate}' expected valid={expectedValid}, error='{error}'");
        if (!expectedValid) Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    /// <summary>
    /// Mirror of the client's "Stress" preset (see <c>devFixtureFormReducer.ts</c>): 420k total entities including
    /// 200k particles. The point of this test is reproducibility of the page-cache back-pressure timeout — historically
    /// the single-mega-tx Populate blew through the dirty-counter budget on this preset (project memory: DC inflation
    /// issue #133, "100K+ hits backpressure"). The fix MUST drain the dirty counter between sub-batches so this
    /// preset completes without throwing <see cref="PageCacheBackpressureTimeoutException"/>.
    ///
    /// Tagged <c>Slow</c> so the default test run skips it; CI / local back-pressure regression checks pick it up via
    /// <c>--filter "Category=Slow"</c>. Runtime budget on a Zen 4 dev machine: &lt;2 min for the healthy case; the
    /// `dotnet test` invocation supplies the hard timeout (NUnit's `[Timeout]` is unsupported on net10).
    /// </summary>
    [Test]
    [Category("Slow")]
    public void Stress_Preset_Completes_Without_Backpressure_Timeout()
    {
        // Mirror client preset values byte-for-byte — this is the canonical back-pressure exerciser.
        var stress = new FixtureConfig(
            CompAArchCount:       100_000,
            CompABArchCount:       50_000,
            CompABCArchCount:      50_000,
            CompDArchCount:        10_000,
            GuildArchCount:           500,
            PlayerArchCount:       10_000,
            ParticleArchCount:    200_000,
            ParticleFragmentation:    0.40,
            Seed:             123_456_789);

        FixtureGenerationResult result = default;
        Assert.DoesNotThrow(
            () => result = FixtureDatabase.CreateOrReuse(_tempDir, force: true, stress),
            "Stress preset should complete without page-cache back-pressure timeout. If this throws "
            + nameof(PageCacheBackpressureTimeoutException) + ", the per-batch DC drain is insufficient.");

        Assert.That(result.WasCreated, Is.True);
        Assert.That(result.TotalEntities, Is.EqualTo(stress.TotalSpawnEstimate));
        Assert.That(result.TotalEntities, Is.EqualTo(420_500));
    }

    /// <summary>
    /// XL configuration — mirrors a user-reported back-pressure repro from 2026-05-26 with 4.25M total entities
    /// (10× the Stress preset). Catches scaling regressions in the page-cache / WAL configuration of the fixture
    /// engine. The 64 MB page cache that worked for Stress was too small for this scale; the engine config now
    /// matches the Workbench's own open path (512 MB cache + 64 MB WAL ring buffer).
    /// Tagged <c>Slow</c>; budget on the dev machine: well under a minute when healthy.
    /// </summary>
    [Test]
    [Category("Slow")]
    public void Xl_Config_Completes_Without_Backpressure_Timeout()
    {
        var xl = new FixtureConfig(
            CompAArchCount:     1_000_000,
            CompABArchCount:      500_000,
            CompABCArchCount:     500_000,
            CompDArchCount:       100_000,
            GuildArchCount:        50_000,
            PlayerArchCount:      100_000,
            ParticleArchCount:  2_000_000,
            ParticleFragmentation:    0.40,
            Seed:             123_456_789);

        FixtureGenerationResult result = default;
        Assert.DoesNotThrow(
            () => result = FixtureDatabase.CreateOrReuse(_tempDir, force: true, xl),
            "4.25M-entity XL config should complete without page-cache back-pressure timeout.");

        Assert.That(result.WasCreated, Is.True);
        Assert.That(result.TotalEntities, Is.EqualTo(xl.TotalSpawnEstimate));
        Assert.That(result.TotalEntities, Is.EqualTo(4_250_000));
    }
}
