using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests.Backup;

/// <summary>Publishes a sealed temp with the store's durable move, and nothing else (no catalog). For pack-level tests.</summary>
internal sealed class DirectPublisher(IStoreFileSystem fs) : IPackPublisher
{
    public string Publish(PackWriter writer)
    {
        var final = writer.TempPath[..^PackFormat.TempSuffix.Length];
        StoreFileSystem.MoveDurable(fs, writer.TempPath, final, replace: false);
        return final;
    }
}

/// <summary>Writes packs from a <see cref="BackupTestImage"/> without a store, for format-level tests.</summary>
internal static class PackTestKit
{
    public static PackIdentity Identity(BackupTestImage image, BackupKind kind, uint id, uint parent = 0, uint baseId = 0, Guid chain = default,
        bool offline = false) =>
        new(image.DatabaseId, Guid.Parse("5a5a5a5a-0000-0000-0000-000000000001"), chain == default ? Guid.Parse("c4a1c4a1-0000-0000-0000-000000000001") : chain,
            id, parent, kind == BackupKind.Incremental ? baseId : id, kind, Guid.Parse("71e71e00-0000-0000-0000-000000000001"), image.FormatRevision,
            "test-1.0", offline);

    /// <summary>Opens a writer on <c>{dir}/{id:D8}-{K}.tpack.tmp</c>.</summary>
    public static PackWriter Begin(IStoreFileSystem fs, string dir, in PackIdentity identity, PackWriterOptions options = null)
    {
        var temp = dir + "/" + PackFormat.FileName(identity.BackupId, identity.Kind) + PackFormat.TempSuffix;
        return new PackWriter(fs, fs.OpenCreateNew(temp), identity, new DirectPublisher(fs), options);
    }

    /// <summary>
    /// Writes and publishes a pack holding <paramref name="pageIds"/> of <paramref name="image"/> (plus the meta pair) and, when asked, its
    /// whole WAL as the tail. <paramref name="override"/> replaces a page's bytes.
    /// </summary>
    public static BackupPointInfo Write(IStoreFileSystem fs, string dir, BackupTestImage image, in PackIdentity identity, IEnumerable<int> pageIds,
        bool withTail, int imagePages = 0, Func<int, byte[]> @override = null, PackWriterOptions options = null, long pointLsn = -1)
    {
        var writer = Begin(fs, dir, identity, options);
        try
        {
            var n = imagePages > 0 ? imagePages : image.PageCount;
            writer.SetImage(n, image.CheckpointLsn, 638_000_000_000_000_000);
            var added = new HashSet<int>();
            foreach (var p in pageIds)
            {
                if (added.Add(p))
                {
                    writer.AddPage(p, @override?.Invoke(p) ?? PageOf(image, p), suspect: false);
                }
            }

            foreach (var p in new[] { 0, 1 })
            {
                if (added.Add(p))
                {
                    writer.AddPage(p, image.Pages[p], false);
                }
            }

            if (withTail)
            {
                AddTail(writer, image);
            }

            return writer.Complete(pointLsn < 0 ? image.CheckpointLsn : pointLsn, 638_000_000_100_000_000);
        }
        catch
        {
            writer.Abort();
            throw;
        }
    }

    public static void AddTail(PackWriter writer, BackupTestImage image, int chunk = 7_000)
    {
        foreach (var s in image.WalSegments)
        {
            writer.BeginTailSegment(s.SegmentId, s.FirstLsn, s.SegmentSize);
            for (var pos = 0; pos < s.Bytes.Length; pos += chunk)
            {
                writer.AppendTail(s.Bytes.AsSpan(pos, Math.Min(chunk, s.Bytes.Length - pos)));
            }

            writer.EndTailSegment();
        }
    }

    /// <summary>The image's page, or a deterministic synthetic one past its end.</summary>
    public static byte[] PageOf(BackupTestImage image, int pageId)
    {
        if (pageId < image.PageCount)
        {
            return image.Pages[pageId];
        }

        var page = new byte[PackFormat.PageSize];
        new Random(pageId).NextBytes(page.AsSpan(0, 2048));
        return page;
    }

    public static IEnumerable<int> Range(int from, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return from + i;
        }
    }
}
