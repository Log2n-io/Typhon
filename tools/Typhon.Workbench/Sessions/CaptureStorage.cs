using System.Globalization;
using Typhon.Engine;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Where the Workbench writes attach-session captures, and how it keeps that directory from growing without bound (#805).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>profilings/</c>.</b> #613 gave databases a managed <c>profilings/</c> directory with a
/// <see cref="RetentionPolicy"/>, but that path is reachable only from an <c>OpenSession</c>: it is resolved from the
/// database's own file path, and an attach session's "file path" is a <c>host:port</c> endpoint. The engine does not
/// put its database path on the live wire, so an attach capture has no database to file itself under and lands in the
/// user's local-app-data instead.
/// </para>
/// <para>
/// <b>Why not <c>TraceRetention.Prune</c>.</b> It enumerates <c>*.typhon-trace</c> plus their sidecars inside a
/// <c>profilings/</c> directory. Attach captures are self-contained <c>.typhon-replay</c> files with no sidecar, so
/// pointing that pruner here would scan a directory it does not understand and silently delete nothing. What IS reused
/// is <see cref="RetentionPolicy"/> itself — same record, same <c>retention.json</c> file name, same
/// budget/keep-latest/pin semantics — so a user who has learned one directory's policy has learned both.
/// </para>
/// <para>
/// <b>The writer prunes.</b> That is the policy's own stated contract: <i>"whoever writes a capture prunes first, from
/// this file, with no tooling required"</i>. This class is that writer for attach captures.
/// </para>
/// </remarks>
public static class CaptureStorage
{
    /// <summary>Extension of a self-contained attach capture.</summary>
    public const string ReplayExtension = ".typhon-replay";

    /// <summary>
    /// Environment override for <see cref="CapturesDirectory"/>. Exists so tests never touch — and
    /// <see cref="ApplyRetention"/> never <i>deletes from</i> — the developer's real capture archive.
    /// </summary>
    public const string DirectoryOverrideVariable = "TYPHON_WORKBENCH_CAPTURES_DIR";

    /// <summary>Directory holding attach captures: <c>%LOCALAPPDATA%/Typhon/Workbench/captures</c> (XDG equivalent on POSIX).</summary>
    public static string CapturesDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                return overrideDir;
            }

            string root;
            if (OperatingSystem.IsWindows())
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            else
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                root = !string.IsNullOrWhiteSpace(xdg)
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }
            return Path.Combine(root, "Typhon", "Workbench", "captures");
        }
    }

    /// <summary>Resolve a fresh timestamped capture path, creating the directory if needed.</summary>
    public static string ResolveDefaultCapturePath()
    {
        var dir = CapturesDirectory;
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"typhon-capture-{stamp}{ReplayExtension}");
    }

    /// <summary>Path for a capture written automatically when a session is torn down, distinguishable from a manual save.</summary>
    /// <param name="directory">
    /// Where to write. Null selects the machine-local captures directory — the fallback for an endpoint-only Attach
    /// session, which has no database to co-locate the capture with. A session that HAS a database passes that
    /// database's <c>profilings/</c>, so the recorded window lands where the Profiles list already looks.
    /// </param>
    public static string ResolveAutoSavePath(string directory = null)
    {
        var dir = string.IsNullOrWhiteSpace(directory) ? CapturesDirectory : directory;
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"typhon-autosave-{stamp}{ReplayExtension}");
    }

    /// <summary>
    /// Enforce the captures directory's <see cref="RetentionPolicy"/>, newest-first: keep
    /// <see cref="RetentionPolicy.KeepLatest"/> captures unconditionally, then delete oldest-first until the total is
    /// within <see cref="RetentionPolicy.BudgetBytes"/>. Pinned names are never deleted but still count toward the
    /// budget, matching the policy's documented semantics.
    /// </summary>
    /// <param name="justWritten">
    /// The capture that was just produced. Never deleted by this pass — evicting the file whose creation triggered the
    /// prune would make capturing unreliable rather than bounded.
    /// </param>
    /// <param name="onFailure">Invoked with a reason if pruning fails. Retention is best-effort; it must never fail a save.</param>
    public static void ApplyRetention(string justWritten, Action<string> onFailure)
    {
        try
        {
            // Prune WHERE THE FILE LANDED, not where captures live by default. Since a session with a database saves
            // into that database's profilings/ instead, reading the default here would prune an unrelated directory
            // while leaving the one that just grew unbounded — and `justWritten` would match nothing in it, quietly
            // disabling the "never evict what we just wrote" guard.
            //
            // Pointing this at profilings/ is safe for engine-written captures: the scan below is restricted to
            // *.typhon-replay, and engine retention enumerates *.typhon-trace. The two sets are disjoint, so each
            // directory keeps exactly one owner per file kind rather than two policies racing over the same files.
            var dir = Path.GetDirectoryName(Path.GetFullPath(justWritten));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return;
            }

            var policy = RetentionPolicy.Read(dir, out _);
            var files = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(dir, "*" + ReplayExtension))
            {
                files.Add(new FileInfo(path));
            }
            // Newest first: index < KeepLatest is protected by the floor.
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
            }

            var keepLatest = Math.Max(policy.KeepLatest, 1);
            var budget = policy.BudgetBytes;
            if (budget <= 0)
            {
                return; // Non-positive budget disables eviction, per the policy's own contract.
            }

            for (var i = files.Count - 1; i >= keepLatest && total > budget; i--)
            {
                var candidate = files[i];
                if (policy.IsPinned(candidate.Name))
                {
                    continue;
                }
                if (string.Equals(candidate.FullName, Path.GetFullPath(justWritten), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var size = candidate.Length;
                try
                {
                    candidate.Delete();
                    total -= size;
                }
                catch (IOException)
                {
                    // Someone has it open — skip rather than fail the save that triggered this.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same.
                }
            }
        }
        catch (Exception ex)
        {
            onFailure?.Invoke($"retention pass failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
