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

            // The window alone is not the hazard — this scope is. It marks the tree as under the fence's bulk value rewrite, the one path that descends with no
            // ValidateVersion, no write latch and no B-link right-walk. A foreign Add can split a leaf the rewrite's slice owns and relocate keys out from under
            // it, which is the collision this detector exists for.
            using var unlatched = tree.EnterUnlatchedRewrite();

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

            Assert.That(thrown, Is.InstanceOf<InvalidOperationException>(), "a foreign write into an unlatched rewrite must fail loudly, not corrupt silently");
            Assert.That(thrown.Message, Does.Contain("EW-01"));
            Assert.That(window.Violations, Is.EqualTo(1));
            Assert.That(window.FirstViolationSite, Is.EqualTo("BTree.Add"));
            Assert.That(window.FirstViolationThreadId, Is.Not.EqualTo(Environment.CurrentManagedThreadId), "the offender must be reported as the OTHER thread");
        });
    }

    /// <summary>
    /// The PRODUCTION bulk path arms the detector — not just a scope a test opened for itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every other case here arms the scope by hand, so the detector could be deleted from the engine and this fixture would stay
    /// green.</b> <see cref="ForeignThreadMutatingAnIndexInsideTheWindow_IsCaught"/> calls <c>tree.EnterUnlatchedRewrite()</c> from the
    /// test, and <c>LiveWorkload_TheFenceNeverSeesAForeignWriter</c> asserts <c>ObservedFenceMutation</c>, which
    /// <see cref="ExclusiveWindow.NoteMutation"/> sets unconditionally — independent of whether anything is armed. Removing the
    /// <c>EnterUnlatchedRewrite</c> from <c>BTree.RunBatch</c> was measured to leave all six cases passing while the detector went
    /// permanently silent. This case is the one that fails for that, because the only thing that can arm the tree here is the engine.
    /// </para>
    /// <para>
    /// <b>It cannot pass by luck.</b> A violation is reachable ONLY from inside the scope <c>RunBatch</c> opens, so if the engine stops
    /// arming, no iteration can ever throw and the case fails. The loop is there for the other direction — the foreign thread has to
    /// land inside a window the batch opens and closes — so it retries until it observes one rather than depending on a single
    /// interleaving. A large batch over the whole key range keeps that window wide.
    /// </para>
    /// </remarks>
    [Test]
    [RuleMutant("EW-01")]
    [CancelAfter(30_000)]
    public void TheProductionBulkPathArmsTheDetector()
    {
        OnFreshTree(static (tree, segment, epochManager) =>
        {
            var window = epochManager.FenceWindow;
            window.ResetCounters();

            using var open = window.Open();

            // Every key in the tree, so RunBatch descends the whole structure and the armed window is as wide as the real fence's.
            var updates = new BTreeValueUpdate<int>[TreeSize];
            for (var i = 0; i < TreeSize; i++)
            {
                updates[i] = new BTreeValueUpdate<int>(i, i + 1);
            }

            // The foreign thread hammers a LATCHED Add until the detector has rejected one of them. Its own exception is NOT the
            // observable: a race against an unlatched rewrite is exactly the corruption EW-01 exists to prevent, so the thread can
            // also see a UniqueConstraintViolationException from a tree this test has deliberately damaged. Everything is swallowed
            // and the window's own counter is what the case asserts on.
            var hammering = true;
            var foreign = new Thread(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var key = TreeSize + 1;
                    while (Volatile.Read(ref hammering))
                    {
                        var accessor = segment.CreateChunkAccessor();
                        try
                        {
                            tree.Add(key++, 1, ref accessor);
                        }
                        catch (Exception)
                        {
                            // Including the EW-01 refusal this case is here for. Counted by the window, not by us.
                        }
                        finally
                        {
                            accessor.Dispose();
                        }
                    }
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            })
            {
                IsBackground = true,
                Name = "ew01-arming-foreign",
            };

            foreign.Start();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (window.Violations == 0 && DateTime.UtcNow < deadline)
                {
                    var accessor = segment.CreateChunkAccessor();
                    try
                    {
                        tree.UpdateValues(updates, ref accessor);
                    }
                    catch (Exception)
                    {
                        // The batch can also trip over a tree the foreign thread is damaging. Not this case's business.
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }
            }
            finally
            {
                Volatile.Write(ref hammering, false);
                foreign.Join(TimeSpan.FromSeconds(10));
            }

            Assert.That(
                window.Violations,
                Is.GreaterThan(0),
                "no foreign write was ever rejected, so the engine's own bulk path never armed the detector");
            Assert.That(window.FirstViolationSite, Is.EqualTo("BTree.Add"));
            Assert.That(
                window.FirstViolationThreadId,
                Is.Not.EqualTo(Environment.CurrentManagedThreadId),
                "the offender must be reported as the OTHER thread");
        });
    }

    /// <summary>
    /// A foreign latched write to a tree the fence is NOT rewriting unlatched is legal, even with the window wide open.
    /// </summary>
    /// <remarks>
    /// <b>This is the false positive the detector used to produce, and it rejected the engine's own concurrency guarantee.</b> `NoteMutation` was handed a
    /// call-site string and nothing else, so it could only ask "is a fence running somewhere on this engine" — never "is the fence touching the tree I hold".
    /// Every latched mutator was armed, so an ordinary off-tick commit of a Versioned, non-spatial entity threw: a spawn writes the EntityMap and any indexed
    /// field writes a B+Tree, both through their full protocols, with the fence nowhere near them. Versioned, non-spatial data is concurrent by design and this
    /// pins that the detector agrees.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void ForeignThreadMutatingATreeTheFenceIsNotRewriting_IsFine()
    {
        OnFreshTree(static (tree, segment, epochManager) =>
        {
            var window = epochManager.FenceWindow;
            window.ResetCounters();

            using var open = window.Open();
            Assert.That(window.IsOpen, Is.True, "the window IS open — that alone must not reject a latched write");

            var thrown = OnForeignThread(epochManager, () =>
            {
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    for (var i = 0; i < 64; i++)
                    {
                        tree.Add(500_000 + i, i, ref accessor);
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(thrown, Is.Null, "a latched insert is safe against a fence that is not rewriting this tree");
                Assert.That(window.Violations, Is.Zero);
                Assert.That(window.FirstViolationSite, Is.Null);
            });
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
                        var entity = tx.OpenMut(live[i]);
                        var comp = entity.Read(EwUnit.C);
                        comp.Key = 500_000 + n * 512 + i;
                        entity.Set(EwUnit.C, comp);
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
