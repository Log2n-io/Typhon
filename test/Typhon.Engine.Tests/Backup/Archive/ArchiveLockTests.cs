using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// <c>wal/&lt;t&gt;/archive.lock</c> (WA-14, WA-24): one holder per timeline per operation, through the OS lock (share mode on Windows, flock on Linux),
/// so the archiver and retention never interleave; a busy lock is a retry, and a file system that does not enforce locks is refused (R3).
/// </summary>
[TestFixture]
internal sealed class ArchiveLockTests
{
    [Test]
    [CancelAfter(10_000)]
    public void ASecondHolder_IsTurnedAway_UntilTheFirstLetsGo()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        fs.CreateDirectory("/s");
        using (var first = WalArchiveTimeline.TryOpen(fs, "/s", ArchiveTestKit.Timeline))
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(WalArchiveTimeline.TryOpen(fs, "/s", ArchiveTestKit.Timeline), Is.Null, "busy: a retry, not a refusal");
            using var other = WalArchiveTimeline.TryOpen(fs, "/s", Guid.NewGuid());
            Assert.That(other, Is.Not.Null, "the lock is per timeline");
        }

        using var again = WalArchiveTimeline.TryOpen(fs, "/s", ArchiveTestKit.Timeline);
        Assert.That(again, Is.Not.Null);
    }

    [Test]
    [CancelAfter(10_000)]
    public void AFileSystemThatDoesNotEnforceLocks_IsRefused()
    {
        var fs = new UnenforcedLocks();
        fs.CreateDirectory("/");
        fs.CreateDirectory("/s");
        var ex = Assert.Throws<NotSupportedException>(() => WalArchiveTimeline.TryOpen(fs, "/s", ArchiveTestKit.Timeline));
        Assert.That(ex.Message, Does.Contain("not enforced"));
    }

    /// <summary>
    /// Another PROCESS holding a timeline's archive.lock keeps the archiver out of that timeline (Windows share mode, Linux flock). The child says whether
    /// it holds the lock: Windows PowerShell 5 (.NET Framework) cannot open a path past MAX_PATH, which a long TMP makes this one, and its failed open
    /// used to be followed by "locked" anyway — the probe then found the lock free and the test failed for the child's sake. PowerShell 7 is preferred.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnotherProcessHoldingTheArchiveLock_KeepsTheArchiverOut()
    {
        var root = Path.Combine(TestStorage.ForFixture(nameof(ArchiveLockTests)), Guid.NewGuid().ToString("N"));
        var paths = new WalArchivePaths(root);
        Directory.CreateDirectory(paths.Timeline(ArchiveTestKit.Timeline));
        var lockPath = paths.Lock(ArchiveTestKit.Timeline);
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo(OnPath("pwsh.exe") ? "pwsh" : "powershell", "-NoProfile -NonInteractive -Command \"try { $f=[IO.File]::Open('"
                + lockPath + "','OpenOrCreate','ReadWrite','None') } catch { [Console]::Out.WriteLine('failed: ' + $_.Exception.InnerException.Message); "
                + "exit 1 }; [Console]::Out.WriteLine('locked'); [Console]::Out.Flush(); [void][Console]::In.ReadLine(); $f.Close()\"");
        }
        else
        {
            psi = new ProcessStartInfo("flock", $"-x -n \"{lockPath}\" -c \"echo locked; read x\"");
        }

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;
        Process child;
        try
        {
            child = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Inconclusive($"no '{psi.FileName}' to hold the lock from another process: {ex.Message}");
            return;
        }

        using (child)
        {
            try
            {
                var said = child.StandardOutput.ReadLine();
                if (said != "locked")
                {
                    Assert.Inconclusive($"'{psi.FileName}' could not hold the lock from another process ({said}): {lockPath.Length} characters");
                }

                Assert.That(WalArchiveTimeline.TryOpen(null, root, ArchiveTestKit.Timeline), Is.Null);
            }
            finally
            {
                child.StandardInput.WriteLine();
                child.WaitForExit(10_000);
            }
        }

        using (var t = WalArchiveTimeline.TryOpen(null, root, ArchiveTestKit.Timeline))
        {
            Assert.That(t, Is.Not.Null);
        }

        Directory.Delete(root, true);
    }

    private static bool OnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, exe)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A file system whose exclusive opens all succeed, as with DOTNET_SYSTEM_IO_DISABLEFILELOCKING set.</summary>
    private sealed class UnenforcedLocks : InMemorySparseFileSystem, IStoreFileSystem
    {
        IDisposable IStoreFileSystem.TryLockExclusive(string path) => new Nothing();

        IDisposable IStoreFileSystem.TryLockTransient(string path) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
