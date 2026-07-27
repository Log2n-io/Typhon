using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Zone-map recompute is narrowed to the component slots actually written this tick (#568 follow-up, reusing #559's FenceWrittenSlots).
//
// A field nobody wrote cannot have changed its min/max, so rescanning it reproduces the same two values. The risk is over-skipping: if the narrowing is wrong
// the zone map goes STALE, which does not throw — it silently prunes clusters out of an indexed range query, so rows quietly disappear. These tests therefore
// assert the observable consequence (the query result), not the zone map's internals.
//
// Reuses ClIdxUnit from ClusterIndexTests: ClIdxHealth.Current is [Index]-ed, ClIdxHealth.Max is not, and ClPosition is a separate unindexed component — so the
// fixture can write "an indexed component" and "only an unindexed component" as distinct actions.
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
class ClusterZoneMapNarrowingTests : TestBase<ClusterZoneMapNarrowingTests>
{
    private const int EntityCount = 200;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.RegisterComponentFromAccessor<ClIdxHealth>();
        dbe.RegisterComponentFromAccessor<ClVHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// The over-skip guard. Writing the INDEXED component must still recompute its zone map — if the narrowing wrongly skips it, the map keeps the old range
    /// and a range query over the NEW values prunes the clusters holding them.
    /// </summary>
    [Test]
    public void WritingTheIndexedComponent_StillRefreshesTheZoneMap()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe, baseHealth: 10);       // every Current in [10, 10+EntityCount)
        dbe.WriteTickFence(1);

        // Move every value far outside the original range. Zone maps must follow, or the query below finds nothing.
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in ids)
            {
                tx.OpenMut(id).Write(ClIdxUnit.Health).Current += 100_000;
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        Assert.That(CountWithHealthAtLeast(dbe, 100_000), Is.EqualTo(EntityCount),
            "every entity was moved above 100 000 — a stale zone map would prune their clusters and return fewer");
        Assert.That(CountWithHealthAtLeast(dbe, 200_000), Is.Zero, "nothing was moved that high");
    }

    /// <summary>
    /// The narrowing's premise. A tick that writes only an UNINDEXED component leaves the indexed field's values untouched, so the indexed query must return
    /// exactly what it returned before — whether or not the zone map was rescanned.
    /// </summary>
    [Test]
    public void WritingOnlyAnUnindexedComponent_LeavesIndexedQueriesCorrect()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe, baseHealth: 10);
        dbe.WriteTickFence(1);

        var before = CountWithHealthAtLeast(dbe, 100);

        for (var tick = 2; tick <= 5; tick++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                foreach (var id in ids)
                {
                    tx.OpenMut(id).Write(ClIdxUnit.Position).X += 1f;
                }

                tx.Commit();
            }

            dbe.WriteTickFence(tick);
        }

        Assert.That(CountWithHealthAtLeast(dbe, 100), Is.EqualTo(before),
            "no indexed value changed across four ticks — the result set must be identical");
        Assert.That(before, Is.GreaterThan(0), "the fixture must produce a non-empty result, or this asserts nothing");
    }

    /// <summary>
    /// Interleaving: an unindexed-only tick must not leave the map in a state that breaks the NEXT indexed write. Catches a narrowing that skips and also
    /// clobbers the written-slot bookkeeping the following tick depends on.
    /// </summary>
    [Test]
    public void UnindexedTick_ThenIndexedWrite_StillRefreshesTheZoneMap()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe, baseHealth: 10);
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in ids)
            {
                tx.OpenMut(id).Write(ClIdxUnit.Position).X += 1f;
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);      // narrowed tick — zone maps skipped

        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in ids)
            {
                tx.OpenMut(id).Write(ClIdxUnit.Health).Current += 50_000;
            }

            tx.Commit();
        }

        dbe.WriteTickFence(3);      // must rescan

        Assert.That(CountWithHealthAtLeast(dbe, 50_000), Is.EqualTo(EntityCount),
            "the indexed write after a narrowed tick must still refresh every zone map");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static EntityId[] Spawn(DatabaseEngine dbe, int baseHealth)
    {
        var ids = new EntityId[EntityCount];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var pos = new ClPosition { X = i, Y = i };
            var hp = new ClIdxHealth(baseHealth + i, 1000);
            ids[i] = tx.Spawn<ClIdxUnit>(ClIdxUnit.Position.Set(in pos), ClIdxUnit.Health.Set(in hp));
        }

        tx.Commit();
        return ids;
    }

    /// <summary>Indexed range query — the zone map prunes clusters underneath it, so a stale map shows up as a short count, never as an error.</summary>
    private static int CountWithHealthAtLeast(DatabaseEngine dbe, int threshold)
    {
        using var tx = dbe.CreateQuickTransaction();
        var n = tx.Query<ClIdxUnit>().WhereField<ClIdxHealth>(h => h.Current >= threshold).Count();
        tx.Commit();
        return n;
    }
}
