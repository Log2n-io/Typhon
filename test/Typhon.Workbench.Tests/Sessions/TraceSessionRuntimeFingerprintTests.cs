using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;
using Typhon.Profiler;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Regression guard for <see cref="TraceSessionRuntime"/>'s cache-reuse vs rebuild decision. The
/// runtime's <c>BuildAsync</c> probes an existing <c>.typhon-trace-cache</c> via
/// <see cref="TraceFileCacheReader.VerifyFingerprint"/> — if it matches, the build is skipped. If
/// the source changes (mtime or bytes), the fingerprint diverges and a full rebuild kicks off.
///
/// These tests pin that contract so a future refactor that moves fingerprinting around can't
/// silently break reopen-performance (every open triggers a full rebuild) or reopen-correctness
/// (stale cache served after the source was edited).
/// </summary>
[TestFixture]
public sealed class TraceSessionRuntimeFingerprintTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-fingerprint", Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public async Task FirstOpen_BuildsCache_AndFiresProgress()
    {
        var tracePath = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 3, instantsPerTick: 2);
        var progressCount = 0;

        using var runtime = TraceSessionRuntime.Start(tracePath, NullLogger.Instance);
        runtime.BuildProgressChanged += _ => progressCount++;
        var metadata = await runtime.MetadataReady;

        Assert.That(metadata, Is.Not.Null);
        Assert.That(progressCount, Is.GreaterThan(0),
            "first open on a fresh trace must build the cache; the build emits at least one progress tick");
        Assert.That(File.Exists(runtime.CacheFilePath), "sidecar cache should be on disk after build");
    }

    [Test]
    public async Task SecondOpen_ReusesCache_WhenSourceUnchanged()
    {
        var tracePath = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 3, instantsPerTick: 2);

        // First open — builds the cache and persists it to disk.
        using (var first = TraceSessionRuntime.Start(tracePath, NullLogger.Instance))
        {
            _ = await first.MetadataReady;
        }

        // Second open — cache exists, fingerprint matches → no rebuild. Subscribing before Start
        // isn't possible (Start kicks off the build immediately), so we subscribe right after and
        // accept a tiny race: a rebuild that finishes in < 1 ms wouldn't fire the event either way.
        // The clearer signal is that MetadataReady completes without any progress callbacks on the
        // reuse path (the "needsRebuild" branch is the only caller of the progress Action).
        var progressCount = 0;
        using var second = TraceSessionRuntime.Start(tracePath, NullLogger.Instance);
        second.BuildProgressChanged += _ => progressCount++;
        _ = await second.MetadataReady;

        Assert.That(progressCount, Is.EqualTo(0),
            "reused cache must not emit progress events — the rebuild branch is the sole source");
    }

    [Test]
    public async Task ThirdOpen_RebuildsCache_WhenSourceFingerprintChanges()
    {
        var tracePath = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 3, instantsPerTick: 2);

        // Prime the cache.
        using (var first = TraceSessionRuntime.Start(tracePath, NullLogger.Instance))
        {
            _ = await first.MetadataReady;
        }

        // Mutate the source so the fingerprint changes. Building a new trace to the same filename
        // changes mtime + length + content — all three inputs to ComputeSourceFingerprint, so the
        // SHA-256 MUST diverge. File.Delete first so TraceFixtureBuilder's Guid-suffixed path
        // collision doesn't produce a second file alongside the first.
        File.Delete(tracePath);
        // TraceFixtureBuilder appends a fresh Guid, so we write to a different name and rename.
        var replacement = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 4, instantsPerTick: 3);
        File.Move(replacement, tracePath, overwrite: true);

        var progressCount = 0;
        using var third = TraceSessionRuntime.Start(tracePath, NullLogger.Instance);
        third.BuildProgressChanged += _ => progressCount++;
        _ = await third.MetadataReady;

        Assert.That(progressCount, Is.GreaterThan(0),
            "fingerprint mismatch (source rewritten) must trigger a rebuild — progress events confirm it");
    }

    [Test]
    public async Task FingerprintPersistedInCache_MatchesSource()
    {
        var tracePath = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 2, instantsPerTick: 1);

        using var runtime = TraceSessionRuntime.Start(tracePath, NullLogger.Instance);
        _ = await runtime.MetadataReady;

        // Open the cache file independently and verify its stored fingerprint equals a freshly
        // computed one for the same source. This is the contract that makes the reuse decision
        // safe — if a refactor ever breaks the write path, the reuse branch would silently start
        // trusting stale caches.
        var freshFingerprint = new byte[32];
        TraceFileCacheReader.ComputeSourceFingerprint(tracePath, freshFingerprint);

        using var fs = File.OpenRead(runtime.CacheFilePath);
        using var reader = new TraceFileCacheReader(fs);
        Assert.That(reader.VerifyFingerprint(freshFingerprint), Is.True,
            "cache header's SourceFingerprint must equal ComputeSourceFingerprint(source)");
    }
}
