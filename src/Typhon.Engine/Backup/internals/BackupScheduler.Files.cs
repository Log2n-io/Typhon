using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The scheduler's bundle file (plan 1416 §4.6, DL-23): <c>backup.status.json</c>, rewritten after each change, every tick while a job runs, and at stop.
/// </summary>
internal sealed partial class BackupScheduler
{
    private string _bundleDirectory;
    private long _fileVersion = -1;
    private HealthInputs _publishedInputs;

    partial void OnStarted() => _bundleDirectory = _engine.MMF.BundleDirectory;

    partial void OnStopped() => WriteStatusFile();

    /// <summary>
    /// Writes the status file when the published snapshot moved since the last write, or a job runs (its progress). Best effort, never throws.
    /// </summary>
    private void WriteStatusFileIfDue(bool running)
    {
        if (_bundleDirectory != null && (running || Volatile.Read(ref _publishedVersion) != _fileVersion))
        {
            WriteStatusFile();
        }
    }

    private void WriteStatusFile()
    {
        var bundle = _bundleDirectory;
        if (bundle == null)
        {
            return;
        }

        BackupStatusFileContent content;
        long version;
        lock (_lock)
        {
            version = _publishedVersion;
            content = new BackupStatusFileContent
            {
                FormatVersion = BackupStatusFileContent.CurrentFormatVersion,
                Pid = Environment.ProcessId,
                MachineName = Environment.MachineName,
                StorePath = _options.StorePath,
                AcceptRequests = _options.AcceptRequests,
                IntervalSeconds = _options.Interval.TotalSeconds,
                DrillWindowSeconds = _options.DrillWindow.TotalSeconds,
                MaxSegmentAgeSeconds = _options.Archive?.MaxSegmentAge.TotalSeconds ?? 0,
                Status = Volatile.Read(ref _status),
                Inputs = _publishedInputs,
            };
        }

        if (BackupStatusFile.TryWrite(bundle, content))
        {
            _fileVersion = version;
        }
    }
}
