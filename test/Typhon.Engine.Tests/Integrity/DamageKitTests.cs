using NUnit.Framework;
using System.IO;
using System.Linq;
using Typhon.Engine;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// Proves the damage kit itself — that each primitive breaks exactly what it says and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist because every repair test in this area is only as trustworthy as the corruption it starts from. If
/// a primitive claimed to tear one sector but also cleared a header field, a repair afterwards could report success
/// while having fixed a different problem — and the test would pass. The failure would be invisible precisely because
/// the assertion at the end is green.
/// </para>
/// <para>
/// So the kit is treated as production code under test: for each primitive, assert the database was healthy first,
/// assert the file changed only inside the declared ranges, and assert the scan reports the declared codes and nothing
/// else. A primitive that cannot pass these has no business underwriting a repair claim.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class DamageKitTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void AHealthyDatabaseIsSound_AndTheBaselineSaysSo()
    {
        BuildHealthyDatabase();

        // Not a tautology: it is the precondition every other test in this file depends on, and it is the assertion
        // that fails first — and legibly — if the fixture stops producing a clean database.
        var snapshot = DamageKit.Baseline(BundlePath);

        Assert.That(snapshot.Bytes.Length, Is.GreaterThan(0));
        Assert.That(snapshot.Bytes.Length % IntegrityConstants.PageSize, Is.Zero,
            "a cleanly-closed database is a whole number of pages");
    }

    [Test]
    [CancelAfter(30_000)]
    public void ClobberStaleMetaSlot_ChangesOnlyTheBytesItDeclares()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);

        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);
    }

    [Test]
    [CancelAfter(30_000)]
    public void ClobberStaleMetaSlot_IsolatesThePairCheck()
    {
        // The surgical case: the current slot keeps its watermarks, so the ONLY thing wrong with this database is that
        // its redundancy is gone. Anything else in the report would mean the primitive is not isolating what it claims.
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);

        var damage = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);

        DamageKit.AssertDetectedExactly(DamageKit.Scan(BundlePath), damage);
    }

    [Test]
    [CancelAfter(30_000)]
    public void ClobberCurrentMetaSlot_CostsTheCleanShutdownWatermark()
    {
        // The two halves of the pair are not interchangeable, and this is the observable that proves it. Both choices
        // report CHK-BOO-03 — but destroying the half the database reads from leaves it on the *previous* metadata
        // write, one generation behind, whose clean-shutdown flag was never set. The finding set is identical; the
        // state of the database is not, and a fixture that could not tell them apart would be measuring less than it
        // appears to.
        BuildHealthyDatabase();
        var healthy = DamageKit.Scan(BundlePath);
        Assert.That(healthy.Identity.CleanShutdown, Is.True, "precondition: the fixture closes cleanly");

        var damage = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Current);
        var report = DamageKit.Scan(BundlePath);

        DamageKit.AssertDetectedExactly(report, damage);
        Assert.That(report.Identity.CleanShutdown, Is.False,
            "the surviving slot predates the final clean close, so its clean-shutdown flag is clear");
    }

    [Test]
    [CancelAfter(30_000)]
    public void ClobberStaleMetaSlot_KeepsTheCleanShutdownWatermark()
    {
        // The other side of the same coin: the current slot survives, so the database's watermarks are untouched and
        // the only thing wrong with it is the lost redundancy.
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);

        DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);
        var report = DamageKit.Scan(BundlePath);

        Assert.That(report.Identity.CleanShutdown, Is.True,
            "clobbering the stale half must not disturb the watermarks the current half carries");
    }

    [Test]
    [CancelAfter(30_000)]
    public void ClobberBothMetaSlots_IsUnopenable()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.ClobberBothMetaSlots(BundlePath);

        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);
        DamageKit.AssertDetectedExactly(DamageKit.Scan(BundlePath), damage);
    }

    [Test]
    [CancelAfter(30_000)]
    public void FlipByteInPage_ChangesExactlyOneByte()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);
        var lastPage = before.Bytes.Length / IntegrityConstants.PageSize - 1;

        var damage = DamageKit.FlipByteInPage(BundlePath, lastPage, IntegrityVerdict.Divergent);

        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);
        Assert.That(damage.Ranges.Single().Length, Is.EqualTo(1), "a one-byte flip must declare one byte");
    }

    [Test]
    [CancelAfter(30_000)]
    public void FlipByteInPage_IsDetected()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        var pageCount = (int)(new FileInfo(DamageKit.DataPath(BundlePath)).Length / IntegrityConstants.PageSize);

        // The last page of this fixture holds cluster rows, so a torn byte there is DataLoss — the caller states that,
        // because only the caller knows which page it picked.
        //
        // It was Divergence when the fixture's last page was a derived-segment page. The file used to run one page
        // longer than its data: leaked dirty marks kept clean pages permanently "dirty", every checkpoint rewrote them,
        // and the tail of the file was slack. Conserving the marks stopped the redundant writes, so the file now ends on
        // the last page that actually holds rows — and a torn byte there costs rows, which is what DataLoss means.
        var damage = DamageKit.FlipByteInPage(BundlePath, pageCount - 1, IntegrityVerdict.DataLoss);
        var report = DamageKit.Scan(BundlePath);

        DamageKit.AssertDetectedExactly(report, damage);
    }

    [Test]
    [CancelAfter(30_000)]
    public void Truncation_IsDetected()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);

        var damage = DamageKit.TruncateMidPage(BundlePath, keepBytesOfLastPage: 512);
        var report = DamageKit.Scan(BundlePath);

        DamageKit.AssertDetectedExactly(report, damage);
    }

    // ── The kit's own guard rails ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(30_000)]
    public void AssertOnlyDeclaredBytesChanged_FailsWhenAPrimitiveUnderDeclares()
    {
        // The assertion is only worth having if it actually fires. Damage two ranges, declare one, and require the
        // check to catch it — otherwise every "changed only what it declared" claim in this file is decoration.
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var honest = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);
        DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Current); // a mutation the record below omits

        var underDeclared = honest with { Description = "under-declared on purpose" };

        Assert.Throws<AssertionException>(() => DamageKit.AssertOnlyDeclaredBytesChanged(before, underDeclared),
            "the byte-range check must catch a primitive that writes outside its declaration");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AssertDetectedExactly_FailsOnAnExtraFinding()
    {
        // Same reasoning in the other direction: a scan reporting more than the fixture isolated must fail, or the
        // fixture is not isolating anything.
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);

        var damage = DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);
        var claimsNothing = damage with { ExpectedFindingCodes = [] };

        Assert.Throws<AssertionException>(() => DamageKit.AssertDetectedExactly(DamageKit.Scan(BundlePath), claimsNothing),
            "a report with findings must not satisfy a record that declares none");
    }

    [Test]
    [CancelAfter(30_000)]
    public void Baseline_FailsOnAnAlreadyDamagedDatabase()
    {
        // The precondition must be a real gate. If Baseline passed on a damaged database, every test here would be
        // attributing pre-existing findings to its own damage.
        BuildHealthyDatabase();
        DamageKit.ClobberMetaSlot(BundlePath, DamageKit.MetaSlot.Stale);

        Assert.Throws<AssertionException>(() => DamageKit.Baseline(BundlePath),
            "the healthy-baseline precondition must reject a database that is already damaged");
    }
}
