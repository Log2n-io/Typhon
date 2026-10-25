using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Backups;

/// <summary>
/// Runs a drill or a settle in a child process (#1417 WB-13, WB-10): the Workbench's own executable in one of its child modes
/// (<see cref="BackupDrillChild"/>), reading its progress and its outcome from stdout. The child's stdin stays open for its whole life and the child exits
/// when it closes (<see cref="BackupDrillChild.WatchStdin"/>), so a Workbench that dies takes its child with it.
/// </summary>
public static class BackupChildProcess
{
    /// <summary>The prefix of a drill's scratch directory under the scratch root; the parent's PID follows it, then the drill's own id.</summary>
    internal const string ScratchPrefix = "wb-drill-";

    /// <summary>
    /// The command that starts a child mode: this process's own executable when it is the Workbench or <c>typhon</c> (both handle the switches), with
    /// the entry assembly when that executable is the <c>dotnet</c> host; otherwise (a test host) the Workbench's build output beside this assembly.
    /// </summary>
    internal static (string FileName, string[] Prefix) ResolveCommand()
    {
        var entry = Assembly.GetEntryAssembly();
        var processPath = Environment.ProcessPath;
        if (entry?.GetName().Name is "Typhon.Workbench" or "typhon" && !string.IsNullOrEmpty(processPath))
        {
            return IsDotnetHost(processPath) ? (processPath, [entry.Location]) : (processPath, []);
        }

        var dll = typeof(BackupDrillChild).Assembly.Location;
        var exe = Path.ChangeExtension(dll, OperatingSystem.IsWindows() ? ".exe" : null);
        return File.Exists(exe) ? (exe, []) : ("dotnet", [dll]);
    }

    private static bool IsDotnetHost(string path) => Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drills <paramref name="point"/> (<c>latest</c> or an id) of <paramref name="store"/> in a child, restoring into a directory of its own under
    /// <paramref name="scratch"/>, loading <paramref name="schemaDllPaths"/> when given. Scratch directories earlier drills left behind are swept first.
    /// </summary>
    /// <exception cref="BackupJobException">The child reported an error, or ended without a report.</exception>
    /// <exception cref="OperationCanceledException">Cancelled: the child was killed.</exception>
    public static BackupDrillReportDto Drill(string store, string point, string scratch, string[] schemaDllPaths, Action<string, long, long> progress,
        CancellationToken ct)
    {
        var root = BackupPaths.Full(scratch);
        SweepStaleScratch(root);
        var work = Path.Combine(root, $"{ScratchPrefix}{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}");
        var args = new List<string> { BackupDrillChild.Switch, "--store", store, "--point", point, "--scratch", work };
        AddSchema(args, schemaDllPaths);

        BackupDrillReportDto report = null;
        try
        {
            var failure = Run(args, line =>
            {
                switch (line.Event)
                {
                    case "progress":
                        progress?.Invoke(line.Stage, line.Done, line.Total);
                        break;
                    case "report":
                        report = line.Report;
                        break;
                }
            }, ct);
            return report ?? throw failure;
        }
        finally
        {
            // The child deletes its own scratch when it ends; a killed one cannot. What cannot go now goes at the next drill's sweep.
            DeleteWithRetry(work);
        }
    }

    /// <summary>Settles a restored <paramref name="bundle"/> in a child, loading <paramref name="schemaDllPaths"/> when given.</summary>
    /// <exception cref="BackupJobException">
    /// The child reported an error (<see cref="BackupJobException.NeedsSchema"/> for a 7012), or ended without a result.
    /// </exception>
    public static BackupSettleReport Settle(string bundle, string[] schemaDllPaths)
    {
        var args = new List<string> { BackupDrillChild.SettleSwitch, "--bundle", bundle };
        AddSchema(args, schemaDllPaths);
        BackupSettleReport settle = null;

        // Not cancellable: the settle runs after the restore's publish, where a cancel is no longer honoured. A Workbench exit still ends it (stdin).
        var failure = Run(args, line =>
        {
            if (line.Event == "settle")
            {
                settle = line.Settle;
            }
        }, CancellationToken.None);
        return settle ?? throw failure;
    }

    private static void AddSchema(List<string> args, string[] schemaDllPaths)
    {
        foreach (var s in schemaDllPaths ?? [])
        {
            args.Add("--schema");
            args.Add(s);
        }
    }

    /// <summary>Runs the child to its exit; returns the failure to throw when it reported no outcome (its error line, or why it said nothing).</summary>
    private static BackupJobException Run(List<string> args, Action<BackupDrillChild.Line> onLine, CancellationToken ct)
    {
        var (fileName, prefix) = ResolveCommand();
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in prefix)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.ArgumentList.Add(BackupDrillChild.WatchStdin);

        BackupJobException error = null;
        var stderr = new StringBuilder();
        using var child = new Process();
        child.StartInfo = psi;
        child.OutputDataReceived += (_, e) =>
        {
            var line = BackupDrillChild.Parse(e.Data);
            if (line?.Event == "error")
            {
                error = new BackupJobException(line.Category ?? "failed", line.Message) { NeedsSchema = line.NeedsSchema };
            }
            else if (line != null)
            {
                onLine(line);
            }
        };
        child.ErrorDataReceived += (_, e) =>
        {
            lock (stderr)
            {
                if (e.Data != null && stderr.Length < 4096)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        child.Start();
        try
        {
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            using (ct.Register(() => Kill(child)))
            {
                // Also drains stdout and stderr: every line handler has run when this returns.
                child.WaitForExit();
            }
        }
        finally
        {
            // The child's stdin was held open until now: its parent-watch sees the end and would exit it, were it still running.
            try
            {
                child.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child already gone.
            }
        }

        ct.ThrowIfCancellationRequested();
        string tail;
        lock (stderr)
        {
            tail = stderr.Length == 0 ? "" : " " + stderr.ToString().Trim();
        }

        return error ?? new BackupJobException("failed", $"The backup child process ended (exit {child.ExitCode}) without a result.{tail}");
    }

    private static void Kill(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Deletes a drill's scratch directory, retrying a few times: a killed child's mappings can outlive it for a moment. A directory that still will not
    /// go is left to the next drill's <see cref="SweepStaleScratch"/>. True when it is gone.
    /// </summary>
    internal static bool DeleteWithRetry(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    return false;
                }

                // On the job's own thread: 50, 100, 200, 400 ms.
                Thread.Sleep(50 << attempt);
            }
        }
    }

    /// <summary>
    /// Deletes the drill scratch directories under <paramref name="root"/> nobody drills in any more: those of a Workbench process that is gone, those of
    /// this one (it runs one drill at a time, and this one has not started), and any not named by a PID. Each holds a whole restored image. A directory of
    /// another live Workbench sharing the root is left alone.
    /// </summary>
    internal static void SweepStaleScratch(string root)
    {
        string[] candidates;
        try
        {
            candidates = Directory.Exists(root) ? Directory.GetDirectories(root, ScratchPrefix + "*") : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var dir in candidates)
        {
            if (!OwnedByAnotherLiveProcess(Path.GetFileName(dir)))
            {
                DeleteWithRetry(dir);
            }
        }
    }

    private static bool OwnedByAnotherLiveProcess(string name)
    {
        var rest = name.AsSpan(ScratchPrefix.Length);
        var dash = rest.IndexOf('-');
        if (dash <= 0 || !int.TryParse(rest[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // No such process.
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            return true;    // There, but not ours to inspect: assume it drills.
        }
    }
}
