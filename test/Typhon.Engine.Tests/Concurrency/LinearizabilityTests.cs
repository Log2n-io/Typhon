using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── The model's engine-side archetype: flat, Versioned, non-indexed. No index, on purpose — a unique index would make two threads spawning the same key
//    throw, which is a different (and already covered) property, and would mask the silent losses this fixture is looking for. ──

[Component("Typhon.Schema.UnitTest.LinValue", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct LinValue
{
    public int V;

    /// <summary>Padding. A chunk-based segment requires a stride of at least 8 bytes, and the size is derived from PUBLIC fields only.</summary>
    public int Pad;

    public LinValue(int v)
    {
        V = v;
        Pad = 0;
    }
}

[Archetype]
internal class LinValueArch : Archetype<LinValueArch>
{
    public static readonly Comp<LinValue> C = Register<LinValue>();
}

/// <summary>
/// The live half of the model-based check: one engine, plus the shared <see cref="UnitOfWork"/> whose transactions the parallel operations run on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deferred, and one shared UoW, on purpose.</b> Production defaults to <see cref="DurabilityMode.Deferred"/> (<c>DatabaseEngine.cs:841</c>), and it is
/// the mode where the UnitOfWork owns ONE <c>ChangeSet</c> shared by every transaction it creates (<c>UnitOfWork.cs:64-66</c>) — the structure #400 is about.
/// The suite references <c>Immediate</c> 188 times against <c>Deferred</c>'s 48, and <c>Immediate</c> gives each transaction its own ChangeSet, so it is the
/// one mode that CANNOT exhibit the defect. This fixture exists to stop selecting the safe mode.
/// </para>
/// <para>
/// <b>Disposal is the fixture's job.</b> CsCheck's initial-state generator is a <c>Gen&lt;(T,M)&gt;</c> with no disposal hook, so an engine created per
/// iteration is leaked unless someone tracks it. Every instance registers itself in <see cref="LinearizabilityTests.Track"/> and teardown disposes the lot.
/// </para>
/// </remarks>
internal sealed class EngineState : IDisposable
{
    private static int _instanceCounter;

    private readonly ServiceProvider _services;
    private readonly UnitOfWork _uow;

    /// <remarks>
    /// Builds its OWN <see cref="ServiceProvider"/> with a unique database name rather than taking a scope off the fixture's. CsCheck keeps every iteration's
    /// initial state alive until the sample ends, so twenty scopes off one provider means twenty simultaneously-open engines pointed at the SAME file — which
    /// fails at the Virtual Disk Manager, long before any operation runs, and looks like a linearizability failure in the output.
    /// </remarks>
    public EngineState()
    {
        var id = System.Threading.Interlocked.Increment(ref _instanceCounter);
        var name = $"Lin_{id}";
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Typhon.Tests", nameof(LinearizabilityTests), name);
        System.IO.Directory.CreateDirectory(root);
        Root = root;

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = name;
                opts.DatabaseDirectory = root;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = System.IO.Path.Combine(root, "wal"),
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        _services = services.BuildServiceProvider();
        try
        {
            _services.EnsureFileDeleted<ManagedPagedMMFOptions>();
            Engine = _services.GetRequiredService<DatabaseEngine>();
            Engine.RegisterComponentFromAccessor<LinValue>();
            Engine.InitializeArchetypes();
            _uow = Engine.CreateUnitOfWork(DurabilityMode.Deferred);
        }
        catch
        {
            // The fixture's tracking list only receives a FULLY constructed instance, so a throw here would otherwise leak the provider — and the leak would be
            // invisible, because teardown reports the count it disposed and that count would simply be lower.
            _services.Dispose();
            throw;
        }
    }

    /// <summary>The instance's own temp directory, removed at teardown.</summary>
    public string Root { get; }

    public DatabaseEngine Engine { get; }

    /// <summary>Spawn one entity carrying <paramref name="value"/>.</summary>
    public void Spawn(int value)
    {
        using var tx = _uow.CreateTransaction();
        tx.Spawn<LinValueArch>(LinValueArch.C.Set(new LinValue(value)));
        tx.Commit();
    }

    /// <summary>Spawn, commit, then update in a SECOND transaction — the post-commit write path, not the spawn-init one.</summary>
    public void SpawnThenUpdate(int value)
    {
        EntityId id;
        using (var tx = _uow.CreateTransaction())
        {
            id = tx.Spawn<LinValueArch>(LinValueArch.C.Set(new LinValue(value)));
            tx.Commit();
        }

        using (var tx = _uow.CreateTransaction())
        {
            tx.OpenMut(id).Write(LinValueArch.C).V = value + EcsModel.UpdateOffset;
            tx.Commit();
        }
    }

    /// <summary>Spawn, commit, then destroy — net nothing live, but real storage churn and a real tombstone.</summary>
    public void SpawnAndDestroy(int value)
    {
        EntityId id;
        using (var tx = _uow.CreateTransaction())
        {
            id = tx.Spawn<LinValueArch>(LinValueArch.C.Set(new LinValue(value)));
            tx.Commit();
        }

        using (var tx = _uow.CreateTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }
    }

    /// <summary>A read-only scan racing the writers. Its RESULT is not asserted — a mid-run count has no single right answer under concurrency; what is
    /// asserted is that it neither throws nor corrupts what the writers are doing.</summary>
    public void Query()
    {
        using var tx = Engine.CreateReadOnlyTransaction();
        tx.Query<LinValueArch>().Count();
    }

    /// <summary>The live values, sorted — the same canonical form <see cref="EcsModel.Sorted"/> produces.</summary>
    public int[] Sorted()
    {
        var values = new List<int>();
        using (var tx = Engine.CreateReadOnlyTransaction())
        {
            foreach (var id in tx.Query<LinValueArch>().Execute())
            {
                values.Add(tx.Open(id).Read(LinValueArch.C).V);
            }
        }

        var copy = values.ToArray();
        Array.Sort(copy);
        return copy;
    }

    public void Dispose()
    {
        _uow?.Dispose();
        _services?.Dispose();
        try
        {
            if (System.IO.Directory.Exists(Root))
            {
                System.IO.Directory.Delete(Root, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

/// <summary>
/// Model-based / linearizability testing on the DEFAULT runtime configuration (#705 T5, design §5.4).
/// </summary>
/// <remarks>
/// <para>
/// Random operation sequences run partly sequentially and partly in parallel; the observed final state must be consistent with SOME legal serialisation.
/// <c>Check.SampleParallel&lt;Actual, Model&gt;</c> replays the candidate linearizations on the MODEL, not on the engine, so one engine is constructed per
/// iteration rather than per permutation — that is what makes this affordable enough to run in the PR gate at all.
/// </para>
/// <para>
/// <b>A green run here means very little on its own</b>, which is why <c>scripts/linearizability-probe.py</c> exists: it plants a known race and requires this
/// model to detect it within a bounded number of seeds. §9 of the test strategy states the trap directly — "audit the model against a known-racy build before
/// believing it" — because the most likely explanation for a model that finds nothing is that the model is too weak.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Seeded")]
[Seeded]
internal sealed class LinearizabilityTests : TestBase<LinearizabilityTests>
{
    // Gate budget. SampleParallel constructs one engine per iteration; at ~15 ms each, 20 iterations is well under a second of the gate's time. The nightly
    // cell below raises both this and the operation width, which is where the seed-hours actually buy coverage.
    private const int GateIterations = 20;
    private const int GateThreads = 2;
    private const int GateParallelOperations = 3;

    private readonly List<EngineState> _created = [];

    private EngineState Track(EngineState state)
    {
        lock (_created)
        {
            _created.Add(state);
        }

        return state;
    }

    [TearDown]
    public void DisposeEngines()
    {
        lock (_created)
        {
            foreach (var s in _created)
            {
                try
                {
                    s.Dispose();
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine($"engine dispose threw during teardown: {ex.GetType().Name}: {ex.Message}");
                }
            }

            TestContext.WriteLine($"disposed {_created.Count} engine(s) created by this test");
            _created.Clear();
        }
    }

    private Gen<(EngineState, EcsModel)> Initial =>
        Gen.Const(0).Select(_ => (Track(new EngineState()), new EcsModel()));

    private static readonly Gen<int> GenValue = Gen.Int[1, 1000];

    private static GenOperation<EngineState, EcsModel> SpawnOp =>
        GenValue.Operation<EngineState, EcsModel>(v => $"Spawn({v})", (a, v) => a.Spawn(v), (m, v) => m.Spawn(v));

    private static GenOperation<EngineState, EcsModel> SpawnThenUpdateOp =>
        GenValue.Operation<EngineState, EcsModel>(v => $"SpawnThenUpdate({v})", (a, v) => a.SpawnThenUpdate(v), (m, v) => m.SpawnThenUpdate(v));

    private static GenOperation<EngineState, EcsModel> SpawnAndDestroyOp =>
        GenValue.Operation<EngineState, EcsModel>(v => $"SpawnAndDestroy({v})", (a, v) => a.SpawnAndDestroy(v), (m, v) => m.SpawnAndDestroy(v));

    private static GenOperation<EngineState, EcsModel> QueryOp =>
        GenValue.Operation<EngineState, EcsModel>(_ => "Query()", (a, _) => a.Query(), (m, _) => m.Query());

    /// <summary>Actual ≡ model: the same multiset of live values, in canonical (sorted) form.</summary>
    private static bool Equal(EngineState actual, EcsModel model)
    {
        var a = actual.Sorted();
        var m = model.Sorted();
        if (a.Length != m.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != m[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string Print(EngineState actual) => $"live=[{string.Join(",", actual.Sorted())}]";

    private static string Print(EcsModel model) => $"live=[{string.Join(",", model.Sorted())}]";

    /// <summary>
    /// Operations run partly in parallel on the production default configuration; the result must match some legal serialisation.
    /// </summary>
    /// <remarks>
    /// <b>Quarantined against #400</b>, which this fixture reproduces on its FIRST run: the shared <c>ChangeSet</c>'s concurrent-mutation guard fires within
    /// milliseconds under the default configuration. That is the T5 result, not an obstacle to it — the model was pointed at the production default precisely
    /// because the suite avoids it 4:1, and the first thing it found was the P0 living there. It cannot go green until #400 is fixed, and forcing it green
    /// (by moving to Immediate, or by removing the guard) would restore the selection bias this whole exercise exists to remove.
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    [Category("Quarantine")]
    public void ParallelOperations_AreLinearizable()
        => Check.SampleParallel(
            Initial,
            [SpawnOp, SpawnThenUpdateOp, SpawnAndDestroyOp, QueryOp],
            Equal,
            // No `seed:` — CsCheck seeds are its OWN string format (SeedString.Parse), not an integer, and it prints the failing seed itself on a
            // counterexample. Forcing TestSeed.RunSeed in here throws IndexOutOfRange before a single operation runs.
            maxParallelOperations: GateParallelOperations,
            iter: GateIterations,
            threads: GateThreads,
            printActual: Print,
            printModel: Print);

    /// <summary>
    /// The same check, wider and longer — the cell where coverage grows with CI-hours (#705 T5 / T6).
    /// </summary>
    [Test]
    [CancelAfter(600_000)]
    [Explicit("Deep linearizability search — minutes; the gate runs the bounded form")]
    [Category("Nightly")]
    [Category("Quarantine")]
    public void ParallelOperations_AreLinearizable_Deep()
        => Check.SampleParallel(
            Initial,
            [SpawnOp, SpawnThenUpdateOp, SpawnAndDestroyOp, QueryOp],
            Equal,
            maxParallelOperations: 5,
            iter: 200,
            threads: 4,
            printActual: Print,
            printModel: Print);

    /// <summary>
    /// The model must REJECT a divergent engine — proven by corrupting the comparison input rather than trusting a green sample.
    /// </summary>
    /// <remarks>
    /// <see cref="Equal"/> is the entire verdict of every case above. If it could not distinguish two different populations, every one of them would be
    /// permanently green while proving nothing — the W1 class this epic is about, in the single most load-bearing function of the fixture.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void EqualityCheck_RejectsADivergentPopulation()
    {
        var state = Track(new EngineState());
        var model = new EcsModel();

        state.Spawn(7);
        model.Spawn(7);
        Assert.That(Equal(state, model), Is.True, "sanity: one spawn on each side must agree");

        // One extra entity in the engine that the model does not know about — a duplicate, which is what a re-issued slot looks like (#708).
        state.Spawn(7);
        Assert.That(Equal(state, model), Is.False, "a duplicated entity must be rejected");

        model.Spawn(7);
        Assert.That(Equal(state, model), Is.True, "sanity: the model catching up must restore agreement");

        // A value the engine does not hold — what a lost write looks like (#400).
        model.Spawn(99);
        Assert.That(Equal(state, model), Is.False, "a missing entity must be rejected");
    }

    /// <summary>
    /// <c>DirtyCounter</c> conservation at quiesce: after the workload settles, the page cache's dirty accounting must be balanced.
    /// </summary>
    /// <remarks>
    /// <b>Asserted as an invariant, NOT as the race detector.</b> #705 records the measurement directly: residue is flat across 1/2/4/8 threads, so a non-zero
    /// value does not discriminate a racy build from a clean one and treating it as a signal would produce confident nonsense in both directions. What it can
    /// do is catch an accounting bug — a mark registered and never released — which is a real defect class of its own (#385 lived here) and is otherwise
    /// unobserved after a concurrent workload.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Category("Quarantine")]
    public void DirtyCounter_IsConservedAtQuiesce()
    {
        var state = Track(new EngineState());

        System.Threading.Tasks.Parallel.For(0, 4, t =>
        {
            for (var i = 0; i < 25; i++)
            {
                state.Spawn((t * 1000) + i);
            }
        });

        state.Engine.ForceCheckpoint();
        state.Engine.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(20));

        var report = state.Engine.RunStorageIntegrityCheck();
        foreach (var issue in report.Issues)
        {
            TestContext.WriteLine($"ISSUE {issue.Kind}: {issue.Detail}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(report.OrphanPageCount, Is.EqualTo(0), "a page owned by nobody after quiesce means the allocator and the occupancy bitmap disagree");
            Assert.That(report.PhantomPageCount, Is.EqualTo(0), "a live page with no occupancy bit is a double-allocation waiting to happen");
        });
    }
}
