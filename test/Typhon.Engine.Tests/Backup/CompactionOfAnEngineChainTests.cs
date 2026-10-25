using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>
/// BKP-13 over a chain the engine captured online (points between real commits and checkpoints, on disk): the compacted head materialises to the same
/// bytes as its chain, verifies in full, restores, and the engine's next incremental rests on it.
/// </summary>
class CompactionOfAnEngineChainTests : BackupTestBase<CompactionOfAnEngineChainTests>
{
    private string _store;
    private string _scratch;

    public override void Setup()
    {
        base.Setup();
        var root = Path.Combine(TestStorage.ForFixture(nameof(CompactionOfAnEngineChainTests)), Guid.NewGuid().ToString("N"));
        _store = Path.Combine(root, "store");
        _scratch = Path.Combine(root, "scratch");
        Directory.CreateDirectory(_scratch);
    }

    public override void TearDown()
    {
        base.TearDown();
        try
        {
            Directory.Delete(Path.GetDirectoryName(_store), true);
        }
        catch (IOException)
        {
        }
    }

    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("BKP-13")]
    public void TheCompactedHeadOfAnEngineChain_IsItsImage_AndTheNextPointRestsOnIt()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var ids = Spawn(dbe, 300);
        BackupPointSummary head = null;
        for (var i = 0; i < 4; i++)
        {
            Update(dbe, ids.Where((_, n) => n % (i + 2) == 0), 100 + i);
            head = Create(dbe);
        }

        Assert.That(head.Kind, Is.EqualTo(BackupPointKind.Incremental), "premise: a chain");
        var before = Materialize(head.BackupId);

        var result = BackupOperations.Compact(_store, PointSelector.ById(head.BackupId));

        Assert.That((result.AlreadyBase, result.ChainDepth), Is.EqualTo((false, 4)));
        Assert.That(result.Point.Kind, Is.EqualTo(BackupPointKind.Compacted));
        Assert.That(result.Point.PointLsn, Is.EqualTo(head.PointLsn));
        Assert.That(Materialize(head.BackupId).AsSpan().SequenceEqual(before), Is.True, "the compacted head materialises to its chain's image");
        Assert.That(BackupOperations.Verify(_store, BackupVerifyLevel.Full).Verdict, Is.EqualTo(BackupVerifyVerdict.Ok));

        Update(dbe, ids.Take(10), 999);
        var next = Create(dbe);
        Assert.That((next.Kind, next.ParentId), Is.EqualTo((BackupPointKind.Incremental, head.BackupId)), "the lineage still continues the chain");
        var store = StoreView.Open(_store);
        Assert.That(ChainResolver.Resolve(store, next.BackupId).Packs.Select(p => p.Kind), Is.EqualTo(new[] { BackupKind.Compacted, BackupKind.Incremental }));

        var restored = BackupOperations.Restore(_store, PointSelector.ById(head.BackupId), Path.Combine(_scratch, "restored"));
        Assert.That(restored.ChainDepth, Is.EqualTo(1), "a restore of the head reads its base alone");
    }

    private BackupPointSummary Create(DatabaseEngine dbe)
    {
        var task = dbe.Backup.CreateAsync(_store);
        Assert.That(task.Wait(Budget), Is.True, "the backup did not finish");
        return task.Result;
    }

    private byte[] Materialize(uint id)
    {
        var fs = StoreFileSystem.Instance;
        var view = StoreView.Open(_store, trustCatalog: false);
        var packs = PackChainPageSource.OpenPacks(fs, view, ChainResolver.Resolve(view, id));
        var path = Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".data");
        try
        {
            using (var target = fs.OpenCreateNew(path))
            {
                ChainMaterializer.Materialize(packs, target);
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            foreach (var p in packs)
            {
                p.Dispose();
            }

            File.Delete(path);
        }
    }
}
