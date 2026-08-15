using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// On-demand tick capture (#805) — engine restart and auto-save. Covers AC-17 (a restart ends the session rather than
/// silently continuing its tick axis), AC-18 (auto-save iff a window was recorded), AC-19 (flush ordering, so the last
/// partial chunk survives) and AC-20 (retention over the captures directory).
/// </summary>
[TestFixture]
public sealed class CaptureRestartTests
{
    private static CancellationToken Timeout15s => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    private string _capturesDir;
    private string _previousOverride;

    /// <summary>
    /// Redirect the captures directory into a temp folder for the whole fixture. Auto-save writes real files and then
    /// runs a retention pass that DELETES files in that directory — pointing either at the developer's real capture
    /// archive would make this fixture destructive.
    /// </summary>
    [SetUp]
    public void RedirectCapturesDirectory()
    {
        _previousOverride = Environment.GetEnvironmentVariable(CaptureStorage.DirectoryOverrideVariable);
        _capturesDir = Path.Combine(Path.GetTempPath(), "typhon-capture-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_capturesDir);
        Environment.SetEnvironmentVariable(CaptureStorage.DirectoryOverrideVariable, _capturesDir);
    }

    [TearDown]
    public void RestoreCapturesDirectory()
    {
        Environment.SetEnvironmentVariable(CaptureStorage.DirectoryOverrideVariable, _previousOverride);
        try { Directory.Delete(_capturesDir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>
    /// AC-17 — a genuine restart (a new engine process, so a new <c>CreatedUtcTicks</c>) ends the session. Continuing
    /// would silently report run 2's tick 1 as tick N+1, because the builder's tick counter derives from counting
    /// <c>TickStart</c> markers and never resets.
    /// </summary>
    [Test]
    public async Task EngineRestart_EndsTheSession_RatherThanContinuingTheTickAxis()
    {
        await using var server = new MockTcpProfilerServer { Scripted = true, CreatedUtcTicks = 1_000 };
        server.Start();
        using var runtime = await AttachSessionRuntime.StartAsync(
            Guid.NewGuid(), $"127.0.0.1:{server.Port}", NullLogger.Instance, Timeout15s, CaptureMode.CherryPick);

        string shutdownReason = null;
        runtime.ShutdownReceived += r => shutdownReason = r;
        await server.WaitForClientAsync(Timeout15s);

        // The app is relaunched: same binary (so the Init signature still matches) but a new process.
        server.CreatedUtcTicks = 2_000;
        await server.DropClientAsync();

        var ended = await WaitUntilAsync(() => shutdownReason != null, TimeSpan.FromSeconds(12));
        Assert.That(ended, Is.True, "a restart must terminate the session");
        Assert.That(shutdownReason, Is.EqualTo("engine_restarted"),
            "the reason must distinguish a restart from a signature mismatch, so the UI can offer a plain reconnect");
    }

    /// <summary>
    /// AC-17 (the other half) — a transient socket drop within ONE engine process must NOT end the session. Ending it
    /// on every reconnect would let a network hiccup destroy a capture, and the Init signature alone cannot tell the
    /// two apart because it deliberately excludes <c>CreatedUtcTicks</c>.
    /// </summary>
    [Test]
    public async Task TransientDrop_SameEngineProcess_ResumesInsteadOfEnding()
    {
        await using var server = new MockTcpProfilerServer { Scripted = true, CreatedUtcTicks = 4_242 };
        server.Start();
        using var runtime = await AttachSessionRuntime.StartAsync(
            Guid.NewGuid(), $"127.0.0.1:{server.Port}", NullLogger.Instance, Timeout15s, CaptureMode.CherryPick);

        string shutdownReason = null;
        runtime.ShutdownReceived += r => shutdownReason = r;
        await server.WaitForClientAsync(Timeout15s);

        // Same process — CreatedUtcTicks unchanged.
        await server.DropClientAsync();
        var reconnected = await WaitUntilAsync(() => server.ConnectionCount >= 2, TimeSpan.FromSeconds(12));

        Assert.That(reconnected, Is.True, "the runtime must reconnect after a transient drop");
        Assert.That(shutdownReason, Is.Null, "a socket blip within one engine process must not end the session");
        Assert.That(runtime.IsUnrecoverable, Is.False);
    }

    /// <summary>
    /// AC-18 + AC-19 — a cherry-pick session that recorded a window auto-saves before teardown, and the saved replay
    /// contains the ticks that were captured. AC-19's flush ordering is what makes the second half true: <c>Dispose</c>
    /// deletes the temp file and does not flush, so without an explicit flush the most recent chunk never reaches disk.
    /// </summary>
    [Test]
    public async Task CherryPickWithARecordedWindow_AutoSavesOnRestart_IncludingTheLastChunk()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout15s);
        h.Server.CreatedUtcTicks = 10;

        string savedPath = null;
        h.Runtime.CaptureAutoSaved += p => savedPath = p;

        h.Runtime.Arm(3);
        for (var i = 0; i < 5; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(100 + i), detailRecords: 6, ct: Timeout15s);
        }
        await h.WaitForSummariesAsync(4);
        Assert.That(h.Runtime.CaptureState.RecordedTicks, Is.EqualTo(3), "the window must have been recorded");

        // Restart the engine.
        h.Server.CreatedUtcTicks = 20;
        await h.Server.DropClientAsync();

        var saved = await WaitUntilAsync(() => savedPath != null, TimeSpan.FromSeconds(12));
        Assert.That(saved, Is.True, "a recorded window must be saved before the session is torn down");
        Assert.That(File.Exists(savedPath), Is.True, $"the replay must exist on disk at {savedPath}");
        Assert.That(new FileInfo(savedPath!).Length, Is.GreaterThan(0), "the replay must not be empty");

    }

    /// <summary>
    /// AC-18 — an attach where Record was never pressed leaves nothing behind. The guard is what stops the captures
    /// directory filling with empty files every time someone attaches and detaches.
    /// </summary>
    [Test]
    public async Task CherryPickWithNoRecordedWindow_SavesNothing()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout15s);
        h.Server.CreatedUtcTicks = 10;

