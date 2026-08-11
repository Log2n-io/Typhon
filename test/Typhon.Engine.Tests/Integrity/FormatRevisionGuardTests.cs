using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using Typhon.Engine;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// IR-01 — a database recorded at another on-disk format revision is diagnosed but never written to.
/// </summary>
/// <remarks>
/// <para>
/// The split is by <b>verb</b>, and both halves are proved against one fixture because they are one decision seen from two
/// sides. <see cref="IntegrityScanner"/> reads a foreign revision and names it — an operator reaching for a scanner has
/// already lost the happy path, so refusing to diagnose would defeat the tool's premise. <see cref="DatabaseRepair"/>
/// refuses to write, with no override. Diagnosis degrades; mutation does not.
/// </para>
/// <para>
/// <b>Equality, not "at least".</b> The instinct that an older revision must be safe to write is the dangerous one, and
/// revision 7 is the standing counter-example: it claimed <c>[54,56)</c> for the chunk stride, bytes a revision-6 writer
/// left as zero — and zero is this build's sentinel for <i>"this segment holds no chunks"</i>. An older page therefore does
/// not fail to decode. It decodes to a confident wrong answer, and everything downstream agrees with it.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class FormatRevisionGuardTests : IntegrityFixtureBase
{
    /// <summary>A revision this build certainly does not speak, and deliberately an OLDER one.</summary>
    private const int ForeignRevision = 6;

    /// <summary>
    /// Distinctive marker from this fixture's own refusal assertion, so <see cref="RuleMutants.AssertDetects"/> can tell
    /// "the verifier rejected the violation" from "something else went wrong first".
    /// </summary>
    private const string RefusalMarker = "IR-01: Apply must refuse a foreign format revision";

    [Test]
    [CancelAfter(30_000)]
    public void AScanStillDiagnosesADatabaseOfAnotherRevision()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var forgery = DamageKit.ForgeFormatRevision(BundlePath, ForeignRevision);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, forgery);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        // Exactly one finding, and it is the revision. An extra code here would mean the forgery damaged something as
        // well as re-labelling it, which would make every conclusion below about the wrong thing.
        DamageKit.AssertDetectedExactly(report, forgery);

        Assert.That(report.Identity.FormatRevision, Is.EqualTo(ForeignRevision),
            "the scan must report the revision it found, not the one it wishes for");

        var finding = report.Findings.Single();
        Assert.That(finding.Severity, Is.EqualTo(IntegritySeverity.Advisory),
            "a foreign revision is not damage — the database is intact, this build simply cannot fully interpret it");
        Assert.That(finding.Summary + finding.Detail,
            Does.Contain($"revision {ForeignRevision}").And.Contain($"revision {DatabaseRepair.SupportedFormatRevision}"),
            "the finding must name BOTH revisions, or the operator cannot tell which build to reach for");

        // The coverage shortfall belongs in Limits, which every report prints — including a green one.
        Assert.That(string.Join("\n", report.Limits.Caveats), Does.Contain(ForeignRevision.ToString()),
            "a scan that could not fully interpret the file must say so in its stated limits");
    }

    /// <summary>
    /// The mutating half: a real plan, with real steps, refused because of the revision — and not one byte written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan is built <i>before</i> the revision is forged, which is the only way to get a plan with steps in it: a
    /// blocked plan has none by construction. That ordering also makes the test discriminating rather than merely green.
    /// Forging the revision re-stamps both meta slots, so it changes the fingerprint — meaning a build with no revision
    /// gate would still refuse, via the staleness check, and a weaker assertion would pass. Requiring the message to name
    /// the two revisions is what separates "refused for the right reason" from "refused".
    /// </para>
    /// <para>
    /// Re-stamping also heals the clobbered slot, so what <c>Apply</c> is handed is a perfectly healthy database at a
    /// foreign revision. That is the stronger claim, not a weaker one: the gate is the revision, not the damage.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("IR-01")]
    public void ApplyRefusesAForeignRevisionAndWritesNothing()
    {
        var plan = BuildARealPlanWithSteps();

        DamageKit.ForgeFormatRevision(BundlePath, ForeignRevision);

        AssertApplyRefusesAndWritesNothing(plan);
    }

    [Test]
    [CancelAfter(30_000)]
    public void PlanEmitsNoStepsAndSaysWhy()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        DamageKit.ForgeFormatRevision(BundlePath, ForeignRevision);

        var plan = DatabaseRepair.Plan(DamageKit.Scan(BundlePath, ScanDepth.Deep));

        Assert.That(plan.IsBlocked, Is.True, "a plan against a foreign revision must declare itself unapplicable");
        Assert.That(plan.Steps, Is.Empty, "a plan must not offer steps that Apply is guaranteed to refuse");
        Assert.That(plan.BlockedReason, Does.Contain($"revision {ForeignRevision}"));

        // Blocked and empty are different states, and the difference is what the operator reads. "Nothing to repair" over
        // a database the tool merely declined to touch is the most misleading sentence this feature could print.
        Assert.That(plan.IsEmpty, Is.True);
        Assert.That(plan.Unaddressed, Is.Not.Empty,
            "the reason must survive into the machine-readable remainder, not only into the rendered text");
    }

    [Test]
    [CancelAfter(30_000)]
    public void ANewerRevisionIsRefusedTheSameWayAsAnOlderOne()
    {
        // The asymmetry people expect — "newer is unreadable, older is fine" — is exactly the one that does not hold, so
        // both directions are exercised rather than one being assumed to follow from the other.
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        DamageKit.ForgeFormatRevision(BundlePath, DatabaseRepair.SupportedFormatRevision + 1);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound), IntegrityReportText.Render(report));
        Assert.That(DatabaseRepair.Plan(report).IsBlocked, Is.True);
    }

    [Test]
    public void AMatchingRevisionIsNotRefused()
    {
        // The guard's off-state, asserted rather than assumed. Without this, a DescribeRevisionRefusal that returned a
        // string unconditionally would make every other test in this fixture pass.
        Assert.That(DatabaseRepair.DescribeRevisionRefusal(DatabaseRepair.SupportedFormatRevision), Is.Null);
        Assert.That(DatabaseRepair.DescribeRevisionRefusal(DatabaseRepair.SupportedFormatRevision - 1), Is.Not.Null);
        Assert.That(DatabaseRepair.DescribeRevisionRefusal(DatabaseRepair.SupportedFormatRevision + 1), Is.Not.Null);
    }

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: proves the refusal assertion can actually fail.
    /// </summary>
    /// <remarks>
    /// The scenario is the verifier's own, minus the forgery — a database at the revision this build speaks. That is the
    /// closest reachable stand-in for "the gate is not there", and it is a genuine IR-01 violation in the direction that
    /// matters: <c>Apply</c> proceeds to mutate. Running the same assertion over it must reject it, and reject it on the
    /// fixture's own message rather than on some incidental failure further down.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [RuleMutant("IR-01")]
    public void Mutant_ApplyWithoutTheRevisionGate()
    {
        var plan = BuildARealPlanWithSteps();

        RuleMutants.AssertDetects("IR-01", RefusalMarker, () => AssertApplyRefusesAndWritesNothing(plan));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Damages the stale meta slot so a plan with at least one real, lossless step exists to be refused.</summary>
    private RepairPlan BuildARealPlanWithSteps()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);

        var damage = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);
        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var plan = DatabaseRepair.Plan(report);

        // Preconditions, not assertions about the feature: without a step there is nothing for the gate to refuse, and
        // the test would pass against a build that refused everything.
        Assert.That(plan.IsBlocked, Is.False, "precondition: the plan must be applicable before the revision is forged");
        Assert.That(plan.Steps, Is.Not.Empty, "precondition: the damage must yield at least one repair step");

        return plan;
    }

    /// <summary>
    /// The verifier: <c>Apply</c> throws, names both revisions, and the bundle is byte-identical afterwards.
    /// </summary>
    private void AssertApplyRefusesAndWritesNothing(RepairPlan plan)
    {
        var before = DamageKit.HashDataFile(BundlePath);
        var siblingsBefore = SiblingDirectories();

        var ex = Assert.Throws<InvalidOperationException>(
            () => DatabaseRepair.Apply(BundlePath, plan, allowLoss: false, backupFirst: true),
            RefusalMarker + " — it applied the plan instead.");

        Assert.That(ex.Message,
            Does.Contain($"revision {ForeignRevision}").And.Contain($"revision {DatabaseRepair.SupportedFormatRevision}"),
            RefusalMarker + " for the REVISION, naming both. Forging the revision also moves the fingerprint, so a "
            + "refusal that merely mentions staleness would be the drift check firing — the same green result from a "
            + "build with no gate at all.\nActual: " + ex.Message);

        Assert.That(DamageKit.HashDataFile(BundlePath), Is.EqualTo(before),
            RefusalMarker + " before writing anything. The data file changed.");

        // backupFirst was left ON deliberately: the pre-repair copy is the FIRST thing Apply does once it commits to a
        // repair, so its absence is direct evidence the gate ran ahead of every mutation rather than merely ahead of the
        // steps.
        Assert.That(SiblingDirectories(), Is.EquivalentTo(siblingsBefore),
            RefusalMarker + " before taking a pre-repair copy. A copy directory appeared.");
    }

    private string[] SiblingDirectories()
        => Directory.GetDirectories(Path.GetDirectoryName(BundlePath.TrimEnd(Path.DirectorySeparatorChar)));
}
