using NUnit.Framework;
using System;
using System.Linq;
using Typhon.Engine;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// Damage of a known shape, through the whole pipeline, checked against what the repair was supposed to achieve.
/// </summary>
/// <remarks>
/// <para>
/// The point of separating these from the scanner tests is the last assertion in each: not *"the tool reported
/// success"* but *"the property the damage destroyed is back"*. A repair that rewrites a page, returns
/// <c>Succeeded</c> and re-scans clean has still failed if the redundancy `CK-05` exists to guarantee is not actually
/// restored — and every intermediate signal would look identical either way.
/// </para>
/// <para>
/// Each test therefore ends by re-damaging in the *opposite* direction: if the repair genuinely rebuilt the pair, the
/// database survives losing the other half too. If it merely made the scanner happy, it will not.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class DamageRepairRoundTripTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void StaleMetaSlot_PlanIsLosslessRegeneration()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        var damage = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var plan = DatabaseRepair.Plan(report);

        Assert.That(plan.Steps, Is.Not.Empty, "a repairable finding must produce a step");
        Assert.That(plan.Steps.Select(s => s.Class), Is.All.EqualTo(RepairClass.Regenerate),
            "restoring a pair slot reads only the half that verifies, so nothing can be lost by it");
        Assert.That(plan.RequiresLossyConsent, Is.False, "a lossless plan must not ask for consent it does not need");
        Assert.That(plan.Loss.Entries, Is.Empty, "a lossless plan must enumerate no loss");
    }

    [Test]
    [CancelAfter(30_000)]
    public void DryRun_WritesNotOneByte()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        var damage = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);

        var plan = DatabaseRepair.Plan(DamageKit.Scan(BundlePath, ScanDepth.Deep));
        var before = DamageKit.HashDataFile(BundlePath);

        var outcome = DatabaseRepair.Apply(BundlePath, plan, allowLoss: false, backupFirst: false, dryRun: true);

        Assert.That(outcome.Succeeded, Is.True);
        Assert.That(DamageKit.HashDataFile(BundlePath), Is.EqualTo(before),
            $"a dry run of {damage.Mode} must leave the data file byte-identical; without this assertion "
            + "\"dry run\" is a label on a button");
    }

    [Test]
    [CancelAfter(30_000)]
    public void Apply_RestoresTheRedundancy_NotJustTheVerdict()
    {
        // The test this file exists for. Repairing the pair must leave BOTH halves valid — that is the property CK-05
        // buys, and it is not the same claim as "the scan is clean now": a database running on a single good slot also
        // scans clean until the day that slot tears.
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        var damage = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);

        var plan = DatabaseRepair.Plan(DamageKit.Scan(BundlePath, ScanDepth.Deep));
        var outcome = DatabaseRepair.Apply(BundlePath, plan, allowLoss: false, backupFirst: false, dryRun: false);

        DamageKit.AssertHealed(outcome, damage);

        // The independent proof: knock out the OTHER half. A genuinely rebuilt pair survives this with the same single
        // finding it started with. A pair that was never rebuilt has no second copy left, and the database is gone.
        var second = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);
        var after = DamageKit.Scan(BundlePath);

        Assert.That(after.Verdict, Is.EqualTo(IntegrityVerdict.Divergent),
            "after a real repair the database must survive losing the other half of the pair — if the redundancy was "
            + "not actually restored this is Unopenable.\n" + IntegrityReportText.Render(after));
        Assert.That(after.Findings.Select(f => f.Code).Distinct(), Is.EqualTo(second.ExpectedFindingCodes));
    }

    [Test]
    [CancelAfter(30_000)]
    public void Apply_RefusesAPlanBuiltForADifferentState()
    {
        // The fingerprint gate, exercised against real drift rather than a synthetic string.
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);

        var plan = DatabaseRepair.Plan(DamageKit.Scan(BundlePath, ScanDepth.Deep));

        // Move the database underneath the reviewed plan.
        var pageCount = DamageKit.Scan(BundlePath, ScanDepth.Spine).Identity.PageCount;
        DamageKit.FlipByteInPage(BundlePath, pageCount - 1, IntegrityVerdict.Divergent);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DatabaseRepair.Apply(BundlePath, plan, allowLoss: false, backupFirst: false, dryRun: false));

        Assert.That(ex.Message, Does.Contain("changed").IgnoreCase,
            "the refusal must say the database moved, not merely fail");
    }

    [Test]
    [CancelAfter(30_000)]
    public void BothSlotsGone_IsNotRepairable()
    {
        // The honest negative. There is no second copy to read from, so a repair tool must decline rather than
        // manufacture one — this is the boundary between regeneration and invention.
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        var damage = DamageKit.ClobberBothMetaSlots(BundlePath);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var plan = DatabaseRepair.Plan(report);

        Assert.That(plan.Steps.Any(s => s.Action == RepairAction.RestorePairSlot), Is.False,
            "with no valid slot to copy from, restoring the pair is not an available step");
        Assert.That(plan.Unaddressed, Is.Not.Empty,
            "what the tool cannot fix must be stated, not silently omitted from the plan");
    }
}
