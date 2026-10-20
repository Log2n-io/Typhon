using System;
using System.IO;
using System.Text.Json;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>{bundle}/backup.status.json</c> (07 §3, plan 1416 §4.6): the engine's backup status for other processes — the CLI, monitoring, the Workbench. It
/// carries the snapshot and what the verdict was judged on, so a reader of a stopped engine's file judges the time rules again at read time (DL-23): a
/// stopped engine's status degrades by itself instead of saying Ok forever.
/// </summary>
internal sealed class BackupStatusFileContent
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; }

    public int Pid { get; set; }

    public string MachineName { get; set; }

    public string StorePath { get; set; }

    public bool AcceptRequests { get; set; }

    public double IntervalSeconds { get; set; }

    public double DrillWindowSeconds { get; set; }

    public double MaxSegmentAgeSeconds { get; set; }

    public BackupStatus Status { get; set; }

    public HealthInputs Inputs { get; set; }
}

/// <summary>
/// Writes and reads the status file. The engine's writes are best effort (temp + replace, no fsync); a failed one is retried at the next change.
/// </summary>
internal static class BackupStatusFile
{
    public const string FileName = "backup.status.json";

    public static string PathIn(string bundleDirectory) => Path.Combine(bundleDirectory, FileName);

    /// <summary>
    /// Replaces the file whole. Never throws (BKP-16): <c>false</c> when it could not be written (a reader holding it on a share, a full disk).
    /// </summary>
    public static bool TryWrite(string bundleDirectory, BackupStatusFileContent content)
    {
        var path = PathIn(bundleDirectory);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(content, BackupJsonContext.Default.BackupStatusFileContent));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The file's content, or <c>null</c> when there is none or it cannot be read. Opened sharing read, write and delete: the engine's replace never fails
    /// on a reader.
    /// </summary>
    public static BackupStatusFileContent TryRead(string bundleDirectory)
    {
        var path = PathIn(bundleDirectory);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var content = JsonSerializer.Deserialize(stream, BackupJsonContext.Default.BackupStatusFileContent);
            return content is { FormatVersion: BackupStatusFileContent.CurrentFormatVersion, Status: not null } ? content : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the engine that wrote <paramref name="content"/> is running now: not stopped, and its process alive on this machine.</summary>
    public static bool IsLive(BackupStatusFileContent content) =>
        content.Status.State != BackupRunState.Stopped
        && string.Equals(content.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        && DatabaseLockFile.IsOwnerLive(content.Pid, content.MachineName);
}
