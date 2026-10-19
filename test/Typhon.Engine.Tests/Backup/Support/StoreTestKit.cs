using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>Writes points into a store the way capture does — through <see cref="IBackupStoreSession"/> and <see cref="IBackupPointSink"/>.</summary>
internal static class StoreTestKit
{
    public static readonly Guid Timeline = Guid.Parse("71e71e00-0000-0000-0000-000000000002");

    /// <summary>A plan for a full point, or an incremental on the current head of <paramref name="chain"/>.</summary>
    public static BackupPointPlan Plan(IBackupStoreSession session, BackupTestImage image, Guid chain = default)
    {
        if (chain == default)
        {
            return new BackupPointPlan(image.DatabaseId, Timeline, BackupKind.Full, Guid.Empty, 0, image.FormatRevision, "test-1.0", false);
        }

        if (!session.TryGetChainHead(chain, out var head))
        {
            throw new InvalidOperationException($"no head for chain {chain}");
        }

        return new BackupPointPlan(image.DatabaseId, Timeline, BackupKind.Incremental, chain, head.BackupId, image.FormatRevision, "test-1.0", false);
    }

    /// <summary>
    /// Writes one point: every page of the image for a full one, <paramref name="pages"/> (plus the meta pair) for an incremental; the image's
    /// WAL as the tail when <paramref name="withTail"/>. <paramref name="override"/> replaces page bytes (to give each point its own content).
    /// </summary>
    public static BackupPointInfo WritePoint(IBackupStoreSession session, BackupTestImage image, Guid chain = default, IEnumerable<int> pages = null,
        bool withTail = false, int imagePages = 0, Func<int, byte[]> @override = null, Action<IBackupPointSink> beforeComplete = null)
    {
        var plan = Plan(session, image, chain);
        var sink = session.BeginPoint(plan);
        try
        {
            var n = imagePages > 0 ? imagePages : image.PageCount;
            sink.SetImage(n, image.CheckpointLsn, 638_000_000_000_000_000 + sink.BackupId);
            var ids = plan.Kind == BackupKind.Full ? Enumerable.Range(0, n) : (pages ?? []).Concat([0, 1]).Distinct();
            foreach (var p in ids)
            {
                sink.AddPage(p, @override?.Invoke(p) ?? PackTestKit.PageOf(image, p), suspect: false);
            }

            if (withTail)
            {
                PackTestKit.AddTail(sink, image);
            }

            beforeComplete?.Invoke(sink);
            return sink.Complete(image.CheckpointLsn, 638_000_000_100_000_000 + sink.BackupId);
        }
        catch
        {
            sink.Abort();
            throw;
        }
    }

    /// <summary>Page bytes distinct per point: page <c>p</c> of point <c>salt</c>. Pages 0 and 1 stay the image's (its meta pair).</summary>
    public static Func<int, byte[]> Salted(BackupTestImage image, int salt) => p =>
    {
        if (p < 2)
        {
            return image.Pages[p];
        }

        var page = (byte[])PackTestKit.PageOf(image, p).Clone();
        page[^1] ^= (byte)(salt * 37 + 1);
        page[^2] = (byte)salt;
        return page;
    };
}

/// <summary>A full check of one pack through <see cref="PackReader"/>: every section, page, tail segment and the FileCRC.</summary>
internal static class PackChecks
{
    /// <summary>Returns <c>null</c> when the pack is sound, else what failed.</summary>
    public static string VerifyFull(IStoreFileSystem fs, string path)
    {
        try
        {
            using var reader = PackReader.Open(fs, path);
            var page = new byte[PackFormat.PageSize];
            foreach (var e in reader.LoadIndex())
            {
                reader.ReadPage(e, page);
            }

            reader.LoadSuspects();
            var dir = reader.LoadTailDirectory();
            for (var i = 0; i < dir.Segments.Length; i++)
            {
                reader.ReadTailSegment(dir, i, null);
            }

            return reader.VerifyFileCrc() ? null : "FileCRC mismatch";
        }
        catch (BackupException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>A content hash of a pack's pages, by id: what a point's packs must still yield after any later operation.</summary>
    public static uint PagesHash(IStoreFileSystem fs, string path)
    {
        using var reader = PackReader.Open(fs, path);
        var page = new byte[PackFormat.PageSize];
        var crc = Crc32CUtil.Start;
        foreach (var e in reader.LoadIndex())
        {
            reader.ReadPage(e, page);
            crc = Crc32CUtil.Append(crc, BitConverter.GetBytes(e.PageId));
            crc = Crc32CUtil.Append(crc, page);
        }

        return Crc32CUtil.Finish(crc);
    }
}
