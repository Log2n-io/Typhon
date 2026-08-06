using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Typhon.Engine;

/// <summary>
/// Owns the on-disk format of a database's cooperative locking protocol: <c>{name}.typhon/db.lock</c> and its companion <c>db.lock.request</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the lock actually is.</b> Two layers, and only one is soft. <c>db.lock</c> is purely cooperative — it records <i>who</i> holds the database so a
/// refused open can name them, and stale entries (dead PID) are cleaned up by the next opener. The real, OS-enforced exclusion is the data file's
/// <c>FileShare.Read</c> handle. There is no way to hold a mapped, write-open file "softly", which is why a holder that wants to step aside must actually
/// dispose its engine rather than downgrade a lock mode.
/// </para>
/// <para>
/// <b>The handoff protocol (#621).</b> A holder may advertise itself as <b>yieldable</b> — the Workbench does; every normal engine does not. A claimant that
/// finds a live <i>yieldable</i> lock writes a <see cref="RequestFileName"/> and waits briefly instead of failing. The holder sees the request, releases the
/// database, and the claimant acquires it.
/// The trigger is the <b>holder's advertisement</b>, not the claimant's configuration, so two ordinary application instances contend exactly as they always
/// did: the incumbent wrote <c>yieldable: false</c>, and the claimant throws.
/// </para>
/// <para>
/// <b>Why files rather than a named mutex or pipe.</b> The two parties may be different users or sessions; the bundle directory is the one thing they provably
/// share; the semantics are identical on Windows and Linux; and the state is inspectable after the fact. Windows named mutexes are session-scoped
/// (<c>Local\</c> vs <c>Global\</c>), which breaks service/desktop combinations.
/// </para>
/// <para>
/// <b>Why this type exists.</b> Both the engine (which enforces the lock) and out-of-process observers such as the Workbench (which must decide whether a
/// database is merely busy, and by whom) read and write these files. Left as string literals joined at each call site, the names and their fields drift between
/// assemblies with nothing to catch it — the same failure that silently broke <c>typhon ui --open-latest</c> when captures moved into the bundle, and
/// that let an instant-shaped trace event be decoded as a span.
/// </para>
/// </remarks>
public static class DatabaseLockFile
{
    /// <summary>File name of the advisory lock inside the database bundle.</summary>
    public const string FileName = "db.lock";

    /// <summary>File name of a claim on a <i>yieldable</i> database — "somebody wants this, please release it".</summary>
    public const string RequestFileName = "db.lock.request";

    /// <summary>
    /// How long a claim stays credible before a holder treats it as abandoned.
    /// </summary>
    /// <remarks>
    /// Bounds the case where a claimant dies, or the user cancels a launch, between writing its request and acquiring:
    /// the request file would otherwise pin the holder out of its own database forever. Generous relative to the claimant's own wait, so a slow-but-alive
    /// claimant is never declared dead out from under itself.
    /// </remarks>
    public static readonly TimeSpan RequestTimeToLive = TimeSpan.FromSeconds(60);

    /// <summary>Who holds a database, and whether they are willing to step aside.</summary>
    /// <param name="Pid">Process id of the holder.</param>
    /// <param name="MachineName">Machine the holder runs on. A different machine cannot be probed for liveness.</param>
    /// <param name="StartedAt">When the holder acquired the database.</param>
    /// <param name="Yieldable">
    /// <c>true</c> only when the holder explicitly advertised it. <b>Absent means false</b>, so a lock written by an
    /// older build — or by any normal engine — is never mistaken for one that will release on request.
    /// </param>
    public readonly record struct LockInfo(int Pid, string MachineName, DateTimeOffset StartedAt, bool Yieldable);

    /// <summary>A claim in flight on a yieldable database.</summary>
    /// <param name="Pid">Process id of the claimant.</param>
    /// <param name="MachineName">Machine the claimant runs on.</param>
    /// <param name="RequestedAt">When the claim was published, for TTL expiry.</param>
    public readonly record struct ClaimRequest(int Pid, string MachineName, DateTimeOffset RequestedAt);

