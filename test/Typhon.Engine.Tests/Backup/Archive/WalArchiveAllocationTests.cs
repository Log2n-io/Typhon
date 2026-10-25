using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// The archiver's steady state (impl/1418 §3.9): a scheduler step that changes nothing allocates nothing, and archiving a segment allocates a few
/// kilobytes at most (handles, paths, its record) — never in proportion to the segment's bytes, which live in native memory.
/// </summary>
[NonParallelizable]
class WalArchiveAllocationTests : ArchiveTestBase<WalArchiveAllocationTests>
{
    [Test]
    [CancelAfter(30_000)]
    public void AnIdleStep_AllocatesNothing_AndASegmentAFewKilobytes()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var archiver = NewArchiver(dbe);
        Tick(archiver, 0);
        Tick(archiver, 0.5);
        SpawnAndSeal(dbe, 50, 0);
        Tick(archiver, 1);
        Tick(archiver, 2);

        // Idle steps: nothing changes, nothing is published.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            archiver.Step(Utc(3 + i * 0.001), Mono(3 + i * 0.001), default);
        }

        var idle = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(idle, Is.Zero, $"1 000 idle steps allocated {idle} B");

        // Segments: sealed by the test (its own allocations excluded), archived by the worker's body on this thread.
        const int segments = 50;
        long archiving = 0;
        for (var i = 0; i < segments; i++)
        {
            SpawnAndSeal(dbe, 5, 1_000 + i * 10);
            var start = GC.GetAllocatedBytesForCurrentThread();
            archiver.ArchivePending(default);
            archiving += GC.GetAllocatedBytesForCurrentThread() - start;
        }

        Assert.That(Archived(dbe).Segments.Count, Is.GreaterThanOrEqualTo(segments));
        var perSegment = archiving / segments;
        TestContext.Out.WriteLine($"archiving: {perSegment} B per segment");
        // Measured on Windows: 3.7 KB under a short TMP, 7.3 KB under a 100-character one (its paths then pass MAX_PATH and every file call copies them
        // to their extended-length form): the publish's file handles, the rename's two paths, the lock file's create and delete, the segment's record.
        // Constant per segment, whatever its size or the archive's (impl/1418 §3.9 estimated 4 KB). It was 10.4 KB at 50 segments and grew with the
        // archive: every 16th publish rebuilt the listing's fingerprint and serialized the whole index into one array.
        Assert.That(perSegment, Is.LessThanOrEqualTo(8 * 1024), $"{perSegment} B per archived segment");
    }

    /// <summary>
    /// A segment costs the same however many the timeline holds: the index cache is rewritten as the archive grows, and a rewrite that rebuilt the
    /// listing's fingerprint and serialized every entry into one array made each publish pay for the whole archive.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ASegmentCostsTheSame_HoweverLargeTheArchive()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var archiver = NewArchiver(dbe);
        Tick(archiver, 0);

        long Measure(int segments, int seed)
        {
            long total = 0;
            for (var i = 0; i < segments; i++)
            {
                SpawnAndSeal(dbe, 1, seed + i);
                var start = GC.GetAllocatedBytesForCurrentThread();
                archiver.ArchivePending(default);
                total += GC.GetAllocatedBytesForCurrentThread() - start;
            }

            return total / segments;
        }

        var early = Measure(16, 0);
        Measure(112, 10_000);
        var late = Measure(16, 20_000);
        TestContext.Out.WriteLine($"per segment: {early} B with 16 archived, {late} B with 144");
        Assert.That(late, Is.LessThanOrEqualTo(early + 512), $"a segment of a 144-segment archive costs {late} B, of a 16-segment one {early} B");
    }
}
