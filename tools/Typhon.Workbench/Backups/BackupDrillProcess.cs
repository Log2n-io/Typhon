using System.Diagnostics;
using System.Reflection;
using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Backups;

/// <summary>
/// Runs a drill in a child process (#1417 WB-13): the Workbench's own executable in its drill-only mode (<see cref="BackupDrillChild"/>), reading its
/// progress and its report from stdout. Cancellation kills the child and deletes the scratch directory it was given.
/// </summary>
public static class BackupDrillProcess
{
    /// <summary>
    /// The command that starts the drill mode: this process's own executable when it is the Workbench or <c>typhon</c> (both handle the switch), with
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
    /// <paramref name="scratch"/>, loading <paramref name="schemaDllPaths"/> when given.
    /// </summary>
    /// <exception cref="BackupJobException">The child reported an error, or ended without a report.</exception>
    /// <exception cref="OperationCanceledException">Cancelled: the child was killed.</exception>
    public static BackupDrillReportDto Run(string store, string point, string scratch, string[] schemaDllPaths, Action<string, long, long> progress,
        CancellationToken ct)
    {
        var work = Path.Combine(BackupPaths.Full(scratch), $"wb-drill-{Guid.NewGuid():N}");
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

        psi.ArgumentList.Add(BackupDrillChild.Switch);
        psi.ArgumentList.Add("--store");
        psi.ArgumentList.Add(store);
        psi.ArgumentList.Add("--point");
        psi.ArgumentList.Add(point);
        psi.ArgumentList.Add("--scratch");
        psi.ArgumentList.Add(work);
        foreach (var s in schemaDllPaths ?? [])
        {
            psi.ArgumentList.Add("--schema");
            psi.ArgumentList.Add(s);
        }

        BackupDrillReportDto report = null;
        string category = null, message = null;
        var stderr = new System.Text.StringBuilder();
        using var child = new Process();
        child.StartInfo = psi;
        child.OutputDataReceived += (_, e) =>
        {
            var line = BackupDrillChild.Parse(e.Data);
            switch (line?.Event)
            {
                case "progress":
                    progress?.Invoke(line.Stage, line.Done, line.Total);
                    break;
                case "report":
                    report = line.Report;
                    break;
                case "error":
                    (category, message) = (line.Category, line.Message);
                    break;
            }
        };
        child.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null && stderr.Length < 4096)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            child.Start();
            child.StandardInput.Close();
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            using (ct.Register(() => Kill(child)))
            {
                child.WaitForExit();
            }

            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            // The child deletes its own scratch when it ends; a killed one cannot.
            DeleteQuietly(work);
        }

        if (report != null)
        {
            return report;
        }

        throw new BackupJobException(category ?? "failed",
            message ?? $"The drill process ended (exit {child.ExitCode}) without a report.{(stderr.Length == 0 ? "" : " " + stderr.ToString().Trim())}");
    }

    private static void Kill(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A killed child's mapping can outlive it for a moment; the directory is the drill's own and harmless.
        }
    }
}
