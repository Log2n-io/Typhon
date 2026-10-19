using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// Builds a database bundle from a backup point the way a restore will (#1413 replaces this with packs): the data file from the point's chain, newest
/// point first, pages no point holds as zeros; the WAL directory from the point's tail only, each segment at its declared size, its copied prefix
/// then zeros (C3). No chunk-summary and no backup-state: a restored bundle never carries them.
/// </summary>
internal static class TestPointMaterializer
{
    /// <summary>The chain that yields <paramref name="point"/>: the point, its parent, back to the full point, newest first.</summary>
    public static List<CapturingSink> Chain(FakeBackupStore store, CapturingSink point)
    {
        var chain = new List<CapturingSink> { point };
        var current = point;
        while (current.Plan.Kind == BackupKind.Incremental)
        {
            current = store.Points.Single(p => p.BackupId == current.Plan.ParentId);
            chain.Add(current);
        }

        return chain;
    }

    /// <summary>Writes <paramref name="point"/>'s data file to <paramref name="dataFilePath"/> and its tail to <paramref name="walDirectory"/>.</summary>
    /// <param name="store">The store holding the chain.</param>
    /// <param name="point">The point to restore.</param>
    /// <param name="dataFilePath">The bundle's data file.</param>
    /// <param name="walDirectory">The engine's WAL directory; emptied first.</param>
    /// <param name="skipFirstTailSegment">Mutant: restore the tail from its second segment on.</param>
    public static void Materialize(FakeBackupStore store, CapturingSink point, string dataFilePath, string walDirectory, bool skipFirstTailSegment = false)
    {
        var chain = Chain(store, point);
        var bundle = Path.GetDirectoryName(dataFilePath);
        foreach (var file in Directory.GetFiles(bundle))
        {
            if (Path.GetFileName(file) != "db.lock")
            {
                File.Delete(file);
            }
        }

        var page = new byte[PagedMMF.PageSize];
        using (var data = new FileStream(dataFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (var p = 0; p < point.ImagePages; p++)
            {
                var image = chain.Select(c => c.Pages.GetValueOrDefault(p)).FirstOrDefault(i => i != null);
                data.Write(image ?? page);
            }
        }

        if (Directory.Exists(walDirectory))
        {
            Directory.Delete(walDirectory, true);
        }

        Directory.CreateDirectory(walDirectory);
        foreach (var segment in point.Tail.Skip(skipFirstTailSegment ? 1 : 0))
        {
            using var wal = new FileStream(Path.Combine(walDirectory, $"{segment.SegmentId:D16}.wal"), FileMode.Create, FileAccess.Write, FileShare.None);
            wal.Write(segment.Bytes.GetBuffer(), 0, (int)segment.Bytes.Length);
            wal.SetLength(segment.DeclaredSize);
        }
    }
}
