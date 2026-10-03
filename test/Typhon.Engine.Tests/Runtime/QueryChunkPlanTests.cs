using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>Indexed, so a change filter on it scans the dirty set and the dispatch materializes an entity list.</summary>
[Component("Typhon.Test.QueryChunkPlan.Score", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct QcpScore
{
    [Index(AllowMultiple = true)]
    public int Value;
}

[Component("Typhon.Test.QueryChunkPlan.Gold", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct QcpGold
{
    [Field]
    public long Value;
}

[Archetype]
partial class QcpUnit : Archetype<QcpUnit>
{
    public static readonly Comp<QcpScore> Score = Register<QcpScore>();
    public static readonly Comp<QcpGold> Gold = Register<QcpGold>();
}

/// <summary>
/// A parallel QuerySystem whose dispatch materializes an entity list — a change filter, or a Versioned write — hands every entity of that list to exactly
/// one chunk, every tick: the chunks' entity slices, decided once in prepare (CD-03), tile the list.
/// </summary>
[TestFixture]
[NonParallelizable]
class QueryChunkPlanTests : TestBase<QueryChunkPlanTests>
{
    private const int Entities = 600;
    private const int Ticks = 8;

    [TestCase(false, TestName = "ChangeFiltered_EveryDirtyEntityGoesToExactlyOneChunk")]
    [TestCase(true, TestName = "Versioned_EveryEntityGoesToExactlyOneChunk")]
    [VerifiesRule("CD-03")]
    public void EveryMaterializedEntityGoesToExactlyOneChunk(bool versioned)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QcpScore>();
        dbe.RegisterComponentFromAccessor<QcpGold>();
        dbe.InitializeArchetypes();
        var ids = new EntityId[Entities];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Entities; i++)
            {
                ids[i] = tx.Spawn<QcpUnit>(QcpUnit.Score.Set(new QcpScore { Value = i }), QcpUnit.Gold.Set(new QcpGold()));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        using (dbe)
        {
            using var txView = dbe.CreateQuickTransaction();
            using var view = txView.Query<QcpUnit>().ToView();
            var seen = new ConcurrentBag<(long Tick, EntityId Id)>();
            var chunkCounts = new ConcurrentDictionary<long, int>();
            var ticks = 0;
            var errors = new ConcurrentBag<string>();
            using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                var dag = schedule.PublicTrack.DeclareDag("Test");

                // Every entity changed every tick, so a change-filtered dispatch's list is the whole archetype.
                dag.CallbackSystem("Touch", ctx =>
                {
                    foreach (var id in ids)
                    {
                        ctx.Transaction.OpenMut(id).Write(QcpUnit.Score).Value++;
                    }

                    Interlocked.Increment(ref ticks);
                });
                dag.QuerySystem("Walk", ctx =>
                {
                    chunkCounts[ctx.TickNumber] = ctx.ChunkCount;
                    // Records only: what is under test is which entities each chunk is handed, not the write path behind the Versioned dispatch.
                    try
                    {
                        foreach (var id in ctx.Entities)
                        {
                            seen.Add((ctx.TickNumber, id));
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.ToString());
                        throw;
                    }
                }, input: () => view, parallel: true, after: "Touch", chunksPerWorker: 4f, minChunkSize: 16,
                    changeFilter: versioned ? null : [typeof(QcpScore)], writesVersioned: versioned);
            }, new RuntimeOptions { WorkerCount = 4, BaseTickRate = 1000, CostBasedChunking = false });

            runtime.Start();
            var advanced = SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= Ticks, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
            Assert.That(advanced, Is.True, "precondition: the runtime ticked");
            Assert.That(errors, Is.Empty, () => errors.First());

            var byTick = seen.GroupBy(s => s.Tick).OrderBy(g => g.Key).ToList();
            Assert.That(byTick, Is.Not.Empty, "precondition: the system ran");
            var all = new HashSet<EntityId>(ids);
            foreach (var tick in byTick)
            {
                Assert.That(chunkCounts[tick.Key], Is.GreaterThan(1), $"precondition: tick {tick.Key} dispatched several chunks");
                var once = tick.Select(s => s.Id).ToList();
                Assert.That(once.Count, Is.EqualTo(Entities), $"tick {tick.Key}: every entity walked exactly once in total");
                Assert.That(once.ToHashSet().SetEquals(all), Is.True, $"tick {tick.Key}: the chunks' slices tile the list — none twice, none missed");
            }
        }
    }
}
