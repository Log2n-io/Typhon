using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// What an open-and-close cycle does to a database that was already healthy.
/// </summary>
/// <remarks>
/// <para>
/// <c>G3</c> proposes repairing derived structures by opening the database and letting the engine's rebuild net run.
/// Before that can be trusted as a <i>repair</i>, it has to be established that it is not itself a <i>change</i>: a
/// step that heals the damage but leaves two new findings behind has not repaired anything, it has traded one report
/// for another.
/// </para>
/// <para>
/// This fixture asks the question in isolation — no damage at all, just open and close — so anything it reports is
/// attributable to regeneration and nothing else.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class RegenerationSideEffectTests : IntegrityFixtureBase
{
    private void Regenerate(bool checkpoint = true)
    {
        using var provider = ReopenProvider();
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.InitializeArchetypes();
            if (checkpoint)
            {
                dbe.ForceCheckpoint();
            }
        }
    }

    /// <summary>Narrows the cycle's side effects to the checkpoint or to the open itself.</summary>
    /// <param name="checkpoint">Whether the cycle forces a checkpoint before closing.</param>
    [TestCase(true)]
    [TestCase(false)]
    [CancelAfter(60_000)]
    public void WhichHalfOfTheCycleIntroducesFindings(bool checkpoint)
    {
        BuildHealthyDatabase();
        Assert.That(DamageKit.Scan(BundlePath, ScanDepth.Deep).Verdict, Is.EqualTo(IntegrityVerdict.Sound));

        Regenerate(checkpoint);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(report.Findings.Select(f => f.Code).Distinct(), Is.Empty,
            $"checkpoint={checkpoint}: " + IntegrityReportText.Render(report));
    }

    /// <summary>
    /// Opening and closing a healthy database leaves it healthy.
    /// </summary>
    /// <remarks>
    /// The precondition for the whole G3 approach. If this fails, "repair by opening" cannot be sound whatever it
    /// happens to fix, because it would be adding findings to every database it touches.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void AnOpenAndCloseCycleLeavesAHealthyDatabaseSound()
    {
        BuildHealthyDatabase();

        var beforeReport = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(beforeReport.Verdict, Is.EqualTo(IntegrityVerdict.Sound), IntegrityReportText.Render(beforeReport));

        Regenerate();

        var afterReport = DamageKit.Scan(BundlePath, ScanDepth.Deep);

        Assert.That(afterReport.Verdict, Is.EqualTo(IntegrityVerdict.Sound),
            "an open-and-close cycle introduced findings into a database that was healthy before it, so 'repair by "
            + "opening' would add damage to every database it touched:\n" + IntegrityReportText.Render(afterReport));
    }

    /// <summary>
    /// Repeated cycles do not accumulate findings.
    /// </summary>
    /// <remarks>
    /// A single cycle can look clean while each one leaves a trace — a WAL segment, a page the bitmap forgets — that
    /// only crosses a threshold after several. Repair is not a once-per-database event, so this is the honest form of
    /// the question.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void RepeatedCyclesDoNotAccumulateFindings()
    {
        BuildHealthyDatabase();

        for (var i = 0; i < 3; i++)
        {
            Regenerate();

            var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
            Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound),
                $"cycle {i + 1} left the database no longer sound:\n" + IntegrityReportText.Render(report));
            Assert.That(report.Findings, Is.Empty, $"cycle {i + 1}:\n" + IntegrityReportText.Render(report));
        }
    }
}
