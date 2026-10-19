using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// T10 / AC-T10 / BKP-05: the first open of a restored bundle adopts the restore's new WAL timeline before the WAL manager starts, then deletes the
/// marker; a crash between the two repeats only the delete; a marker it cannot trust, or a WAL directory that is not the bundle's, refuses the open
/// with the data file untouched; and an open with no marker pays one existence probe.
/// </summary>
[TestFixture]
internal sealed class TimelineAdoptionTests
{
    private string _root;

    [SetUp]
    public void SetUp() => _root = Path.Combine(TestStorage.ForFixture(nameof(TimelineAdoptionTests)), Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        DatabaseEngine.RestoreAdoptionProbe = null;
        DatabaseEngine.BeforeWalManagerProbe = null;
        DatabaseEngine.RestoreMarkerFileSystemForTest = null;
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A fresh database, backed up, restored under <c>{root}/restored</c>; returns the image and the restore.</summary>
    private (BackupTestImage Image, RestoreOutcome Outcome) Restored(bool unclean = true)
    {
        var image = BackupTestImages.Build("BkT10" + (unclean ? "U" : "C"), unclean, entities: 12, root: Path.Combine(_root, "src"));
        var store = Path.Combine(_root, "store");
        using (var session = BackupStore.OpenForWrite(store, image.DatabaseId, true))
        {
            StoreTestKit.WritePoint(session, image, withTail: true);
        }

        return (image, RestoreStager.Run(store, PointSelector.Latest, Path.Combine(_root, "restored")));
    }

    private static ServiceProvider Provider(BackupTestImage image, string bundle, string walDir = null) =>
        BackupTestImages.BuildProvider(image.DatabaseName, Path.GetDirectoryName(bundle), walDir ?? Path.Combine(bundle, "wal"));

    private static DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static (Guid Timeline, ulong Generation) OnDisk(string bundle)
    {
        using var source = new OfflineBundlePageSource(bundle);
        var view = BootstrapReader.Read(source);
        var timeline = view.TryGet(DatabaseEngine.BK_TimelineId, out var v) ? BackupImageMeta.UnpackGuid(v) : Guid.Empty;
        return (timeline, view.SelectedGeneration);
    }

    private static string Marker(string bundle) => Path.Combine(bundle, RestoreMarker.FileName);

    [TestCase(true)]
    [TestCase(false)]
    [CancelAfter(60_000)]
    public void TheFirstOpen_AdoptsTheNewTimeline_BeforeTheWalManager_ThenDeletesTheMarker(bool unclean)
    {
        var (image, outcome) = Restored(unclean);
        var bundle = outcome.Target;
        var original = OnDisk(image.BundlePath).Timeline;
        Guid seenByWalManager = default;
        DatabaseEngine.BeforeWalManagerProbe = e => seenByWalManager = e.TimelineId;

        using (var provider = Provider(image, bundle))
        using (var scope = provider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.LastOpenRestoreAdoption, Is.EqualTo(RestoreAdoption.Flipped));
            Assert.That(dbe.TimelineId, Is.EqualTo(outcome.NewTimelineId).And.Not.EqualTo(original));
            Assert.That(seenByWalManager, Is.EqualTo(outcome.NewTimelineId), "every WAL segment this engine writes belongs to the new timeline");
            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Query<CompAArch>().Execute().Count, Is.EqualTo(12));
        }

        DatabaseEngine.BeforeWalManagerProbe = null;
        Assert.That(File.Exists(Marker(bundle)), Is.False);
        Assert.That(OnDisk(bundle).Timeline, Is.EqualTo(outcome.NewTimelineId));

