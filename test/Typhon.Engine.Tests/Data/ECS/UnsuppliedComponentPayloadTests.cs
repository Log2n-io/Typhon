using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Unsupplied.SvA", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct UnsuppliedSvA
{
    public float X, Y, Z;

    public UnsuppliedSvA(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

[Component("Typhon.Test.Unsupplied.SvB", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct UnsuppliedSvB
{
    public float Dx, Dy, Dz;

    public UnsuppliedSvB(float dx, float dy, float dz)
    {
        Dx = dx;
        Dy = dy;
        Dz = dz;
    }
}

[Archetype]
partial class UnsuppliedSvUnit : Archetype<UnsuppliedSvUnit>
{
    public static readonly Comp<UnsuppliedSvA> A = Register<UnsuppliedSvA>();
    public static readonly Comp<UnsuppliedSvB> B = Register<UnsuppliedSvB>();
}

/// <summary>
/// A component the spawn never supplied must not surface a DIFFERENT entity's data (#845).
/// </summary>
/// <remarks>
/// <para>
/// These deliberately force a chunk RECYCLE rather than reading a fresh allocation: spawn with a distinctive pattern,
/// destroy, drain, then spawn again omitting one component. A fresh chunk is zero for uninteresting reasons — the
/// operating system hands out zeroed pages — so a test that skips the recycle passes whether or not the engine clears
/// anything, which is why this went unnoticed for so long.
/// </para>
/// <para>
/// The two storage modes answer differently, and both answers are correct. SingleVersion stages its payload in the
/// spawn arena, which clears each slot at Alloc, so an unsupplied component genuinely is zero. Versioned does not
/// allocate at all: no chunk, no revision chain, root 0 — the component is ABSENT, which is a state the record can
/// express and the engine can therefore refuse to invent a value for.
/// </para>
/// <para>
/// That makes three distinguishable states for a Versioned slot, and the tests here walk all of them: present
/// (bit set, root ≠ 0), disabled (bit clear, root ≠ 0, value retained — re-enabling needs no value), and absent
/// (bit clear, root 0 — <c>Enable</c> refuses, <c>Enable(comp, in value)</c> is the way in). Conflating the last two is
/// what #845 records, and what design decision #14's zero-initialisation was a workaround for.
/// </para>
/// </remarks>
[NonParallelizable]
class UnsuppliedComponentPayloadTests : TestBase<UnsuppliedComponentPayloadTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.RegisterComponentFromAccessor<UnsuppliedSvA>();
        dbe.RegisterComponentFromAccessor<UnsuppliedSvB>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// SingleVersion: clean, because #839 stages non-Versioned payloads in an arena that clears each slot at Alloc.
    /// </summary>
    [Test]
    public void SingleVersion_UnsuppliedComponent_IsZero_NotThePreviousOccupant()
    {
        using var dbe = SetupEngine();

        EntityId a;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var x = new UnsuppliedSvA(111, 222, 333);
            var y = new UnsuppliedSvB(444, 555, 666);
            a = tx.Spawn<UnsuppliedSvUnit>(UnsuppliedSvUnit.A.Set(in x), UnsuppliedSvUnit.B.Set(in y));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(a);
            tx.Commit();
        }
        dbe.FlushDeferredCleanups();

        EntityId b;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var x = new UnsuppliedSvA(1, 2, 3);
            b = tx.Spawn<UnsuppliedSvUnit>(UnsuppliedSvUnit.A.Set(in x));
            tx.Commit();
        }

        using (var en = dbe.CreateQuickTransaction())
        {
            en.OpenMut(b).Enable(UnsuppliedSvUnit.B);
            en.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        ref readonly var vr = ref read.Open(b).Read(UnsuppliedSvUnit.B);
        var v = vr;   // ref readonly locals cannot be captured by the lambda below

        Assert.Multiple(() =>
        {
            Assert.That(v.Dx, Is.Zero, "a component this spawn never supplied must not carry the destroyed entity's X");
            Assert.That(v.Dy, Is.Zero, "…nor its Y");
            Assert.That(v.Dz, Is.Zero, "…nor its Z");
        });
    }

    /// <summary>
    /// Versioned: enabling a component the spawn never supplied is REFUSED, because there is no value to enable.
    /// </summary>
    /// <remarks>
    /// The old behaviour returned the previous occupant of the recycled chunk — a destroyed entity's committed values, on a live entity (#845). The fix is
    /// not to zero that chunk but to stop creating it: a Versioned slot gets a chunk and a chain only when the spawn supplies a value, so an unsupplied one
    /// is genuinely absent and <c>Enable</c> can say so. This replaces design decision #14, which zero-initialised precisely because <c>Enable</c> had no way
    /// to tell "never supplied" from "written then disabled".
    /// </remarks>
    [Test]
    [VerifiesRule("STAGE-02")]
    public void Versioned_EnablingANeverSuppliedComponent_IsRefused()
    {
        using var dbe = SetupEngine();

        var b = SpawnRecycledVersioned(dbe);

        using var en = dbe.CreateQuickTransaction();

        // EntityRef is a ref struct and cannot be captured by Assert.Throws' lambda.
        string message = null;
        try
        {
            en.OpenMut(b).Enable(EcsUnit.Velocity);
        }
        catch (System.InvalidOperationException ex)
        {
            message = ex.Message;
        }

        Assert.That(message, Is.Not.Null, "enabling a component the spawn never supplied must be refused, not silently allowed");
        Assert.That(message, Does.Contain("never supplied"),
            "the refusal must name the cause. Allowing it instead handed back a destroyed entity's values from the recycled chunk (#845)");
    }

    /// <summary>
    /// The complement: supplying a value and enabling in one step is how a never-supplied component is brought into use.
    /// </summary>
    [Test]
    public void Versioned_EnableWithAValue_SuppliesAndEnables()
    {
        using var dbe = SetupEngine();

        var b = SpawnRecycledVersioned(dbe);

        using (var en = dbe.CreateQuickTransaction())
        {
            var vel = new EcsVelocity(7, 8, 9);
            en.OpenMut(b).Enable(EcsUnit.Velocity, in vel);
            en.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        ref readonly var vr = ref read.Open(b).Read(EcsUnit.Velocity);
        var v = vr;

        Assert.Multiple(() =>
        {
            Assert.That(v.Dx, Is.EqualTo(7f), "the supplied value must win, not the recycled chunk's 444");
            Assert.That(v.Dy, Is.EqualTo(8f));
            Assert.That(v.Dz, Is.EqualTo(9f));
        });
    }

    /// <summary>
    /// The case the refusal must NOT catch: disable preserves the payload, so re-enabling needs no value.
    /// </summary>
    [Test]
    public void Versioned_DisableThenEnable_KeepsTheValue_AndNeedsNoNewOne()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(10, 20, 30);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Disable(EcsUnit.Velocity);
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            try
            {
                tx.OpenMut(id).Enable(EcsUnit.Velocity);
            }
            catch (System.InvalidOperationException ex)
            {
                Assert.Fail($"a component that WAS supplied and then disabled still has its value — re-enabling it must not be refused: {ex.Message}");
            }
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        ref readonly var vr = ref read.Open(id).Read(EcsUnit.Velocity);
        var v = vr;

        Assert.That(v.Dx, Is.EqualTo(10f), "disable preserves the payload — the round trip must return the original value");
    }

    /// <summary>
    /// The chain root must land in the PERSISTED record, not merely in the creating transaction's cache.
    /// </summary>
    /// <remarks>
    /// <see cref="Versioned_EnableWithAValue_SuppliesAndEnables"/> reads through a fresh transaction, but a value can
    /// still be reachable there from the component's <c>SingleCache</c>. Writing the component AGAIN from a third
    /// transaction is what proves the record itself carries <c>CompRevFirstChunkId</c>: copy-on-write has to walk the
    /// chain from the record's root, so a root that was never persisted resolves nothing and the second write lands on a
    /// component the reader cannot see.
    /// </remarks>
    [Test]
    public void Versioned_ComponentSuppliedMidLife_IsWritableFromALaterTransaction()
    {
        using var dbe = SetupEngine();

        var b = SpawnRecycledVersioned(dbe);

        using (var en = dbe.CreateQuickTransaction())
        {
            var vel = new EcsVelocity(7, 8, 9);
            en.OpenMut(b).Enable(EcsUnit.Velocity, in vel);
            en.Commit();
        }

        using (var upd = dbe.CreateQuickTransaction())
        {
            upd.OpenMut(b).Write(EcsUnit.Velocity) = new EcsVelocity(70, 80, 90);
            upd.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        ref readonly var vr = ref read.Open(b).Read(EcsUnit.Velocity);
        var v = vr;

        Assert.That(v.Dx, Is.EqualTo(70f),
            "an ordinary write after a mid-life supply must be visible — if it is not, the chain root never reached the "
          + "EntityMap record and copy-on-write had no chain to extend");
    }

    /// <summary>
    /// Supplying a value BEFORE the spawn commits must survive the commit — the spawn owns publication, not the
    /// mid-life path.
    /// </summary>
    /// <remarks>
    /// The pending-spawn case is a distinct code path and fails differently: <c>FinalizeSpawns</c> writes
    /// <c>SetCompRevFirstChunkId(recordPtr, vi, entry.Rev[slot])</c> unconditionally, so a root published through the
    /// live-entity route is CLOBBERED by a still-zero <c>entry.Rev</c>, and the value is silently lost at commit. The
    /// allocation therefore has to be recorded in the SpawnEntry itself.
    /// </remarks>
    [Test]
    public void Versioned_EnableWithAValue_OnAPendingSpawn_SurvivesTheCommit()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

            var vel = new EcsVelocity(31, 32, 33);
            tx.OpenMut(id).Enable(EcsUnit.Velocity, in vel);
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        var e = read.Open(id);

        Assert.That(e.IsEnabled(EcsUnit.Velocity), Is.True, "the enable must survive the commit");

        ref readonly var vr = ref e.Read(EcsUnit.Velocity);
        var v = vr;

        Assert.Multiple(() =>
        {
            Assert.That(v.Dx, Is.EqualTo(31f), "the value supplied before the spawn committed must survive it");
            Assert.That(v.Dy, Is.EqualTo(32f));
            Assert.That(v.Dz, Is.EqualTo(33f));
        });
    }

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion to <see cref="Versioned_EnablingANeverSuppliedComponent_IsRefused"/>: prove the refusal is keyed on
    /// ABSENCE, not fired unconditionally.
    /// </summary>
    /// <remarks>
    /// A green refusal test could mean the guard reads the slot's absence correctly, or simply that <c>Enable</c> now throws for everything — very different
    /// claims, and only the first is STAGE-02. Giving the slot storage (the state the old contract created at spawn for every component) must make the refusal
    /// stop: same entity, same slot, same call, opposite outcome. If this throws, the guard is blanket and the verifier next door proves nothing.
    /// </remarks>
    [Test]
    [RuleMutant("STAGE-02")]
    public void Mutant_GivingTheSlotStorage_StopsTheRefusal()
    {
        using var dbe = SetupEngine();

        var b = SpawnRecycledVersioned(dbe);

        // Unmutated: absent, so the refusal fires. Without this the mutation below could be "detecting" a guard that never fired at all.
        using (var probe = dbe.CreateQuickTransaction())
        {
            var refused = false;
            try
            {
                probe.OpenMut(b).Enable(EcsUnit.Velocity);
            }
            catch (System.InvalidOperationException)
            {
                refused = true;
            }

            Assert.That(refused, Is.True, "sanity: the slot must start absent, or this mutation proves nothing");
        }

        // The mutation: supply a value, which creates the chunk and the chain the old contract created at spawn.
        using (var supply = dbe.CreateQuickTransaction())
        {
            var vel = new EcsVelocity(1, 2, 3);
            supply.OpenMut(b).Enable(EcsUnit.Velocity, in vel);
            supply.Commit();
        }

        using (var disable = dbe.CreateQuickTransaction())
        {
            disable.OpenMut(b).Disable(EcsUnit.Velocity);
            disable.Commit();
        }

        using var after = dbe.CreateQuickTransaction();
        string message = null;
        try
        {
            after.OpenMut(b).Enable(EcsUnit.Velocity);
        }
        catch (System.InvalidOperationException ex)
        {
            message = ex.Message;
        }

        Assert.That(message, Is.Null,
            "with storage present the refusal must NOT fire — a guard that throws here too is unconditional, and the verifier it backs would pass for a "
          + $"reason unrelated to absence. Got: {message}");
    }

    /// <summary>
    /// A component supplied mid-life is visible to a SECOND open in the same transaction, before any commit.
    /// </summary>
    /// <remarks>
    /// The root only reaches the EntityMap record at commit, so between the supply and the commit "root 0" means two
    /// opposite things: absent, or created-here-and-not-yet-published. A resolver that reads absence from the record
    /// alone conflates them and hands back location 0 for a component this very transaction wrote — which the enabled
    /// bit then lets <c>Read</c> dereference, reproducing #845's failure mode inside a single transaction. Caught by
    /// review, not by the first round of tests, because every one of those committed before reading back.
    /// </remarks>
    [Test]
    public void Versioned_ComponentSuppliedMidLife_IsVisibleToASecondOpen_BeforeCommit()
    {
        using var dbe = SetupEngine();

        var b = SpawnRecycledVersioned(dbe);

        using var tx = dbe.CreateQuickTransaction();

        var vel = new EcsVelocity(7, 8, 9);
        tx.OpenMut(b).Enable(EcsUnit.Velocity, in vel);

        // Second Open in the same transaction — the record still has root 0, the value lives only in SingleCache.
        var e = tx.Open(b);
        Assert.That(e.IsEnabled(EcsUnit.Velocity), Is.True, "the enable is staged, so the bit is set");

        ref readonly var vr = ref e.Read(EcsUnit.Velocity);
        var v = vr;

        Assert.That(v.Dx, Is.EqualTo(7f),
            "re-opening in the same transaction must still see the supplied value, not the recycled chunk's 444 or zeros");
    }

    /// <summary>
    /// Supplying a value and then disabling it in the SAME transaction must leave a coherent record.
    /// </summary>
    /// <remarks>
    /// Publication is driven off newly-set enable bits, so a slot that ends the transaction DISABLED publishes no chain
    /// root — while the commit's component pipeline still processes the created revision, indexing it and copying it into
    /// the cluster slot. That leaves index entries and cluster bytes for a component the record says is absent, plus an
    /// orphaned chain. The entity must come out of it in one of the two legal states, not a mixture.
    /// </remarks>
    [Test]
    public void Versioned_SupplyThenDisableInOneTransaction_LeavesACoherentRecord()
    {
        using var dbe = SetupEngine();

        var b = SpawnRecycledVersioned(dbe);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var vel = new EcsVelocity(7, 8, 9);
            var e = tx.OpenMut(b);
            e.Enable(EcsUnit.Velocity, in vel);
            e.Disable(EcsUnit.Velocity);
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        var after = read.Open(b);

        Assert.That(after.IsEnabled(EcsUnit.Velocity), Is.False, "it was disabled before the commit, so it must read back disabled");

        // Disabled-with-a-value is a legal state and re-enabling it must work without re-supplying — that is the state
        // the supply created. The alternative legal outcome would be for the supply to have been discarded entirely,
        // but then the value must not be reachable either. What must NOT happen is a record claiming absence over a
        // chain and index entries that exist.
        using var re = dbe.CreateQuickTransaction();
        string message = null;
        try
        {
            re.OpenMut(b).Enable(EcsUnit.Velocity);
        }
        catch (System.InvalidOperationException ex)
        {
            message = ex.Message;
        }

        Assert.That(message, Is.Null,
            "the value was supplied in that transaction, so the component has one and re-enabling must not be refused. A refusal means the chain was created "
          + $"but its root never reached the record — an orphaned chain the entity cannot reach. Got: {message}");
    }

    /// <summary>
    /// A rolled-back mid-life supply must free the chunk and the chain it allocated.
    /// </summary>
    /// <remarks>
    /// A code review predicted a leak here, on solid-looking reasoning: rollback frees <c>Created</c> revisions by walking
    /// the pending-spawn list, which a LIVE entity is not in, and the copy-on-write branch beside it explicitly excludes
    /// <c>Created</c> — so a mid-life supply is the first thing to be both Created and not-spawned, and appears to fall
    /// between them. Measured, it does not: the counts below stay flat across rounds, so something else reclaims both
    /// chunks. The prediction was not acted on, because a redundant free is a DOUBLE free — strictly worse than the leak
    /// it would have prevented. This test is what settled it, and is kept as the guard: if a future change does open that
    /// gap, the count starts climbing here.
    /// <para>
    /// Asserted as a delta across rounds, not a single pass: one round cannot tell a freed chunk from a segment that has
    /// not grown yet.
    /// </para>
    /// </remarks>
    [Test]
    public void Versioned_RolledBackMidLifeSupply_FreesItsChunkAndChain()
    {
        using var dbe = SetupEngine();

        var b = SpawnRecycledVersioned(dbe);
        var table = dbe.GetComponentTable<EcsVelocity>();

        var afterFirst = 0;
        for (var round = 0; round < 4; round++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                var vel = new EcsVelocity(round, round, round);
                tx.OpenMut(b).Enable(EcsUnit.Velocity, in vel);
                // No Commit — Dispose rolls back.
            }

            var used = table.ComponentSegment.AllocatedChunkCount + table.CompRevTableSegment.AllocatedChunkCount;
            if (round == 0)
            {
                afterFirst = used;
                continue;
            }

            Assert.That(used, Is.EqualTo(afterFirst),
                $"round {round}: {used} chunks allocated versus {afterFirst} after the first rolled-back supply. A count that climbs with the number of "
              + "rolled-back transactions means each one leaked its content chunk and revision chain");
        }
    }

    /// <summary>
    /// WAL replay of a value for a Versioned slot with NO chain root must CREATE the chain, not skip the append (#845).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven straight at <c>RecoveryApplier</c> rather than through a crash-and-reopen. The end-to-end route is the one
    /// that looks right and proves nothing: an oracle-shaped version of this passed with the fix reverted, and throw-probes
    /// showed neither <c>ApplySlotToExistingCluster</c> nor its dispatcher was reached, because the driver folded the
    /// spawn and the slot record into a single <c>ApplySpawnedEntity</c> — a path that already creates chain roots
    /// correctly. Coverage is which lines execute, not how realistic the setup looks.
    /// </para>
    /// <para>
    /// The state under test is one the new contract created and recovery had never seen: an entity committed WITHOUT a
    /// component, whose record therefore carries <c>CompRevFirstChunkId == 0</c>, and a later transaction supplying one.
    /// The replay used to append to that root, and appending to root 0 is a silent no-op — the value reached the cluster
    /// SoA and looked correct until the next open rebuilt the HEAD from a chain that was never there.
    /// </para>
    /// </remarks>
    [Test]
    public void Recovery_SlotApplyForARootlessVersionedSlot_CreatesTheChain()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));   // Velocity absent → its record slot holds root 0
            tx.Commit();
        }

        var meta = Archetype<EcsUnit>.Metadata;
        var velSlot = meta.GetSlot(EcsUnit.Velocity._componentTypeId);

        // The payload the WAL would carry: the component value, no overhead — the same shape BuildCommitBatch emits.
        var replayed = new EcsVelocity(41, 42, 43);
        var payload = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<EcsVelocity>()];
        MemoryMarshal.Write(payload, in replayed);

        using (var applier = new Typhon.Engine.Internals.RecoveryApplier(dbe))
        {
            // The real driver runs the whole replay inside one epoch scope; chunk accessors assert on it.
            using var epoch = Typhon.Engine.Internals.EpochGuard.Enter(dbe.EpochManager);

            // Same order the driver uses: enabled-bits first, then the slot payloads. Both records are what an
            // Enable(comp, in value) commit emits, and the slot apply alone leaves the component unreadable.
            applier.ApplySetEnabledBitsToExisting((long)id.RawValue, (ushort)(1 << velSlot | 1 << meta.GetSlot(EcsUnit.Position._componentTypeId)));

            applier.ApplySlotToExisting((long)id.RawValue,
            [
                new Typhon.Engine.Internals.RecoveryApplier.SlotData { SlotIndex = velSlot, Payload = payload, Tsn = 1 },
            ]);
        }

        using var read = dbe.CreateQuickTransaction();
        ref readonly var vr = ref read.Open(id).Read(EcsUnit.Velocity);
        var v = vr;

        Assert.That(v.Dx, Is.EqualTo(41f),
            "the replayed value must be readable — if it is not, the apply wrote the cluster SoA and created no chain, so the point read resolves nothing");
    }

    /// <summary>Spawns B against a chunk deliberately recycled from a destroyed A, omitting Velocity.</summary>
    private static EntityId SpawnRecycledVersioned(DatabaseEngine dbe)
    {
        EntityId a;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(111, 222, 333);
            var vel = new EcsVelocity(444, 555, 666);
            a = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(a);
            tx.Commit();
        }
        dbe.FlushDeferredCleanups();

        using var spawn = dbe.CreateQuickTransaction();
        var p = new EcsPosition(1, 2, 3);
        var b = spawn.Spawn<EcsUnit>(EcsUnit.Position.Set(in p));
        spawn.Commit();
        return b;
    }
}
