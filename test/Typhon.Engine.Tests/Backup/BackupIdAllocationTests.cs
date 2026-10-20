using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// AC-S2 / BKP-08 / DC-6: a point id names one content for the store's life. Deleting the newest pack must not let its id be handed out again,
/// which a plain "max pack id + 1" would.
/// </summary>
[TestFixture]
internal sealed class BackupIdAllocationTests
{
    private const string Root = "/s";

    private const string Marker = "BKP-08: an id was handed out twice";

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-08")]
    public void DeletingTheNewestPack_NeverReusesItsId() => AssertNewestIdNeverReused(loseHighWater: false, loseCatalog: false);

    /// <summary>
    /// M5: a lost store.hwm used to reset the floor to the packs present, and the rebuilt catalog then overwrote the high-water it still recorded.
    /// The floor is the higher of the two when the catalog is this store's.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-08")]
    public void ALostHighWater_IsCoveredByTheCatalog_SoTheDeletedNewestIdStaysUsed() => AssertNewestIdNeverReused(loseHighWater: true, loseCatalog: false);

    /// <summary>
    /// A store that lost both its high-water and its catalog can only count the packs present: deleting the newest one frees its id. The check must
    /// see it.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-08")]
    public void Mutant_AStoreThatLostEveryHighWater_ReusesAnId() =>
        RuleMutants.AssertDetects("BKP-08", Marker, () => AssertNewestIdNeverReused(loseHighWater: true, loseCatalog: true));

    private static void AssertNewestIdNeverReused(bool loseHighWater, bool loseCatalog)
    {
        var image = BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var full = StoreTestKit.WritePoint(store, image);
            StoreTestKit.WritePoint(store, image, full.ChainId, pages: [2]);
            StoreTestKit.WritePoint(store, image, full.ChainId, pages: [3]);
        }

        fs.DeleteFile(Root + "/packs/00000003-I.tpack");
        if (loseHighWater)
        {
            fs.DeleteFile(Root + "/store.hwm");
        }

        if (loseCatalog)
        {
            fs.DeleteFile(Root + "/catalog.json");
        }

        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var next = StoreTestKit.WritePoint(store, image);
            Assert.That(next.BackupId, Is.EqualTo(4u), $"{Marker}: #3 was allocated once; its pack is gone but its id is not free");
        }
    }

    /// <summary>A catalog that is not this store's (copied in from another) is no authority on its ids: its high-water is ignored.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void AnotherStoresCatalog_DoesNotMoveTheFloor()
    {
        var image = BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            StoreTestKit.WritePoint(store, image);
        }

        var foreign = StoreCatalog.Build(new StoreIdentityFile { StoreId = Guid.NewGuid(), DatabaseId = image.DatabaseId }, "", 1000, [], []);
        fs.DeleteFile(Root + "/catalog.json");
        using (var f = fs.OpenCreateNew(Root + "/catalog.json"))
        {
            f.Write(StoreCatalog.Serialize(foreign), 0);
        }

        fs.DeleteFile(Root + "/store.hwm");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            Assert.That(StoreTestKit.WritePoint(store, image).BackupId, Is.EqualTo(2u), "past this store's packs, not another store's numbers");
        }
    }

    /// <summary>M6: an id past 99 999 999 names its pack with more than eight digits, and the store still sees it.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void AnIdPastEightDigits_IsStillAPackTheStoreSees()
    {
        var image = BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            StoreTestKit.WritePoint(store, image);
        }

        StoreHighWater.Write(fs, Root + "/store.hwm", 99_999_999);
        uint id;
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            id = StoreTestKit.WritePoint(store, image).BackupId;
        }

        Assert.That(id, Is.EqualTo(100_000_000u));
        Assert.That(fs.FileExists(Root + "/packs/100000000-F.tpack"), Is.True);
        Assert.That(StoreView.Open(Root, fs).Catalog.Packs.Select(p => p.BackupId), Does.Contain(100_000_000u), "the store sees the pack");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            Assert.That(StoreTestKit.WritePoint(store, image).BackupId, Is.EqualTo(100_000_001u), "and allocates past it");
        }
    }

    [TestCase("00000001-F.tpack", true, 1u)]
    [TestCase("100000000-I.tpack", true, 100_000_000u)]
    [TestCase("4294967295-C.tpack.tmp", true, uint.MaxValue)]
    [TestCase("000000001-F.tpack", false, 0u)]
    [TestCase("0000001-F.tpack", false, 0u)]
    [TestCase("4294967296-F.tpack", false, 0u)]
    [TestCase("12345678-X.tpack", false, 0u)]
    [TestCase("12345678-F.tpackx", false, 0u)]
    public void PackFileNames_ParseOnlyTheirCanonicalForm(string name, bool parses, uint id)
    {
        Assert.That(PackFormat.TryParseFileName(name, out var parsed, out _, out _), Is.EqualTo(parses));
        if (parses)
        {
            Assert.That(parsed, Is.EqualTo(id));
            Assert.That(PackFormat.FileName(id, BackupKind.Full)[..^"-F.tpack".Length], Is.EqualTo(name[..name.IndexOf('-')]), "FileName writes it");
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnAbortedPoint_ConsumesItsId()
    {
        var image = BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs);
        StoreTestKit.WritePoint(store, image);
        var sink = store.BeginPoint(StoreTestKit.Plan(store, image));
        Assert.That(sink.BackupId, Is.EqualTo(2u));
        sink.Abort();

        Assert.That(StoreTestKit.WritePoint(store, image).BackupId, Is.EqualTo(3u));
    }

    [Test]
    [CancelAfter(15_000)]
    public void ADamagedHighWater_DegradesToThePacks_AndSaysSo()
    {
        var image = BackupTestImages.Clean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            StoreTestKit.WritePoint(store, image);
            StoreTestKit.WritePoint(store, image);
        }

        fs.Poke(Root + "/store.hwm", 9, [0xFF]);
        var log = new ListLogger();
        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs, log))
        {
            Assert.That(StoreTestKit.WritePoint(store, image).BackupId, Is.EqualTo(3u), "past every pack present");
        }

        Assert.That(log.Messages, Has.Some.Contains("high-water").And.Contains("damaged"));
        Assert.That(StoreHighWater.Read(fs, Root + "/store.hwm", out _), Is.EqualTo(3u), "the next allocation repairs it");
    }

    /// <summary>Collects formatted log lines.</summary>
    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
