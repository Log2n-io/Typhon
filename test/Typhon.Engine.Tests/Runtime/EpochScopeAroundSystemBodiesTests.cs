using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// Own archetype: ArchetypeRegistry is process-global and unsynchronised across parallel fixtures (#720).
[Component("Typhon.Test.EpochScope.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EpochScopePos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class EpochScopeUnit : Archetype<EpochScopeUnit>
{
    public static readonly Comp<EpochScopePos> Pos = Register<EpochScopePos>();
}

/// <summary>
/// RT-01 — every system body, of every shape, runs inside an epoch scope opened by the framework.
/// </summary>
/// <remarks>
/// <para>
/// The rule exists because <see cref="ClusterSpatialQuery{TArch}"/> is public and its enumerator dereferences cluster pages during the narrowphase, so those
/// pages must be pinned for the walk (PS-02). <c>EpochGuard</c> is internal and stays internal — a public RAII pin is a footgun (PS-09) — so the guarantee has
/// to come from the dispatcher rather than from the caller.
/// </para>
/// <para>
/// Three shapes, three different mechanisms, which is exactly why this is worth a test per shape rather than one test: a serial body is inside its
/// transaction's scope, a parallel query body is inside its worker accessor's, and a chunked callback body is inside one the dispatcher opens per chunk. That
/// last one had <b>none</b> before #909 — and it is a public shape, so a user could write it and then had no legal way to call the public spatial query from
/// it. <see cref="AChunkedCallbackBody_CanRunThePublicClusterSpatialQuery"/> is that case, and it fails on the pre-fix dispatcher.
/// </para>
/// <para>
/// The probe is <see cref="EpochManager.IsCurrentThreadInScope"/>, reached through the public <see cref="DatabaseEngine.EpochManager"/> — deliberately, since
/// a verifier that needed friend access could not answer the question the rule is about.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class EpochScopeAroundSystemBodiesTests : TestBase<EpochScopeAroundSystemBodiesTests>
{
    private const float CellSize = 100f;
    private const float World = 1_000f;
    private const int Entities = 400;

    private DatabaseEngine CreateEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EpochScopePos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), CellSize));
        dbe.InitializeArchetypes();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Entities; i++)
            {
                var x = i % 20 * 50f;
                var y = i / 20 * 50f;
                var v = new EpochScopePos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x + 1f, MaxY = y + 1f } };
                tx.Spawn<EpochScopeUnit>(EpochScopeUnit.Pos.Set(in v));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }

    /// <summary>Runs <paramref name="declare"/>'s systems until <paramref name="ticks"/> ticks have been observed, then shuts down.</summary>
    private static void RunTicks(DatabaseEngine dbe, Action<RuntimeSchedule> declare, ref int ticksSeen, int ticks = 3)
    {
        using var runtime = TyphonRuntime.Create(dbe, declare, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 200, CostBasedChunking = false });
        runtime.Start();

        // Copied into a local because SpinUntil cannot close over a ref parameter.
        var reached = false;
        var seen = 0;
        SpinWait.SpinUntil(() =>
        {
            seen = Volatile.Read(ref TickCounter);
            reached = seen >= ticks;
            return reached;
        }, TimeSpan.FromSeconds(10));

        runtime.Shutdown();
        ticksSeen = seen;
        Assert.That(reached, Is.True, $"precondition: the runtime reached only {seen} of {ticks} ticks");
    }

    /// <summary>Shared tick counter — the fixture is <c>[NonParallelizable]</c>, and a static keeps the lambda plumbing out of every test.</summary>
    private static int TickCounter;

    [SetUp]
    public void ResetCounter() => Volatile.Write(ref TickCounter, 0);

    [Test]
    [VerifiesRule("RT-01")]
    public void ASerialCallbackSystemBody_RunsInsideAnEpochScope()
    {
        using var dbe = CreateEngine();
        var observations = new List<bool>();

        var ticksSeen = 0;
        RunTicks(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Serial", _ =>
            {
                lock (observations)
                {
                    observations.Add(dbe.EpochManager.IsCurrentThreadInScope);
                }

                Interlocked.Increment(ref TickCounter);
            });
        }, ref ticksSeen);

        Assert.That(observations, Is.Not.Empty, "precondition: the body never ran");
        Assert.That(observations, Has.All.True, "RT-01: a serial CallbackSystem body must run inside an epoch scope");
    }

    [Test]
    [VerifiesRule("RT-01")]
    public void AParallelQuerySystemBody_RunsInsideAnEpochScope()
    {
        using var dbe = CreateEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EpochScopeUnit>().ToView();
        var observations = new List<bool>();

        var ticksSeen = 0;
        RunTicks(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref TickCounter));
            dag.QuerySystem("Walk", _ =>
            {
                lock (observations)
                {
                    observations.Add(dbe.EpochManager.IsCurrentThreadInScope);
                }
            }, input: () => view, parallel: true, after: "Tick");
        }, ref ticksSeen);

        view.Dispose();
        Assert.That(observations, Is.Not.Empty, "precondition: the body never ran");
        Assert.That(observations, Has.All.True, "RT-01: a parallel QuerySystem body must run inside an epoch scope");
    }

    [Test]
    [VerifiesRule("RT-01")]
    public void AChunkedCallbackSystemBody_RunsInsideAnEpochScope()
    {
        using var dbe = CreateEngine();
        var observations = new List<bool>();
        var probe = new ChunkedProbeSystem(_ =>
        {
            lock (observations)
            {
                observations.Add(dbe.EpochManager.IsCurrentThreadInScope);
            }
        });

        var ticksSeen = 0;
        RunTicks(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref TickCounter));
            dag.Add(probe);
        }, ref ticksSeen);

        Assert.That(observations, Is.Not.Empty, "precondition: the body never ran");
        Assert.That(observations, Has.All.True,
            "RT-01: a chunked CallbackSystem body must run inside an epoch scope — this is the shape that had none, and it is public");
    }

    /// <summary>
    /// The case the rule exists for: the public spatial query, called from the one public system shape that used to be unable to satisfy its precondition.
    /// </summary>
    [Test]
    [VerifiesRule("RT-01")]
    public void AChunkedCallbackBody_CanRunThePublicClusterSpatialQuery()
    {
        using var dbe = CreateEngine();
        var hits = new List<int>();
        var errors = new List<string>();
        var probe = new ChunkedProbeSystem(ctx =>
        {
            try
            {
                // A box covering the whole world: every spawned entity must come back, and each hit must carry an addressable EntityId (#909 part 2).
                var sphere = new BSphere2F { CenterX = World / 2f, CenterY = World / 2f, Radius = World };
                var found = 0;
                var addressable = 0;
                var e = dbe.ClusterSpatialQuery<EpochScopeUnit>().Radius(in sphere);
                try
                {
                    while (e.MoveNext())
                    {
                        found++;
                        if (!e.Current.Entity.IsNull)
                        {
                            addressable++;
                        }
                    }
                }
                finally
                {
                    e.Dispose();
                }

                lock (hits)
                {
                    hits.Add(found);
                    if (found != addressable)
                    {
                        errors.Add($"{found - addressable} of {found} hits carried a null EntityId");
                    }
                }
            }
            catch (Exception ex)
            {
                lock (hits)
                {
                    errors.Add(ex.ToString());
                }
            }
        });

        var ticksSeen = 0;
        RunTicks(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref TickCounter));
            dag.Add(probe);
        }, ref ticksSeen);

        Assert.That(errors, Is.Empty, "the public spatial query must run from a chunked callback body without the caller opening anything");
        Assert.That(hits, Is.Not.Empty, "precondition: the body never ran");
        Assert.That(hits, Has.All.EqualTo(Entities), "every spawned entity must come back from a world-sized radius query");
    }

    /// <summary>A chunked callback system whose body is supplied per test. Four chunks, so more than one worker runs it.</summary>
    private sealed class ChunkedProbeSystem : ChunkedCallbackSystem
    {
        private readonly Action<TickContext> _body;

        public ChunkedProbeSystem(Action<TickContext> body) => _body = body;

        protected override void Configure(SystemBuilder b) => b
            .Name("ChunkedProbe")
            .ChunkedParallel(4)
            .After("Tick");

        protected override void Execute(TickContext ctx) => _body(ctx);
    }
}
