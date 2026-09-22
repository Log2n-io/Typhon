using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-12.4</c> — a repair produces the identical packing under the serial fence and under the parallel fence at
/// <c>W ∈ {1, 2, 8}</c> (#872 step 12).
/// </summary>
/// <remarks>
/// <para><b>What could make it differ, and why it does not.</b> The repair has one parallel stage and two serial ones.
/// NOMINATION runs on AabbRefresh workers, so which worker appends a given cell key, and in what order, is scheduling —
/// but the output is consumed as a SET: the planner sorts the nominations and skips repeats, so any permutation of the
/// same cells yields the same iteration. PLANNING runs in Prep, one work item per archetype, so the ranking, the Morton
/// sort and the destination assignment are single-threaded by contract. EXECUTION is sliced across workers, and it is
/// the reason <c>MigrationRequest</c> gained a pinned SLOT in this step: with only the cluster pinned, two workers
/// claiming into one fresh cluster would fill it first-fit in whatever order they arrived, and the sorted packing would
/// survive only when a slice boundary happened not to fall inside a cell's run.</para>
/// <para><b>Compared as the packing, not as chunk ids.</b> Which chunk id a destination gets depends on the segment's
/// free list, which depends on the whole allocation history of the run. The result that matters — and the one the design
/// means by "identical" — is which entities share a cluster and in what slot order, so the canonical form here is the
/// per-cluster tag sequence, with clusters ordered by their own first tag. Two runs that agree on that agree on every
/// bound, every zone map and every query answer.</para>
/// <para><b>Driven by a real <see cref="TyphonRuntime"/> for the parallel arms.</b> <c>ClusterMigrationTests</c> records
/// what happens otherwise: ablating the parallel EntityMap phase left 5 692 tests green, because nearly every fixture
/// drives <c>WriteTickFence</c> — the serial drain — under the name of the parallel one.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterRepairParallelTests : TestBase<ClusterRepairParallelTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _reference = null;
        _referenceArm = null;
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;
    private const int Population = 2000;

    /// <summary>The <c>workerCount</c> value that selects the serial <c>WriteTickFence</c> arm rather than a runtime.</summary>
    private const int SerialArm = 0;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            clusterRepairExtentRatio: 0.75f,
            reclusterBudgetMs: 100f, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            repairWorstClustersPerUnit: 0));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawn a cell whose first-fit layout is geometrically scrambled — see <c>ClusterRepairTests</c>.</summary>
    private static void SpawnDegradedCell(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < Population; i++)
        {
            var x = 4f + ((i * 37) % 92) + ((i / 92) % 4) * 0.2f;
            var y = 4f + ((i * 61) % 92) + ((i / 92) % 4) * 0.2f;
            tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, i)));
        }

        tx.Commit();
    }

    /// <summary>
    /// The packing as a canonical string: one line per cluster, holding its tags in ascending slot order, with the lines
    /// sorted by their own first tag.
    /// </summary>
    /// <remarks>
    /// Sorting the LINES rather than keying them on chunk id is what makes this invariant to allocation order while
    /// staying sensitive to everything else — membership, slot order, and cluster count all change the string. Comparing
    /// strings rather than nested collections keeps the failure message readable: a diff points straight at the cluster
    /// that came out different.
    /// </remarks>
    private static unsafe List<string> SnapshotPacking(DatabaseEngine dbe)
    {
        var lines = new List<string>();
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read-only.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                var tags = new List<int>();
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    tags.Add(positions[slot].Tag);
                }

                if (tags.Count > 0)
                {
                    lines.Add(string.Join(",", tags));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        lines.Sort(static (a, b) =>
        {
            var ai = int.Parse(a.AsSpan(0, a.IndexOf(',') < 0 ? a.Length : a.IndexOf(',')));
            var bi = int.Parse(b.AsSpan(0, b.IndexOf(',') < 0 ? b.Length : b.IndexOf(',')));
            return ai != bi ? ai.CompareTo(bi) : string.CompareOrdinal(a, b);
        });

        return lines;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("RP-02")]
    [CancelAfter(60_000)]
    public void ARepairProducesTheSamePacking_WhicheverFenceRunsIt([Values(SerialArm, 1, 2, 8)] int workerCount)
    {
        using var dbe = SetupEngine();
        SpawnDegradedCell(dbe);
        dbe.WriteTickFence(1);

        var before = SnapshotPacking(dbe);

        if (workerCount == SerialArm)
        {
            // Tick 2 nominates and plans, tick 3 settles the bounds. Four is comfortably past both and idempotent —
            // the planner refuses a cell already packed in sort order.
            for (var tick = 2; tick <= 5; tick++)
            {
                dbe.WriteTickFence(tick);
            }
        }
        else
        {
            RunParallel(dbe, workerCount);
        }

        var after = SnapshotPacking(dbe);
        var arm = workerCount == SerialArm ? "the serial WriteTickFence" : $"the parallel fence at W={workerCount}";

        Assert.That(after, Is.Not.EqualTo(before), $"{arm} did not repack anything, so the comparison below proves nothing");
        Assert.That(after, Has.Count.GreaterThan(1), $"{arm} left one cluster, so a packing comparison would be trivially satisfied");

        // ── Cross-arm equality, and here that IS the criterion ──────────────────────────────────────────────────────
        //
        // The neighbouring drift fixture routes every arm through an independent oracle rather than comparing arms, and
        // gives the reason: two arms sharing every line of the rule go green together when the rule is wrong. That
        // argument does not transfer. AC-12.4 asks whether the RESULT depends on worker count, so agreement between the
        // arms is the property itself rather than a proxy for it — and an oracle here would have to re-derive the cluster
        // ranking, the Morton sort and the packing, at which point it is a second implementation of the planner and
        // agreement with it says nothing about scheduling.
        //
        // What keeps it from being vacuous is the pair of assertions above: an arm that repacked NOTHING, or collapsed
        // the cell to one cluster, fails before it can agree with anyone. Quality is asserted elsewhere — ClusterRepairTests
        // measures the mean extent against the theoretical optimum, so "all arms are wrong together" is not a way through.
        // ── The reference is whichever arm ran first, and the residual is stated rather than papered over ─────────────
        //
        // A separate reference engine was tried and cannot work here: the test harness gives each TEST one database name,
        // so a second engine opened while the arm's is still alive throws DatabaseLockedException, and one opened after it
        // closes would read the repaired database rather than re-derive the answer. So the first arm to execute records
        // the packing and the rest are compared against it.
        //
        // What that costs: exactly one of the four arms does not run the equality assertion — unavoidable, since the first
        // arm has nothing to compare against — and NUnit chooses which. What keeps the fixture honest is that the choice
        // cannot change the verdict. Equality is symmetric and transitive, so "all four agree" is proved by any three
        // comparisons against a common member, whichever member that is; and every arm, reference included, must first
        // pass the two assertions above showing it actually repacked into more than one cluster. An arm that did nothing
        // fails before it can agree with anyone.
        lock (ReferenceLock)
        {
            _reference ??= after;
            _referenceArm ??= arm;
        }

        Assert.That(after, Is.EqualTo(_reference), $"{arm} produced a different packing from {_referenceArm}");
    }

    /// <summary>The packing recorded by the first arm of this fixture run; every later arm is compared against it.</summary>
    /// <remarks>
    /// Static so it survives NUnit's per-test instance, and cleared in <c>OneTimeSetUp</c> so it spans exactly one fixture
    /// run. See the comment at the assertion for why an independently computed reference is not available here.
    /// </remarks>
    private static List<string> _reference;

    /// <inheritdoc cref="_reference"/>
    private static string _referenceArm;

    private static readonly object ReferenceLock = new();

    private static void RunParallel(DatabaseEngine dbe, int workerCount)
    {
        var ticks = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Sample", _ => Interlocked.Increment(ref ticks));
            }, new RuntimeOptions
            {
                WorkerCount = workerCount,
                // 100 Hz for the reason ClusterDriftParallelTests records: at 1000 Hz the fence overruns its tick and
                // disposes accessors under the next tick's feet, which fails for reasons unrelated to what is asserted.
                BaseTickRate = 100,
                EnableParallelFence = true,
            });

        Exception unhandled = null;
        runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 6, TimeSpan.FromSeconds(15));
        runtime.Shutdown();

        Assert.That(unhandled, Is.Null, $"the parallel fence must not throw while repairing. Got: {unhandled}");
        Assert.That(ticks, Is.GreaterThanOrEqualTo(6), "the runtime must actually have ticked, or nothing was measured");
    }
}