    /// <summary>The advisory lock path for a database bundle. Does not check for existence.</summary>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    public static string PathFor(string bundleDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleDirectory);
        return Path.Combine(bundleDirectory, FileName);
    }

    /// <summary>The claim-request path for a database bundle. Does not check for existence.</summary>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    public static string RequestPathFor(string bundleDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleDirectory);
        return Path.Combine(bundleDirectory, RequestFileName);
    }

    /// <summary>Serialises a lock file's contents. The single writer of this format.</summary>
    /// <param name="pid">Process id of the holder.</param>
    /// <param name="startedAt">When the holder acquired the database.</param>
    /// <param name="machineName">Machine the holder runs on.</param>
    /// <param name="yieldable">Whether this holder will release the database on request.</param>
    public static string SerializeLock(int pid, DateTimeOffset startedAt, string machineName, bool yieldable) =>
        JsonSerializer.Serialize(new
        {
            pid,
            startedAt = startedAt.ToString("o"),
            machineName,
            yieldable,
        });

    /// <summary>
    /// Reads the lock file. Returns <c>false</c> for absent, empty, unparseable or truncated files.
    /// </summary>
    /// <remarks>
    /// A lock file is written non-atomically, so a reader can catch it mid-write; the honest answer to "who holds this?"
    /// in that instant is "cannot tell", not a fabricated identity. Callers that must act should re-read rather than treat an unreadable lock as absent.
    /// </remarks>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    /// <param name="info">Receives the recorded holder.</param>
    public static bool TryReadLock(string bundleDirectory, out LockInfo info)
    {
        info = default;
        try
        {
            var path = PathFor(bundleDirectory);
            if (!File.Exists(path))
            {
                return false;
            }

            var json = ReadAllTextShared(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var machine = root.TryGetProperty("machineName", out var m) ? m.GetString() ?? "unknown" : "unknown";
            var startedAt = root.TryGetProperty("startedAt", out var ts) && DateTimeOffset.TryParse(ts.GetString(), 
                out var parsed) ? parsed : DateTimeOffset.MinValue;
            // Absent ⇒ false. This default is what keeps every pre-#621 lock file, and every ordinary engine's lock,
            // safe from being treated as willing to yield.
            var yieldable = root.TryGetProperty("yieldable", out var y) && y.ValueKind == JsonValueKind.True;

            info = new LockInfo(root.GetProperty("pid").GetInt32(), machine, startedAt, yieldable);
            return true;
        }
        catch (Exception ex) when (IsExpectedIoOrFormatFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Reads the lock file's recorded holder, ignoring whether it is yieldable.</summary>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    /// <param name="pid">Receives the holder's process id.</param>
    /// <param name="machineName">Receives the holder's machine name.</param>
    /// <param name="startedAt">Receives when the holder acquired the database.</param>
    public static bool TryReadHolder(string bundleDirectory, out int pid, out string machineName, out DateTimeOffset startedAt)
    {
        if (TryReadLock(bundleDirectory, out var info))
        {
            (pid, machineName, startedAt) = (info.Pid, info.MachineName, info.StartedAt);
            return true;
        }
        (pid, machineName, startedAt) = (0, null, DateTimeOffset.MinValue);
        return false;
    }

    /// <summary>Whether a database bundle currently has an advisory lock file present. Says nothing about whether its owner is alive.</summary>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    public static bool Exists(string bundleDirectory) =>
        !string.IsNullOrEmpty(bundleDirectory) && File.Exists(Path.Combine(bundleDirectory, FileName));

    /// <summary>
    /// Whether the database is held by a process that is still running — the question "can an open plausibly succeed right now?".
    /// </summary>
    /// <remarks>
    /// <para>Mirrors the engine's own acceptance rule so an observer polling for its turn reaches the same verdict the next open will. Three cases return
    /// <c>false</c>, i.e. "worth trying": no lock file; a lock whose PID has exited (the engine deletes such a lock and proceeds); an unreadable lock (the
    /// engine treats a corrupt lock as removable). A lock from a <i>different machine</i> returns <c>true</c> — its PID means nothing locally, so the engine
    /// treats it as live, and a poller that disagreed would spin forever attempting opens that always fail.</para>
    /// <para>Inherently racy, and safely so: it is a "should I bother?" gate in front of an operation that re-checks under
    /// the real lock. A false positive costs one delayed poll; a false negative costs one failed open.</para>
    /// </remarks>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    public static bool IsHeldByLiveProcess(string bundleDirectory) => TryReadLock(bundleDirectory, out var info) && IsOwnerLive(info.Pid, info.MachineName);

    /// <summary>Whether the recorded owner of a lock or claim is still running. A different machine is always treated as live.</summary>
    /// <param name="pid">Process id recorded in the file.</param>
    /// <param name="machineName">Machine name recorded in the file.</param>
    public static bool IsOwnerLive(int pid, string machineName)
    {
        if (pid <= 0)
        {
            // Never a real holder: 0 is the System Idle Process on Windows and not a process at all on POSIX. Probing it
            // would reach the "cannot inspect" branch and read as live, letting one corrupt record wedge a database.
            return false;
        }

        if (!string.Equals(machineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true; // remote PID is unverifiable — the engine treats it as live, so must we
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false; // exited between lookup and query
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exists but cannot be inspected — another user's session, a protected process, or PID 0. "Cannot tell"
            // must read as live, because the alternative is evicting a process that is running.
            return true;
        }
    }

    // ── Claim requests ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Publishes a claim on a yieldable database. Best-effort: a failed write costs the wait, not correctness.</summary>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    public static void WriteRequest(string bundleDirectory)
    {
        try
        {
            File.WriteAllText(RequestPathFor(bundleDirectory), JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                machineName = Environment.MachineName,
                requestedAt = DateTimeOffset.UtcNow.ToString("o"),
            }));
        }
        catch (Exception ex) when (IsExpectedIoOrFormatFailure(ex))
        {
            // The holder will not be asked, so the claimant simply waits out its retry and fails as it would have before.
        }
    }

    /// <summary>Removes a claim. Called by the <b>claimant</b> once it has acquired, and by a holder retiring an orphan.</summary>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    public static void DeleteRequest(string bundleDirectory)
    {
        try
        {
            var path = RequestPathFor(bundleDirectory);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (IsExpectedIoOrFormatFailure(ex))
        {
            // Best-effort. A lingering request only delays the holder's resume until the TTL retires it.
        }
    }

    /// <summary>
    /// Whether a claim is in flight, and who by.
    /// </summary>
    /// <remarks>
    /// <b>Fail toward yielding.</b> A request file that exists but cannot be parsed still returns <c>true</c> with a default <paramref name="request"/>: the
    /// file's mere presence is the signal, and reading it is only ever to decide whether the claimant is still alive. Treating a half-written request as
    /// "no request" would let a holder ignore a claim it was about to be asked for — the one outcome this protocol must not produce.
    /// </remarks>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    /// <param name="request">
    /// Receives the claimant's identity, or <c>null</c> when the file is present but could not be read. Nullable rather than a sentinel value: any in-band
    /// marker (a zero pid, say) collides with a legitimately-recorded one, and the two cases lead to opposite decisions — an unreadable claim is honoured,
    /// a claim from a dead pid is retired.
    /// </param>
    public static bool TryReadRequest(string bundleDirectory, out ClaimRequest? request)
    {
        request = null;
        string json;
        try
        {
            var path = RequestPathFor(bundleDirectory);
            if (!File.Exists(path))
            {
                return false;
            }
            json = ReadAllTextShared(path);
        }
        catch (Exception ex) when (IsExpectedIoOrFormatFailure(ex))
        {
            return true; // present but unreadable — still a claim
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            request = new ClaimRequest(
                root.GetProperty("pid").GetInt32(),
                root.TryGetProperty("machineName", out var m) ? m.GetString() ?? "unknown" : "unknown",
                root.TryGetProperty("requestedAt", out var ts) && DateTimeOffset.TryParse(ts.GetString(), out var parsed) ? 
                    parsed : DateTimeOffset.MinValue);
            return true;
        }
        catch (Exception ex) when (IsExpectedIoOrFormatFailure(ex))
        {
            return true;
        }
    }

    /// <summary>
    /// Whether a claim exists that a holder must still honour — i.e. one whose claimant is alive and not past its TTL.
    /// </summary>
    /// <remarks>
    /// An orphaned request is <b>deleted</b> here rather than merely reported, because the only party that ever notices one is the holder it is blocking, and
    /// leaving it in place would keep that holder out of its own database.
    /// </remarks>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    /// <param name="utcNow">Current time, injectable so TTL expiry is testable without waiting.</param>
    public static bool HasLiveRequest(string bundleDirectory, DateTimeOffset utcNow)
    {
        if (!TryReadRequest(bundleDirectory, out var request))
        {
            return false;
        }

        // Present but unreadable: honoured on presence alone. It has no timestamp to age and no pid to probe, and guessing either way would mean ignoring a
        // claim we advertised we would answer.
        if (request is not { } claim)
        {
            return true;
        }

        var expired = claim.RequestedAt != DateTimeOffset.MinValue && utcNow - claim.RequestedAt > RequestTimeToLive;
        if (!expired && IsOwnerLive(claim.Pid, claim.MachineName))
        {
            return true;
        }

        DeleteRequest(bundleDirectory);
        return false;
    }

    private static string ReadAllTextShared(string path)
    {
        // FileShare.ReadWrite: the writer may still have this path open, and a reader must never be the reason an open fails.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool IsExpectedIoOrFormatFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException;
}
