using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// WA-12 (BKP-11): an offline point of a cleanly closed database has its clean flag set; restored past its own WAL, its open must replay the archived WAL,
/// which the clean path never reads. The marker's <c>ForceCrashRecovery</c> sends it down the crash path; without it the restored state is the base's.
/// </summary>
[NonParallelizable]
class RestoreCleanBaseTests : ArchiveTestBase<RestoreCleanBaseTests>
{
    private const string Marker = "BKP-11: a clean base restored past its WAL";

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-11")]
    public void ACleanOfflineBase_RestoredPastItsWal_OpensOnTheCrashPath_ToTheTargetsState()
    {
        var (run, dir, cut) = RestorePastTheCleanBase(dropForce: false);
        AssertTargetState(run, dir, cut);
    }

    /// <summary>The same restore with the marker's flag dropped: the open takes the clean path and the restored state is the base's. Must be caught.</summary>
    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("BKP-11")]
    public void Mutant_AMarkerWithoutForceCrashRecovery_IsReported()
    {
        var (run, dir, cut) = RestorePastTheCleanBase(dropForce: true);
        RuleMutants.AssertDetects("BKP-11", Marker, () => AssertTargetState(run, dir, cut));
    }

    private (ArchivedWorkloadResult Run, string Dir, long Cut) RestorePastTheCleanBase(bool dropForce)
    {
        var run = RunArchivedWorkload(TestSeed.For("bkp-archive-clean-base"), stepsPerPhase: 4);
        var view = StoreView.Open(StorePath, trustCatalog: false);
        var clean = view.Find(run.Points[1]).Original;
        Assert.That(clean.Offline, Is.True, "premise: the second point is an offline point of a clean close");
        var target = run.Frames.Select(f => f.LastLsn).Where(l => l > clean.PointLsn && run.Expected(l) != run.Expected(clean.PointLsn)).Max();
        var dir = Path.Combine(TestStorage.Root, nameof(RestoreCleanBaseTests), CurrentDatabaseName + ".r");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        var outcome = RestoreStager.Run(StorePath, PointSelector.AtLsn(target), dir);
        Assert.That((outcome.PointId, outcome.ForcedCrashRecovery), Is.EqualTo((clean.BackupId, true)), "premise: the clean point is the base");
        var markerPath = Path.Combine(outcome.Target, RestoreMarker.FileName);
        var marker = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(markerPath), RestoreJsonContext.Default.RestoreMarkerFile);
        Assert.That((marker.FormatVersion, marker.ForceCrashRecovery, marker.TargetLsn, marker.EffectiveLsn), Is.EqualTo((2, true, target, outcome.EffectiveLsn)));
        if (dropForce)
        {
            marker.ForceCrashRecovery = false;
            File.WriteAllBytes(markerPath, RestoreMarker.Serialize(marker));
        }

        return (run, dir, outcome.EffectiveLsn);
    }

    private void AssertTargetState(ArchivedWorkloadResult run, string dir, long cut)
    {
        SuppressArchiving = true;
        try
        {
            using var provider = ProviderFor(dir);
            using var scope = provider.CreateScope();
            var dbe = Open(scope);
            using var t = dbe.CreateQuickTransaction();
            var versioned = t.Query<CompAArch>().Execute().ToDictionary(id => id, id => t.Open(id).Read(CompAArch.A).A);
            var singleVersion = t.Query<SvTestArchetype>().Execute().ToDictionary(id => id, id => t.Open(id).Read(SvTestArchetype.SvComp).Value);
            Assert.That(Diff(run.Expected(cut), versioned, singleVersion), Is.Null, $"{Marker}: the restored state is not the target's");
        }
        finally
        {
            SuppressArchiving = false;
            Directory.Delete(dir, true);
        }
    }
}
