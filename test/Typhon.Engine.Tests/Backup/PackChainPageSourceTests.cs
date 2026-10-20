using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// A point read page by page without materialising it (plan 1413 §2.10): identical to the materialised file, sparse when it is one pack alone,
/// and — being an <see cref="IPageSource"/> — something the integrity scanner reads exactly as it reads the restored bundle.
/// </summary>
[TestFixture]
internal sealed class PackChainPageSourceTests
{
    [TestCase(1)]
    [TestCase(9)]
    [TestCase(40)]
    [CancelAfter(30_000)]
    public void EveryPage_EqualsTheModel(int depth)
    {
        var chain = SyntheticChain.Build(depth, 100 + depth);
        var view = chain.View();
        var page = new byte[PackFormat.PageSize];
        for (var i = 0; i < depth; i += Math.Max(1, depth / 5))
        {
            var id = chain.Points[i].BackupId;
            // A tiny INDEX cache: blocks are evicted and reloaded constantly.
            using var source = PackChainPageSource.Open(chain.Fs, view, ChainResolver.Resolve(view, id), new PackSourceOptions { IndexBlockCacheSize = 1 });
            Assert.That(source.PageCount, Is.EqualTo(chain.ExpectedPageCount(i)));
            Assert.That(source.Describe(), Does.Contain($"#{id}"));
            // Out of order on purpose: a scanner jumps around.
            foreach (var p in Enumerable.Range(0, source.PageCount).Reverse())
            {
                Assert.That(source.TryReadPage(p, page), Is.True);
                if (!page.AsSpan().SequenceEqual(chain.ExpectedPage(i, p)))
                {
                    Assert.Fail($"point #{id}: page {p} differs from the model");
                }
            }

            Assert.That(source.TryReadPage(source.PageCount, page), Is.False);
            Assert.That(source.TryReadPage(-1, page), Is.False);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void OnePackAlone_IsSparse()
    {
        var chain = SyntheticChain.Build(3, 7);
        var incremental = chain.Points[2];
        using var reader = PackReader.Open(chain.Fs, incremental.PackPath);
        var held = reader.LoadIndex().Select(e => (int)e.PageId).ToHashSet();

        using var source = PackPageSource.Open(chain.Fs, incremental.PackPath);
        var page = new byte[PackFormat.PageSize];
        for (var p = 0; p < source.PageCount; p++)
        {
            Assert.That(source.TryReadPage(p, page), Is.EqualTo(held.Contains(p)), $"page {p}");
            if (held.Contains(p))
            {
                Assert.That(page, Is.EqualTo(chain.ExpectedPage(2, p)));
            }
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void ADamagedFrame_IsAbsentAndListed_OrThrows()
    {
        var chain = SyntheticChain.Build(2, 9);
        var victim = chain.Points[1];
        uint pageId;
        using (var reader = PackReader.Open(chain.Fs, victim.PackPath))
        {
            var entry = reader.LoadIndex().First(e => e.StoredLength > 0 && e.PageId >= 2);
            pageId = entry.PageId;
            chain.Fs.Poke(victim.PackPath, entry.DataOffset + 2, [0x55, 0x66]);
        }

        var view = chain.View();
        var page = new byte[PackFormat.PageSize];
        using (var source = PackChainPageSource.Open(chain.Fs, view, ChainResolver.Resolve(view, victim.BackupId),
                   new PackSourceOptions { OnDamage = PackDamageMode.ReportAbsent }))
        {
            Assert.That(source.TryReadPage((int)pageId, page), Is.False);
            Assert.That(source.Damaged.Single().PageId, Is.EqualTo((int)pageId));
            Assert.That(source.TryReadPage(0, page), Is.True, "the rest of the point stays readable");
        }

        using (var source = PackChainPageSource.Open(chain.Fs, view, ChainResolver.Resolve(view, victim.BackupId)))
        {
            var ex = Assert.Throws<BackupPackCorruptException>(() => source.TryReadPage((int)pageId, page));
            Assert.That(ex.PageId, Is.EqualTo(pageId));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void TheScanner_SeesThePointAsItSeesTheMaterialisedFile()
    {
        var image = BackupTestImages.Clean;
        var dir = Path.Combine(TestStorage.ForFixture(nameof(PackChainPageSourceTests)), Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = BackupStore.OpenForWrite(Path.Combine(dir, "store"), image.DatabaseId, true))
            {
                var full = StoreTestKit.WritePoint(store, image);
                // An incremental of real pages, unchanged: the point is the database, so the scanner has a real structure to walk.
                StoreTestKit.WritePoint(store, image, full.ChainId, pages: Enumerable.Range(2, Math.Min(6, image.PageCount - 2)));
            }

            var bundle = Path.Combine(dir, image.DatabaseName + ".typhon");  // the name the image records (DC-1)
            Directory.CreateDirectory(bundle);
            var view = StoreView.Open(Path.Combine(dir, "store"));
            var chain = ChainResolver.Resolve(view, 2);
            var packs = PackChainPageSource.OpenPacks(StoreFileSystem.Instance, view, chain);
            using (var file = StoreFileSystem.Instance.OpenCreateNew(Path.Combine(bundle, "data")))
            {
                ChainMaterializer.Materialize(packs, file);
            }

            foreach (var p in packs)
            {
                p.Dispose();
            }

            IntegrityReport fromPacks;
            using (var source = PackChainPageSource.Open(Path.Combine(dir, "store"), PointSelector.ById(2)))
            {
                fromPacks = IntegrityScanner.Scan(source, IntegrityOptions.Standard);
            }

            IntegrityReport fromFile;
            using (var source = new OfflineBundlePageSource(bundle))
            {
                fromFile = IntegrityScanner.Scan(source, IntegrityOptions.Standard);
            }

            // The bundle has no WAL beside it; the pack source has none at all: WAL checks are the bundle's only, by design (plan Q7).
            static string[] PageFindings(IntegrityReport r) =>
                r.Findings.Where(f => !f.Code.StartsWith("WAL", StringComparison.Ordinal)).Select(f => $"{f.Code}@{f.Locus}").OrderBy(s => s).ToArray();

            Assert.That(PageFindings(fromPacks), Is.EqualTo(PageFindings(fromFile)),
                string.Join("; ", fromFile.Findings.Select(f => f.ToString())));
            Assert.That(fromPacks.Totals.PagesScanned, Is.EqualTo(fromFile.Totals.PagesScanned));
            Assert.That(fromPacks.Totals.ChecksumFailures, Is.Zero);
            Assert.That(fromPacks.Identity.Name, Is.EqualTo(image.DatabaseName));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
