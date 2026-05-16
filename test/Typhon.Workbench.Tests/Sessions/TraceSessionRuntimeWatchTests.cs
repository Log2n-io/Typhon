using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Covers <see cref="TraceSessionRuntime.NewVersionAvailable"/> — the debounced <see cref="FileSystemWatcher"/>
/// that detects when the source <c>.typhon-trace</c> is overwritten on disk (a profiling re-run regenerating
/// the same file). Detection is fingerprint-verified, so only a genuine content change flips the flag.
/// </summary>
[TestFixture]
public sealed class TraceSessionRuntimeWatchTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-tracewatch", Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public async Task NewVersionAvailable_IsFalse_WhenSourceUnchanged()
    {
        var tracePath = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 3, instantsPerTick: 2);

        using var runtime = TraceSessionRuntime.Start(tracePath, NullLogger.Instance);
        _ = await runtime.MetadataReady;

        // Give the watcher a window in which it could (wrongly) fire — nothing touched the file.
        await Task.Delay(300);
        Assert.That(runtime.NewVersionAvailable, Is.False,
            "an untouched source file must never flip the new-version flag");
    }

    [Test]
    public async Task NewVersionAvailable_FlipsTrue_WhenSourceOverwritten()
    {
        var tracePath = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 3, instantsPerTick: 2);

        using var runtime = TraceSessionRuntime.Start(tracePath, NullLogger.Instance);
        _ = await runtime.MetadataReady;
        // The watcher is armed synchronously right after the build completes; a short settle removes
        // the microscopic race between MetadataReady resolving and StartSourceWatch running.
        await Task.Delay(150);
        Assert.That(runtime.NewVersionAvailable, Is.False, "flag must start clear");

        // Simulate a profiling re-run: overwrite the source in place with a structurally different trace.
        // Content + length + mtime all change → ComputeSourceFingerprint diverges → detection must fire.
        var replacement = TraceFixtureBuilder.BuildMinimalTrace(_tempDir, tickCount: 6, instantsPerTick: 4);
        File.Copy(replacement, tracePath, overwrite: true);

        // Wait out the 1 s debounce + fingerprint re-compute. Poll rather than a fixed sleep so the test
        // finishes as soon as detection lands instead of always paying the worst-case wait.
        var detected = await WaitForAsync(() => runtime.NewVersionAvailable, timeoutMs: 4000);
        Assert.That(detected, Is.True,
            "overwriting the source .typhon-trace must flip NewVersionAvailable within the debounce window");
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return predicate();
    }
}
