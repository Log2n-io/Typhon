using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

[Component("Typhon.Test.ChunkRange.Data", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ChunkRangeData
{
    [Field]
    public int Value;
}

[Archetype]
partial class ChunkRangeUnit : Archetype<ChunkRangeUnit>
{
    public static readonly Comp<ChunkRangeData> Data = Register<ChunkRangeData>();
}

/// <summary>A parallel QuerySystem's chunks tile the cluster list its dispatch counted in Prepare, even when the list grows while they run (CD-02).</summary>
[TestFixture]
[NonParallelizable]
class ChunkClusterRangeTests : TestBase<ChunkClusterRangeTests>
{
    private const int Entities = 2000;

    /// <summary>
    /// The first chunk of every dispatch spawns more than a cluster's worth of entities into the archetype its system walks, so the list grows before the
    /// next chunk reads its range. One worker runs the chunks in order, two per dispatch (the entity rule at two chunks per worker). They must still tile
    /// the list as Prepare counted it; split against the live length instead, they walked "[0,18) [19,37)" for a list of 36. Run on the accessor path
    /// and on the per-chunk Transaction path, which compute their ranges at separate sites.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("CD-02")]
    public void AListThatGrowsDuringTheDispatch_IsStillTiled(bool writesVersioned)
    {
        using var dbe = SetupEngine();
        var state = dbe._archetypeStates[Archetype<ChunkRangeUnit>.Metadata.ArchetypeId].ClusterState;
        var clusters = state.ActiveClusterCount;
        var clusterSize = state.Layout.ClusterSize;
        Assert.That(clusters, Is.GreaterThanOrEqualTo(16), "precondition: too few clusters to split");

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<ChunkRangeUnit>().ToView();
        var ranges = new List<(long Tick, int Start, int End, int Chunks)>();
        var counted = new Dictionary<long, int>();
        var ticksSeen = 0;
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Walk", ctx =>
            {
                lock (ranges)
                {
                    ranges.Add((ctx.TickNumber, ctx.StartClusterIndex, ctx.EndClusterIndex, ctx.ChunkCount));
                    if (ctx.ChunkIndex == 0)
                    {
                        // Nothing spawns between Prepare and the first chunk, so the list is still the length the dispatch counted.
                        counted[ctx.TickNumber] = state.ActiveClusterCount;
                    }
                }

                if (ctx.ChunkIndex == 0)
                {
                    using var side = ctx.CreateSideTransaction(DurabilityMode.Deferred);
                    for (var i = 0; i <= clusterSize; i++)
                    {
                        var v = new ChunkRangeData { Value = -1 };
                        side.Spawn<ChunkRangeUnit>(ChunkRangeUnit.Data.Set(in v));
                    }

                    side.Commit();
                }
            }, input: () => view, parallel: true, writesVersioned: writesVersioned, chunksPerWorker: 2f, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 100, CostBasedChunking = false }))
        {
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= 4, TimeSpan.FromSeconds(10));
            runtime.Shutdown();
        }

        view.Dispose();
        Assert.That(Volatile.Read(ref ticksSeen), Is.GreaterThanOrEqualTo(4), "precondition: the runtime did not reach tick 4");
        Assert.That(state.ActiveClusterCount, Is.GreaterThan(clusters), "precondition: the spawns never grew the cluster list");
        Assert.That(ranges.Any(r => r.Chunks >= 2), Is.True, "precondition: no dispatch ran two chunks, so the growth never met a split");

        foreach (var tick in ranges.GroupBy(r => r.Tick))
        {
            var length = counted[tick.Key];
            var sorted = tick.OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
            var walked = string.Join(" ", sorted.Select(r => $"[{r.Start},{r.End})"));
            var expected = 0;
            foreach (var r in sorted)
            {
                Assert.That(r.Start == expected && r.End >= r.Start, Is.True,
                    $"CD-02 violated: tick {tick.Key}'s chunk ranges do not tile [0, {length}): {walked}");
                expected = r.End;
            }

            Assert.That(expected, Is.EqualTo(length), $"CD-02 violated: tick {tick.Key}'s chunk ranges stop at {expected}, not {length}: {walked}");
        }
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ChunkRangeData>();
        dbe.InitializeArchetypes();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Entities; i++)
            {
                var v = new ChunkRangeData { Value = i };
                tx.Spawn<ChunkRangeUnit>(ChunkRangeUnit.Data.Set(in v));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }
}
