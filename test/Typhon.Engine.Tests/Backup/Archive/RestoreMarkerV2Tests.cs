using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Text;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>restore.json</c> version 2 (WA-12, WA-22): the target and effective LSNs, the origin the restored timeline records with its parent in one flip, and
/// <c>ForceCrashRecovery</c>, which a clean image restored past its own WAL needs or its open would ignore the archived WAL. Version 1 is still read; a
/// newer one is refused.
/// </summary>
[NonParallelizable]
class RestoreMarkerV2Tests : ArchiveTestBase<RestoreMarkerV2Tests>
{
    private const string Marker = "BKP-11: a clean image restored to an LSN";

    private static RestoreMarkerFile NewMarker(Guid databaseId, bool force, long effective = 0) => new()
    {
        FormatVersion = RestoreMarkerFile.CurrentFormatVersion,
        DatabaseId = databaseId,
        NewTimelineId = Guid.NewGuid(),
        RestoredUtc = DateTime.UtcNow,
        Tool = "test",
        RestoredFrom = new RestoredFrom { StoreId = Guid.NewGuid(), ChainId = Guid.NewGuid(), BackupId = 3, PointLsn = 40, TimelineId = Guid.NewGuid() },
        TargetLsn = effective == 0 ? 0 : effective + 2,
        EffectiveLsn = effective,
        ForceCrashRecovery = force,
    };

    [Test]
    public void Version2_RoundTrips_Version1IsStillRead_ANewerOneIsRefused()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        fs.CreateDirectory("/b");
        var marker = NewMarker(Guid.NewGuid(), force: true, effective: 120);
        Write(fs, RestoreMarker.Serialize(marker));
        Assert.That(RestoreMarker.TryRead(fs, "/b", out var read, out _), Is.EqualTo(RestoreMarkerState.Valid));
        Assert.That((read.FormatVersion, read.TargetLsn, read.EffectiveLsn, read.ForceCrashRecovery, read.OriginLsn), Is.EqualTo((2, 122L, 120L, true, 120L)));

        var v1 = NewMarker(Guid.NewGuid(), force: false);
        v1.FormatVersion = 1;
        Write(fs, RestoreMarker.Serialize(v1));
        Assert.That(RestoreMarker.TryRead(fs, "/b", out read, out _), Is.EqualTo(RestoreMarkerState.Valid));
        Assert.That((read.ForceCrashRecovery, read.OriginLsn), Is.EqualTo((false, 40L)), "a version-1 marker's origin is its point's P");

        var v3 = NewMarker(Guid.NewGuid(), force: false);
        v3.FormatVersion = 3;
        Write(fs, RestoreMarker.Serialize(v3));
        Assert.That(RestoreMarker.TryRead(fs, "/b", out _, out _), Is.EqualTo(RestoreMarkerState.Invalid));

        // A build that reads version 1 only (PR 1: FormatVersion must equal 1) refuses version 2 rather than misread it.
        Assert.That(Encoding.UTF8.GetString(RestoreMarker.Serialize(marker)), Does.Contain("\"formatVersion\": 2"));
    }

    [TestCase(true)]
    [TestCase(false)]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-11")]
    public void ForceCrashRecovery_OpensACleanImageOnTheCrashPath_AndTheRestoredTimelineRecordsItsParentAndOrigin(bool force)
    {
        var root = Path.Combine(TestStorage.Root, nameof(RestoreMarkerV2Tests), CurrentDatabaseName + (force ? ".f" : ".n"));
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        Directory.CreateDirectory(root);
        RestoreMarkerFile marker;
        using (var provider = ProviderFor(root))
        {
            using (var scope = provider.CreateScope())
            {
                var dbe = Open(scope);
                Spawn(dbe, 5);
                marker = NewMarker(dbe.DatabaseId, force, effective: dbe.WalManager.DurableLsn);
                File.WriteAllBytes(Path.Combine(dbe.MMF.BundleDirectory, RestoreMarker.FileName), RestoreMarker.Serialize(marker));
            }

            using (var scope = provider.CreateScope())
            {
                var dbe = Open(scope);
                Assert.That(dbe.CrashRecoveryAtOpen, Is.EqualTo(force), $"{Marker}: ForceCrashRecovery={force} after a clean close");
                Assert.That(dbe.TimelineId, Is.EqualTo(marker.NewTimelineId));
                Assert.That(dbe.TimelineParentId, Is.EqualTo(marker.RestoredFrom.TimelineId));
                Assert.That(dbe.TimelineOriginLsn, Is.EqualTo(marker.EffectiveLsn));
                Assert.That(dbe.RestoreOriginLsn, Is.EqualTo(marker.EffectiveLsn));
            }
        }

        Directory.Delete(root, true);
    }

    private static void Write(InMemorySparseFileSystem fs, byte[] bytes)
    {
        fs.DeleteFile("/b/" + RestoreMarker.FileName);
        using var f = fs.OpenCreateNew("/b/" + RestoreMarker.FileName);
        f.Write(bytes, 0);
    }
}
