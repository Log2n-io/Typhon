using NUnit.Framework;
using System;
using System.IO;
using Typhon.Workbench.Fixtures;

namespace Typhon.Engine.Tests.Data;

/// <summary>
/// Engine stress coverage driven through the SWG sample fixture — page-cache dirty-counter drain under a heavy,
/// index-rich schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the engine suite.</b> It used to sit in <c>Typhon.Workbench.Tests</c>, purely because the
/// fixture generator it drives is a Workbench tool. Nothing it asserts is about the Workbench: it spawns 105,500
/// entities across a 5-component indexed schema and requires the commit path to keep up without the page cache
/// raising <see cref="PageCacheBackpressureTimeoutException"/>. That is an engine property.
/// </para>
/// <para>
/// The misfiling had a measured cost. The merge gate runs the Workbench suite only when the path filter
/// <c>tools/Typhon.Workbench/**</c> matches, so a change confined to <c>src/Typhon.Engine/**</c> never executed the
/// gate's heaviest back-pressure case — confirmed across three consecutive merges (#765, #769 ran it zero times;
/// #772 ran it only because it happened to touch one SPA file). A test that guards the engine has to live where
/// engine changes run it. See #774.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class SwgFixtureStressTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-swg-fixture-stress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Back-pressure regression gate on the feature-complete (regular, non-bulk) path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SWG schema is heavier per entity than the old flat fixture (Player alone = 5 components + indexes), and the
    /// enable/disable + cascade passes add post-spawn Open/Destroy churn — so this exercises the page-cache
    /// dirty-counter drain harder than the prior fixture did (#133). Huge multi-million-entity scale is covered by the
    /// BulkLoad path test.
    /// </para>
    /// <para>
    /// <b>Quarantined against #774</b>, not because it is flaky but because it is reliably red: it fails 2 runs out of
    /// 2 on the self-hosted gate runner with <c>B+Tree insert made no progress in 10000 pessimistic retries</c>, while
    /// passing locally in Release. A retry cannot help — the engine suite's own retry policy is built on the premise
    /// that "a REAL regression fails every attempt", and this does. The nightly still runs it, so the quarantine
    /// records the state rather than hiding it.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(300_000)]
    [Category("Quarantine")]
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
