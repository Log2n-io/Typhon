using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// On-demand tick capture (#805) — what happens when the profiled application QUITS.
/// </summary>
/// <remarks>
/// <para>
/// <c>CaptureRestartTests</c> covers the restart path — a socket drop followed by an <c>Init</c> carrying a new
/// <c>CreatedUtcTicks</c>. Quitting is a different path: the engine sends a <b>Shutdown frame</b>, which runs
/// <c>AutoSaveOnTeardown("engine_shutdown")</c> on the read loop. Nothing covered that, nor the sequence that follows it
/// in the UI: the shutdown banner offers <i>Capture &amp; Analyse</i>, which is a SECOND save
/// (<c>POST /save-replay</c> → <c>SaveSessionAsync</c>) on a runtime that has already auto-saved.
/// </para>
/// <para>
/// The client awaits that POST with no timeout, so a server that never answers leaves the button on
/// <c>Capturing…</c> for ever. These tests therefore assert on COMPLETION under a deadline, not merely on the result.
/// </para>
/// </remarks>
[TestFixture]
public sealed class CaptureShutdownSaveTests
{
    private static CancellationToken Timeout15s => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    private string _capturesDir;
    private string _previousOverride;

    /// <summary>Auto-save writes real files and prunes the directory afterwards — never point it at the real archive.</summary>
    [SetUp]
    public void RedirectCapturesDirectory()
    {
        _previousOverride = Environment.GetEnvironmentVariable(CaptureStorage.DirectoryOverrideVariable);
        _capturesDir = Path.Combine(Path.GetTempPath(), "typhon-capture-shutdown-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Record two windows, the way an operator does: arm, let it run, arm again.</summary>
    private static async Task RecordTwoWindowsAsync(CaptureHarness h)
    {
        h.Runtime.Arm(3);
        for (var i = 0; i < 4; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(100 + i), detailRecords: 6, ct: Timeout15s);
        }

        h.Runtime.Arm(3);
        for (var i = 4; i < 8; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(100 + i), detailRecords: 6, ct: Timeout15s);
        }

        await h.WaitForSummariesAsync(7);
        Assert.That(h.Runtime.CaptureState.RecordedTicks, Is.EqualTo(6), "two three-tick windows must have been recorded");
    }

    /// <summary>
    /// The reported bug: capture two windows, quit the application, then accept the banner's offer to capture the
    /// result. The save must actually come back — the UI has no timeout behind it.
    /// </summary>
    [Test]
    public async Task QuitThenCaptureAndAnalyse_CompletesInsteadOfHanging()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout15s);
        h.Server.CreatedUtcTicks = 10;

        string autoSavedPath = null;
        h.Runtime.CaptureAutoSaved += p => autoSavedPath = p;

        await RecordTwoWindowsAsync(h);

        // The user quits the app — a clean Shutdown frame, not a dropped socket.
        await h.Server.SendShutdownAsync(Timeout15s);
        var autoSaved = await WaitUntilAsync(() => autoSavedPath != null, TimeSpan.FromSeconds(12));
        Assert.That(autoSaved, Is.True, "quitting must auto-save the recorded window before teardown");

        // The banner's Capture & Analyse button: POST /save-replay → SaveSessionAsync on the same runtime.
        var target = Path.Combine(_capturesDir, "capture-and-analyse.typhon-replay");
        var save = h.Runtime.SaveSessionAsync(target, CancellationToken.None);
        var settled = await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.That(settled, Is.SameAs(save),
            "Capture & Analyse never returned — the client awaits this POST with no timeout, so the button sits on 'Capturing…' for ever");
        Assert.That(await save, Is.GreaterThan(0), "the second save must produce a non-empty replay");
        Assert.That(File.Exists(target), Is.True);
    }

    /// <summary>
    /// Closing the session — or pressing <i>Stop watching</i> — preserves the recorded window too.
    /// </summary>
    /// <remarks>
    /// Auto-save originally ran only on the teardown FRAMES (engine shutdown, engine restart). Those are not the only
    /// way a session ends: the operator closes it, or stops watching, and both routes reach <c>Dispose</c> having
    /// recorded exactly the ticks they meant to. While a live run also produced a complete engine capture this only
    /// duplicated it; with a live-only run writing nothing else, it is the difference between keeping the recording and
    /// silently throwing it away.
    /// </remarks>
    [Test]
    public async Task ClosingTheSession_SavesTheRecordedWindow()
    {
        string savedPath = null;
        {
            await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout15s);
            h.Runtime.CaptureAutoSaved += p => savedPath = p;
            await RecordTwoWindowsAsync(h);
            // No shutdown frame, no restart — the session is simply disposed, as StopWatching and session close both do.
        }

        Assert.That(savedPath, Is.Not.Null, "disposing a session that recorded a window must not discard it");
        Assert.That(File.Exists(savedPath), Is.True);
        Assert.That(new FileInfo(savedPath).Length, Is.GreaterThan(0));
    }

    /// <summary>An attach where Record was never pressed still leaves nothing behind when it is closed.</summary>
    [Test]
    public async Task ClosingASessionThatRecordedNothing_WritesNoFile()
    {
        string savedPath = null;
        {
            await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout15s);
            h.Runtime.CaptureAutoSaved += p => savedPath = p;
            for (var i = 0; i < 3; i++)
            {
                await h.SendTickAsync(i, engineTick: (uint)(100 + i), detailRecords: 6, ct: Timeout15s);
            }
            await h.WaitForSummariesAsync(2);
        }

        Assert.That(savedPath, Is.Null, "closing an idle session must not litter the captures directory");
    }

    /// <summary>
    /// The same save, without the preceding quit. Isolates the auto-save from the save: if this passes and the test
    /// above hangs, the teardown path is what breaks the later one.
    /// </summary>
    [Test]
    public async Task SaveWhileStillLive_Completes()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout15s);
        h.Server.CreatedUtcTicks = 10;

        await RecordTwoWindowsAsync(h);

        var target = Path.Combine(_capturesDir, "live-save.typhon-replay");
        var save = h.Runtime.SaveSessionAsync(target, CancellationToken.None);
        var settled = await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.That(settled, Is.SameAs(save), "a save on a live session must return");
        Assert.That(await save, Is.GreaterThan(0));
    }
}
