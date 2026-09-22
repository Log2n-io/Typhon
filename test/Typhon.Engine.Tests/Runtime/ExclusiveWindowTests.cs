using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Ew01.Unit", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct EwUnitComp
{
    /// <summary>Indexed, because an indexed field is what makes the tick fence write a B+Tree at all: a changed value lands in the shadow buffer that
    /// the fence's Prep phase drains into <c>Move</c> / <c>Remove</c>.</summary>
    [Index]
    public int Key;

    public int Payload;
}

[Archetype]
class EwUnit : Archetype<EwUnit>
{
    public static readonly Comp<EwUnitComp> C = Register<EwUnitComp>();
}

/// <summary>
/// <c>EW-01</c> — the tick fence runs with no concurrent mutation of the structures it maintains.
/// </summary>
/// <remarks>
/// <para>
/// This is the verifier step 3 of #872 deliberately deferred, and the reasoning it recorded is why it looks like this. CI requires a <c>[RuleMutant]</c>
/// beside any <c>[VerifiesRule]</c> — "a rule whose verifier cannot fail is worse than a rule with no verifier" — and a mutant needs a real detector. Two
/// cheaper proxies were tried and rejected then: <c>TransactionChain.ActiveCount</c> counts handles that exist rather than threads that are mutating, and
/// reddened 21 tests that legitimately hold a committed-or-idle transaction across the fence; and "no system runs concurrently with the fence" verifies the
/// half that was never in doubt, because that is what <c>TickEndCallback</c> means.
/// </para>
/// <para>
/// The honest detector asserts at the MUTATION sites, which is what <see cref="ExclusiveWindow"/> does, and what these tests exercise.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ExclusiveWindowTests : TestBase<ExclusiveWindowTests>
{
    private const int TreeSize = 2_000;

    private delegate void TreeAction(IntSingleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, EpochManager epochManager);

    /// <summary>A filled tree in a service scope of its own, plus the engine-scoped window that guards it.</summary>
    private static unsafe void OnFreshTree(TreeAction body)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                var raw = $"ew_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(1_024 * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        using var provider = serviceCollection.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        using var mpmmf = provider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = provider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var tree = new IntSingleBTree<PersistentStore>(segment);
            var accessor = segment.CreateChunkAccessor();
            try
            {
                for (var i = 1; i <= TreeSize; i++)
                {
                    tree.Add(i * 10, i, ref accessor);
                }
            }
            finally
            {
                accessor.Dispose();
            }

            body(tree, segment, epochManager);
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <summary>Runs <paramref name="work"/> on a thread of its own and returns whatever it threw, or <c>null</c>.</summary>
    /// <remarks>
    /// The scope is entered on the worker, not inherited: epoch scopes are per thread (<c>EP-01</c> pins pages for the life of the scope that took them),
    /// so a thread that opens a <c>ChunkAccessor</c> without one throws before it can reach the code under test — which reads as the guard firing when it is
    /// nothing of the kind.
    /// </remarks>
    private static Exception OnForeignThread(EpochManager epochManager, Action work)
    {
        Exception caught = null;
        var thread = new Thread(() =>
        {
            var depth = epochManager.EnterScope();
            try
            {
                work();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                epochManager.ExitScope(depth);
            }
        })
        {
            IsBackground = true,
            Name = "ew01-foreign",
        };

        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        return caught;
    }

    // ══════════════════════════════════════════════════════════════════════
    // The mutant — the detector must be able to fail
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [RuleMutant("EW-01")]
    [CancelAfter(15_000)]
    public void ForeignThreadMutatingAnIndexInsideTheWindow_IsCaught()
    {
        OnFreshTree(static (tree, segment, epochManager) =>
        {
            var window = epochManager.FenceWindow;
            window.ResetCounters();

            using var open = window.Open();
            Assert.That(window.IsOpen, Is.True);

            var thrown = OnForeignThread(epochManager, () =>
            {
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    tree.Add(999_999, 1, ref accessor);
                }
                finally
                {
                    accessor.Dispose();
                }
            });

            Assert.That(thrown, Is.InstanceOf<InvalidOperationException>(), "a foreign write inside the window must fail loudly, not corrupt silently");
            Assert.That(thrown.Message, Does.Contain("EW-01"));
            Assert.That(window.Violations, Is.EqualTo(1));
            Assert.That(window.FirstViolationSite, Is.EqualTo("BTree.Add"));
            Assert.That(window.FirstViolationThreadId, Is.Not.EqualTo(Environment.CurrentManagedThreadId), "the offender must be reported as the OTHER thread");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void ForeignThreadMutatingAnIndexOutsideTheWindow_IsFine()
    {
        // The other half of the mutant: the guard must be inert when no fence is running, or every ordinary concurrent write in the engine would throw.
        OnFreshTree(static (tree, segment, epochManager) =>
        {
            var window = epochManager.FenceWindow;
            window.ResetCounters();
            Assert.That(window.IsOpen, Is.False);

            var thrown = OnForeignThread(epochManager, () =>
            {
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    tree.Add(999_999, 1, ref accessor);
                }
                finally
                {
                    accessor.Dispose();
                }
            });

            Assert.That(thrown, Is.Null, thrown?.ToString());
            Assert.That(window.Violations, Is.Zero);
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void ForeignThreadREADINGInsideTheWindow_IsNotAViolation()
    {
        // EW-01 forbids concurrent MUTATION. A detector that also indicted readers would be the ActiveCount mistake again in a new costume — it would redden
        // every fixture that holds a long-lived read transaction across a tick, which is the documented way to own a pull View.
        OnFreshTree(static (tree, segment, epochManager) =>
        {
            var window = epochManager.FenceWindow;
            window.ResetCounters();
            using var open = window.Open();

            var found = 0;
            var thrown = OnForeignThread(epochManager, () =>
            {
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    for (var i = 1; i <= 200; i++)
                    {
                        if (tree.TryGet(i * 10, ref accessor).Value == i)
                        {
                            found++;
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            });

            Assert.That(thrown, Is.Null, thrown?.ToString());
            Assert.That(found, Is.EqualTo(200), "the reads must actually have happened, or this proves nothing about readers");
            Assert.That(window.Violations, Is.Zero, "a reader is not a violation");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void TheFenceThreadsOwnMutationsAreNotViolations()
    {
        // The thread that opens the window is doing the fence's serial work, so its writes are the legal ones. If this were not so, the guard would fire on
        // the fence's own shadow drain the first time it ran.
        OnFreshTree(static (tree, segment, epochManager) =>
        {
            var window = epochManager.FenceWindow;
            window.ResetCounters();

            using (window.Open())
            {
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    tree.Add(999_999, 1, ref accessor);
                }
                finally
                {
                    accessor.Dispose();
                }
            }

            Assert.That(window.Violations, Is.Zero);
            Assert.That(window.ObservedFenceMutation, Is.True, "the fence's own write must be observed, or the vacuity guard is itself vacuous");
            Assert.That(window.IsOpen, Is.False, "the scope must close the window it opened");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-6.1 — the live workload
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("EW-01")]
    [CancelAfter(15_000)]
    public void LiveWorkload_TheFenceNeverSeesAForeignWriter()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EwUnitComp>();
        dbe.InitializeArchetypes();

        var live = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 256; i++)
            {
                live.Add(tx.Spawn<EwUnit>(EwUnit.C.Set(new EwUnitComp { Key = i * 7 + 1, Payload = i })));
            }

            tx.Commit();
        }

        var window = dbe.EpochManager.FenceWindow;
        window.ResetCounters();

        var ticks = 0;
        var churn = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Ew01");

            // Spawn, destroy and rewrite the INDEXED field every tick. The rewrite is what puts entries in the shadow buffer the fence then drains into
            // B+Tree Move/Remove — a workload that only spawned would leave the fence with nothing to do on an index and the whole assertion vacuous.
            dag.CallbackSystem("Churn", ctx =>
            {
                var n = Interlocked.Increment(ref churn);
                var tx = ctx.Transaction;

                for (var i = 0; i < 8; i++)
                {
                    tx.Spawn<EwUnit>(EwUnit.C.Set(new EwUnitComp { Key = 100_000 + n * 64 + i, Payload = n }));
                }

                lock (live)
                {
                    for (var i = 0; i < 16 && i < live.Count; i++)
                    {
                        ref var comp = ref tx.OpenMut(live[i]).Write(EwUnit.C);
                        comp.Key = 500_000 + n * 512 + i;
                    }

                    if (live.Count > 32)
                    {
                        for (var i = 0; i < 4; i++)
                        {
                            tx.Destroy(live[^1]);
                            live.RemoveAt(live.Count - 1);
                        }
                    }
                }
            });

            dag.CallbackSystem("Count", _ => Interlocked.Increment(ref ticks), after: "Churn");
        }, new RuntimeOptions { WorkerCount = 4, BaseTickRate = 500 });

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 6, TimeSpan.FromSeconds(10));
        runtime.Shutdown();

        Assert.That(ticks, Is.GreaterThanOrEqualTo(6), "the workload must actually have ticked");

        // The vacuity guard comes FIRST on purpose. "Zero foreign writers" is also what an engine whose fence never opened, or never wrote an index inside
        // the window, would report — and that is the shape a future refactor would silently produce.
        Assert.That(window.ObservedFenceMutation, Is.True,
            "the fence never mutated a guarded structure, so a zero violation count would say nothing about EW-01");
        Assert.That(window.Violations, Is.Zero,
            $"EW-01 violated at {window.FirstViolationSite} from thread {window.FirstViolationThreadId}");
    }
}
