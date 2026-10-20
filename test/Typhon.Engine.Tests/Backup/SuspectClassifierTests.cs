using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// DL-22 (d): a point's suspect pages are judged by the point's own occupancy. A suspect on an allocated page is data kept damaged (a warning); one on a
/// free page is not data at all, and must not keep every later point Warning.
/// </summary>
[TestFixture]
internal sealed class SuspectClassifierTests
{
    private const string Root = "/s";

    [Test]
    [CancelAfter(15_000)]
    public void ASuspectOnAFreePage_IsNotOneOnAnAllocatedPage()
    {
        var image = BackupTestImages.Clean;
        int allocated, free;
        using (var bundle = new OfflineBundlePageSource(image.BundlePath))
        {
            var occupancy = SuspectClassifier.ReadOccupancy(bundle);
            Assert.That(occupancy, Is.Not.Null.And.Property(nameof(OccupancyView.IsComplete)).True, "premise: the image has a readable bitmap");
            allocated = Enumerable.Range(2, image.PageCount - 2).First(occupancy.IsAllocated);
            free = Enumerable.Range(2, image.PageCount + 64).First(p => !occupancy.IsAllocated(p));
        }

        var n = System.Math.Max(image.PageCount, free + 1);
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var sink = store.BeginPoint(StoreTestKit.Plan(store, image));
            sink.SetImage(n, image.CheckpointLsn, 1);
            for (var p = 0; p < n; p++)
            {
                sink.AddPage(p, PackTestKit.PageOf(image, p), suspect: p == allocated || p == free);
            }

            sink.Complete(image.CheckpointLsn, 2);
            StoreTestKit.WritePoint(store, image, sink.ChainId, pages: [allocated], imagePages: n);
        }

        var view = StoreView.Open(Root, fs);
        Assert.That(SuspectClassifier.Classify(fs, view, 1), Is.EqualTo((1, 1)));
        Assert.That(SuspectClassifier.Classify(fs, view, 2), Is.EqualTo((0, 0)), "a point is judged by its own pack's suspects");
    }
}
