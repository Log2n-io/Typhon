using System;

namespace Typhon.Engine;

/// <summary>The engine's side of its configured backups (#1416): the options, checked once at construction, and what they feed.</summary>
public partial class DatabaseEngine
{
    /// <summary>This engine's backup configuration; null when backup is off.</summary>
    internal BackupOptions BackupOptions => _options.Backup;

    /// <summary>
    /// Checks the backup configuration before anything runs (a host that skips the options validators still cannot start with a broken one), and hands
    /// the capture tuning it carries to the coordinator.
    /// </summary>
    private void ConfigureBackup()
    {
        ValidateArchiveConfiguration();
        var backup = _options.Backup;
        if (backup == null)
        {
            return;
        }

        if (!backup.Validate(_options.Wal, out var error))
        {
            throw new ArgumentException(error);
        }

        BackupCoordinator.Options.CbwBufferPages = backup.CbwBufferPages;
        BackupCoordinator.Options.CbwStallTimeout = backup.CbwStallTimeout;
    }
}
