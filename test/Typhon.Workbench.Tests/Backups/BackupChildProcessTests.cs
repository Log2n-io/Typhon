using System.Diagnostics;
using System.IO.Pipes;
using NUnit.Framework;
using Typhon.Workbench.Backups;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>
/// #1417 WB-13: the child process — its protocol lines are told apart from anything else on its stdout, an error it reports keeps its category, a cancel
/// kills it and removes the scratch it was given, it exits when its parent's end of its stdin closes, and the next drill sweeps the scratch an earlier
/// one could not remove. (A passing drill and a schema'd settle end to end: <see cref="BackupsJobEndpointTests"/>.)
/// </summary>
[TestFixture]
public sealed class BackupChildProcessTests
{
    private string _root;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "typhon-wb-drill-child", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void OnlyPrefixedLines_AreTheProtocol()
    {
        var output = new StringWriter();
        Assert.That(BackupDrillChild.TryRun(["--backup-drill", "--store"], output), Is.EqualTo(BackupDrillChild.ExitUsage));
        var line = output.ToString().TrimEnd('\n');

        Assert.That(BackupDrillChild.Parse(line)?.Event, Is.EqualTo("error"));
        Assert.That(BackupDrillChild.Parse("info: Typhon.Engine.DatabaseEngine[0] Open: 12 ms"), Is.Null, "an engine log line is not the protocol");
        Assert.That(BackupDrillChild.Parse(BackupDrillChild.LinePrefix + "{not json"), Is.Null);
        Assert.That(BackupDrillChild.TryRun(["--urls", "http://127.0.0.1:1"]), Is.Null, "any other command line is not the drill mode");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnErrorTheChildReports_KeepsItsCategory()
    {
        // No store there: the child's facade call throws, and the parent raises what the child classified.
        var e = Assert.Throws<BackupJobException>(() =>
            BackupChildProcess.Drill(Path.Combine(_root, "no-store"), "latest", Path.Combine(_root, "scratch"), [], null, CancellationToken.None));
        Assert.That(e.Category, Is.EqualTo("failed"));
        Assert.That(e.Message, Is.Not.Empty);
    }

    [Test]
    [CancelAfter(10_000)]
    public void TheChild_IsOrphaned_WhenItsParentsEndOfThePipeCloses()
    {
        // The child's stdin is the parent's pipe, held open for the child's whole life: a Workbench that dies closes it, however it dies.
        using var parent = new AnonymousPipeServerStream(PipeDirection.Out);
        using var input = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, parent.ClientSafePipeHandle));
        using var orphaned = new ManualResetEventSlim();
        BackupDrillChild.WatchParent(input, orphaned.Set);

        Assert.That(orphaned.IsSet, Is.False, "the parent is alive");
        parent.Dispose();
        Assert.That(orphaned.Wait(TimeSpan.FromSeconds(5)), Is.True, "the parent's end closed: the child exits");
    }

    [Test]
    [CancelAfter(30_000)]
    public void TheNextDrill_SweepsTheScratchEarlierDrillsLeft_ButNotALiveWorkbenchs()
    {
        var scratch = Path.Combine(_root, "scratch");
        using (var gone = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false }))
        {
            gone!.WaitForExit();
            Make($"wb-drill-{gone.Id}-dead");
        }

        var stale = new[] { "wb-drill-0123456789abcdef", $"wb-drill-{Environment.ProcessId}-mine" };
        foreach (var s in stale)
        {
            Make(s);
        }

        var live = $"wb-drill-{(OperatingSystem.IsWindows() ? 4 : 1)}-live";   // System / init: alive, and not this Workbench
        Make(live);
        File.WriteAllText(Path.Combine(scratch, "unrelated.txt"), "");

        BackupChildProcess.SweepStaleScratch(scratch);

        Assert.That(Directory.GetFileSystemEntries(scratch).Select(Path.GetFileName), Is.EquivalentTo(new[] { live, "unrelated.txt" }));

        void Make(string name)
        {
            var dir = Path.Combine(scratch, name, "db.typhon");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "data"), "a whole image");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void ACancel_KillsTheChild_AndRemovesItsScratch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(scratch);

        Assert.Throws<OperationCanceledException>(() =>
            BackupChildProcess.Drill(Path.Combine(_root, "no-store"), "latest", scratch, [], null, cts.Token));
        Assert.That(Directory.GetFileSystemEntries(scratch), Is.Empty, "the child's own scratch directory is removed");
    }
}
