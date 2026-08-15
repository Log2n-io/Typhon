using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// AC-20 — retention over the Workbench captures directory (#805).
/// </summary>
/// <remarks>
/// <para>
/// The #613 <c>profilings/</c> retention does not reach attach captures: that path is resolved from a database file
/// path and an attach session's is a <c>host:port</c> endpoint, and <c>TraceRetention.Prune</c> enumerates
/// <c>*.typhon-trace</c> rather than the self-contained <c>.typhon-replay</c> files written here. What is shared is
/// <see cref="RetentionPolicy"/> itself — same record, same <c>retention.json</c>, same semantics.
/// </para>
/// <para>
/// Every test here redirects the captures directory to a temp folder. Without that they would prune the developer's
/// real capture archive, which is a destructive side effect no test is entitled to.
/// </para>
/// </remarks>
[TestFixture]
public sealed class CaptureStorageRetentionTests
{
    private string _dir;
    private string _previousOverride;

    [SetUp]
    public void SetUp()
    {
        _previousOverride = Environment.GetEnvironmentVariable(CaptureStorage.DirectoryOverrideVariable);
        _dir = Path.Combine(Path.GetTempPath(), "typhon-capture-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(CaptureStorage.DirectoryOverrideVariable, _dir);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(CaptureStorage.DirectoryOverrideVariable, _previousOverride);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private string WriteCapture(string name, int bytes, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_dir, name + CaptureStorage.ReplayExtension);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private string[] RemainingCaptures() =>
        [.. Directory.EnumerateFiles(_dir, "*" + CaptureStorage.ReplayExtension).Select(Path.GetFileName).Order()];

    [Test]
    public void ApplyRetention_EvictsOldestFirst_UntilWithinBudget()
    {
        new RetentionPolicy { BudgetBytes = 300, KeepLatest = 1 }.Write(_dir);

        var now = DateTime.UtcNow;
        WriteCapture("oldest", 200, now.AddMinutes(-30));
        WriteCapture("middle", 200, now.AddMinutes(-20));
        var newest = WriteCapture("newest", 200, now.AddMinutes(-10));

        CaptureStorage.ApplyRetention(newest, _ => { });

        var remaining = RemainingCaptures();
        Assert.That(remaining, Does.Not.Contain("oldest" + CaptureStorage.ReplayExtension), "the oldest capture must be evicted first");
        Assert.That(remaining, Contains.Item("newest" + CaptureStorage.ReplayExtension), "the newest capture must survive");
    }

    [Test]
    public void ApplyRetention_NeverDeletesTheCaptureJustWritten()
    {
        // A budget far below a single file's size: without the guard, the file that triggered the prune is the obvious
        // eviction candidate, and capturing would silently produce nothing.
        new RetentionPolicy { BudgetBytes = 1, KeepLatest = 0 }.Write(_dir);

        var justWritten = WriteCapture("fresh", 500, DateTime.UtcNow);
        CaptureStorage.ApplyRetention(justWritten, _ => { });

        Assert.That(File.Exists(justWritten), Is.True,
            "evicting the capture whose creation triggered the prune would make capturing unreliable rather than bounded");
    }

    [Test]
    public void ApplyRetention_HonoursKeepLatestFloor_EvenOverBudget()
    {
        new RetentionPolicy { BudgetBytes = 1, KeepLatest = 3 }.Write(_dir);

        var now = DateTime.UtcNow;
        WriteCapture("a", 100, now.AddMinutes(-40));
        WriteCapture("b", 100, now.AddMinutes(-30));
        WriteCapture("c", 100, now.AddMinutes(-20));
        var d = WriteCapture("d", 100, now.AddMinutes(-10));

        CaptureStorage.ApplyRetention(d, _ => { });

        Assert.That(RemainingCaptures(), Has.Length.GreaterThanOrEqualTo(3),
            "KeepLatest is a floor: the newest captures survive even when that exceeds the budget");
    }

    [Test]
    public void ApplyRetention_NeverEvictsAPinnedCapture()
    {
        new RetentionPolicy
        {
            BudgetBytes = 1,
            KeepLatest = 0,
            Pinned = ["keepme" + CaptureStorage.ReplayExtension],
        }.Write(_dir);

        var now = DateTime.UtcNow;
        WriteCapture("keepme", 500, now.AddMinutes(-40));
        var newest = WriteCapture("disposable", 100, now.AddMinutes(-1));

        CaptureStorage.ApplyRetention(newest, _ => { });

        Assert.That(RemainingCaptures(), Contains.Item("keepme" + CaptureStorage.ReplayExtension),
            "a pinned capture counts toward the budget but is never evicted");
    }

    [Test]
    public void ApplyRetention_WithNonPositiveBudget_DeletesNothing()
    {
        new RetentionPolicy { BudgetBytes = 0, KeepLatest = 0 }.Write(_dir);

        var now = DateTime.UtcNow;
        WriteCapture("a", 5_000, now.AddMinutes(-40));
        var b = WriteCapture("b", 5_000, now.AddMinutes(-1));

        CaptureStorage.ApplyRetention(b, _ => { });

        Assert.That(RemainingCaptures(), Has.Length.EqualTo(2), "a non-positive budget disables eviction, per the policy's contract");
    }

    /// <summary>
    /// Retention prunes the directory the capture landed in, not the machine-local default.
    /// </summary>
    /// <remarks>
    /// A session with a database saves into that database's <c>profilings/</c>. Reading the default directory here
    /// would prune an unrelated folder while the one that just grew went unbounded — and worse, <c>justWritten</c>
    /// would match nothing in the scanned folder, so the "never evict what we just wrote" guard would be silently off.
    /// </remarks>
    [Test]
    public void ApplyRetention_PrunesTheDirectoryTheCaptureWasWrittenTo()
    {
        var profilings = Path.Combine(_dir, "world.typhon", "profilings");
        Directory.CreateDirectory(profilings);
        new RetentionPolicy { BudgetBytes = 300, KeepLatest = 1 }.Write(profilings);

        var now = DateTime.UtcNow;
        string Write(string name, int bytes, DateTime stamp)
        {
            var p = Path.Combine(profilings, name + CaptureStorage.ReplayExtension);
            File.WriteAllBytes(p, new byte[bytes]);
            File.SetLastWriteTimeUtc(p, stamp);
            return p;
        }

        Write("old", 200, now.AddMinutes(-30));
        Write("mid", 200, now.AddMinutes(-20));
        var newest = Write("new", 200, now.AddMinutes(-10));

        CaptureStorage.ApplyRetention(newest, _ => { });

        var remaining = Directory.EnumerateFiles(profilings, "*" + CaptureStorage.ReplayExtension).Select(Path.GetFileName).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(remaining, Does.Not.Contain("old" + CaptureStorage.ReplayExtension), "the directory that grew is the one that must be pruned");
            Assert.That(remaining, Contains.Item("new" + CaptureStorage.ReplayExtension), "and the capture that triggered the prune is never its own victim");
        });
    }

