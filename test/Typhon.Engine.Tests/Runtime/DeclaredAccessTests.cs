using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// Own archetypes: a fixture sharing another's archetype inherits its flakes (#720).
[Component("Typhon.Test.Dv01.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct Dv01Pos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Component("Typhon.Test.Dv01.Vel", 1, StorageMode = StorageMode.SingleVersion)]
struct Dv01Vel
{
    public float X;
}

[Archetype]
partial class Dv01Unit : Archetype<Dv01Unit>
{
    public static readonly Comp<Dv01Pos> Pos = Register<Dv01Pos>();
    public static readonly Comp<Dv01Vel> Vel = Register<Dv01Vel>();
}

/// <summary>
/// DV-01 (#1650): every kind of access a system makes inside its body — component read and write through each entry point, spawn, destroy, event write
/// and event drain — throws <see cref="InvalidAccessException"/> naming the system and the type unless the system declared it; the declared complement of
/// each passes. Each probe catches inside the system body, so what is asserted is the validator's verdict, not the tick's exception policy.
/// </summary>
[TestFixture]
class DeclaredAccessTests : TestBase<DeclaredAccessTests>
{
    private sealed class Probe : CallbackSystem
    {
        public Action<SystemBuilder> Declare;
        public Action<TickContext> Body;
        public Exception Caught;
        public readonly ManualResetEventSlim Ran = new();
        private int _done;

        protected override void Configure(SystemBuilder b)
        {
            b.Name("Probe");
            Declare?.Invoke(b);
        }

        protected override void Execute(TickContext ctx)
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
            {
                return;
            }
            try
            {
                Body(ctx);
            }
            catch (Exception e)
            {
                Caught = e;
            }
            finally
            {
                Ran.Set();
            }
        }
    }

    private DatabaseEngine _dbe;
    private EntityId _id;

    [SetUp]
    public void SetUpEngine()
    {
        // Assert, not Assume: without the key these verifiers would go Inconclusive, which reads as green.
        Assert.That(CheckConfig.DeclaredAccessActive, Is.True, "this suite's typhon.telemetry.json sets Typhon:Checks:DeclaredAccess");
        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<Dv01Pos>();
        _dbe.RegisterComponentFromAccessor<Dv01Vel>();
        _dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000, 1000), cellSize: 100f));
        _dbe.InitializeArchetypes();

        // Before the runtime starts: outside any system, nothing is checked.
        using var tx = _dbe.CreateQuickTransaction();
        _id = tx.Spawn<Dv01Unit>(Dv01Unit.Pos.Set(At(10, 10)), Dv01Unit.Vel.Set(new Dv01Vel { X = 1 }));
        tx.Commit();
    }

    [TearDown]
    public void TearDownEngine() => _dbe?.Dispose();

    private static Dv01Pos At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private Exception Run(Action<SystemBuilder> declare, Action<TickContext> body, Action<Dag> extra = null)
    {
        var probe = new Probe { Declare = declare, Body = body };
        using var runtime = TyphonRuntime.Create(_dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            extra?.Invoke(dag);   // first: a queue the probe declares must exist when Add runs its Configure
            dag.Add(probe);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        runtime.Start();
        var ran = probe.Ran.Wait(TimeSpan.FromSeconds(10));
        runtime.Shutdown();
        Assert.That(ran, Is.True, "precondition: the probe system ran");
        return probe.Caught;
    }

    private static void AssertUndeclared<T>(Exception e, string verb)
    {
        Assert.That(e, Is.InstanceOf<InvalidAccessException>(), e?.ToString());
        var iae = (InvalidAccessException)e;
        Assert.That(iae.SystemName, Is.EqualTo("Probe"));
        Assert.That(iae.UndeclaredType, Is.EqualTo(typeof(T)));
        Assert.That(iae.Message, Does.Contain(verb).And.Contain(typeof(T).Name));
    }

    private static void ForFirstCluster(TickContext ctx, ClusterAction action)
    {
        foreach (var cluster in ctx.Transaction.For<Dv01Unit>().GetClusterEnumerator())
        {
            action(cluster);
            return;
        }
        Assert.Fail("precondition: the spawned entity has a cluster");
    }

    private delegate void ClusterAction(ClusterRef<Dv01Unit> cluster);

    // ── Reads ────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("DV-01")]
    public void Read_Undeclared_Throws() =>
        AssertUndeclared<Dv01Pos>(Run(b => b.Writes<Dv01Vel>(), ctx => ctx.Transaction.Open(_id).Read(Dv01Unit.Pos)), "read");

    [Test]
    [VerifiesRule("DV-01")]
    public void TryRead_Undeclared_Throws() =>
        AssertUndeclared<Dv01Vel>(Run(b => b.Reads<Dv01Pos>(), ctx => ctx.Transaction.Open(_id).TryRead(Dv01Unit.Vel, out _)), "read");

    [Test]
    [VerifiesRule("DV-01")]
    public void ClusterReadOnlySpan_Undeclared_Throws() =>
        AssertUndeclared<Dv01Vel>(Run(b => b.Reads<Dv01Pos>(), ctx => ForFirstCluster(ctx, c => c.GetReadOnlySpan(Dv01Unit.Vel))), "read");

    [Test]
    [VerifiesRule("DV-01")]
    public void ClusterGetReadOnly_Undeclared_Throws() =>
        AssertUndeclared<Dv01Vel>(Run(b => b.Reads<Dv01Pos>(), ctx => ForFirstCluster(ctx, c => c.GetReadOnly(Dv01Unit.Vel, 0))), "read");

    [Test]
    [VerifiesRule("DV-01")]
    public void Read_DeclaredByEveryReadKind_AndByAWrite_Passes()
    {
        Action<SystemBuilder>[] grants =
        [
            b => b.Reads<Dv01Pos>(), b => b.ReadsFresh<Dv01Pos>(), b => b.AdditionalReads<Dv01Pos>(),   // ReadsSnapshot needs Versioned (AC-05)
            b => b.Writes<Dv01Pos>()
        ];
        foreach (var grant in grants)
        {
            Assert.That(Run(grant, ctx => ctx.Transaction.Open(_id).Read(Dv01Unit.Pos)), Is.Null);
        }
    }

    [Test]
    [VerifiesRule("DV-01")]
    public void ASystemThatDeclaredNothing_IsChecked()
    {
        // A class-form system with no declaration is not exempt (it used to be): every access it makes is undeclared.
        AssertUndeclared<Dv01Pos>(Run(null, ctx => ctx.Transaction.Open(_id).Read(Dv01Unit.Pos)), "read");
    }

    [Test]
    [VerifiesRule("DV-01")]
    public void ALambdaFormSystem_IsNotChecked()
    {
        // Dag.CallbackSystem has no declaration surface: it is ordered by its explicit edges alone, and the validator does not see it.
        Exception caught = null;
        var ran = new ManualResetEventSlim();
        using (var runtime = TyphonRuntime.Create(_dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Lambda", ctx =>
               {
                   try
                   {
                       ctx.Transaction.Open(_id).Read(Dv01Unit.Pos);
                   }
                   catch (Exception e)
                   {
                       caught = e;
                   }
                   ran.Set();
               }), new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            Assert.That(ran.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the lambda system ran");
            runtime.Shutdown();
        }
        Assert.That(caught, Is.Null);
    }

    // ── Writes, through each entry point ─────────────────────────────────

    [Test]
    [VerifiesRule("DV-01")]
    public void Set_Undeclared_Throws() =>
        AssertUndeclared<Dv01Vel>(Run(b => b.Reads<Dv01Vel>(), ctx => ctx.Transaction.OpenMut(_id).Set(Dv01Unit.Vel, new Dv01Vel { X = 2 })), "wrote");

    [Test]
    [VerifiesRule("DV-01")]
    public void ClusterSet_Undeclared_Throws() =>
        AssertUndeclared<Dv01Vel>(Run(b => b.Reads<Dv01Vel>(), ctx => ForFirstCluster(ctx, c => c.Set(Dv01Unit.Vel, 0, new Dv01Vel()))), "wrote");

    [Test]
    [VerifiesRule("DV-01")]
    public void ClusterGet_Undeclared_Throws() =>
        AssertUndeclared<Dv01Vel>(Run(b => b.Reads<Dv01Vel>(), ctx => ForFirstCluster(ctx, c => c.Get(Dv01Unit.Vel, 0))), "wrote");

    [Test]
    [VerifiesRule("DV-01")]
    public void ClusterGetSpan_Undeclared_Throws() =>
        AssertUndeclared<Dv01Vel>(Run(b => b.Reads<Dv01Vel>(), ctx => ForFirstCluster(ctx, c => c.GetSpan(Dv01Unit.Vel))), "wrote");

    [Test]
    [VerifiesRule("DV-01")]
    public void WriteSpatial_Undeclared_Throws() =>
        AssertUndeclared<Dv01Pos>(Run(b => b.Reads<Dv01Pos>(), ctx => ForFirstCluster(ctx, c => c.WriteSpatial(Dv01Unit.Pos, 0, At(20, 20)))), "wrote");

    [Test]
    [VerifiesRule("DV-01")]
    public void Set_Declared_Passes() =>
        Assert.That(Run(b => b.Writes<Dv01Vel>(), ctx => ctx.Transaction.OpenMut(_id).Set(Dv01Unit.Vel, new Dv01Vel { X = 2 })), Is.Null);

    // ── SideWrites: a side transaction only ─────────────────────────────

    [Test]
    [VerifiesRule("DV-01")]
    public void SideWrites_ThroughTheSystemTransaction_Throws()
    {
        var e = Run(b => b.SideWrites<Dv01Vel>(), ctx => ctx.Transaction.OpenMut(_id).Set(Dv01Unit.Vel, new Dv01Vel { X = 3 }));
        AssertUndeclared<Dv01Vel>(e, "wrote");
        Assert.That(e.Message, Does.Contain("side transaction"));
    }

    [Test]
    [VerifiesRule("DV-01")]
    public void SideWrites_ThroughASideTransaction_Passes()
    {
        var e = Run(b => b.SideWrites<Dv01Vel>(), ctx =>
        {
            using var side = ctx.CreateSideTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
            Assert.That(side.IsSideTransaction, Is.True);
            side.OpenMut(_id).Set(Dv01Unit.Vel, new Dv01Vel { X = 3 });
            side.Commit();
        });
        Assert.That(e, Is.Null);
    }

    // ── Spawn / destroy ─────────────────────────────────────────────────

    [Test]
    [VerifiesRule("DV-01")]
    public void Spawn_WithoutEveryComponentDeclared_Throws()
    {
        var e = Run(b => b.Writes<Dv01Pos>(), ctx => ctx.Transaction.Spawn<Dv01Unit>(Dv01Unit.Pos.Set(At(30, 30))));
        AssertUndeclared<Dv01Vel>(e, "spawned");
    }

    [Test]
    [VerifiesRule("DV-01")]
    public void Spawn_WithEveryComponentDeclared_Passes() =>
        Assert.That(Run(b => b.Writes<Dv01Pos>().Writes<Dv01Vel>(), ctx => ctx.Transaction.Spawn<Dv01Unit>(Dv01Unit.Pos.Set(At(30, 30)))), Is.Null);

    [Test]
    [VerifiesRule("DV-01")]
    public void Destroy_WithEveryComponentDeclared_Passes() =>
        Assert.That(Run(b => b.Writes<Dv01Pos>().Writes<Dv01Vel>(), ctx => ctx.Transaction.Destroy(_id)), Is.Null);

    [Test]
    [VerifiesRule("DV-01")]
    public void SideWrites_ReadThroughASideTransaction_Passes()
    {
        var e = Run(b => b.SideWrites<Dv01Vel>(), ctx =>
        {
            using var side = ctx.CreateSideTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
            side.Open(_id).Read(Dv01Unit.Vel);
        });
        Assert.That(e, Is.Null);
    }

    [Test]
    [VerifiesRule("DV-01")]
    public void AParallelSystem_IsCheckedInEveryChunk()
    {
        // A parallel QuerySystem enters the validator per chunk on the worker pool: a different dispatch site from the serial callback probes above.
        Exception caught = null;
        using var ran = new ManualResetEventSlim();
        using var txView = _dbe.CreateQuickTransaction();
        using var view = txView.Query<Dv01Unit>().ToView();
        var probe = new ParallelProbe(view, e =>
        {
            Interlocked.CompareExchange(ref caught, e, null);
            ran.Set();
        });
        using (var runtime = TyphonRuntime.Create(_dbe, schedule => schedule.PublicTrack.DeclareDag("Test").Add(probe),
                   new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }))
        {
            runtime.Start();
            Assert.That(ran.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the parallel system ran a chunk");
            runtime.Shutdown();
        }
        AssertUndeclared<Dv01Vel>(caught, "read");
    }

    private sealed class ParallelProbe(ViewBase view, Action<Exception> report) : QuerySystem
    {
        protected override void Configure(SystemBuilder b) => b.Name("Probe").Input(() => view).Parallel().Reads<Dv01Pos>();

        protected override void Execute(TickContext ctx)
        {
            try
            {
                foreach (var id in ctx.Entities)
                {
                    ctx.Accessor.Open(id).Read(Dv01Unit.Vel);
                }
                report(null);
            }
            catch (Exception e)
            {
                report(e);
            }
        }
    }

    [Test]
    [VerifiesRule("DV-01")]
    public void Destroy_WithoutEveryComponentDeclared_Throws() =>
        AssertUndeclared<Dv01Pos>(Run(b => b.Writes<Dv01Vel>(), ctx => ctx.Transaction.Destroy(_id)), "destroyed");

    // ── Events ──────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("DV-01")]
    public void EventWrite_Undeclared_Throws()
    {
        EventQueue<int> queue = null;
        var e = Run(null, ctx => ctx.Writer(queue).Push(1), dag => queue = dag.CreateEventQueue<int>("Dv01Events"));
        Assert.That(e, Is.InstanceOf<InvalidAccessException>(), e?.ToString());
        Assert.That(e.Message, Does.Contain("Probe").And.Contain("Dv01Events").And.Contain("WritesEvents"));
    }

    [Test]
    [VerifiesRule("DV-01")]
    public void EventDrain_Undeclared_Throws()
    {
        EventQueue<int> queue = null;
        var e = Run(null, ctx => queue.Drain(stackalloc int[4]), dag => queue = dag.CreateEventQueue<int>("Dv01Drained"));
        Assert.That(e, Is.InstanceOf<InvalidAccessException>(), e?.ToString());
        Assert.That(e.Message, Does.Contain("Probe").And.Contain("Dv01Drained").And.Contain("ReadsEvents"));
    }

    [Test]
    [VerifiesRule("DV-01")]
    public void Events_Declared_Pass()
    {
        EventQueue<int> queue = null;
        var e = Run(b => b.WritesEvents(queue).ReadsEvents(queue), ctx =>
        {
            ctx.Writer(queue).Push(1);
            queue.Drain(stackalloc int[4]);
        }, dag => queue = dag.CreateEventQueue<int>("Dv01Both"));
        Assert.That(e, Is.Null);
    }
}