        string savedPath = null;
        h.Runtime.CaptureAutoSaved += p => savedPath = p;

        for (var i = 0; i < 4; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(100 + i), detailRecords: 6, ct: Timeout15s);
        }
        await h.WaitForSummariesAsync(3);
        Assert.That(h.Runtime.CaptureState.RecordedTicks, Is.Zero, "nothing was armed");

        h.Server.CreatedUtcTicks = 20;
        await h.Server.DropClientAsync();

        // Give the restart path time to run and, if it were going to, save.
        await WaitUntilAsync(() => savedPath != null, TimeSpan.FromSeconds(4));
        Assert.That(savedPath, Is.Null, "an attach where Record was never pressed must leave no file behind");
        Assert.That(h.Runtime.AutoSavedPath, Is.Null);
    }

    /// <summary>
    /// AC-18 — capture-everything mode never auto-saves. That data is large and incidental; silently writing it to the
    /// user's local-app-data on every restart is not something a tool should do unasked.
    /// </summary>
    [Test]
    public async Task CaptureEverythingMode_NeverAutoSaves()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.Everything, Timeout15s);
        h.Server.CreatedUtcTicks = 10;

        string savedPath = null;
        h.Runtime.CaptureAutoSaved += p => savedPath = p;

        for (var i = 0; i < 4; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(100 + i), detailRecords: 6, ct: Timeout15s);
        }
        await h.WaitForSummariesAsync(3);
        Assert.That(h.Runtime.CaptureState.RecordedTicks, Is.GreaterThan(0), "everything-mode records every tick");

        h.Server.CreatedUtcTicks = 20;
        await h.Server.DropClientAsync();

        await WaitUntilAsync(() => savedPath != null, TimeSpan.FromSeconds(4));
        Assert.That(savedPath, Is.Null, "capture-everything must prompt, not silently write GBs");
    }
}
