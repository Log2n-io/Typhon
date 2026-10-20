using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// P2: verify, status and --restorable resolve every point's chain. With a catalog scan per link they were cubic in the store's points; with the
/// catalog indexed and resolutions memoised per view they are quadratic in the worst case (one chain) and linear when chains are shallow.
/// <c>status</c> also stops short of a quick verify: headers, footers and chains only.
/// </summary>
[TestFixture]
internal sealed class StoreScalingTests
{
    /// <summary>One chain of 2 000 points, resolved in memory (no pack on disk): the shape that was worst.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void ResolvingEveryPointOfALongChain_TakesLinkChecksNotCatalogScans()
    {
        const int points = 2_000;
        var identity = new StoreIdentityFile { StoreId = Guid.NewGuid(), DatabaseId = Guid.NewGuid() };
        var chain = Guid.NewGuid();
        var packs = new List<CatalogPack>(points);
        for (uint id = 1; id <= points; id++)
        {
            packs.Add(new CatalogPack
            {
                File = PackFormat.FileName(id, id == 1 ? BackupKind.Full : BackupKind.Incremental),
                BackupId = id,
                Kind = id == 1 ? BackupKind.Full : BackupKind.Incremental,
                DatabaseId = identity.DatabaseId,
                StoreId = identity.StoreId,
                ChainId = chain,
                ParentId = id - 1,
                BaseId = 1,
                ImagePages = 64,
                CheckpointLsn = id * 10,
                PointLsn = (id * 10) + 5,
            });
        }

        var view = new StoreView
        {
            Paths = new StorePaths("/s"),
            Identity = identity,
            Catalog = StoreCatalog.Build(identity, "", points, packs, []),
            Temps = [],
        };

        var clock = Stopwatch.StartNew();
        var restorable = ChainResolver.RestorablePoints(view);
        var again = ChainResolver.RestorablePoints(view);
        clock.Stop();

        Assert.That(restorable, Has.Count.EqualTo(points));
        Assert.That(again, Is.EqualTo(restorable));
        Assert.That(view.Resolved, Has.Count.EqualTo(points), "each point resolved once per view, then remembered");
        // 2 000 chains of up to 2 000 links: ~2 M link checks, well under a second. A catalog scan per link made it ~4 G comparisons, minutes.
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), $"{clock.ElapsedMilliseconds} ms to resolve {points} points twice");
    }

    /// <summary>
    /// <c>status</c> reads headers, footers and chains, never an index or a tail: a newest point whose index is damaged (its header and footer intact)
    /// is Ok to status and Damaged to verify, which reads it. Status used to run a quick verify of every pack's index and tail.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void Status_ReadsHeadersAndFooters_VerifyReadsTheRest()
    {
        var root = Path.Combine(TestStorage.ForFixture(nameof(StoreScalingTests)), Guid.NewGuid().ToString("N"));
        var store = Path.Combine(root, "store");
        var image = BackupTestImages.Build("BkScale", unclean: false, entities: 10, root: root);
        try
        {
            OfflineCapture.Create(image.BundlePath, store);
            var pack = Directory.GetFiles(Path.Combine(store, "packs"), "*.tpack").Single();
            long indexOffset;
            using (var reader = PackReader.Open(StoreFileSystem.Instance, pack))
            {
                indexOffset = reader.Footer.IndexOffset;
            }

            using (var f = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite))
            {
                f.Position = indexOffset + 16;   // the first entry's PageCrc: only the IndexCRC covers it
                var b = f.ReadByte();
                f.Position = indexOffset + 16;
                f.WriteByte((byte)(b ^ 0xFF));
            }

            Assert.That(BackupOperations.Status(store).Health, Is.EqualTo(BackupHealth.Ok), "status reads no index");
            Assert.That(BackupOperations.Verify(store, BackupVerifyLevel.Quick).Verdict, Is.EqualTo(BackupVerifyVerdict.NoRestorablePoint),
                "verify reads it");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
