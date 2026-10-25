using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// Chain resolution over catalog entries (plan 1413 §2.5): the compacted pack of an id wins, every link is checked, and a broken chain names
/// the missing point and what is still restorable.
/// </summary>
[TestFixture]
internal sealed class ChainResolutionTests
{
    private static readonly Guid Db = Guid.Parse("d0d0d0d0-0000-0000-0000-000000000001");
    private static readonly Guid Store = Guid.Parse("5a5a5a5a-0000-0000-0000-000000000001");
    private static readonly Guid ChainA = Guid.Parse("c4a1c4a1-0000-0000-0000-00000000000a");
    private static readonly Guid ChainB = Guid.Parse("c4a1c4a1-0000-0000-0000-00000000000b");

    private static CatalogPack Pack(uint id, BackupKind kind, uint parent, uint baseId, Guid chain, int pages = 100, long c = 0, long p = -1) => new()
    {
        File = PackFormat.FileName(id, kind),
        BackupId = id,
        Kind = kind,
        ParentId = parent,
        BaseId = baseId,
        ChainId = chain,
        DatabaseId = Db,
        StoreId = Store,
        ImagePages = pages,
        CheckpointLsn = c == 0 ? id * 10 : c,
        PointLsn = p < 0 ? (c == 0 ? id * 10 : c) + 5 : p,
    };

    private static StoreView View(params CatalogPack[] packs) => View(packs, []);

    private static StoreView View(CatalogPack[] packs, CatalogUnreadable[] unreadable) => new()
    {
        Paths = new StorePaths("/s"),
        Identity = new StoreIdentityFile { FormatVersion = 1, StoreId = Store, DatabaseId = Db },
        Catalog = new StoreCatalogFile { Packs = packs.ToList(), Unreadable = unreadable.ToList() },
        Temps = [],
    };

    /// <summary>F1 ← I2 ← I3 ← I4 on chain A; F5 ← I6 on chain B.</summary>
    private static List<CatalogPack> TwoChains() =>
    [
        Pack(1, BackupKind.Full, 0, 1, ChainA),
        Pack(2, BackupKind.Incremental, 1, 1, ChainA),
        Pack(3, BackupKind.Incremental, 2, 1, ChainA),
        Pack(4, BackupKind.Incremental, 3, 1, ChainA),
        Pack(5, BackupKind.Full, 0, 5, ChainB),
        Pack(6, BackupKind.Incremental, 5, 5, ChainB),
    ];

    [Test]
    [CancelAfter(5_000)]
    public void APoint_ResolvesToItsBaseAndEveryLinkBetween()
    {
        var view = View(TwoChains().ToArray());

        Assert.That(ChainResolver.Resolve(view, 4).Packs.Select(p => p.BackupId), Is.EqualTo(new uint[] { 1, 2, 3, 4 }));
        Assert.That(ChainResolver.Resolve(view, 6).Packs.Select(p => p.BackupId), Is.EqualTo(new uint[] { 5, 6 }));
        Assert.That(ChainResolver.Resolve(view, 1).Depth, Is.EqualTo(1));
        Assert.That(ChainResolver.Select(view, PointSelector.Latest), Is.EqualTo(6u));
    }

    [Test]
    [CancelAfter(5_000)]
    public void ACompactedPack_IsPreferredOverTheOriginalOfTheSameId()
    {
        var packs = TwoChains();
        packs.Add(Pack(3, BackupKind.Compacted, 0, 3, ChainA));
        var view = View(packs.ToArray());

        var chain = ChainResolver.Resolve(view, 4);
        Assert.That(chain.Packs.Select(p => (p.BackupId, p.Kind)), Is.EqualTo(new[] { (3u, BackupKind.Compacted), (4u, BackupKind.Incremental) }));
        Assert.That(ChainResolver.Resolve(view, 3).Base.Kind, Is.EqualTo(BackupKind.Compacted));
    }

    [Test]
    [CancelAfter(5_000)]
    public void AMissingLink_BreaksTheChain_AndNamesWhatIsStillRestorable()
    {
        var packs = TwoChains().Where(p => p.BackupId != 2).ToArray();
        var view = View(packs);

        var ex = Assert.Throws<BackupChainBrokenException>(() => ChainResolver.Resolve(view, 4));
        Assert.That((ex.PointId, ex.MissingId), Is.EqualTo((4u, 2u)));
        Assert.That(ex.RestorableAlternatives, Is.EqualTo(new uint[] { 1, 5, 6 }));
        Assert.That(ex.Message, Does.Contain("backup-0002 is required for point #4"));
    }

