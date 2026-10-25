using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// Archive retention (#1415's seam, 05 §3 step 5, WA-16): below a kept timeline's floor, segments go oldest first, the directory flushed once for them
/// all, so a crash leaves the coverage starting later; a timeline no kept point needs goes whole, the live one never; a busy timeline is skipped and said.
/// </summary>
[TestFixture]
internal sealed class PruneArchiveTests
{
    private const string Root = "/s";
    private static readonly Guid Kept = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Gone = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Live = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static T Seed<T>(T fs) where T : InMemorySparseFileSystem
    {
        fs.CreateDirectory("/");
        fs.CreateDirectory(Root);
        BackupStore.OpenForWrite(Root, ArchiveTestKit.Database, true, fs).Dispose();
        for (var i = 0; i < 5; i++)
        {
            ArchiveTestKit.Publish(fs, Root, ArchiveTestKit.Segment(i + 1, 1 + i * 100, 8 * 1024, seed: i), 100 + i * 100, timeline: Kept);
        }

        for (var i = 0; i < 2; i++)
        {
            ArchiveTestKit.Publish(fs, Root, ArchiveTestKit.Segment(i + 1, 1 + i * 100, 8 * 1024, seed: i), 100 + i * 100, timeline: Gone);
            ArchiveTestKit.Publish(fs, Root, ArchiveTestKit.Segment(i + 1, 1 + i * 100, 8 * 1024, seed: i), 100 + i * 100, timeline: Live);
        }

        return fs;
    }

    private static long[] Ids(IStoreFileSystem fs, Guid t) =>
        WalArchiveStore.Load(fs, Root, t, trustIndex: false).Segments.Select(s => s.SegmentId).ToArray();

    [Test]
    [CancelAfter(10_000)]
    public void BelowTheFloor_SegmentsGoOldestFirst_ANeedlessTimelineGoesWhole_TheLiveOneStays()
    {
        var fs = Seed(new InMemorySparseFileSystem());
        ArchivePruneResult result;
        using (var session = BackupStore.OpenForWrite(Root, ArchiveTestKit.Database, false, fs))
        {
            result = WalArchiveStore.PruneArchive(session, [(Kept, 300)], Live);
        }

        Assert.That(Ids(fs, Kept), Is.EqualTo(new long[] { 3, 4, 5 }), "LastLsn < 300 is deleted (WA-16: < C, never ≤): segment 3 ends at 300 and stays");
        Assert.That(result.Deleted.Where(d => d.TimelineId == Kept).Select(d => d.File), Is.EqualTo(new[] { "0000000000000001.twal", "0000000000000002.twal" }));
        Assert.That(result.TimelinesDeleted, Is.EqualTo(new[] { Gone }));
        Assert.That(fs.DirectoryExists(new WalArchivePaths(Root).Timeline(Gone)), Is.False);
        Assert.That(Ids(fs, Live), Is.EqualTo(new long[] { 1, 2 }), "the live timeline with no kept point is kept whole");
        Assert.That(result.Skipped, Is.Empty);
    }

    /// <summary>
    /// The deletes below a floor are flushed with one directory flush per timeline, not one each: every deleted segment lies below every kept point's
    /// floor, so the order a crash persists them in decides no restore.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void ThePrunedSegmentsOfATimeline_AreFlushedOnce()
    {
        var fs = Seed(new FlushCounting());
        fs.Flushed.Clear();
        using (var session = BackupStore.OpenForWrite(Root, ArchiveTestKit.Database, false, fs))
        {
            WalArchiveStore.PruneArchive(session, [(Kept, 400)], Live);
        }

        Assert.That(Ids(fs, Kept), Is.EqualTo(new long[] { 4, 5 }), "premise: three segments pruned");
        var dir = new WalArchivePaths(Root).Timeline(Kept).Replace('\\', '/');
        Assert.That(fs.Flushed.Count(d => d.Replace('\\', '/') == dir), Is.EqualTo(1));
    }

