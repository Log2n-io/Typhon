using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// End-to-end stress test for the BulkLoad fixture path at <b>17 M entities</b> on the SWG schema
/// (10 k ResourceType + 200 k Guild + 10 k Recipe + 4 M Player + 1 M Deposit + 3 M Harvester + 780 k Factory
/// + 8 M Item, seed 123_456_789). The whole-feature acceptance criterion AC-10 (issue #380):
/// a 17 M config that historically stalls at &lt; 1 % completes in &lt; 3 min via BulkLoad.
/// </summary>
/// <remarks>
/// <para>
/// This is the canonical "Loïc's screenshot config works" gate. Marked <see cref="ExplicitAttribute"/> because
/// it takes minutes to run — too heavy for the default CI suite. Invoke directly via
/// <c>dotnet test --filter "FullyQualifiedName~BulkLoad17M"</c>.
/// </para>
/// <para>
/// Progress is logged every ~30 s via the standard <see cref="IProgress{T}"/> hook so the test output shows
/// forward motion (or lack thereof — if the destroy phase stalls again the log will show it).
/// </para>
/// </remarks>
[TestFixture]
[Explicit("17 M-entity stress test — takes minutes to run; invoke explicitly")]
public sealed class BulkLoad17MFixtureTest
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-bulkload-17m-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void BulkLoad_17M_Custom_Config_Generates_Successfully()
    {
        // 17 M-total SWG config exercised via the BulkLoad path (spawn-only: empty CCs, no enable/disable/cascade).
        var cfg = FixtureConfig.Default with
        {
            ResourceTypeCount = 10_000,
            GuildCount = 200_000,
            RecipeCount = 10_000,
            PlayerCount = 4_000_000,
            DepositCount = 1_000_000,
            HarvesterCount = 3_000_000,
            FactoryCount = 780_000,
            ItemCount = 8_000_000,
        };

        Assert.That(cfg.TotalSpawnEstimate, Is.EqualTo(17_000_000), "config should sum to 17 M entities");

        // Phase-keyed progress logging every ~10 s. Writes to BOTH TestContext.Out (post-mortem in the
        // test runner) AND a known log file (`%TEMP%\typhon-bulkload-17m.log`) that can be tail-ed live
        // while the test runs — dotnet test buffers stdout until completion, so the log file is the only
        // way to see progress streaming in real time.
        var logPath = Path.Combine(Path.GetTempPath(), "typhon-bulkload-17m.log");
        File.WriteAllText(logPath, $"=== BulkLoad 17 M test started at {DateTime.Now:O} ===\n");
        var lastLogAt = Stopwatch.StartNew();
        var totalSw = Stopwatch.StartNew();
        var progress = new Progress<FixtureProgressReport>(p =>
        {
            if (lastLogAt.ElapsedMilliseconds < 10_000) return;
            lastLogAt.Restart();
            var pctText = p.Total > 0 ? $"{(100.0 * p.Completed / p.Total):F2} %" : "—";
            var line = $"[{totalSw.Elapsed:hh\\:mm\\:ss}] {p.Phase}: {p.Completed:N0} / {p.Total:N0} ({pctText})";
            TestContext.Out.WriteLine(line);
            try { File.AppendAllText(logPath, line + "\n"); } catch { /* best-effort */ }
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
                databaseName: "high-5",
                useBulkLoad: true);
        }
        catch (Exception ex)
        {
            // Capture full exception details into the streaming log so a truncated test output doesn't hide them.
            try
            {
                File.AppendAllText(logPath,
                    $"\n=== EXCEPTION at {totalSw.Elapsed:hh\\:mm\\:ss} ===\n" +
                    $"{ex.GetType().FullName}: {ex.Message}\n" +
                    $"{ex.StackTrace}\n" +
                    (ex.InnerException is not null ? $"INNER: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}\n" : "") +
                    "=== END EXCEPTION ===\n");
            }
            catch { /* best-effort */ }
            throw;
        }

        totalSw.Stop();

        TestContext.Out.WriteLine($"=== BulkLoad 17 M complete in {totalSw.Elapsed:hh\\:mm\\:ss} ===");
        TestContext.Out.WriteLine($"    TyphonFilePath: {result.TyphonFilePath}");
        TestContext.Out.WriteLine($"    TotalEntities:  {result.TotalEntities:N0}");
        TestContext.Out.WriteLine($"    WasCreated:     {result.WasCreated}");

        Assert.That(result.WasCreated, Is.True, "fixture should have been created (force=true)");
        Assert.That(result.TotalEntities, Is.EqualTo(17_000_000));
        Assert.That(File.Exists(result.TyphonFilePath), Is.True, ".typhon marker should exist on disk");
    }
}
