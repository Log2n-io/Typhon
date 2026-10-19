using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// A restore excludes, on the real file system, for its whole run: an engine opening the target it is replacing (H4), and a second restore to the
/// same target (H5). Both used to be checked once, before staging, and could mix or race with what came during it.
/// </summary>
[TestFixture]
internal sealed class RestoreExclusionTests
{
    private const string Name = "BkExcl";
    private string _root;
    private string _bundle;
    private string _store;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestStorage.ForFixture(nameof(RestoreExclusionTests)), Guid.NewGuid().ToString("N"));
        var image = BackupTestImages.Build(Name, unclean: false, entities: 20, root: _root);
        _bundle = image.BundlePath;
        _store = Path.Combine(_root, "store");
        OfflineCapture.Create(_bundle, _store);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnEngineOpeningTheTargetWhileItIsReplaced_IsRefused()
    {
        Exception engineOpen = null;
        var tried = false;
        var outcome = RestoreStager.Run(_store, PointSelector.Latest, _bundle, new RestoreOptions
        {
            Replace = true,
            Progress = (_, _) =>
            {
                if (tried)
                {
                    return;
                }

                tried = true;
                using var provider = BackupTestImages.BuildProvider(Name, Path.GetDirectoryName(_bundle), Path.Combine(_bundle, "wal"));
                using var scope = provider.CreateScope();
                try
                {
                    scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
                }
                catch (Exception ex)
                {
                    engineOpen = ex;
                }
            },
        });

        Assert.That(tried, Is.True, "premise: the open was attempted while the restore was staging");
        Assert.That(engineOpen, Is.Not.Null, "an engine opened the database the restore was about to move aside");
        Assert.That(FindInChain<DatabaseLockedException>(engineOpen), Is.Not.Null, engineOpen.ToString());
        Assert.That(outcome.DisplacedBundle, Is.Not.Null);
        Assert.That(DatabaseLockFile.Exists(outcome.DisplacedBundle), Is.False, "the restore released the lock it took, where the bundle went");
    }

    [Test]
    [CancelAfter(30_000)]
    public void ASecondRestoreToTheSameTarget_IsRefusedWhileTheFirstRuns_AndTheFirstCompletes()
    {
        var target = Path.Combine(_root, "restored");
        RestoreRefusedException second = null;
        var tried = false;
        var outcome = RestoreStager.Run(_store, PointSelector.Latest, target, new RestoreOptions
        {
            Progress = (_, _) =>
            {
                if (tried)
                {
                    return;
                }

                tried = true;
                second = Assert.Throws<RestoreRefusedException>(() => RestoreStager.Run(_store, PointSelector.Latest, target));
            },
        });

        Assert.That(tried, Is.True);
        Assert.That(second.Reason, Is.EqualTo(RestoreRefusalReason.ForeignStaging));
        Assert.That(second.Message, Does.Contain("another restore"));
        Assert.That(File.Exists(Path.Combine(outcome.Target, "data")), Is.True, "the first restore was not disturbed");
        Assert.That(File.Exists(outcome.Target + RestoreStager.LockSuffix), Is.False, "the lock exists only while held");

        // Once the first is done, the target is free again.
        Assert.That(RestoreStager.Run(_store, PointSelector.Latest, target, new RestoreOptions { Replace = true }).DisplacedBundle, Is.Not.Null);
    }

    private static T FindInChain<T>(Exception ex) where T : Exception
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }
        }

        return null;
    }
}
