using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema

[Component("Typhon.Test.Cmd.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CmdPosition
{
    public float X;
    public float Y;

    public CmdPosition(float x, float y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>Carries another entity's id, which is what makes the corpse-and-loot case a real test rather than a compile check.</summary>
[Component("Typhon.Test.Cmd.Owner", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CmdOwner
{
    /// <summary>
    /// The owner's id, as its raw 64-bit value.
    /// </summary>
    /// <remarks>
    /// <b>A <c>long</c> and not an <see cref="EntityId"/>, and the reason is a finding rather than a preference:</b> the schema builder knows no mapping
    /// for <c>EntityId</c>, so a <c>[Field] public EntityId</c> member is silently skipped — a component whose ONLY field is one fails registration with a
    /// bare "sequence contains no elements" out of LINQ that names neither the component nor the field. <c>demo/SwgTatooine/Ecs/Components.cs</c> declares
    /// three such members and gets away with it only because those components carry other fields too. Not this fixture's subject; it carries the raw value
    /// so the test asserts what it is about — that the 8 bytes handed to the caller are the 8 bytes stored.
    /// </remarks>
    public long OwnerRaw;

    public CmdOwner(EntityId owner) => OwnerRaw = (long)owner.RawValue;
}

[Archetype]
class CmdCorpse : Archetype<CmdCorpse>
{
    public static readonly Comp<CmdPosition> Position = Register<CmdPosition>();
}

[Archetype]
class CmdLoot : Archetype<CmdLoot>
{
    public static readonly Comp<CmdPosition> Position = Register<CmdPosition>();
    public static readonly Comp<CmdOwner> Owner = Register<CmdOwner>();
}

#endregion

/// <summary>
/// #1099 — the write side of deferred entity commands: <c>ctx.Commands.Spawn</c> hands back a real final id from a parallel chunk, with no atomic per
/// spawn and nothing alive until the apply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Most of these drive the writer directly rather than through a running runtime</b>, which is deliberate: the write path is the whole subject, a
/// writer resolved from the buffer exercises it in full (validation, key blocks, segment append, counters), and staging the precondition beats waiting for
/// a tick to produce it. One test goes through a real system body, because the context plumbing is the part direct calls cannot check.
/// </para>
/// <para>
/// <b>Nothing here asserts that a queued entity becomes alive</b> — the apply phase is #1102. What it does assert is the other half of the validity
/// contract, which is the half an application can get wrong: the id is final and distinct, and until the apply the entity is not alive, not openable and
/// not query-visible, with none of those answers throwing.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class EntityCommandWriteSideTests : TestBase<EntityCommandWriteSideTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CmdPosition>();
        dbe.RegisterComponentFromAccessor<CmdOwner>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>A runtime with one empty DAG, never started — enough to own a bound command buffer.</summary>
    private static TyphonRuntime QuietRuntime(DatabaseEngine dbe, int workerCount = 4, int commandsPerTick = 4096) =>
        TyphonRuntime.Create(dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Idle", _ => { }),
            new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = 1000, EntityCommandsPerTick = commandsPerTick });

    // ── The id ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline claim: a spawn queued from a chunk returns a real id, not a placeholder and not null.
    /// </summary>
    [Test]
    public void AQueuedSpawnReturnsARealFinalId()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);
        var w = runtime.EntityCommands.GetWriter(0, 0);

        var id = w.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(1, 2)));

        Assert.Multiple(() =>
        {
            Assert.That(id.IsNull, Is.False, "a spawn into a registered archetype with room must be accepted");
            Assert.That(id.EntityKey, Is.GreaterThan(0), "the key comes from the archetype's monotonic counter, which starts above zero");
            Assert.That(id.ArchetypeId, Is.EqualTo(dbe.RoutingIdOf(ArchetypeRegistry.GetMetadata<CmdCorpse>())),
                "the routing id must be the one the apply will use to find the archetype's storage");
            Assert.That(runtime.EntityCommands.Count, Is.EqualTo(1));
            Assert.That(runtime.EntityCommands.PendingEntities, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Ids are distinct across a burst from one producer, across producers, and across archetypes — each for a different reason, so each is asserted.
    /// </summary>
    [Test]
    public void IdsAreDistinctAcrossABurstAcrossProducersAndAcrossArchetypes()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);

        var seen = new HashSet<ulong>();
        var corpses = 0;
        for (var chunk = 0; chunk < 4; chunk++)
        {
            var w = runtime.EntityCommands.GetWriter(chunk % 2, chunk);
            for (var i = 0; i < 50; i++)
            {
                var corpse = w.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(i, chunk)));
                var loot = w.Spawn<CmdLoot>(CmdLoot.Position.Set(new CmdPosition(i, chunk)));
                Assert.That(seen.Add(corpse.RawValue), Is.True, $"corpse id {corpse} repeated (chunk {chunk}, i {i})");
                Assert.That(seen.Add(loot.RawValue), Is.True, $"loot id {loot} repeated (chunk {chunk}, i {i})");
                corpses++;
            }
        }

        Assert.That(seen, Has.Count.EqualTo(corpses * 2));
        Assert.That(runtime.EntityCommands.PendingEntities, Is.EqualTo(corpses * 2));
    }

    /// <summary>
    /// <b>The id must not depend on which worker ran the chunk.</b> Two writers on DIFFERENT worker slots but the same chunk index draw from the same key
    /// block, continuing one cursor; two writers on the same slot but different chunk indices draw from different blocks. That is the whole point of
    /// striding by <c>ChunkIndex</c> rather than by slot, and it is what makes a seeded scenario reproducible on a machine with a different worker count.
    /// </summary>
    [Test]
    public void TheKeyFollowsTheChunkIndexNotTheWorkerSlot()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);
        var blockSize = runtime.EntityCommands.Keys.BlockSize;

        // Same chunk, two different slots: consecutive keys out of one block.
        var a = runtime.EntityCommands.GetWriter(0, 1).Spawn<CmdCorpse>();
        var b = runtime.EntityCommands.GetWriter(1, 1).Spawn<CmdCorpse>();

        // A different chunk: a different block, exactly blockSize apart from chunk 1's.
        var c = runtime.EntityCommands.GetWriter(0, 2).Spawn<CmdCorpse>();

        Assert.Multiple(() =>
        {
            Assert.That(b.EntityKey, Is.EqualTo(a.EntityKey + 1),
                "two producers on one chunk index share a key block and a cursor, whatever worker slot each ran on");
            Assert.That(c.EntityKey, Is.EqualTo(a.EntityKey + blockSize),
                $"chunk 2's block must start one whole block ({blockSize}) past chunk 1's");
            Assert.That(runtime.EntityCommands.GenerationsTaken(ArchetypeRegistry.GetMetadata<CmdCorpse>().ArchetypeId), Is.EqualTo(1),
                "one generation per archetype per tick is the healthy figure — more means the derived block is too small for the workload");
        });
    }

    /// <summary>
    /// Exhausting a producer's block takes a fresh generation rather than refusing, and the keys stay distinct across the boundary. The design's position,
    /// against the survey's recommendation that exhaustion be a loud refusal: taking the next generation is the same mechanism again, not a fallback to a
    /// different one, and refusing would lose a legitimately large burst for a reason the caller cannot act on.
    /// </summary>
    [Test]
    public void ExhaustingABlockTakesAFreshGenerationRatherThanRefusing()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);
        var blockSize = runtime.EntityCommands.Keys.BlockSize;
        var w = runtime.EntityCommands.GetWriter(0, 0);

        var keys = new HashSet<long>();
        for (var i = 0; i < blockSize * 2 + 1; i++)
        {
            var id = w.Spawn<CmdCorpse>();
            Assert.That(id.IsNull, Is.False, $"spawn {i} of {blockSize * 2 + 1} was refused; a block boundary must not lose a command");
            Assert.That(keys.Add(id.EntityKey), Is.True, $"key {id.EntityKey} repeated at spawn {i} — a generation reissued keys");
        }

        Assert.That(runtime.EntityCommands.GenerationsTaken(ArchetypeRegistry.GetMetadata<CmdCorpse>().ArchetypeId), Is.EqualTo(3),
            "two full blocks plus one more key is three generations for this producer");
        Assert.That(runtime.EntityCommands.OverflowCount, Is.Zero);
    }

    // ── The validity contract ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each term of the contract separately, because they fail independently: the id is valid, and the entity is not alive, not openable either way, and
    /// not query-visible — with none of those throwing. A test that asserted only "not alive" would pass against an engine that threw on the open.
    /// </summary>
    [Test]
    public void UntilTheApplyTheEntityIsNotAliveNotOpenableAndNotVisible_AndNothingThrows()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);
        var id = runtime.EntityCommands.GetWriter(0, 0).Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(5, 5)));
        Assert.That(id.IsNull, Is.False, "precondition: the command was queued");

        using var tx = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(tx.IsAlive(id), Is.False, "the entity must not be alive before the apply");
            Assert.That(tx.TryOpen(id, out _), Is.False, "and must not be openable for reading");
            Assert.That(tx.TryOpenMut(id, out _), Is.False, "and must not be openable for writing");

            var found = 0;
            foreach (var _ in tx.Query<CmdCorpse>())
            {
                found++;
            }

            Assert.That(found, Is.Zero, "and must not appear in a query");
        });
    }

    // ── Refusals and overflow, which are different things ───────────────────────────────────────

    /// <summary>
    /// An unregistered archetype is refused at the call — the same moment <c>Transaction.Spawn</c> throws for it (D-2), but answered rather than thrown
    /// because this runs in a parallel chunk where an exception becomes a system failure and can cancel the tick.
    /// </summary>
    [Test]
    public void AnUnregisteredArchetypeIsRefusedAtTheCallAndCounted()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);

        // SpawnUnregArch's component is registered by no fixture, which is what makes it genuinely unspawnable. An archetype declared over a component
        // THIS fixture registers would be initialised by InitializeArchetypes and spawn perfectly well — the trap #1095's own tests fell into.
        var id = runtime.EntityCommands.GetWriter(0, 0).Spawn<SpawnUnregArch>();

        Assert.Multiple(() =>
        {
            Assert.That(id.IsNull, Is.True, "nothing can be spawned into an archetype this database does not know");
            Assert.That(runtime.EntityCommands.RejectedCount, Is.EqualTo(1u), "an impossible request is a rejection");
            Assert.That(runtime.EntityCommands.OverflowCount, Is.Zero, "and not an overflow — overflow is the engine out of room");
            Assert.That(runtime.EntityCommands.Count, Is.Zero, "a refused command must not occupy a segment slot");
        });
    }

    /// <summary>A null target is a rejection, not a silently-dropped no-op.</summary>
    [Test]
    public void DestroyingTheNullEntityIsRefusedAndCounted()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);

        Assert.That(runtime.EntityCommands.GetWriter(0, 0).Destroy(EntityId.Null), Is.False);
        Assert.That(runtime.EntityCommands.RejectedCount, Is.EqualTo(1u));
    }

    /// <summary>
    /// Past the budget a command is dropped, returns null and is counted EXACTLY — the whole basis of the tolerate-and-count verdict is that a reported
    /// zero means nothing was lost, so the count has to be exact rather than sampled.
    /// </summary>
    [Test]
    public void PastTheBudgetCommandsAreDroppedAndCountedExactly()
    {
        using var dbe = SetupEngine();
        // One slot's share of a 16-command budget, so the ceiling is reachable without queueing thousands.
        using var runtime = QuietRuntime(dbe, workerCount: 1, commandsPerTick: 16);
        var w = runtime.EntityCommands.GetWriter(0, 0);

        var accepted = 0;
        var refused = 0;
        for (var i = 0; i < 64; i++)
        {
            if (w.Spawn<CmdCorpse>().IsNull)
            {
                refused++;
            }
            else
            {
                accepted++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.GreaterThan(0), "64 commands against a 16-command budget must hit the ceiling");
            Assert.That(accepted + refused, Is.EqualTo(64), "every call is either accepted or refused — there is no third outcome");
            Assert.That(runtime.EntityCommands.OverflowCount, Is.EqualTo((uint)refused), "the overflow count must equal what was actually lost, exactly");
            Assert.That(runtime.EntityCommands.Count, Is.EqualTo(accepted));
        });
    }

    // ── SpawnMany ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A run is queued whole or not at all, fills the caller's span, and is contiguous — the property that lets the API return a count instead of a range.
    /// </summary>
    [Test]
    public void SpawnManyFillsTheSpanWithOneContiguousRun()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);
        var w = runtime.EntityCommands.GetWriter(0, 0);

        Span<EntityId> ids = stackalloc EntityId[8];
        var n = w.SpawnMany<CmdCorpse>(8, ids, CmdCorpse.Position.Set(new CmdPosition(3, 4)));

        // Copied out of the span because a ref struct cannot be captured by the Assert.Multiple lambda.
        var keys = new long[8];
        for (var i = 0; i < 8; i++)
        {
            keys[i] = ids[i].EntityKey;
        }

        Assert.Multiple(() =>
        {
            Assert.That(n, Is.EqualTo(8));
            for (var i = 1; i < 8; i++)
            {
                Assert.That(keys[i], Is.EqualTo(keys[0] + i), $"id {i} must continue the run");
            }

            Assert.That(runtime.EntityCommands.Count, Is.EqualTo(1), "one header for the whole run — that is why SpawnMany exists");
            Assert.That(runtime.EntityCommands.PendingEntities, Is.EqualTo(8), "but eight entities");
        });
    }

    /// <summary>A span too short for the run refuses the whole command rather than filling part of it.</summary>
    [Test]
    public void SpawnManyIntoAShortSpanQueuesNothing()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);

        Span<EntityId> ids = stackalloc EntityId[3];
        Assert.That(runtime.EntityCommands.GetWriter(0, 0).SpawnMany<CmdCorpse>(8, ids), Is.Zero);
        Assert.That(runtime.EntityCommands.Count, Is.Zero, "a partial run would hand the caller ids it cannot account for");
    }

    // ── The motivating case, through a real system body ─────────────────────────────────────────

    /// <summary>
    /// The corpse-and-loot case through <c>ctx.Commands</c> in a running system: the corpse's id is final at the moment it is copied into the loot's
    /// component, so <b>no remap pass exists anywhere in the engine</b> — the concrete advantage over the placeholder schemes, which must remap ids
    /// embedded in recorded payloads at playback and cannot carry the link across a tick boundary at all.
    /// </summary>
    [Test]
    public void TheCorpseAndLootCaseRecordsAFinalIdThroughTheContext()
    {
        using var dbe = SetupEngine();

        EntityId recordedCorpse = default;
        EntityId recordedOwner = default;
        var ran = new ManualResetEventSlim(false);

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Die", ctx =>
            {
                if (ran.IsSet)
                {
                    return;
                }

                var cmds = ctx.Commands;
                if (!cmds.IsValid)
                {
                    ran.Set();
                    return;
                }

                var corpse = cmds.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(1, 1)));
                if (!corpse.IsNull)
                {
                    cmds.Spawn<CmdLoot>(CmdLoot.Position.Set(new CmdPosition(1, 1)), CmdLoot.Owner.Set(new CmdOwner(corpse)));
                    recordedCorpse = corpse;
                    recordedOwner = corpse;
                }

                ran.Set();
            });
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        runtime.Start();
        Assert.That(ran.Wait(TimeSpan.FromSeconds(5)), Is.True, "the system never ran");

        Assert.Multiple(() =>
        {
            Assert.That(recordedCorpse.IsNull, Is.False, "ctx.Commands must be valid in a system body and must accept the spawn");
            Assert.That(recordedOwner, Is.EqualTo(recordedCorpse),
                "the id written into Loot.Owner is the same 8 bytes the caller was handed — nothing rewrites it");
        });
    }

    /// <summary>
    /// A lifecycle hook owns no worker slot, so its handle is invalid and every call on it is a no-op — the shape <c>ctx.Writer(queue)</c> already uses
    /// for a null queue. It must not throw: a hook that queues a command is a mistake to report, not a reason to fail start-up.
    /// </summary>
    [Test]
    public void ALifecycleHookGetsAnInvalidHandleAndNoThrow()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);

        var ctx = new TickContext { TickNumber = 0, WorkerId = TickContext.NonWorkerId };
        var cmds = ctx.Commands;

        // Every call evaluated here, outside the lambda, because a ref struct cannot be captured into one. That the calls do not throw is half the claim.
        var valid = cmds.IsValid;
        var pending = cmds.Pending;
        var spawnedNull = cmds.Spawn<CmdCorpse>().IsNull;
        var destroyed = cmds.Destroy(EntityId.FromParts(1, 1));

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.False);
            Assert.That(pending, Is.Zero);
            Assert.That(spawnedNull, Is.True);
            Assert.That(destroyed, Is.False);
        });
    }

    // ── Cost ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Zero steady-state allocation on the push path, counted rather than timed. The first commands into a slot allocate its two buffers lazily, which is
    /// the point of lazy allocation; after that a spawn must allocate nothing at all.
    /// </summary>
    [Test]
    public void TheSteadyStatePushPathAllocatesNothing()
    {
        using var dbe = SetupEngine();
        using var runtime = QuietRuntime(dbe);
        var w = runtime.EntityCommands.GetWriter(0, 0);

        // Warm the segment past its lazy allocation and past any growth the loop below could trigger.
        for (var i = 0; i < 2048; i++)
        {
            w.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(i, 0)));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 512; i++)
        {
            w.Spawn<CmdCorpse>(CmdCorpse.Position.Set(new CmdPosition(i, 1)));
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.Zero, $"512 steady-state spawns allocated {allocated} bytes; the push path must not allocate");
    }
}
