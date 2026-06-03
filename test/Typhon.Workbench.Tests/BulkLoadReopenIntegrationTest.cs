using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Reproducer for the "Schema migration required" banner Loïc hits when reopening a BulkLoad-generated database
/// at scale. From the failing-run banner:
/// <list type="bullet">
///   <item><c>Typhon.Workbench.Fixture.CompA — schema_error</c></item>
///   <item><c>Typhon.Workbench.Fixtures.CompAArch — archetype_finalize_failed</c></item>
///   <item><c>Typhon.Workbench.Fixtures.CompABArch — archetype_finalize_failed</c></item>
/// </list>
/// <para>
/// Hypothesis under test: same lost-write race that <see cref="RawValueHashMapScaleRepro"/> surfaces on a directory
/// chunk at ~700 K hashmap inserts, biting a different page here — the metadata page holding CompA's persisted
/// <c>ComponentR1</c> chunk. CompA is the first-registered component, so its chunk lands at the lowest slot of
/// <c>_componentsTable.ComponentSegment</c> on the most-frequently-touched metadata page. The CompAArch /
/// CompABArch failures are cascade fallout (both reference CompA, which never re-registers cleanly on reopen).
/// </para>
/// <para>
/// The <c>schema_error</c> tag (vs <c>breaking_change</c>) confirms it is not a structural schema diff — it is a
/// generic exception during <c>RegisterComponentByType</c>, consistent with corrupted bytes that fail to even
/// deserialize into a <c>ComponentR1</c>. This is the signature of a torn / stale page write losing trailing bytes,
/// not of an added / removed / type-changed field.
/// </para>
/// </summary>
/// <remarks>
/// Marked <see cref="ExplicitAttribute"/> — runs the bulk fixture generation + a reopen at each scale, which
/// takes ~30 s – 2 min per case depending on entity count. Use as the entry-point repro for the stability
/// initiative: when the lost-write race is closed, this test should go green at every scale.
/// <para>
/// Invoke:
/// <code>dotnet test test/Typhon.Workbench.Tests/Typhon.Workbench.Tests.csproj --filter "FullyQualifiedName~BulkLoadReopen"</code>
/// </para>
/// </remarks>
[TestFixture]
[Explicit("Stability-initiative entry-point repro — bulk-load + reopen at scale; minutes per case")]
public sealed class BulkLoadReopenIntegrationTest
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-bulkload-reopen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Bisect-style scales. Pick a small one (should pass per Loïc's manual report), a medium one (probe), and a
    /// few larger ones to chase the failure boundary. ParticleArchCount dominates because it's a SingleVersion
    /// cluster archetype and drives the bulk-spawn page-cache pressure profile most aggressively.
    /// </summary>
    [TestCase(1_000_000, TestName = "scale_1m_entities")]
    [TestCase(3_000_000, TestName = "scale_3m_entities")]
    [TestCase(8_500_000, TestName = "scale_8.5m_loic_screenshot_half")]
    [TestCase(17_000_000, TestName = "scale_17m_loic_screenshot_full")]
    public async Task BulkLoad_Reopen_AtScale_ShouldNotRequireMigration(int totalEntities)
    {
        // Mirror Loïc's failing screenshot config proportions (17 M total: 4 M / 2 M / 2 M / 400 k / 200 k / 400 k
        // / 8 M = 17 M). At smaller totals we keep the same relative shape so the pressure profile scales
        // smoothly — CompA-heavy registration + Particle-heavy SingleVersion spawn is what stresses the metadata
        // pages I suspect are the lost-write victim. Naive equal-proportion configs (small CompAArchCount, tiny
        // other counts) at 1 M / 3 M don't hit the failure boundary — confirmed by earlier runs.
        const double Reference = 17_000_000.0;
        // Use double arithmetic — int*int overflows for reference=4_000_000 × totalEntities=17_000_000 (= 6.8e13).
        int Scaled(int reference) => Math.Max(1, (int)Math.Round((double)reference * totalEntities / Reference));
        // Map the 17 M SWG breakdown through Scaled() so the test's totalEntities param drives the shape (bulk path:
        // CC contents + enable/disable + cascade are skipped, so only the counts matter here).
        var cfg = FixtureConfig.Default with
        {
            ResourceTypeCount = Scaled(10_000),
            GuildCount        = Scaled(200_000),
            RecipeCount       = Scaled(10_000),
            PlayerCount       = Scaled(4_000_000),
            DepositCount      = Scaled(1_000_000),
            HarvesterCount    = Scaled(3_000_000),
            FactoryCount      = Scaled(780_000),
            ItemCount         = Scaled(8_000_000),
        };

        // ─── Phase 1: bulk-generate the fixture ────────────────────────────────────────────────────────
        var genSw = Stopwatch.StartNew();
        var lastReportAt = Stopwatch.StartNew();
        var progress = new Progress<FixtureProgressReport>(p =>
        {
            // Periodic progress log so a stuck phase shows up in the test output. dotnet test buffers stdout
            // until completion; the messages still help in post-mortem.
            if (lastReportAt.ElapsedMilliseconds < 10_000) return;
            lastReportAt.Restart();
            var pct = p.Total > 0 ? $"{(100.0 * p.Completed / p.Total):F1}%" : "—";
            TestContext.Out.WriteLine($"[{genSw.Elapsed:mm\\:ss}] {p.Phase}: {p.Completed:N0} / {p.Total:N0} ({pct})");
        });

        FixtureGenerationResult result;
        try
        {
            result = FixtureDatabase.CreateOrReuse(
                outputDir: _tempDir,
                force: true,
                config: cfg,
                progress: progress,
                ct: CancellationToken.None,
                databaseName: $"reopen-{totalEntities}",
                useBulkLoad: true);
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"GENERATION FAILED at {genSw.Elapsed:mm\\:ss}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        genSw.Stop();

        TestContext.Out.WriteLine($"[{totalEntities:N0}] generated in {genSw.Elapsed:mm\\:ss}: {result.TyphonFilePath}");
        TestContext.Out.WriteLine($"    WasCreated     : {result.WasCreated}");
        TestContext.Out.WriteLine($"    TotalEntities  : {result.TotalEntities:N0}");
        var binPath = Path.Combine(Path.GetDirectoryName(result.TyphonFilePath)!,
            Path.GetFileNameWithoutExtension(result.TyphonFilePath) + ".bin");
        if (File.Exists(binPath))
        {
            var binSize = new FileInfo(binPath).Length;
            TestContext.Out.WriteLine($"    .bin size      : {binSize:N0} bytes ({binSize / 1024.0 / 1024.0:F1} MB)");
        }
        else
        {
            TestContext.Out.WriteLine($"    .bin           : MISSING at {binPath}");
        }
        Assert.That(File.Exists(result.TyphonFilePath), Is.True, ".typhon marker must exist after CreateOrReuse");
        Assert.That(result.WasCreated, Is.True, $"force=true should have regenerated; got WasCreated=false (TotalEntities={result.TotalEntities})");
        Assert.That(result.TotalEntities, Is.GreaterThan(totalEntities / 2),
            $"BulkLoad should have spawned approximately {totalEntities:N0} entities; got only {result.TotalEntities:N0}");

        // ─── Phase 2: cold reopen via the same EngineLifecycle path the Workbench uses ────────────────
        // This is the actual repro: a fresh load context, schema DLLs probed from the database directory,
        // RegisterComponentByType called for every persisted component, archetype RunClassConstructor +
        // EnsureFinalized for every archetype, and InitializeArchetypes wiring the per-engine state. If any
        // of those steps throw, the lifecycle's State is non-Ready and the Diagnostics array tells us which
        // component failed and why.
        var reopenSw = Stopwatch.StartNew();
        using var lifecycle = await EngineLifecycle.OpenAsync(result.TyphonFilePath);
        reopenSw.Stop();

        // Always dump the full diagnostics on test output so a failure leaves a complete forensic trail.
        TestContext.Out.WriteLine($"[{totalEntities:N0}] reopened in {reopenSw.Elapsed:mm\\:ss}");
        TestContext.Out.WriteLine($"    State              : {lifecycle.State}");
        TestContext.Out.WriteLine($"    LoadedComponents   : {lifecycle.LoadedComponentTypes}");
        TestContext.Out.WriteLine($"    Diagnostics ({lifecycle.Diagnostics.Length}):");
        foreach (var d in lifecycle.Diagnostics)
        {
            TestContext.Out.WriteLine($"      • {d.ComponentName} / {d.Kind}");
            TestContext.Out.WriteLine($"        {Trunc(d.Detail, 400)}");
        }

        // Build a compact summary for the assertion message — same info as the per-line dump above, but on one
        // line so the test-runner failure list shows the smoking gun without having to drill into the log.
        var diagSummary = lifecycle.Diagnostics.Length == 0
            ? "(no diagnostics)"
            : string.Join(" | ", lifecycle.Diagnostics.Select(d => $"{d.ComponentName}={d.Kind}"));

        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready),
            $"Reopen at {totalEntities:N0} entities should not require migration. " +
            $"Diagnostics: {diagSummary}");
    }

    private static string Trunc(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