    [Test]
    [CancelAfter(5_000)]
    public void AnUnreadableLink_IsReportedAsSuch()
    {
        var packs = TwoChains().Where(p => p.BackupId != 3).ToArray();
        var view = View(packs, [new CatalogUnreadable { File = "00000003-I.tpack", Reason = "FooterCRC mismatch" }]);

        var ex = Assert.Throws<BackupChainBrokenException>(() => ChainResolver.Resolve(view, 4));
        Assert.That(ex.MissingId, Is.EqualTo(3u));
        Assert.That(ex.Message, Does.Contain("FooterCRC mismatch"));
    }

    /// <summary>
    /// DL-9: a chain's <c>Depth</c> counts points (an id with an <c>-I</c> and a <c>-C</c> once) and its <c>HeadDepth</c> is what a restore of the
    /// head reads, the compacted base preferred. A head that does not resolve has none.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    public void TheCatalog_CountsPointsOnce_AndItsHeadDepthIsWhatARestoreOfTheHeadReads()
    {
        var packs = TwoChains();
        packs.Add(Pack(3, BackupKind.Compacted, 0, 3, ChainA));
        var identity = new StoreIdentityFile { FormatVersion = 1, StoreId = Store, DatabaseId = Db };
        var catalog = StoreCatalog.Build(identity, "f", 6, packs, []);

        var a = catalog.Chains.Single(c => c.ChainId == ChainA);
        var b = catalog.Chains.Single(c => c.ChainId == ChainB);
        Assert.That((a.Depth, a.HeadDepth, a.HeadBackupId), Is.EqualTo((4, 2, 4u)), "4 points; #4 reads 3-C and 4-I");
        Assert.That((b.Depth, b.HeadDepth), Is.EqualTo((2, 2)));
        Assert.That(ChainResolver.Resolve(View(packs.ToArray()), 4).Depth, Is.EqualTo(a.HeadDepth), "the catalog agrees with the resolver");

        var broken = TwoChains().Where(p => p.BackupId != 2).ToList();
        var brokenCatalog = StoreCatalog.Build(identity, "f", 6, broken, []);
        Assert.That(brokenCatalog.Chains.Single(c => c.ChainId == ChainA).HeadDepth, Is.Zero, "a head that does not resolve has no depth");
        Assert.That(brokenCatalog.FormatVersion, Is.EqualTo(2));
    }

    [Test]
    [CancelAfter(5_000)]
    public void AVersion1Catalog_IsRebuilt()
    {
        var identity = new StoreIdentityFile { FormatVersion = 1, StoreId = Store, DatabaseId = Db };
        var catalog = StoreCatalog.Build(identity, "f", 6, TwoChains(), []);
        catalog.FormatVersion = 1;

        Assert.That(StoreCatalog.TryDeserialize(StoreCatalog.Serialize(catalog)), Is.Null, "v1 counted packs as depth: never trusted");
    }

    private static readonly object[] BadLinks =
    [
        new object[] { "another chain", (Action<List<CatalogPack>>)(l => l[2].ChainId = ChainB), 3u },
        new object[] { "a parent that is not older", (Action<List<CatalogPack>>)(l => l[3].ParentId = 4), 4u },
        new object[] { "a shrinking data file", (Action<List<CatalogPack>>)(l => l[2].ImagePages = 200), 3u },
        new object[] { "a checkpoint going back", (Action<List<CatalogPack>>)(l => l[1].CheckpointLsn = 1000), 2u },
        new object[] { "another base id", (Action<List<CatalogPack>>)(l => l[2].BaseId = 2), 3u },
        new object[] { "another database", (Action<List<CatalogPack>>)(l => l[1].DatabaseId = Guid.NewGuid()), 2u },
    ];

    [TestCaseSource(nameof(BadLinks))]
    [CancelAfter(5_000)]
    public void AnInconsistentLink_BreaksTheChain(string what, Action<List<CatalogPack>> damage, uint brokenAt)
    {
        var packs = TwoChains();
        damage(packs);
        var view = View(packs.ToArray());

        var ex = Assert.Throws<BackupChainBrokenException>(() => ChainResolver.Resolve(view, 4), what);
        Assert.That(ex.MissingId, Is.EqualTo(brokenAt), what);
        Assert.That(ChainResolver.RestorablePoints(view), Does.Contain(5u).And.Contain(6u), "the other chain is unaffected");
    }
}