    /// <summary>
    /// Pruning a database's <c>profilings/</c> must never touch an engine-written capture.
    /// </summary>
    /// <remarks>
    /// This is the safety property that lets the two retention policies share a directory at all: this pass scans
    /// <c>*.typhon-replay</c> and engine-side <c>TraceRetention</c> scans <c>*.typhon-trace</c>, so each file kind has
    /// exactly one owner. If this ever fails, the Workbench is deleting post-mortem captures it did not write and has
    /// no budget information about — silently, from inside a save.
    /// </remarks>
    [Test]
    public void ApplyRetention_NeverEvictsAnEngineWrittenTrace()
    {
        var profilings = Path.Combine(_dir, "world.typhon", "profilings");
        Directory.CreateDirectory(profilings);
        new RetentionPolicy { BudgetBytes = 1, KeepLatest = 1 }.Write(profilings);

        var enginePath = Path.Combine(profilings, "20260815-120000-000.typhon-trace");
        File.WriteAllBytes(enginePath, new byte[10_000]);
        File.SetLastWriteTimeUtc(enginePath, DateTime.UtcNow.AddDays(-9));

        var replay = Path.Combine(profilings, "typhon-autosave-x" + CaptureStorage.ReplayExtension);
        File.WriteAllBytes(replay, new byte[10_000]);

        CaptureStorage.ApplyRetention(replay, _ => { });

        Assert.That(File.Exists(enginePath), Is.True,
            "a budget of 1 byte would evict everything it can see — an engine capture must not be one of those things");
    }
}
