using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── SingleVersion only, deliberately ───────────────────────────────────────────────────────────────────────────────
// The archetype has NO Versioned component, which is the whole point: the ECS cleanup drain used to be gated on the
// revision-chain queue being non-empty, and an all-SingleVersion workload never supersedes a revision, so that queue is
// permanently empty and the gate never opened. A fixture with a Versioned component in it would open the gate for the
// wrong reason and report green against the exact configuration that leaked (#681).
[Component("Typhon.Test.Drain.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct DrainPos
{
    public float X, Y;

    public DrainPos(float x, float y)
    {
        X = x;
        Y = y;
    }
}

[Component("Typhon.Test.Drain.Vel", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct DrainVel
{
    public float Dx, Dy;

    public DrainVel(float dx, float dy)
    {
        Dx = dx;
        Dy = dy;
    }
}

[Archetype]
partial class DrainUnit : Archetype<DrainUnit>
{
    public static readonly Comp<DrainPos> Position = Register<DrainPos>();
    public static readonly Comp<DrainVel> Velocity = Register<DrainVel>();
}

/// <summary>
/// Regression tests for #681 — the ECS cleanup queue filled on every destroy and nothing in production drained it, so a
/// destroyed entity's <c>EntityMap</c> record was permanent.
/// </summary>
/// <remarks>
/// <para>
/// These assert the shape a leak has, not a point-in-time total: entities are spawned and destroyed in ROUNDS and the
/// map is measured after each one. A single spawn/destroy/assert cannot tell a reclaimed map from a map that happens to
/// be small yet, and the test this replaces made exactly that mistake — it called the drain itself, then asserted only
/// that the entities were invisible, which destroy alone already guarantees. It passed for the entire period the queue
/// was leaking.
/// </para>
/// <para>
/// Nothing here calls <c>ProcessEcsCleanups</c> or <c>FlushDeferredCleanups</c>. The drain under test is the one that
/// runs on ordinary transaction disposal; invoking it explicitly would measure the call rather than the engine.
/// </para>
/// </remarks>
[NonParallelizable]
class EcsCleanupDrainTests : TestBase<EcsCleanupDrainTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<DrainPos>();
        dbe.RegisterComponentFromAccessor<DrainVel>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static long MapEntries(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<DrainUnit>.Metadata.ArchetypeId].EntityMap.EntryCount;

    private static EntityId SpawnOne(DatabaseEngine dbe, float x)
    {
        using var tx = dbe.CreateQuickTransaction();
        var pos = new DrainPos(x, 0);
        var vel = new DrainVel(0, 0);
        var id = tx.Spawn<DrainUnit>(DrainUnit.Position.Set(in pos), DrainUnit.Velocity.Set(in vel));
        tx.Commit();
        return id;
    }

    private static void DestroyOne(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(id);
        tx.Commit();
    }

    /// <summary>
    /// The defect's actual signature: churn at a flat population must not grow the map.
    /// </summary>
    /// <remarks>
    /// This is the unit-scale version of what the SpaceBattle demo showed at 100 000 ticks — 561 796 EntityMap chunks
    /// against a live population that never exceeded ~13 900, holding 89 % of the data file. Rounds, not a single pass:
    /// a leak is a slope, and one round can only ever measure an intercept.
    /// </remarks>
    [Test]
    [VerifiesRule("REAP-01")]
    public void SingleVersionChurn_LeavesTheEntityMapFlat_AcrossRounds()
    {
        using var dbe = SetupEngine();

        const int PerRound = 25;
        const int Rounds = 6;

        long afterFirstRound = -1;

        for (var round = 0; round < Rounds; round++)
        {
            var ids = new EntityId[PerRound];
            for (var i = 0; i < PerRound; i++)
            {
                ids[i] = SpawnOne(dbe, i);
            }
            for (var i = 0; i < PerRound; i++)
            {
                DestroyOne(dbe, ids[i]);
            }

            // One extra transaction so the tail advances past the last destroy and its records become reclaimable.
            // Cleanup is cut off at ComputeNextMinTSN, so the destroying transaction itself can never be the one that
            // reclaims its own victims — something must retire behind it first.
            SpawnAndDestroyOne(dbe);

            var entries = MapEntries(dbe);
            if (round == 0)
            {
                afterFirstRound = entries;
                continue;
            }

            Assert.That(entries, Is.EqualTo(afterFirstRound),
                $"round {round}: the EntityMap holds {entries} records after {(round + 1) * PerRound} spawn/destroy "
                + $"pairs against a live population of 0, versus {afterFirstRound} after the first round. A count that "
                + "rises with cumulative destroys rather than live entities is #681 — every destroyed entity's record "
                + "is retained for the life of the engine");
        }
    }

    private static void SpawnAndDestroyOne(DatabaseEngine dbe)
    {
        var id = SpawnOne(dbe, -1);
        DestroyOne(dbe, id);
    }

    /// <summary>
    /// The queue itself must not accumulate — it retains an <c>ArchetypeMetadata</c> reference per entry, so an
    /// unbounded queue pins that object graph on the managed heap as well as leaking the map records.
    /// </summary>
    [Test]
    public void CleanupQueue_DrainsWithoutAnyExplicitCall()
    {
        using var dbe = SetupEngine();

        for (var i = 0; i < 40; i++)
        {
            DestroyOne(dbe, SpawnOne(dbe, i));
        }
        SpawnAndDestroyOne(dbe);

        Assert.That(dbe.EcsCleanupQueueSize, Is.Zero,
            "the queue must be drained by ordinary transaction disposal. Its only consumer used to be reachable from "
            + "tests alone, so it grew one entry per destroyed entity for the lifetime of the engine");
    }

    /// <summary>
    /// A READ-ONLY transaction holding the tail must drain too, even though it has no ChangeSet of its own.
    /// </summary>
    /// <remarks>
    /// This is the shape that actually holds the queue back in a real workload: a long reader opened before a burst of
    /// destroys keeps <c>ComputeNextMinTSN</c> behind them, so it is precisely that reader's retirement which makes the
    /// records reclaimable — and it is the one transaction with no mark owner to hand the drain. Gating on
    /// <c>_changeSet != null</c> would defer the whole queue to whichever writer came along next, which in a read-mostly
    /// steady state can be a long time.
    /// </remarks>
    [Test]
    public void ReadOnlyTailTransaction_StillDrainsTheQueue()
    {
        using var dbe = SetupEngine();

        var ids = new EntityId[20];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = SpawnOne(dbe, i);
        }

        // The reader opens FIRST, so every destroy below retires behind it and nothing can be reclaimed while it lives.
        var reader = dbe.TransactionChain.CreateTransaction(dbe, readOnly: true);

        foreach (var id in ids)
        {
            DestroyOne(dbe, id);
        }

        Assert.That(dbe.EcsCleanupQueueSize, Is.EqualTo(ids.Length),
            "precondition: the reader must be holding the queue back, or this test proves nothing about its retirement");

        reader.Dispose();

        Assert.That(dbe.EcsCleanupQueueSize, Is.Zero,
            "the read-only transaction that was blocking reclamation must drain the queue when it retires, not leave it "
            + "for the next writer — it has no ChangeSet, so the drain has to create one");
    }

    /// <summary>
    /// The SAME drain also prunes the MVCC EnabledBits override dictionary, and that half was leaking too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EnabledBitsOverrides.Prune</c> has exactly one caller in the entire engine — inside
    /// <c>DatabaseEngine.ProcessEcsCleanups</c> — so before #681 it never ran in production either. This is not merely a
    /// memory leak: the class is built around a fast path documented as "when <c>_overrideCount</c> == 0, the inline
    /// EntityRecord.EnabledBits is correct (zero overhead)". <c>Prune</c> is the only route back to zero, so every
    /// enable/disable permanently forced EVERY subsequent EnabledBits resolution through a dictionary lookup, engine-wide
    /// and for ever.
    /// </para>
    /// <para>
    /// The class even carries a <c>HighWaterMarkWarningThreshold</c> that warns about "stale transactions blocking
    /// cleanup" — it anticipated unbounded growth and attributed it to the wrong cause. The cleanup was not blocked; it
    /// was never called.
    /// </para>
    /// </remarks>
    [Test]
    public void EnabledBitsOverrides_ReturnToTheZeroOverheadFastPath_AfterTheDrain()
    {
        using var dbe = SetupEngine();

        var ids = new EntityId[20];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = SpawnOne(dbe, i);
        }

        foreach (var id in ids)
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.OpenMut(id).Disable(DrainUnit.Velocity);
            tx.Commit();
        }

        Assert.That(dbe.EnabledBitsOverrides._overrideCount, Is.GreaterThan(0),
            "precondition: disabling must actually record overrides, or the assertion below is vacuous");

        // Retire the transactions behind the last disable so minTSN advances past them.
        SpawnAndDestroyOne(dbe);
        SpawnAndDestroyOne(dbe);

        Assert.That(dbe.EnabledBitsOverrides._overrideCount, Is.Zero,
            "the override dictionary must return to zero so the documented zero-overhead fast path is restored. Its only "
            + "prune runs inside ProcessEcsCleanups, which had no production caller before #681 — so every enable/disable "
            + "permanently pushed all EnabledBits resolution onto the dictionary-lookup slow path");
    }

    /// <summary>
    /// The drain writes to a PERSISTENT LinearHash, so its pages must carry dirty marks that are then released — an
    /// under-release pins the page for ever (#824) and an over-release lets it be evicted with unwritten bytes (#385).
    /// </summary>
    /// <remarks>
    /// This is the assertion that would have caught wiring the drain up with the ChangeSet-less accessor it used to
    /// create: that path raises ActiveChunkWriters but never reaches <c>IncrementDirty</c>, so the page finishes the
    /// write window with no writeback debt at all and the removal is undone by the next eviction (PS-10).
    /// </remarks>
    [Test]
    public void Drain_LeavesNoOutstandingDirtyMarks_AtQuiesce()
    {
        using var dbe = SetupEngine();

        for (var i = 0; i < 30; i++)
        {
            DestroyOne(dbe, SpawnOne(dbe, i));
        }
        SpawnAndDestroyOne(dbe);

        var pins = dbe.MMF.CountUnevictablePages();

        Assert.That(dbe.EcsCleanupQueueSize, Is.Zero, "precondition: the drain must have run");
        Assert.That(pins.Acw, Is.Zero,
            "no ActiveChunkWriters may survive the drain — the accessor it creates must be disposed, or the page is "
            + "blocked from checkpoint capture for ever (CP-11/CP-13)");
    }
}
