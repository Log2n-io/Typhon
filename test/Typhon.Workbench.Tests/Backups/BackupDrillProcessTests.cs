using NUnit.Framework;
using Typhon.Workbench.Backups;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>
/// #1417 WB-13: the drill's child process — its protocol lines are told apart from anything else on its stdout, an error it reports keeps its category, and
/// a cancel kills it and removes the scratch it was given. (A passing drill end to end: <see cref="BackupsJobEndpointTests"/>.)
/// </summary>
[TestFixture]
public sealed class BackupDrillProcessTests
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
            BackupDrillProcess.Run(Path.Combine(_root, "no-store"), "latest", Path.Combine(_root, "scratch"), [], null, CancellationToken.None));
        Assert.That(e.Category, Is.EqualTo("failed"));
        Assert.That(e.Message, Is.Not.Empty);
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
            BackupDrillProcess.Run(Path.Combine(_root, "no-store"), "latest", scratch, [], null, cts.Token));
        Assert.That(Directory.GetFileSystemEntries(scratch), Is.Empty, "the child's own scratch directory is removed");
    }
}