    private sealed class FlushCounting : InMemorySparseFileSystem
    {
        public readonly System.Collections.Generic.List<string> Flushed = [];

        protected override void OnFlushDirectory(string directory) => Flushed.Add(directory);
    }

    /// <summary>
    /// Retention's archive step as wired (#1416 merge): the engine's retention names its live timeline and the needless ones go; a caller that sees only the
    /// store (<c>typhon backup prune</c>, the facade: <see cref="Guid.Empty"/>) deletes no timeline whole, since any of them may be a live engine's.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void TheDefaultPruner_WithoutTheLiveTimeline_DeletesNoTimelineWhole()
    {
        var fs = Seed(new InMemorySparseFileSystem());
        using (var session = BackupStore.OpenForWrite(Root, ArchiveTestKit.Database, false, fs))
        {
            RetentionExecutor.DefaultArchivePruner(session, [(Kept, 300)], Guid.Empty);
        }

        Assert.That(Ids(fs, Kept), Is.EqualTo(new long[] { 3, 4, 5 }), "the kept timeline is still pruned below its floor");
        Assert.That(Ids(fs, Gone), Is.EqualTo(new long[] { 1, 2 }), "a timeline with no kept point may be live: kept whole");
        Assert.That(Ids(fs, Live), Is.EqualTo(new long[] { 1, 2 }));

        using (var session = BackupStore.OpenForWrite(Root, ArchiveTestKit.Database, false, fs))
        {
            RetentionExecutor.DefaultArchivePruner(session, [(Kept, 300)], Live);
        }

        Assert.That(fs.DirectoryExists(new WalArchivePaths(Root).Timeline(Gone)), Is.False, "with the live timeline known, a needless one goes whole");
        Assert.That(Ids(fs, Live), Is.EqualTo(new long[] { 1, 2 }));
    }

    [Test]
    [CancelAfter(10_000)]
    public void ABusyTimeline_IsSkippedAndReported()
    {
        var fs = Seed(new InMemorySparseFileSystem());
        using var held = WalArchiveTimeline.TryOpen(fs, Root, Kept);
        using var session = BackupStore.OpenForWrite(Root, ArchiveTestKit.Database, false, fs);
        var result = WalArchiveStore.PruneArchive(session, [(Kept, 300)], Live);
        Assert.That(result.Skipped.Select(s => s.TimelineId), Is.EqualTo(new[] { Kept }));
        Assert.That(Ids(fs, Kept).Length, Is.EqualTo(5));
    }

    [TestCase(CrashModelKind.Windows)]
    [TestCase(CrashModelKind.Linux)]
    [CancelAfter(30_000)]
    public void ACrashAnywhereInAPrune_LeavesTheCoverageStartingLater_NeverAHole(CrashModelKind kind)
    {
        for (var k = 0; ; k++)
        {
            var fs = Seed(new CrashModelFileSystem(kind));
            fs.SyncAll();
            fs.CrashBefore(k);
            var finished = false;
            try
            {
                using var session = BackupStore.OpenForWrite(Root, ArchiveTestKit.Database, false, fs);
                WalArchiveStore.PruneArchive(session, [(Kept, 400)], Live);
                finished = true;
            }
            catch (SimulatedCrashException)
            {
            }

            var before = Ids(fs, Kept);
            fs.Crash();
            var ids = Ids(fs, Kept);
            Assert.That(ids, Is.EqualTo(Enumerable.Range((int)ids[0], 5 - (int)ids[0] + 1).Select(i => (long)i)), $"{kind} k={k}: a hole in the coverage");
            if (finished)
            {
                // A delete is durable once its directory is flushed (Linux) or with the next write-through rename (Windows): at worst the coverage
                // starts earlier after the crash, which costs disk, never a restore.
                Assert.That(before, Is.EqualTo(new long[] { 4, 5 }));
                return;
            }
        }
    }
}