        using (var provider = Provider(image, bundle))
        using (var scope = provider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.LastOpenRestoreAdoption, Is.EqualTo(RestoreAdoption.None));
            Assert.That(dbe.TimelineId, Is.EqualTo(outcome.NewTimelineId), "the timeline is durable, not re-minted");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public void ACrashBetweenTheFlipAndTheDelete_ReopensWithoutASecondFlip()
    {
        var (image, outcome) = Restored();
        var bundle = outcome.Target;
        DatabaseEngine.RestoreAdoptionProbe = () => throw new InvalidOperationException("crash between flip and delete");
        using (var provider = Provider(image, bundle))
        using (var scope = provider.CreateScope())
        {
            Assert.That(() => Open(scope), Throws.Exception);
        }

        DatabaseEngine.RestoreAdoptionProbe = null;
        Assert.That(File.Exists(Marker(bundle)), Is.True, "the delete never ran");
        Assert.That(OnDisk(bundle).Timeline, Is.EqualTo(outcome.NewTimelineId), "the flip was durable before the delete");

        using (var provider = Provider(image, bundle))
        using (var scope = provider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.LastOpenRestoreAdoption, Is.EqualTo(RestoreAdoption.AlreadyAdopted), "no second flip");
            Assert.That(dbe.TimelineId, Is.EqualTo(outcome.NewTimelineId));
            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Query<CompAArch>().Execute().Count, Is.EqualTo(12));
        }

        Assert.That(File.Exists(Marker(bundle)), Is.False);
    }

    private static readonly object[] Refusals =
    [
        new object[] { "an unreadable marker", (Action<string, RestoreOutcome>)((b, _) => File.WriteAllText(Marker(b), "{ not json")), "cannot be read" },
        new object[] { "a marker of another database", (Action<string, RestoreOutcome>)((b, _) => OtherDatabase(Marker(b))), "names database" },
    ];

    [TestCaseSource(nameof(Refusals))]
    [CancelAfter(60_000)]
    public void AMarkerTheOpenCannotTrust_RefusesIt_WithTheDataFileUntouched(string what, Action<string, RestoreOutcome> damage, string message)
    {
        var (image, outcome) = Restored();
        var bundle = outcome.Target;
        damage(bundle, outcome);
        var before = File.ReadAllBytes(Path.Combine(bundle, "data"));

        using (var provider = Provider(image, bundle))
        using (var scope = provider.CreateScope())
        {
            var ex = Assert.Catch(() => Open(scope), what);
            Assert.That(Flatten(ex), Has.Some.Matches<Exception>(e => e is StorageException { ErrorCode: TyphonErrorCode.InvalidDatabaseBundle }
                                                                       && e.Message.Contains(message)), what);
        }

        Assert.That(File.ReadAllBytes(Path.Combine(bundle, "data")), Is.EqualTo(before), $"{what}: the refused open wrote the data file");
        Assert.That(File.Exists(Marker(bundle)), Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    public void AWalDirectoryElsewhere_IsRefused_BeforeTheWalIsScanned()
    {
        var (image, outcome) = Restored();
        var bundle = outcome.Target;
        var elsewhere = Path.Combine(_root, "other-wal");
        Directory.CreateDirectory(elsewhere);
        var before = File.ReadAllBytes(Path.Combine(bundle, "data"));

        using (var provider = Provider(image, bundle, elsewhere))
        using (var scope = provider.CreateScope())
        {
            var ex = Assert.Catch(() => Open(scope));
            Assert.That(Flatten(ex), Has.Some.Matches<Exception>(e => e is StorageException && e.Message.Contains("replay that log")));
        }

        Assert.That(File.ReadAllBytes(Path.Combine(bundle, "data")), Is.EqualTo(before));
        Assert.That(Directory.GetFiles(elsewhere), Is.Empty, "the foreign WAL directory was never touched");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AMarkerWithoutData_IsNeverMistakenForANewDatabase()
    {
        var (image, outcome) = Restored();
        var bundle = outcome.Target;
        File.Delete(Path.Combine(bundle, "data"));

        using (var provider = Provider(image, bundle))
        using (var scope = provider.CreateScope())
        {
            var ex = Assert.Catch(() => Open(scope));
            Assert.That(Flatten(ex), Has.Some.Matches<Exception>(e => e is StorageException && e.Message.Contains("no data file")));
        }

        Assert.That(File.Exists(Path.Combine(bundle, "data")), Is.False, "no empty database was created under the restored name");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AnOpenWithNoMarker_PaysOneExistenceProbe()
    {
        var image = BackupTestImages.Build("BkT10Plain", unclean: false, entities: 4, root: Path.Combine(_root, "plain"));
        var counting = new CountingFileSystem();
        DatabaseEngine.RestoreMarkerFileSystemForTest = counting;
        using (var provider = Provider(image, image.BundlePath))
        using (var scope = provider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.LastOpenRestoreAdoption, Is.EqualTo(RestoreAdoption.None));
        }

        Assert.That(counting.Calls, Is.EqualTo(new[] { "FileExists" }), "a normal open pays exactly one existence probe, and reads nothing");
    }

    private static void OtherDatabase(string marker)
    {
        var m = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(marker), RestoreJsonContext.Default.RestoreMarkerFile);
        m.DatabaseId = Guid.NewGuid();
        File.WriteAllBytes(marker, RestoreMarker.Serialize(m));
    }

    private static IEnumerable<Exception> Flatten(Exception e)
    {
        for (; e != null; e = e.InnerException)
        {
            yield return e;
        }
    }

    /// <summary>Records every call the open makes through the marker seam.</summary>
    private sealed class CountingFileSystem : IStoreFileSystem
    {
        private readonly IStoreFileSystem _disk = StoreFileSystem.Instance;

        public List<string> Calls { get; } = [];

        public IStoreFile OpenCreateNew(string path, long preallocationSize = 0) => Record(nameof(OpenCreateNew), () => _disk.OpenCreateNew(path, preallocationSize));

        public IStoreFile OpenRead(string path) => Record(nameof(OpenRead), () => _disk.OpenRead(path));

        public IDisposable TryLockExclusive(string path) => Record(nameof(TryLockExclusive), () => _disk.TryLockExclusive(path));

        public bool ProbeExclusive(string path) => Record(nameof(ProbeExclusive), () => _disk.ProbeExclusive(path));

        public void Rename(string source, string destination, bool replace) => Record(nameof(Rename), () => _disk.Rename(source, destination, replace));

        public void FlushDirectory(string directory) => Record(nameof(FlushDirectory), () => _disk.FlushDirectory(directory));

        public bool FileExists(string path) => Record(nameof(FileExists), () => _disk.FileExists(path));

        public bool DirectoryExists(string path) => Record(nameof(DirectoryExists), () => _disk.DirectoryExists(path));

        public void CreateDirectory(string path) => Record(nameof(CreateDirectory), () => _disk.CreateDirectory(path));

        public void DeleteFile(string path) => Record(nameof(DeleteFile), () => _disk.DeleteFile(path));

        public void DeleteDirectory(string path) => Record(nameof(DeleteDirectory), () => _disk.DeleteDirectory(path));

        public IReadOnlyList<StoreFileEntry> EnumerateFiles(string directory) => Record(nameof(EnumerateFiles), () => _disk.EnumerateFiles(directory));

        public bool IsBundleHeldByLiveProcess(string bundleDirectory) =>
            Record(nameof(IsBundleHeldByLiveProcess), () => _disk.IsBundleHeldByLiveProcess(bundleDirectory));

        private T Record<T>(string call, Func<T> f)
        {
            Calls.Add(call);
            return f();
        }

        private void Record(string call, Action f)
        {
            Calls.Add(call);
            f();
        }
    }
}
