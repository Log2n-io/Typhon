using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>
/// WA-14 (BKP-10): a copied bundle has its original's database and timeline ids, so two histories would interleave in one <c>wal/&lt;t&gt;/</c>. The second
/// archiver finds an LSN overlap or a name holding other bytes, stops (Diverged), and never touches what the first archived.
/// </summary>
[NonParallelizable]
class ArchiveDivergenceTests : ArchiveTestBase<ArchiveDivergenceTests>
{
    private const string Marker = "BKP-10: two histories in one timeline";

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-10")]
    public void ACopiedDatabase_ArchivingToTheSameTimeline_IsCaught_AndTheOriginalsArchiveIsUntouched()
    {
        var copyRoot = Path.Combine(TestStorage.Root, nameof(ArchiveDivergenceTests), CurrentDatabaseName + ".copy");
        if (Directory.Exists(copyRoot))
        {
            Directory.Delete(copyRoot, true);
        }

        string bundle, wal;
        Guid timeline;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            timeline = dbe.TimelineId;
            var archiver = NewArchiver(dbe);
            SpawnAndSeal(dbe, 20, 0);
            Tick(archiver, 0);
            Tick(archiver, 1);
            bundle = dbe.MMF.BundleDirectory;
            wal = dbe.WalDirectory;
        }

        // The copy: the bundle with its WAL inside (a restored-style layout), taken after the clean close.
        var copy = Path.Combine(copyRoot, Path.GetFileName(bundle));
        CopyDirectory(bundle, copy);
        CopyDirectory(wal, Path.Combine(copy, "wal"));

        // The original goes on and archives its continuation.
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var archiver = NewArchiver(dbe);
            SpawnAndSeal(dbe, 20, 100);
            Tick(archiver, 0);
            Tick(archiver, 1);
            Assert.That(archiver.State.Condition, Is.EqualTo(WalArchiveCondition.Archiving));
        }

        var original = Snapshot(timeline);

        // The copy goes on differently, into the same store and timeline.
        using (var provider = ProviderFor(copyRoot))
        {
            using var scope = provider.CreateScope();
            var dbe = Open(scope);
            Assert.That(dbe.TimelineId, Is.EqualTo(timeline), "premise: a copy keeps its original's timeline");
            var archiver = NewArchiver(dbe);
            SpawnAndSeal(dbe, 50, 7_000);
            Spawn(dbe, 5, 8_000);
            Tick(archiver, 0);
            Tick(archiver, 1);
            Tick(archiver, 2);
            Assert.That(archiver.State.Condition, Is.EqualTo(WalArchiveCondition.Diverged), Marker + " went unnoticed");
            Assert.That(archiver.State.LastError, Is.Not.Null);
        }

        Assert.That(Snapshot(timeline), Is.EqualTo(original), "the diverged archiver changed the original's archive");
        Directory.Delete(copyRoot, true);
    }

    private static string[] Snapshot(Guid timeline)
    {
        var dir = new WalArchivePaths(StorePath).Timeline(timeline);
        return Directory.GetFiles(dir, "*.twal").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + ":" + Crc32CUtil.Compute(File.ReadAllBytes(f))).ToArray();
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            if (!f.EndsWith(".lock", StringComparison.Ordinal))
            {
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            }
        }
    }
}
