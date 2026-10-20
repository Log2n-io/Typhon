using System;
using System.IO;
using System.Text.Json;

namespace Typhon.Engine.Internals;

/// <summary><c>{bundle}/backup.request</c>: a process asking the running engine for a point (07 §3, plan 1416 §4.6). It carries no path, ever (BKP-16).</summary>
internal sealed class BackupRequestContent
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; }

    public Guid RequestId { get; set; }

    public string Kind { get; set; }

    public string RequestedBy { get; set; }

    public DateTime RequestedUtc { get; set; }
}

/// <summary>A request as the engine honours it: the kind and the id it echoes. An unreadable request is an Auto request with no id.</summary>
internal readonly record struct BackupRequest(Guid RequestId, BackupRequestKind Kind, string RequestedBy);

/// <summary>
/// The request side of the bundle files (DL-24): the engine polls for the file once a second (one <c>File.Exists</c>, its path resolved once), parses it
/// only when present, and deletes it when its job starts. Whoever writes it writes it whole (temp + rename): one writer format, <see cref="Write"/>.
/// </summary>
internal sealed class BackupRequestFile
{
    public const string FileName = "backup.request";

    private readonly string _path;

    public BackupRequestFile(string bundleDirectory) => _path = PathIn(bundleDirectory);

    public static string PathIn(string bundleDirectory) => Path.GetFullPath(Path.Combine(bundleDirectory, FileName));

    /// <summary>
    /// The request present now, if any. A file that cannot be read or parsed is still a request — an Auto one with no id (the <c>db.lock.request</c> rule:
    /// fail toward the claim). Whatever else it holds is ignored: the engine writes only to its configured store.
    /// </summary>
    public bool TryPoll(out BackupRequest request)
    {
        request = default;
        if (!File.Exists(_path))
        {
            return false;
        }

        request = new BackupRequest(Guid.Empty, BackupRequestKind.Auto, null);
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var content = JsonSerializer.Deserialize(stream, BackupJsonContext.Default.BackupRequestContent);
            if (content is { FormatVersion: > 0 })
            {
                var kind = string.Equals(content.Kind, "full", StringComparison.OrdinalIgnoreCase) ? BackupRequestKind.Full : BackupRequestKind.Auto;
                request = new BackupRequest(content.RequestId, kind, content.RequestedBy);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Present but unreadable: still a claim.
        }

        return true;
    }

    /// <summary>Retires the request its job started for. Best effort: one left behind is honoured again, a point too many rather than one too few.</summary>
    public void Delete()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes a request whole: a temp sibling, then a rename over nothing (an existing request is left alone: the caller waits for it).</summary>
    /// <returns><c>false</c> when a request is already pending.</returns>
    public static bool Write(string bundleDirectory, BackupRequestContent content)
    {
        var path = PathIn(bundleDirectory);
        if (File.Exists(path))
        {
            return false;
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(content, BackupJsonContext.Default.BackupRequestContent));
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
