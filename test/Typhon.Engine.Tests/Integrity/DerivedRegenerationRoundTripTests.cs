using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// <c>G3</c> — damage a derived structure, plan, apply, and require the property back rather than the verdict.
/// </summary>
/// <remarks>
/// <para>
/// The whole class of repair this exercises rests on one claim: indexes, entity maps, revision-chain references and
/// cluster copies are <b>pure functions of primary data</b>, so regenerating them can never lose anything. That claim
/// is either true — in which case the repair is free — or it is false, in which case the tool quietly destroys data
/// while reporting success. So every test here asserts <i>zero loss</i> explicitly, not merely that the scan came back
/// clean.
/// </para>
/// <para>
/// Regeneration happens by <b>opening the database</b>, which is why the callback is injected: the repair module must
/// not depend on engine construction. Until this test existed nothing supplied that callback, so a plan containing the
/// step threw when applied — a worse failure than the step being absent, because the plan is shown to an operator as a
/// list of what will happen and they consent to it.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class DerivedRegenerationRoundTripTests : IntegrityFixtureBase
{
    /// <summary>Opens and cleanly closes the bundle, exactly as the CLI's callback does.</summary>
    /// <remarks>
    /// Deliberately registers <b>no component type</b>. If regeneration needed the schema assembly it would fail here,
    /// and it would fail on precisely the forensic case the feature exists for: a database recovered on a machine that
    /// never ran the application.
    /// </remarks>
    private void Regenerate(string bundlePath)
    {
        using var provider = ReopenProvider();
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.InitializeArchetypes();
            dbe.ForceCheckpoint();
        }
    }

    /// <summary>
    /// A dangling EntityMap directory pointer is planned as lossless and repaired to <c>Sound</c>.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Ignore("#755 — RETARGETED from #771, whose SEG-02 is fixed: this now fails on CHK-MAP-04 instead, i.e. the damage "
        + "is simply not repaired. Regenerate() opens without registering CompA, so the archetype is never materialized "
        + "and its EntityMap is never rebuilt. That is G3's real blocker and it is the same theme one layer down — "
        + "repair-by-opening cannot regenerate derived state for archetypes whose CLR types it does not have.")]
    public void ADamagedEntityMapIsRegeneratedWithoutLoss()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.RedirectEntityMapDirectorySlot(BundlePath);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var plan = DatabaseRepair.Plan(report);
        Assert.That(plan.Steps.Any(s => s.Action == RepairAction.RegenerateDerivedStructures), Is.True,
            "a lossless finding must route to the regeneration step:\n" + RenderSteps(plan));
        Assert.That(plan.RequiresLossyConsent, Is.False, "regenerating derived state asks for no consent it does not need");
        Assert.That(plan.Loss.Entries, Is.Empty, "a plan that only regenerates must enumerate no loss");

        var outcome = DatabaseRepair.Apply(BundlePath, plan, allowLoss: false, backupFirst: false, dryRun: false,
            regenerateDerived: Regenerate);

        DamageKit.AssertHealed(outcome, damage);
    }

    /// <summary>
    /// The entities are still all there afterwards — the assertion the verdict cannot make.
    /// </summary>
    /// <remarks>
    /// A repair that dropped the map and rebuilt it from nothing would also scan <c>Sound</c>: an empty map is
    /// internally consistent. Counting the entities through the rebuilt map is what separates "regenerated" from
    /// "discarded", and it is the same distinction <c>MAP-02</c> exists to make.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Ignore("#755 — RETARGETED from #771 (SEG-02 fixed). Same cause as the test above: the regeneration never happens "
        + "for an archetype the repair process did not register, so the map is still damaged when this counts through it.")]
    public void EveryEntitySurvivesTheRegeneration()
    {
        const int Entities = 64;
        BuildHealthyDatabase(Entities);
        DamageKit.Baseline(BundlePath);
        DamageKit.RedirectEntityMapDirectorySlot(BundlePath);

        var plan = DatabaseRepair.Plan(DamageKit.Scan(BundlePath, ScanDepth.Deep));
        var outcome = DatabaseRepair.Apply(BundlePath, plan, allowLoss: false, backupFirst: false, dryRun: false,
            regenerateDerived: Regenerate);

        Assert.That(outcome.Succeeded, Is.True);
        Assert.That(outcome.Results.All(r => r.ActualLoss.Kind == LossKind.None), Is.True,
            "regenerating derived structures must report no loss: "
            + string.Join(" · ", outcome.Results.Select(r => $"{r.Step.Action}={r.ActualLoss.Kind}")));

        // MAP-02's silence over a known population is the count: it fires on any live entity the map cannot reach.
        var after = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(after.Findings.Where(f => f.Code is "CHK-MAP-01" or "CHK-MAP-02"), Is.Empty,
            $"all {Entities} entities must still be reachable through the rebuilt map:\n" + IntegrityReportText.Render(after));
        Assert.That(after.Verdict, Is.EqualTo(IntegrityVerdict.Sound), IntegrityReportText.Render(after));
    }

    /// <summary>
    /// A cluster copy that diverged from its chain is repaired from the chain, which is the authority.
    /// </summary>
    /// <remarks>
    /// This is the <c>D11</c> shape and the one <c>05 §3</c> calls the cheapest genuine repair capability: the chain
    /// holds the value, the cluster is a read-path copy, so the repair direction is unambiguous and free.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Ignore("#755 — RETARGETED from #771 (SEG-02 fixed). CLU-03 is still not healed by an open-and-close: G3's design "
        + "says wire RebuildClusterFromChains to a PER-PAGE damage trigger, and a plain open arms none. That was always "
        + "the second of this test's two reasons; it is now the only one.")]
    public void ADivergedClusterCopyIsRebuiltFromItsChain()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.DivergeClusterCopyFromChain(BundlePath, out _);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        DamageKit.AssertDetectedExactly(report, damage);

        var plan = DatabaseRepair.Plan(report);
        var outcome = DatabaseRepair.Apply(BundlePath, plan, allowLoss: false, backupFirst: false, dryRun: false,
            regenerateDerived: Regenerate);

        DamageKit.AssertHealed(outcome, damage);
    }

    /// <summary>
    /// Applying a plan with a regeneration step and no callback refuses, rather than silently skipping it.
    /// </summary>
    /// <remarks>
    /// This is the defect the CLI had until the callback was wired: <c>Apply</c> threw because nothing supplied one.
    /// Throwing is the correct behaviour — the alternative is a repair that reports success having skipped the only
    /// step that would have fixed anything — so it is pinned rather than removed.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void WithoutACallbackTheRegenerationStepRefusesRatherThanSkipping()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        DamageKit.RedirectEntityMapDirectorySlot(BundlePath);

        var plan = DatabaseRepair.Plan(DamageKit.Scan(BundlePath, ScanDepth.Deep));
        var outcome = DatabaseRepair.Apply(BundlePath, plan, allowLoss: false, backupFirst: false, dryRun: false);

        Assert.That(outcome.Succeeded, Is.False,
            "a step that could not run must not be reported as a successful repair");
        Assert.That(string.Join(" ", outcome.Results.Select(r => r.Detail ?? "")), Does.Contain("callback").IgnoreCase);
    }

    private static string RenderSteps(RepairPlan plan)
        => plan.Steps.Count == 0
            ? "(no steps)"
            : string.Join("\n", plan.Steps.Select(s => $"  {s.Order}. {s.Action} — {s.Description}"));
}
