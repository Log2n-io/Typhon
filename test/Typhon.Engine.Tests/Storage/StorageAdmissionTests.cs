using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Phase 2 of the page-cache starvation policy (#1382), S4/S5: while the page cache cannot keep up, a new unit of work waits at its creation — holding
/// nothing — until fewer than the gate's limit run; the limit follows the health state; past its deadline a unit is admitted all the same.
/// </summary>
[TestFixture]
[NonParallelizable] // the health tunables are process-wide
class StorageAdmissionTests : TestBase<StorageAdmissionTests>
{
    private DatabaseEngine OpenEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>A unit created on a thread of its own, which ends: the test thread holds no admission, so the gate applies to it (PS-23).</summary>
    private static UnitOfWork CreateElsewhere(DatabaseEngine dbe, TimeSpan timeout = default)
    {
        UnitOfWork uow = null;
        var t = new Thread(() => uow = dbe.CreateUnitOfWork(timeout: timeout));
        t.Start();
        t.Join();
        return uow;
    }

    private static TimeSpan HoldStarved(PagedMMF mmf)
    {
        // Starved, and held there (its limit only shrinks while what runs is within it): the waiters' own evaluations see no pressure, and would step
        // the state down after the calm period.
        var calm = PagedMMF.CalmBeforeStepDown;
        PagedMMF.CalmBeforeStepDown = TimeSpan.FromMinutes(5);
        mmf.SetStorageHealthForTests(StorageHealthState.Starved);
        return calm;
    }

    private static void Release(PagedMMF mmf, TimeSpan calm)
    {
        mmf.SetAdmissionLimitForTests(0);
        mmf.SetStorageHealthForTests(StorageHealthState.Healthy);
        PagedMMF.CalmBeforeStepDown = calm;
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("PS-23")]
    public void AUnitOfWork_WaitsAtTheGate_UntilOneEnds()
    {
        using var dbe = OpenEngine();
        var mmf = dbe.MMF;
        var calm = HoldStarved(mmf);
        try
        {
            var first = CreateElsewhere(dbe);
            mmf.SetAdmissionLimitForTests((int)mmf.AdmittedUnits);   // full: the next one waits
            var waits = mmf.AdmissionWaits;
            UnitOfWork second = null;
            var creator = new Thread(() => second = dbe.CreateUnitOfWork(timeout: TimeSpan.FromSeconds(10)));
            creator.Start();

            var sw = Stopwatch.StartNew();
            while (mmf.AdmissionWaitersForTests == 0 && sw.ElapsedMilliseconds < 5_000)
            {
                Thread.Yield();
            }

            Assert.That(mmf.AdmissionWaitersForTests, Is.EqualTo(1), "premise: the second unit waits at the gate");
            Assert.That(creator.Join(50), Is.False, "it waits while the first runs");

            first.Dispose();
            Assert.That(creator.Join(5_000), Is.True, "the first's end let it in");
            Assert.That(second, Is.Not.Null);
            Assert.That(mmf.AdmissionWaits - waits, Is.EqualTo(1));
            Assert.That(mmf.AdmissionDeadlineAdmits, Is.Zero, "admitted by the release, not by its deadline");
            second.Dispose();
        }
        finally
        {
            Release(mmf, calm);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("PS-23")]
    public void AThreadInsideAnEpochScope_IsNeverHeldAtTheGate()
    {
        using var dbe = OpenEngine();
        var mmf = dbe.MMF;
        var calm = HoldStarved(mmf);
        try
        {
            mmf.SetAdmissionLimitForTests(Math.Max(1, (int)mmf.AdmittedUnits));
            using var anchor = CreateElsewhere(dbe, TimeSpan.FromMilliseconds(1));   // past the limit: admitted at its deadline
            var sw = Stopwatch.StartNew();
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                using var uow = dbe.CreateUnitOfWork(timeout: TimeSpan.FromSeconds(10));
            }

            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1_000), "a thread holding a pin is never made to wait with it");
        }
        finally
        {
            Release(mmf, calm);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("PS-23")]
    public void AThreadHoldingAUnit_IsNeverHeldForItsNext()
    {
        using var dbe = OpenEngine();
        var mmf = dbe.MMF;
        var calm = HoldStarved(mmf);
        try
        {
            using var first = dbe.CreateUnitOfWork();
            mmf.SetAdmissionLimitForTests((int)mmf.AdmittedUnits);   // full, with this thread's own unit counted
            var admits = mmf.AdmissionDeadlineAdmits;
            var sw = Stopwatch.StartNew();
            using (dbe.CreateUnitOfWork(timeout: TimeSpan.FromSeconds(10)))
            {
            }

            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1_000), "it would wait for itself, holding its first unit's slot and marks");
            Assert.That(mmf.AdmissionDeadlineAdmits, Is.EqualTo(admits));
        }
        finally
        {
            Release(mmf, calm);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("PS-23")]
    public void PastItsDeadline_AUnitOfWorkIsAdmittedAllTheSame()
    {
        using var dbe = OpenEngine();
        var mmf = dbe.MMF;
        var calm = HoldStarved(mmf);
        try
        {
            using var first = CreateElsewhere(dbe);
            mmf.SetAdmissionLimitForTests((int)mmf.AdmittedUnits);
            var admits = mmf.AdmissionDeadlineAdmits;
            var sw = Stopwatch.StartNew();
            using (var late = dbe.CreateUnitOfWork(timeout: TimeSpan.FromMilliseconds(100)))
            {
                Assert.That(late, Is.Not.Null, "slowed, not refused");
            }

            Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(90), "it waited for its deadline");
            Assert.That(mmf.AdmissionDeadlineAdmits - admits, Is.EqualTo(1));
        }
        finally
        {
            Release(mmf, calm);
        }
    }

    [Test]
    [VerifiesRule("PS-23")]
    public void TheLimit_FollowsTheHealthState()
    {
        using var dbe = OpenEngine();
        var mmf = dbe.MMF;
        var units = new UnitOfWork[8];
        var before = mmf.AdmittedUnits;
        try
        {
            for (var i = 0; i < units.Length; i++)
            {
                units[i] = dbe.CreateUnitOfWork();
            }

            var running = (int)mmf.AdmittedUnits;
            mmf.AdjustAdmissionForTests(StorageHealthState.Pressured);
            Assert.That(mmf.AdmissionLimit, Is.Zero, "pressure alone does not turn the gate on");

            mmf.AdjustAdmissionForTests(StorageHealthState.Overloaded);
            Assert.That(mmf.AdmissionLimit, Is.EqualTo(Math.Max(PagedMMF.OverloadedAdmissionFloor, running - running / 4)),
                "Overloaded: three quarters of what runs, never below the floor");
            var overloaded = mmf.AdmissionLimit;
            mmf.AdjustAdmissionForTests(StorageHealthState.Overloaded);
            Assert.That(mmf.AdmissionLimit, Is.EqualTo(overloaded), "and holds it");

            // What runs is within the limit (8 units and the floor): Starved halves it, down to one.
            Assume.That(running, Is.LessThanOrEqualTo(overloaded), "premise: the units created fit the Overloaded limit");
            mmf.AdjustAdmissionForTests(StorageHealthState.Starved);
            Assert.That(mmf.AdmissionLimit, Is.EqualTo(Math.Max(PagedMMF.MinAdmissionLimit, overloaded / 2)), "Starved halves it");
            var halved = mmf.AdmissionLimit;
            if (running > halved)
            {
                mmf.AdjustAdmissionForTests(StorageHealthState.Starved);
                Assert.That(mmf.AdmissionLimit, Is.EqualTo(halved), "not again while what was admitted before still runs above it");
            }

            for (var i = 0; i < units.Length; i++)
            {
                units[i].Dispose();
                units[i] = null;
            }

            for (var i = 0; i < 10; i++)
            {
                mmf.AdjustAdmissionForTests(StorageHealthState.Starved);
            }

            Assert.That(mmf.AdmissionLimit, Is.EqualTo(PagedMMF.MinAdmissionLimit), "down to one once drained, never zero");

            mmf.AdjustAdmissionForTests(StorageHealthState.Overloaded);
            Assert.That(mmf.AdmissionLimit, Is.EqualTo(PagedMMF.OverloadedAdmissionFloor), "Overloaded after Starved: back to the floor, not held at one");

            mmf.AdjustAdmissionForTests(StorageHealthState.Pressured);
            Assert.That(mmf.AdmissionLimit, Is.GreaterThan(PagedMMF.OverloadedAdmissionFloor), "Pressured raises it");

            mmf.AdjustAdmissionForTests(StorageHealthState.Healthy);
            Assert.That(mmf.AdmissionLimit, Is.Zero, "Healthy turns it off");
        }
        finally
        {
            foreach (var uow in units)
            {
                uow?.Dispose();
            }

            mmf.SetAdmissionLimitForTests(0);
        }

        Assert.That(mmf.AdmittedUnits, Is.EqualTo(before), "every admitted unit released at its dispose");
    }
}
