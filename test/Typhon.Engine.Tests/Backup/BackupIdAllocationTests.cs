using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;

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
    public void DeletingTheNewestPack_NeverReusesItsId() => AssertNewestIdNeverReused(loseHighWater: false);

    /// <summary>A store whose high-water is gone can only count the packs present: deleting the newest one frees its id. The check must see it.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-08")]
    public void Mutant_AStoreThatLostItsHighWater_ReusesAnId() =>
        RuleMutants.AssertDetects("BKP-08", Marker, () => AssertNewestIdNeverReused(loseHighWater: true));

    private static void AssertNewestIdNeverReused(bool loseHighWater)
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

        using (var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs))
        {
            var next = StoreTestKit.WritePoint(store, image);
            Assert.That(next.BackupId, Is.EqualTo(4u), $"{Marker}: #3 was allocated once; its pack is gone but its id is not free");
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
