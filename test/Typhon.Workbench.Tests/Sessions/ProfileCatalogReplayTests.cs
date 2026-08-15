using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Services;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// A cherry-picked window saved into a database's <c>profilings/</c> must be findable and openable there.
/// </summary>
/// <remarks>
/// <para>
/// With the engine writing no file for a live-only run, this replay is the <b>only</b> artifact of a recording session.
/// If the Profiles list — the one surface built for finding captures — does not show it, the operator's deliberately
/// armed ticks are invisible; if it shows it but refuses to attach it, that is worse still, because the tool then
/// advertises something it will not open.
/// </para>
/// <para>
/// The replay is produced by actually saving one, not by hand-crafting bytes: what is under test is whether the
/// catalog's header projection agrees with what <c>SaveSessionAsync</c> writes, and a fixture file would only prove
/// the projection agrees with itself.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ProfileCatalogReplayTests
{
    private static CancellationToken Timeout15s => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    private string _root;
    private string _bundle;
    private string _profilings;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "typhon-catalog-replay-" + Guid.NewGuid().ToString("N"));
        _bundle = Path.Combine(_root, "world.typhon");
        _profilings = Typhon.Engine.TraceLocation.ProfilingsDirectoryOf(_bundle);
        Directory.CreateDirectory(_profilings);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private OpenSession PausedSessionOnBundle() => new(
        Guid.NewGuid(),
        _bundle,
        new DatabaseHolder(Environment.ProcessId, Environment.MachineName, DateTimeOffset.UtcNow, "localhost:9100"),
        []);

    /// <summary>Records a short window and saves it where a watching session would.</summary>
    private async Task<string> SaveRecordedWindowAsync()
    {
        await using var harness = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout15s);
        harness.Runtime.Arm(3);
        for (var i = 0; i < 4; i++)
        {
            await harness.SendTickAsync(i, engineTick: (uint)(500 + i), detailRecords: 6, ct: Timeout15s);
        }
        await harness.WaitForSummariesAsync(3);

        var path = Path.Combine(_profilings, "typhon-autosave-catalog-test" + CaptureStorage.ReplayExtension);
        var bytes = await harness.Runtime.SaveSessionAsync(path, Timeout15s);
        Assert.That(bytes, Is.GreaterThan(0), "the fixture must actually produce a replay");
        return path;
    }

    [Test]
    public async Task ARecordedWindowSavedIntoProfilings_AppearsInTheProfilesList()
    {
        var replay = await SaveRecordedWindowAsync();
        var session = PausedSessionOnBundle();

        var list = ProfileCatalog.List(session);
        var row = list.Profiles.SingleOrDefault(p => p.FileName == Path.GetFileName(replay));

        Assert.That(row, Is.Not.Null, "the recorded window is the only artifact of a live-only run — it has to be listed");
        Assert.That(row.IsReadable, Is.True,
            "and listed with its real columns: a replay embeds the same TraceFileHeader, so nothing has to degrade to an unreadable row");
        session.Dispose();
    }

    [Test]
    public async Task ARecordedWindow_PassesTheProvenanceCheckUsedWhenAttaching()
    {
        var replay = await SaveRecordedWindowAsync();

        var accepted = ProfileCatalog.BelongsToDatabase(replay, Guid.Empty, out var reason);

        Assert.That(accepted, Is.True,
            $"a listed capture must be attachable — reading it with the trace-only reader would reject it as unreadable ({reason})");
        Assert.That(reason, Is.Null);
    }

    /// <summary>
    /// Engine-written captures keep listing exactly as before — the replay pass is additive, not a replacement.
    /// </summary>
    [Test]
    public async Task ListingReplays_DoesNotDisturbEngineWrittenCaptures()
    {
        var replay = await SaveRecordedWindowAsync();
        // A file with the trace extension that is NOT a valid trace: it must still list (as unreadable), which is the
        // pre-existing contract for a capture truncated mid-write.
        var bogusTrace = Path.Combine(_profilings, "20260815-120000-000.typhon-trace");
        await File.WriteAllBytesAsync(bogusTrace, new byte[64]);

        var session = PausedSessionOnBundle();
        var list = ProfileCatalog.List(session);

        Assert.Multiple(() =>
        {
            Assert.That(list.Profiles.Any(p => p.FileName == Path.GetFileName(replay)), Is.True);
            Assert.That(list.Profiles.Any(p => p.FileName == Path.GetFileName(bogusTrace)), Is.True,
                "an unreadable trace still gets a row so the user can see the file exists");
        });
        session.Dispose();
    }
}
