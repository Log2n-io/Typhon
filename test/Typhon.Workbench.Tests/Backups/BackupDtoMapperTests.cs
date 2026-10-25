using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Workbench.Backups;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>
/// #1417: what the engine's lifecycle results say reaches the wire as the engine says it — a retention's failed compactions with their kind (a damaged
/// kept chain is not an I/O failure), the kept-point health reasons with the engine's own sentence, and a retention that broke a kept point classified
/// as store damage, not as a defect.
/// </summary>
[TestFixture]
public sealed class BackupDtoMapperTests
{
    private static BackupRetentionResult Retention(params BackupCompactionFailure[] failures) =>
        new(new BackupRetentionPlan([1, 2], [], [2], [], 0, 10, 10, BackupRetentionBlock.None, null, "fp"), [], [], 0, TimeSpan.FromMilliseconds(5),
            failures);

    [Test]
    public void ARetentionsFailedCompactions_KeepTheirKind_DamageApartFromFailure()
    {
        var dto = BackupDtoMapper.Map(Retention(
            new BackupCompactionFailure(2, BackupFailureKind.StoreDamaged, "pack 00000001-F.tpack: page 7 fails its check"),
            new BackupCompactionFailure(5, BackupFailureKind.Failed, "the disk is full")));

        Assert.That(dto.FailedCompactions.Select(f => (f.BackupId, f.Kind, f.Message)), Is.EqualTo(new[]
        {
            (2u, "StoreDamaged", "pack 00000001-F.tpack: page 7 fails its check"),
            (5u, "Failed", "the disk is full"),
        }));
        Assert.That(BackupDtoMapper.Map(Retention()).FailedCompactions, Is.Empty);
    }

    [TestCase(BackupHealth.Failing, BackupHealthReason.KeptPointBroken)]
    [TestCase(BackupHealth.Degraded, BackupHealthReason.KeptPointDamaged)]
    public void AKeptPointHealthReason_CarriesTheEnginesSentence(BackupHealth health, BackupHealthReason reason)
    {
        var sentence = BackupHealthEvaluator.Explain(default, reason, DateTime.UtcNow);
        var status = BackupStatus.Off with { Health = health, Reason = reason, ReasonText = sentence };

        var dto = BackupDtoMapper.Map(status, "statusFile", true, 1, "m", "s", true, null);

        Assert.That((dto.Health, dto.HealthReason, dto.ReasonText), Is.EqualTo((health.ToString(), reason.ToString(), sentence)));
        Assert.That(sentence, Does.Not.Contain(reason.ToString()), "a sentence of its own, not the name");
    }

    [Test]
    public void ARetentionThatBrokeAKeptPoint_IsStoreDamage_InTheEnginesWords()
    {
        var e = new RetentionBrokeAKeptPointException("D:/store", 4, "its base pack is gone");

        Assert.That(BackupDtoMapper.Category(e), Is.EqualTo("store_damaged"));
    }
}
