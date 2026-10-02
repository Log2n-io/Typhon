using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema

/// <summary>No indexed field — the baseline every other arm is differenced against.</summary>
[Component("Typhon.Test.SpCost.Plain", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpPlainData
{
    public int CellId;
    public int SpawnKey;
    public int Payload;

    public SpPlainData(int cellId, int spawnKey, int payload)
    {
        CellId = cellId;
        SpawnKey = spawnKey;
        Payload = payload;
    }
}

/// <summary>One indexed field, <c>AllowMultiple</c> — the shape a game puts on a mob's cell or lair.</summary>
[Component("Typhon.Test.SpCost.OneIdx", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpOneIdxData
{
    [Index(AllowMultiple = true)] public int CellId;
    public int SpawnKey;
    public int Payload;

    public SpOneIdxData(int cellId, int spawnKey, int payload)
    {
        CellId = cellId;
        SpawnKey = spawnKey;
        Payload = payload;
    }
}

/// <summary>Two indexed fields, one multi and one unique — the shape the #1098 burst measured.</summary>
[Component("Typhon.Test.SpCost.TwoIdx", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpTwoIdxData
{
    [Index(AllowMultiple = true)] public int CellId;
    [Index] public int SpawnKey;
    public int Payload;

    public SpTwoIdxData(int cellId, int spawnKey, int payload)
    {
        CellId = cellId;
        SpawnKey = spawnKey;
        Payload = payload;
    }
}

/// <summary>
/// One indexed field, UNIQUE — the arm that separates a B+Tree insert from an <c>AllowMultiple</c> buffer append.
/// </summary>
/// <remarks>
/// Without this the two cannot be told apart: the one-index arm indexes a 4-distinct-value field with <c>AllowMultiple</c>, so its cost could be the tree
/// or the per-key VSBS buffer that 750 entities share. #1098 counted ZERO full leaves for that shape, which makes the tree an unlikely suspect and the
/// buffer a likely one — but counting leaves does not measure a buffer, so it has to be measured.
/// </remarks>
[Component("Typhon.Test.SpCost.UniqIdx", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpUniqIdxData
{
    public int CellId;
    [Index] public int SpawnKey;
    public int Payload;

    public SpUniqIdxData(int cellId, int spawnKey, int payload)
    {
        CellId = cellId;
        SpawnKey = spawnKey;
        Payload = payload;
    }
}

[Archetype]
class SpUniqIdxMob : Archetype<SpUniqIdxMob>
{
    public static readonly Comp<SpUniqIdxData> Data = Register<SpUniqIdxData>();
}

[Archetype]
class SpPlainMob : Archetype<SpPlainMob>
{
    public static readonly Comp<SpPlainData> Data = Register<SpPlainData>();
}

[Archetype]
class SpOneIdxMob : Archetype<SpOneIdxMob>
{
    public static readonly Comp<SpOneIdxData> Data = Register<SpOneIdxData>();
}

[Archetype]
class SpTwoIdxMob : Archetype<SpTwoIdxMob>
{
    public static readonly Comp<SpTwoIdxData> Data = Register<SpTwoIdxData>();
}

#endregion

/// <summary>
/// Where the ~4.6 us a spawn costs actually goes, split by phase and by schema, and what the batch API changes.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>[Explicit]</c>, so the gate never runs it.</b> Run one line and read the table:
/// <c>dotnet test -c Release --filter "FullyQualifiedName~SpawnCostSplitTests"</c>. Add
/// <c>$env:TYPHON__CHECKS__ENABLED = 'false'</c> first to see the configuration the Release NuGet actually ships.
/// </para>
/// <para>
/// <b>Two things make the headline 4.6 us an overstatement of shipped cost, and both are controlled here rather than argued.</b> This suite turns strict
/// mode ON for every fixture through its <c>typhon.telemetry.json</c> — that is the whole reason the merge gate has a <c>ChecksOffGated</c> pass — so every
/// <c>CheckConfig.Require</c> in the spawn path is live, which it is not in a Release NuGet. And the first measurement used <c>Spawn</c> in a loop, the
/// per-entity API, when <c>SpawnBatchAllocate</c> plus <c>SpawnBatchWriteAll</c> exists precisely to amortise the per-call work.
/// </para>
/// <para>
/// <b>The split is by DIFFERENCE, not by instrumentation</b>, because adding timers inside the spawn path would change what is being timed. Three schemas
/// differing only in their indexed fields give the index's share as a subtraction, and the one-index arm sitting between the other two is what says the
/// relationship is linear enough for the subtraction to mean anything. The phase split — staging, commit, fence — comes from reading the clock at the two
/// boundaries that already exist.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("wall-clock measurement; run on demand, never in the gate")]
// Manual and not Nightly: a wall-clock figure compared against nothing is not a gate signal, and the nightly would record a number no one
// reads. It exists to be run by hand when a cost claim is in question, and to leave its method written down -- three harnesses got this
// measurement wrong before one got it right.
[Category("Manual")]
[NonParallelizable]
class SpawnCostSplitTests : TestBase<SpawnCostSplitTests>
{
    private const int Burst = 3000;

    private static int[] Keys(bool ascending)
    {
        var keys = new int[Burst];
        for (var i = 0; i < Burst; i++)
        {
            keys[i] = i;
        }

        if (!ascending)
        {
            var rng = new Random(1102);
            for (var i = Burst - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (keys[i], keys[j]) = (keys[j], keys[i]);
            }
        }

        return keys;
    }

    /// <summary>A throwaway burst on a disjoint key range, so the measured burst runs against a warm JIT and a non-empty tree.</summary>
    private static void Warm(DatabaseEngine dbe, int indexedFields)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < 200; i++)
        {
            var k = 1_000_000 + i;
            switch (indexedFields)
            {
                case 0:
                    tx.Spawn<SpPlainMob>(SpPlainMob.Data.Set(new SpPlainData(i % 4, k, i)));
                    break;
                case 1:
                    tx.Spawn<SpOneIdxMob>(SpOneIdxMob.Data.Set(new SpOneIdxData(i % 4, k, i)));
                    break;
                case 3:
                    tx.Spawn<SpUniqIdxMob>(SpUniqIdxMob.Data.Set(new SpUniqIdxData(i % 4, k, i)));
                    break;
                default:
                    tx.Spawn<SpTwoIdxMob>(SpTwoIdxMob.Data.Set(new SpTwoIdxData(i % 4, k, i)));
                    break;
            }
        }

        tx.Commit();
    }

    private static void Report(string label, double stagingMs, double commitMs, double fenceMs)
    {
        var total = stagingMs + commitMs + fenceMs;
        TestContext.Out.WriteLine($"SPLIT {label,-28} staging {stagingMs,7:F2}  commit {commitMs,7:F2}  fence {fenceMs,6:F2}  total {total,7:F2} ms"
            + $"  = {total * 1000 / Burst,6:F2} us/spawn");
    }

    /// <summary>Per-entity <c>Spawn</c>, which is what the first measurement used.</summary>
    /// <param name="indexedFields">0 = none, 1 = one AllowMultiple, 2 = one multi plus one unique, 3 = one UNIQUE only.</param>
    /// <param name="ascending">Whether the unique index's keys arrive in order.</param>
    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(2, true)]
    [TestCase(3, false)]
    [TestCase(3, true)]
    public void PerEntitySpawn(int indexedFields, bool ascending)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpPlainData>();
        dbe.RegisterComponentFromAccessor<SpOneIdxData>();
        dbe.RegisterComponentFromAccessor<SpTwoIdxData>();
        dbe.RegisterComponentFromAccessor<SpUniqIdxData>();
        dbe.InitializeArchetypes();

        // A DISCARDED warm-up burst in the same engine, on its own key range. Without it the first TestCase through a given generic instantiation pays JIT
        // and first-touch for the whole Spawn<TArch> path and the next case runs warm — which is what produced a 12 ms "staging" figure for ascending keys
        // against 1.5 ms for shuffled, an 8x gap in a phase that does not touch the index at all. Declaration order, not the engine.
        Warm(dbe, indexedFields);

        var keys = Keys(ascending);
        var sw = Stopwatch.StartNew();
        double stagingMs;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Burst; i++)
            {
                switch (indexedFields)
                {
                    case 0:
                        tx.Spawn<SpPlainMob>(SpPlainMob.Data.Set(new SpPlainData(i % 4, keys[i], i)));
                        break;
                    case 1:
                        tx.Spawn<SpOneIdxMob>(SpOneIdxMob.Data.Set(new SpOneIdxData(i % 4, keys[i], i)));
                        break;
                    case 3:
                        tx.Spawn<SpUniqIdxMob>(SpUniqIdxMob.Data.Set(new SpUniqIdxData(i % 4, keys[i], i)));
                        break;
                    default:
                        tx.Spawn<SpTwoIdxMob>(SpTwoIdxMob.Data.Set(new SpTwoIdxData(i % 4, keys[i], i)));
                        break;
                }
            }

            stagingMs = sw.Elapsed.TotalMilliseconds;
            tx.Commit();
        }

        var commitMs = sw.Elapsed.TotalMilliseconds - stagingMs;
        dbe.WriteTickFence(0);
        var fenceMs = sw.Elapsed.TotalMilliseconds - stagingMs - commitMs;

        Report($"Spawn idx={indexedFields} {(ascending ? "asc" : "shuf")}", stagingMs, commitMs, fenceMs);
        TestContext.Out.WriteLine($"  strict mode: {CheckConfig.Enabled}");
    }

    /// <summary>
    /// The batch API: one key allocation and one value write for the whole cohort.
    /// </summary>
    /// <remarks>
    /// <c>SpawnBatchAllocate</c> plus <c>SpawnBatchWriteAll</c>, not <c>SpawnBatch</c> — that overload shares ONE value set across every entity, which
    /// would give all 3 000 the same index key and measure a degenerate insert rather than a cheaper one.
    /// </remarks>
    [TestCase(0)]
    [TestCase(2)]
    public void BatchSpawn(int indexedFields)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpPlainData>();
        dbe.RegisterComponentFromAccessor<SpOneIdxData>();
        dbe.RegisterComponentFromAccessor<SpTwoIdxData>();
        dbe.RegisterComponentFromAccessor<SpUniqIdxData>();
        dbe.InitializeArchetypes();

        var keys = Keys(false);
        var ids = new EntityId[Burst];
        var sw = Stopwatch.StartNew();
        double stagingMs;

        using (var tx = dbe.CreateQuickTransaction())
        {
            if (indexedFields == 0)
            {
                var values = new SpPlainData[Burst];
                for (var i = 0; i < Burst; i++)
                {
                    values[i] = new SpPlainData(i % 4, keys[i], i);
                }

                tx.SpawnBatchAllocate<SpPlainMob>(Burst, ids);
                tx.SpawnBatchWriteAll(0, Burst, SpPlainMob.Data, values);
            }
            else
            {
                var values = new SpTwoIdxData[Burst];
                for (var i = 0; i < Burst; i++)
                {
                    values[i] = new SpTwoIdxData(i % 4, keys[i], i);
                }

                tx.SpawnBatchAllocate<SpTwoIdxMob>(Burst, ids);
                tx.SpawnBatchWriteAll(0, Burst, SpTwoIdxMob.Data, values);
            }

            stagingMs = sw.Elapsed.TotalMilliseconds;
            tx.Commit();
        }

        var commitMs = sw.Elapsed.TotalMilliseconds - stagingMs;
        dbe.WriteTickFence(0);
        var fenceMs = sw.Elapsed.TotalMilliseconds - stagingMs - commitMs;

        Report($"Batch idx={indexedFields}", stagingMs, commitMs, fenceMs);
        TestContext.Out.WriteLine($"  strict mode: {CheckConfig.Enabled}");
    }
}
