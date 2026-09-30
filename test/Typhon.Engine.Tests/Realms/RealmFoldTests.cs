using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// The per-realm counter fold, on its own — the primitive every producer of the per-tick maintenance rates goes through (SO-03).
/// </summary>
/// <remarks>
/// <para><b>These exist because the fold shipped untested and a real defect went with it.</b> The end-to-end fixture drives the fold through the tick fence,
/// where every realm lookup happens to succeed, so it could not reach the one branch that was wrong: a run closed with NO realm to publish to. That branch
/// is reachable in production — <c>ArchetypeClusterState.RealmSpatialForFold</c> answers null for an archetype with no realm table, and a realm id past the
/// table's length answers null too — and it returned before banking the run, which stranded the counts in the fold and published them under the NEXT
/// realm's name. That is the same "the whole tick under one realm" failure the mutant test stages by hand, arriving by a path no test walked.</para>
/// <para>Testing the primitive directly is what makes the branch reachable at all, which is the general lesson: a shared fold used by six producers needs
/// its own tests, not only the tests of its six users.</para>
/// </remarks>
[TestFixture]
class RealmFoldTests
{
    private static SpatialGrid Grid() => new(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10));

    private static RealmArchetypeSpatial Realm(ushort id) => new(null, new RealmId(id), Grid());

    /// <summary>A run closed with no realm to publish to must still reach the slice totals, and must NOT reach the next realm.</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SO-03")]
    public void ARunWithNoRealmToPublishTo_IsBankedInTheSliceAndNotCarriedToTheNextRealm()
    {
        var realmA = Realm(1);
        var realmB = Realm(2);
        var fold = default(RealmFold);

        fold.Switch(realmA);
        fold.T.ClustersScanned += 5;

        // The lookup answers null — an archetype with no realm table, or an id past its length. Ten clusters are scanned under it.
        fold.Switch(null);
        fold.T.ClustersScanned += 10;

        fold.Switch(realmB);
        fold.T.ClustersScanned += 3;
        fold.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(realmA.Counters.F.ClustersScanned, Is.EqualTo(5), "realm A's own run");
            Assert.That(realmB.Counters.F.ClustersScanned, Is.EqualTo(3),
                "realm B must NOT inherit the 10 counted while there was no realm — that is the misattribution the fold exists to prevent");
            Assert.That(fold.Slice.ClustersScanned, Is.EqualTo(18),
                "the slice total feeds the archetype-wide counter, so dropping the unattributable 10 would silently shorten it");
        });
    }

    /// <summary>A realm the fold never counted anything into is not marked, so the emission gate leaves it out and its absence means "not measured".</summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SO-03")]
    public void ARealmWithNothingCounted_IsNotMarkedTouched()
    {
        var realm = Realm(1);
        var fold = default(RealmFold);

        fold.Switch(realm);
        fold.Flush();

        Assert.That(realm.Counters.F.Touched, Is.Zero, "an empty run must not stamp the realm, or the gate emits a record asserting a measured zero");
    }

    /// <summary>Anything counted marks the realm, and the flag is set with the counters rather than independently of them.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void ARealmWithSomethingCounted_IsMarkedTouched()
    {
        var realm = Realm(1);
        var fold = default(RealmFold);

        fold.Switch(realm);
        fold.T.DriftersDetected += 1;
        fold.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(realm.Counters.F.Touched, Is.EqualTo((byte)1));
            Assert.That(realm.Counters.F.DriftersDetected, Is.EqualTo(1));
        });
    }

    /// <summary>Flushing twice publishes once: the run is cleared as it is published, so a stray extra call cannot double a realm's figures.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void FlushingTwice_PublishesOnce()
    {
        var realm = Realm(1);
        var fold = default(RealmFold);

        fold.Switch(realm);
        fold.T.MigrationCount += 7;
        fold.Flush();
        fold.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(realm.Counters.F.MigrationCount, Is.EqualTo(7));
            Assert.That(fold.Slice.MigrationCount, Is.EqualTo(7));
        });
    }

    /// <summary>
    /// Switching to the realm already open is a no-op, so a per-item call in a producer loop costs a reference compare and publishes nothing.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void SwitchingToTheSameRealm_DoesNotPublish()
    {
        var realm = Realm(1);
        var fold = default(RealmFold);

        fold.Switch(realm);
        fold.T.CrossingsExecuted += 1;
        fold.Switch(realm);
        fold.T.CrossingsExecuted += 1;

        Assert.That(realm.Counters.F.CrossingsExecuted, Is.Zero, "nothing is published until the realm actually changes or the fold is flushed");

        fold.Flush();

        Assert.That(realm.Counters.F.CrossingsExecuted, Is.EqualTo(2));
    }

    /// <summary>
    /// <c>LargestArrivalRun</c> is a MAXIMUM across realms, not a sum — SO-01 requires the roll-up to fold by kind, and adding two realms' largest arrival
    /// runs would report a burst neither cell ever received.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SO-01")]
    public void LargestArrivalRun_FoldsWithAMaxRatherThanASum()
    {
        var realmA = Realm(1);
        var realmB = Realm(2);
        var fold = default(RealmFold);

        fold.Switch(realmA);
        fold.T.LargestArrivalRun = 9;
        fold.Switch(realmB);
        fold.T.LargestArrivalRun = 4;
        fold.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(realmA.Counters.F.LargestArrivalRun, Is.EqualTo(9));
            Assert.That(realmB.Counters.F.LargestArrivalRun, Is.EqualTo(4));
            Assert.That(fold.Slice.LargestArrivalRun, Is.EqualTo(9), "the slice's largest is the largest of its realms', not 13");
        });
    }

    /// <summary>A realm reopened later in the same slice accumulates onto what it already holds rather than replacing it.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void ARealmReopenedLater_AccumulatesRatherThanReplaces()
    {
        var realmA = Realm(1);
        var realmB = Realm(2);
        var fold = default(RealmFold);

        fold.Switch(realmA);
        fold.T.SlotsScanned += 10;
        fold.Switch(realmB);
        fold.T.SlotsScanned += 1;
        fold.Switch(realmA);
        fold.T.SlotsScanned += 20;
        fold.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(realmA.Counters.F.SlotsScanned, Is.EqualTo(30), "interleaved runs of one realm add up");
            Assert.That(realmB.Counters.F.SlotsScanned, Is.EqualTo(1));
            Assert.That(fold.Slice.SlotsScanned, Is.EqualTo(31));
        });
    }

    /// <summary>
    /// EVERY counter declared on the block reaches BOTH destinations of a flush — the realm's published block and the slice totals.
    /// </summary>
    /// <remarks>
    /// The counters are enumerated longhand in several places (the declaration, the flush's slice block, the flush's publish block, and the end-to-end
    /// test's summer). Adding a counter and forgetting one of them produces a realm total that is silently short, which is the failure SO-03 forbids and
    /// the one a reader cannot see. Reflection over the block is the cheap insurance: it cannot be forgotten, because it enumerates the declaration itself.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SO-03")]
    public void EveryDeclaredCounter_ReachesBothTheRealmBlockAndTheSliceTotals()
    {
        var realm = Realm(1);
        var fold = default(RealmFold);
        fold.Switch(realm);

        // Every member set to a distinct non-zero value, through the declaration rather than a hand-written list.
        var members = typeof(RealmTickCounters.Fields)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        object staged = default(RealmTickCounters.Fields);
        var expected = new List<string>();
        foreach (var m in members)
        {
            if (m.Name == nameof(RealmTickCounters.Fields.Touched))
            {
                continue;   // set by the flush itself, not carried through it
            }

            if (m.FieldType == typeof(int))
            {
                m.SetValue(staged, 7);
            }
            else if (m.FieldType == typeof(double))
            {
                m.SetValue(staged, 7d);
            }
            else
            {
                Assert.Fail($"{m.Name} is a {m.FieldType.Name}, which this test does not know how to stage — teach it, do not skip it");
            }

            expected.Add(m.Name);
        }

        fold.T = (RealmTickCounters.Fields)staged;
        fold.Flush();

        Assert.That(expected, Is.Not.Empty, "reflection found no counters, so this test proves nothing");
        object published = realm.Counters.F;
        object sliced = fold.Slice;
        Assert.Multiple(() =>
        {
            foreach (var name in expected)
            {
                var m = typeof(RealmTickCounters.Fields).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                Assert.That(System.Convert.ToDouble(m.GetValue(published)), Is.Not.Zero, $"{name} never reached the realm's block");
                Assert.That(System.Convert.ToDouble(m.GetValue(sliced)), Is.Not.Zero, $"{name} never reached the slice totals");
            }
        });
    }

    /// <summary>
    /// The counter block fits the reserve its layout declares.
    /// </summary>
    /// <remarks>
    /// <c>RealmTickCounters</c> is <c>[StructLayout(Explicit, Size = 320)]</c> with the block at offset 64, so the counters have 256 bytes. Overflowing that
    /// is not a build error — it is a <c>TypeLoadException</c> the first time anything touches a <c>RealmArchetypeSpatial</c>, which is every spatial query,
    /// so the whole engine fails at run time for a field someone added.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void TheCounterBlock_FitsInsideItsDeclaredReserve()
    {
        Assert.That(Unsafe.SizeOf<RealmTickCounters.Fields>(), Is.LessThanOrEqualTo(256),
            "the block outgrew the 256 bytes between its offset and the struct's declared size — raise Size, do not shrink the padding");
    }
}
