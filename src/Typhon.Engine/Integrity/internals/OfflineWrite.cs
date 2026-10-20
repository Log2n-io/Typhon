using System;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>
/// The one gate in front of every write to a data file made without an engine (BKP-09, CS-05). A clean close leaves the chunk summary and the
/// backup-state beside the data file, vouched for by nonces in its meta; an offline writer does not touch the meta, so both would go on describing a file
/// they no longer match. They are deleted first, and one that cannot be deleted stops the writer: the engine survives a failed delete because its open
/// and close reset those nonces, but nothing would invalidate a file left behind an offline write.
/// </summary>
internal static class OfflineWrite
{
    /// <summary>
    /// Deletes the chunk summary and the backup-state of <paramref name="bundleDirectory"/>. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when either cannot be deleted; the caller must then write nothing.
    /// </summary>
    internal static void InvalidateDerivedFiles(string bundleDirectory)
    {
        File.Delete(Path.Combine(bundleDirectory, ChunkSummaryFile.FileName));
        var backupState = BackupStateFile.PathIn(bundleDirectory);
        File.Delete(backupState);
        BackupStateFile.Delete(backupState + ".tmp");   // never read, so never trusted: best effort
    }
}
