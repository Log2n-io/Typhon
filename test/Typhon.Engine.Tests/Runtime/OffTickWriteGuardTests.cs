using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// Own archetypes: a fixture sharing another's archetype inherits its flakes (#720).
[Component("Typhon.Test.OffTick.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct OtPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class OtSpatial : Archetype<OtSpatial>
{
    public static readonly Comp<OtPos> Pos = Register<OtPos>();
}

[Component("Typhon.Test.OffTick.Hp", 1, StorageMode = StorageMode.SingleVersion)]
struct OtHp
{
    public int Value;
}

[Archetype]
partial class OtSv : Archetype<OtSv>
{
    public static readonly Comp<OtHp> Hp = Register<OtHp>();
}

[Component("Typhon.Test.OffTick.Scratch", 1, StorageMode = StorageMode.Transient)]
struct OtScratch
{
    public int Value;
}

[Archetype]
partial class OtTransient : Archetype<OtTransient>
{
    public static readonly Comp<OtScratch> Scratch = Register<OtScratch>();
}

[Component("Typhon.Test.OffTick.Gold", 1)]
struct OtGold
{
    public long Value;
}

[Component("Typhon.Test.OffTick.Badge", 1)]
struct OtBadge
{
    public long Value;
}

[Archetype]
partial class OtVersioned : Archetype<OtVersioned>
{
    public static readonly Comp<OtGold> Gold = Register<OtGold>();
    public static readonly Comp<OtBadge> Badge = Register<OtBadge>();
}

/// <summary>
/// The off-tick write guard (#1651, EW-01 / EW-02): while a runtime ticks, a transaction opened on a thread running no system is refused, at the call and
/// deterministically, every write that races the tick fence — a SingleVersion or Transient component, anything on a spatial or realm-keyed archetype. Its
/// legal complements pass: a Versioned write and any spawn, destroy, enable or disable of another archetype off-tick (the commit waits out a running fence
/// through the structural gate), anything before Start or after Shutdown, the same writes handed to the tick through <see cref="TyphonRuntime.WriteInTick"/>.
/// A transaction opened inside a system is bound to that system's run.
/// </summary>
[TestFixture]
class OffTickWriteGuardTests : TestBase<OffTickWriteGuardTests>
{
    private DatabaseEngine _dbe;
    private EntityId _spatial, _sv, _transient, _versioned;

    [SetUp]
    public void SetUpEngine()
    {
        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<OtPos>();
        _dbe.RegisterComponentFromAccessor<OtHp>();
        _dbe.RegisterComponentFromAccessor<OtScratch>();
        _dbe.RegisterComponentFromAccessor<OtGold>();
        _dbe.RegisterComponentFromAccessor<OtBadge>();
        _dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000, 1000), cellSize: 100f));
        _dbe.InitializeArchetypes();

        // Before Start: every write is legal.
        using var tx = _dbe.CreateQuickTransaction();
        _spatial = tx.Spawn<OtSpatial>(OtSpatial.Pos.Set(At(10, 10)));
        _sv = tx.Spawn<OtSv>(OtSv.Hp.Set(new OtHp { Value = 1 }));
        _transient = tx.Spawn<OtTransient>(OtTransient.Scratch.Set(new OtScratch { Value = 1 }));
        _versioned = tx.Spawn<OtVersioned>(OtVersioned.Gold.Set(new OtGold { Value = 1 }), OtVersioned.Badge.Set(new OtBadge { Value = 1 }));
        Assert.That(tx.Commit(), Is.True);
    }

    [TearDown]
    public void TearDownEngine() => _dbe?.Dispose();

    private static OtPos At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static RuntimeOptions Options(int capacity = 4096, int perTick = 256) =>
        new() { WorkerCount = 1, BaseTickRate = 1000, InTickQueueCapacity = capacity, InTickMaxItemsPerTick = perTick };

    /// <summary>Starts a runtime and returns once it has completed two ticks — observed from a system, not polled.</summary>
    private TyphonRuntime StartRuntime(Action<Dag> configure = null, RuntimeOptions options = null)
    {
        var ticked = new CountdownEvent(2);
        var runtime = TyphonRuntime.Create(_dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Heartbeat", _ =>
            {
                if (!ticked.IsSet)
                {
                    ticked.Signal();
                }
            });
            configure?.Invoke(dag);
        }, options ?? Options());
        runtime.Start();
        Assert.That(ticked.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the runtime ticks");
        return runtime;
    }

    private static void AssertRefused(Action write, string what, string rule = "EW-02")
    {
        var e = Assert.Throws<InvalidOperationException>(() => write());
        Assert.That(e.Message, Does.Contain(rule).And.Contain(what));
    }

    // ── Refused off-tick ────────────────────────────────────────────────

    [Test]
    [VerifiesRule("EW-02")]
    public void AnOffTickSingleVersionWrite_IsRefused()
    {
        using var runtime = StartRuntime();
        using var tx = _dbe.CreateQuickTransaction();
        AssertRefused(() => tx.OpenMut(_sv).Set(OtSv.Hp, new OtHp { Value = 2 }), "SingleVersion");
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void AnOffTickTransientWrite_IsRefused()
    {
        using var runtime = StartRuntime();
        using var tx = _dbe.CreateQuickTransaction();
        AssertRefused(() => tx.OpenMut(_transient).Set(OtTransient.Scratch, new OtScratch { Value = 2 }), "Transient");
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void AnOffTickSpatialWrite_ThroughSetAndThroughWriteSpatial_IsRefused()
    {
        using var runtime = StartRuntime();
        using var tx = _dbe.CreateQuickTransaction();
        AssertRefused(() => tx.OpenMut(_spatial).Set(OtSpatial.Pos, At(20, 20)), "SpatialIndex");
        AssertRefused(() =>
        {
            foreach (var cluster in tx.For<OtSpatial>().GetClusterEnumerator())
            {
                cluster.WriteSpatial(OtSpatial.Pos, 0, At(30, 30));
            }
        }, "SpatialIndex");
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void AnOffTickClusterWrite_IsRefused()
    {
        using var runtime = StartRuntime();
        using var tx = _dbe.CreateQuickTransaction();
        AssertRefused(() =>
        {
            foreach (var cluster in tx.For<OtSv>().GetClusterEnumerator())
            {
                cluster.Set(OtSv.Hp, 0, new OtHp { Value = 3 });
            }
        }, "SingleVersion");
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void AnOffTickSpawnDestroyOrEnable_OfASpatialArchetype_IsRefused()
    {
        // The fence moves a spatial archetype's entities between clusters: no off-tick structural change of one, gate or not.
        using var runtime = StartRuntime();
        using var tx = _dbe.CreateQuickTransaction();
        AssertRefused(() => tx.Spawn<OtSpatial>(OtSpatial.Pos.Set(At(50, 50))), "spawned");
        AssertRefused(() => tx.SpawnByArchetypeId(Archetype<OtSpatial>.Metadata.ArchetypeId, OtSpatial.Pos.Set(At(50, 50))), "spawned");
        AssertRefused(() => tx.Destroy(_spatial), "destroyed");
        AssertRefused(() => tx.DestroyBatch([_spatial]), "destroyed");
        AssertRefused(() => tx.OpenMut(_spatial).Disable(OtSpatial.Pos), "enabled or disabled");
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void ATransactionOpenedBeforeStart_IsGuardedOnceTheRuntimeTicks()
    {
        using var tx = _dbe.CreateQuickTransaction();
        tx.OpenMut(_sv).Set(OtSv.Hp, new OtHp { Value = 5 });   // before Start: legal
        using var runtime = StartRuntime();
        AssertRefused(() => tx.OpenMut(_sv).Set(OtSv.Hp, new OtHp { Value = 6 }), "SingleVersion");
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void WriteTickFence_WhileARuntimeTicks_IsRefused()
    {
        using var runtime = StartRuntime();
        var e = Assert.Throws<InvalidOperationException>(() => _dbe.WriteTickFence(0));
        Assert.That(e.Message, Does.Contain("EW-02"));
    }

    // ── Legal complements ───────────────────────────────────────────────

    [Test]
    [VerifiesRule("EW-02")]
    public void AnOffTickSpawnDestroyAndEnable_OfANonSpatialArchetype_Pass()
    {
        // ADR-072 D1: structural mutation outside the tick is legal. Versioned and SingleVersion archetypes alike: the structural gate keeps the commit out of
        // the fence window, and a spawn's in-place bytes are invisible until it publishes.
        using var runtime = StartRuntime();
        EntityId versioned, sv;
        using (var tx = _dbe.CreateQuickTransaction())
        {
            versioned = tx.Spawn<OtVersioned>(OtVersioned.Gold.Set(new OtGold { Value = 4 }), OtVersioned.Badge.Set(new OtBadge { Value = 4 }));
            sv = tx.SpawnByArchetypeId(Archetype<OtSv>.Metadata.ArchetypeId, OtSv.Hp.Set(new OtHp { Value = 4 }));
            tx.OpenMut(_versioned).Disable(OtVersioned.Badge);
            Assert.That(tx.Commit(), Is.True);
        }
        using (var tx = _dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(versioned).Read(OtVersioned.Gold).Value, Is.EqualTo(4));
            Assert.That(tx.Open(sv).Read(OtSv.Hp).Value, Is.EqualTo(4));
            Assert.That(tx.Open(_versioned).IsEnabled(OtVersioned.Badge), Is.False);
            tx.Destroy(versioned);
            tx.Destroy(_sv);
            tx.OpenMut(_versioned).Enable(OtVersioned.Badge);
            Assert.That(tx.Commit(), Is.True);
        }
        using (var tx = _dbe.CreateQuickTransaction())
        {
            Assert.That(tx.IsAlive(versioned), Is.False);
            Assert.That(tx.IsAlive(_sv), Is.False);
            Assert.That(tx.Open(_versioned).IsEnabled(OtVersioned.Badge), Is.True);
        }
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void AnOffTickStructuralCommit_WaitsOutARunningFence()
    {
        // Staged, no runtime: hold the window open on another thread, as a fence would. The gate applies to every unbound structural commit.
        var window = _dbe.EpochManager.FenceWindow;
        using var opened = new ManualResetEventSlim();
        using var close = new ManualResetEventSlim();
        var fence = new Thread(() =>
        {
            using (window.Open())
            {
                opened.Set();
                close.Wait();
            }
        });
        fence.Start();
        Assert.That(opened.Wait(TimeSpan.FromSeconds(10)), Is.True);

        var committed = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            using var tx = _dbe.CreateQuickTransaction();
            tx.Spawn<OtVersioned>(OtVersioned.Gold.Set(new OtGold { Value = 12 }));
            tx.Commit();
            committed.Set();
        });
        writer.Start();
        Assert.That(committed.Wait(100), Is.False, "the commit waits while the window is open");
        close.Set();
        Assert.That(committed.Wait(TimeSpan.FromSeconds(10)), Is.True, "and lands once it closes");
        fence.Join();
        writer.Join();
        committed.Dispose();
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void AFence_WaitsForAnAdmittedStructuralCommit_AndAFenceThreadIsNeverGated()
    {
        var window = _dbe.EpochManager.FenceWindow;
        Assert.That(window.EnterStructuralWriter(), Is.True, "precondition: admitted with no fence running");
        using var opened = new ManualResetEventSlim();
        var fence = new Thread(() =>
        {
            using (window.Open())
            {
                opened.Set();
            }
        });
        fence.Start();
        Assert.That(opened.Wait(100), Is.False, "the fence waits for the admitted commit");
        window.ExitStructuralWriter();
        Assert.That(opened.Wait(TimeSpan.FromSeconds(10)), Is.True, "and opens once it leaves");
        fence.Join();

        // The fence's own thread commits a structural change with the window open: not gated, or it would wait for itself.
        using (window.Open())
        {
            using var tx = _dbe.CreateQuickTransaction();
            tx.Spawn<OtVersioned>(OtVersioned.Gold.Set(new OtGold { Value = 13 }));
            Assert.That(tx.Commit(), Is.True);
        }
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void AnOffTickVersionedNonSpatialWrite_Passes()
    {
        using var runtime = StartRuntime();
        using var tx = _dbe.CreateQuickTransaction();
        tx.OpenMut(_versioned).Set(OtVersioned.Gold, new OtGold { Value = 7 });
        Assert.That(tx.Commit(), Is.True);
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void AWriteAfterShutdown_EvenThroughATransactionStampedWhileTicking_Passes()
    {
        var runtime = StartRuntime();
        using var stamped = _dbe.CreateQuickTransaction();   // opened while the runtime ticks
        runtime.Shutdown();
        stamped.OpenMut(_sv).Set(OtSv.Hp, new OtHp { Value = 8 });
        Assert.That(stamped.Commit(), Is.True);
        using (var tx = _dbe.CreateQuickTransaction())
        {
            tx.Spawn<OtSpatial>(OtSpatial.Pos.Set(At(60, 60)));
            Assert.That(tx.Commit(), Is.True);
        }
        runtime.Dispose();
    }

    [Test]
    [VerifiesRule("EW-02")]
    public void TheSameWrites_HandedToTheTick_Pass_AndThisTicksSystemsSeeTheSpawn()
    {
        var seenInRun = -1L;
        EntityId spawned = default;
        using var seen = new ManualResetEventSlim();
        using var runtime = StartRuntime(dag => dag.CallbackSystem("Seer", ctx =>
        {
            if (!seen.IsSet && !spawned.IsNull && ctx.Transaction.TryOpen(spawned, out _))
            {
                Volatile.Write(ref seenInRun, ctx.TickNumber);
                seen.Set();
            }
        }));
        var tick = runtime.WriteInTick((tx, _) =>
        {
            tx.OpenMut(_sv).Set(OtSv.Hp, new OtHp { Value = 10 });
            tx.OpenMut(_spatial).Set(OtSpatial.Pos, At(40, 40));
            tx.OpenMut(_versioned).Disable(OtVersioned.Badge);
            spawned = tx.Spawn<OtSv>(OtSv.Hp.Set(new OtHp { Value = 11 }));
        }, DurabilityMode.Deferred).GetAwaiter().GetResult();
        Assert.That(tick, Is.GreaterThanOrEqualTo(0));

        var read = runtime.InTick((tx, _) => tx.Open(_sv).Read(OtSv.Hp).Value, DurabilityMode.Deferred).GetAwaiter().GetResult();
        Assert.That(read, Is.EqualTo(10));
        // The task completes inside the drain, before that tick's systems run: wait for the system, then check WHICH tick it saw the spawn on.
        Assert.That(seen.Wait(TimeSpan.FromSeconds(10)), Is.True, "a system sees the spawn");
        Assert.That(Volatile.Read(ref seenInRun), Is.EqualTo(tick), "drained before the tick's systems: they see the spawn on the tick it ran");
    }

    // ── In-tick work ────────────────────────────────────────────────────

    [Test]
    public void InTick_BeforeStart_RunsInline_AndAFaultGoesToTheTask()
    {
        using var runtime = TyphonRuntime.Create(_dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { }), Options());
        var tick = runtime.WriteInTick((tx, _) => tx.OpenMut(_sv).Set(OtSv.Hp, new OtHp { Value = 12 }), DurabilityMode.Deferred);
        Assert.That(tick.IsCompletedSuccessfully, Is.True);
        Assert.That(tick.Result, Is.EqualTo(-1));

        var faulted = runtime.InTick<int>((_, _) => throw new ApplicationException("boom"), DurabilityMode.Deferred);
        Assert.That(faulted.IsFaulted, Is.True);
        Assert.That(faulted.Exception!.InnerException, Is.InstanceOf<ApplicationException>());
    }

    [Test]
    public void InTick_RefusesImmediateDurability()
    {
        using var runtime = StartRuntime();
        Assert.Throws<ArgumentException>(() => _ = runtime.InTick((_, _) => 1, DurabilityMode.Immediate));
    }

    [Test]
    public void InTick_AFaultGoesToTheTask_NotToTheTick()
    {
        using var runtime = StartRuntime();
        var faulted = runtime.InTick<int>((_, _) => throw new ApplicationException("boom"), DurabilityMode.Deferred);
        var e = Assert.Throws<ApplicationException>(() => faulted.GetAwaiter().GetResult());
        Assert.That(e.Message, Is.EqualTo("boom"));
        Assert.That(runtime.InTick((_, tick) => tick, DurabilityMode.Deferred).GetAwaiter().GetResult(), Is.GreaterThanOrEqualTo(0),
            "the runtime keeps ticking and draining");
    }

    [Test]
    public void InTick_AFullQueue_RefusesAtOnce_AndAtMostThePerTickCapRunsPerTick()
    {
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var runtime = StartRuntime(options: Options(capacity: 2, perTick: 1));
        // The blocker holds the tick start; the two queued behind it cannot drain while we fill the queue.
        var blocker = runtime.InTick((_, tick) =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            return tick;
        }, DurabilityMode.Deferred);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the blocker is running");
        var a = runtime.InTick((_, tick) => tick, DurabilityMode.Deferred);
        var b = runtime.InTick((_, tick) => tick, DurabilityMode.Deferred);
        var c = runtime.InTick((_, tick) => tick, DurabilityMode.Deferred);
        Assert.That(c.IsFaulted, Is.True);
        Assert.That(c.Exception!.InnerException, Is.InstanceOf<TickQueueFullException>());
        Assert.That(((TickQueueFullException)c.Exception.InnerException).IsTransient, Is.True);
        gate.Set();
        var ticks = new[] { blocker.GetAwaiter().GetResult(), a.GetAwaiter().GetResult(), b.GetAwaiter().GetResult() };
        Assert.That(ticks[1], Is.GreaterThan(ticks[0]), "one item per tick");
        Assert.That(ticks[2], Is.GreaterThan(ticks[1]), "one item per tick");
    }

    [Test]
    public void InTick_PendingItemsAtShutdown_AndLaterCalls_Fault()
    {
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var runtime = StartRuntime(options: Options(perTick: 1));
        var blocker = runtime.InTick((_, _) =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            return 0;
        }, DurabilityMode.Deferred);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the blocker is running");
        var pending = runtime.InTick((_, _) => 1, DurabilityMode.Deferred);
        var stop = new Thread(runtime.Shutdown);
        stop.Start();
        gate.Set();
        stop.Join();
        Assert.That(blocker.GetAwaiter().GetResult(), Is.Zero);
        Assert.That(pending.IsFaulted || pending.IsCompletedSuccessfully, Is.True, "a pending item completes or faults: never left hanging");
        var late = runtime.InTick((_, _) => 2, DurabilityMode.Deferred);
        Assert.That(late.IsFaulted, Is.True);
        Assert.That(late.Exception!.InnerException, Is.InstanceOf<InvalidOperationException>());
        runtime.Dispose();
    }

    [Test]
    public void StartTwice_IsRefused_AndTheEngineStopsCountingTheRuntimeOnShutdown()
    {
        var runtime = StartRuntime();
        Assert.Throws<InvalidOperationException>(runtime.Start);
        runtime.Shutdown();
        Assert.That(_dbe.RuntimeTicking, Is.False, "a refused second Start must not leave the engine counted as ticking");
        runtime.Dispose();
    }

    [Test]
    public void FatalStop_KeepsTheGuardUntilDispose()
    {
        var runtime = StartRuntime();
        runtime.FatalStop();
        Assert.That(_dbe.RuntimeTicking, Is.True, "FatalStop does not wait for the tick in flight");
        runtime.Dispose();
        Assert.That(_dbe.RuntimeTicking, Is.False);
    }

    // ── Transactions opened inside a system (EW-01) ─────────────────────

    [Test]
    [VerifiesRule("EW-01")]
    public void ASideTransactionCommittedInsideItsSystem_Passes_AndOneCommittedOnALaterRun_IsRefused()
    {
        // WorkerCount = 1 runs every system on the scheduler thread, so the leaked transaction is committed on its own thread (affinity holds) but in a
        // later run of the system that opened it — after that run returned, which is the EW-01 hazard.
        Exception inside = null, later = null;
        Transaction leaked = null;
        using var done = new ManualResetEventSlim();
        var run = 0;
        using var runtime = StartRuntime(dag => dag.CallbackSystem("Side", ctx =>
        {
            switch (Interlocked.Increment(ref run))
            {
                case 1:
                    try
                    {
                        using var side = ctx.CreateSideTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
                        side.OpenMut(_versioned).Set(OtVersioned.Gold, new OtGold { Value = 13 });
                        side.Commit();
                    }
                    catch (Exception e)
                    {
                        inside = e;
                    }
                    leaked = ctx.CreateSideTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
                    leaked.OpenMut(_versioned).Set(OtVersioned.Gold, new OtGold { Value = 14 });
                    break;
                case 2:
                    try
                    {
                        leaked.Commit();
                    }
                    catch (Exception e)
                    {
                        later = e;
                    }
                    leaked.Dispose();
                    done.Set();
                    break;
            }
        }));
        Assert.That(done.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the system ran twice");
        Assert.That(inside, Is.Null);
        Assert.That(later, Is.InstanceOf<InvalidOperationException>());
        Assert.That(later.Message, Does.Contain("EW-01"));
    }

    [Test]
    [VerifiesRule("EW-01")]
    public void AQuickTransactionLeakedFromASystem_IsRefusedItsWritesOnALaterRun()
    {
        Exception later = null;
        Transaction leaked = null;
        using var done = new ManualResetEventSlim();
        var run = 0;
        using var runtime = StartRuntime(dag => dag.CallbackSystem("Leaker", _ =>
        {
            switch (Interlocked.Increment(ref run))
            {
                case 1:
                    leaked = _dbe.CreateQuickTransaction();   // not a side transaction: bound to this run all the same
                    break;
                case 2:
                    try
                    {
                        leaked.OpenMut(_sv).Set(OtSv.Hp, new OtHp { Value = 15 });
                    }
                    catch (Exception e)
                    {
                        later = e;
                    }
                    leaked.Dispose();
                    done.Set();
                    break;
            }
        }));
        Assert.That(done.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the system ran twice");
        Assert.That(later, Is.InstanceOf<InvalidOperationException>());
        Assert.That(later.Message, Does.Contain("EW-01"));
    }
}
