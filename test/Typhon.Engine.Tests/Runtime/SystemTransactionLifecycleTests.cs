using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// RT-02 (#879): a system's transaction is started once per run and ended once, by that run. Two NullReferenceExceptions from a commit's tail on runtime
/// workers read as one Transaction ended by two owners: a system completed twice, its next run started over the live one, and the live run's end
/// committed and disposed the new run's transaction under it. The guards make that loud instead of aliasing a transaction.
/// </summary>
[TestFixture]
class SystemTransactionLifecycleTests : TestBase<SystemTransactionLifecycleTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static int IndexOf(TyphonRuntime runtime, string name)
    {
        var systems = runtime.Scheduler.Systems;
        for (var i = 0; i < systems.Length; i++)
        {
            if (systems[i].Name == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"no system {name}");
    }

    private static int CountSpawned(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<CompAArch>().Count();
    }

    /// <summary>A second end of one run finds the slot empty: it is counted and ends nothing, and the run's own commit landed once.</summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("RT-02")]
    public void ASystemEndedTwice_IsCounted_AndEndsNothingElse([Values(1, 2)] int workers)
    {
        using var dbe = SetupEngine();
        TyphonRuntime runtime = null;
        var spawns = 0;
        var secondEnds = 0;
        runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("A", ctx =>
            {
                ctx.Transaction.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1)));
                Interlocked.Increment(ref spawns);
            });
            dag.CallbackSystem("B", _ =>
            {
                runtime.EndSystemForTest(IndexOf(runtime, "A"), true);
                Interlocked.Increment(ref secondEnds);
            }, after: "A");
        }, new RuntimeOptions { WorkerCount = workers, BaseTickRate = 1000 });

        runtime.Start();
        Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref secondEnds) >= 3, TimeSpan.FromSeconds(5)), Is.True, "premise: the ticks ran");
        runtime.Dispose();

        Assert.That(runtime.DuplicateSystemEnds, Is.EqualTo(secondEnds), "every second end counted");
        Assert.That(CountSpawned(dbe), Is.EqualTo(spawns), "each run's commit landed once");
    }

    /// <summary>
    /// A start over a live run of the same system is refused before it touches the slot: the live run still commits, and nothing is counted twice. A refused
    /// start is not ended — the scheduler ends only a run whose start succeeded — so the live run's own end is the only one.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("RT-02")]
    public void ASystemStartedOverALiveRun_IsRefused_AndTheLiveRunCommits([Values(1, 2)] int workers)
    {
        using var dbe = SetupEngine();
        TyphonRuntime runtime = null;
        var spawns = 0;
        var refused = 0;
        runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("A", ctx =>
            {
                var self = IndexOf(runtime, "A");
                try
                {
                    runtime.StartSystemForTest(self, ctx.WorkerId);
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref refused);
                }

                ctx.Transaction.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1)));
                Interlocked.Increment(ref spawns);
            });
        }, new RuntimeOptions { WorkerCount = workers, BaseTickRate = 1000 });

        runtime.Start();
        Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref spawns) >= 3, TimeSpan.FromSeconds(5)), Is.True, "premise: the ticks ran");
        runtime.Dispose();

        Assert.That(refused, Is.EqualTo(spawns), "every start over a live run refused");
        Assert.That(runtime.DuplicateSystemEnds, Is.Zero, "the live run ended once");
        Assert.That(CountSpawned(dbe), Is.EqualTo(spawns), "every live run committed");
    }
}
