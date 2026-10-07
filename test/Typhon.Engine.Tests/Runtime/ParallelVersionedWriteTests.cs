using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

[Component("Typhon.Test.ParallelVersioned.Tag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PvwTag
{
    [Index(AllowMultiple = true)]
    public int Value;
}

[Component("Typhon.Test.ParallelVersioned.Gold", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct PvwGold
{
    [Field]
    public long Value;
}

[Archetype]
partial class PvwUnit : Archetype<PvwUnit>
{
    public static readonly Comp<PvwTag> Tag = Register<PvwTag>();
    public static readonly Comp<PvwGold> Gold = Register<PvwGold>();
}

/// <summary>
/// #1116: a parallel QuerySystem declared WritesVersioned writes a Versioned component through each chunk's own transaction (dispatch paths 3 and 4). Every
/// entity's write must land, every tick, with no chunk failing.
/// </summary>
[TestFixture]
[NonParallelizable]
class ParallelVersionedWriteTests : TestBase<ParallelVersionedWriteTests>
{
    private const int Entities = 600;
    private const int Ticks = 10;

    [TestCase(1, false, TestName = "OneWorker_EveryVersionedWriteLands")]
    [TestCase(4, false, TestName = "FourWorkers_EveryVersionedWriteLands")]
    [TestCase(8, false, TestName = "EightWorkers_EveryVersionedWriteLands")]
    [TestCase(4, true, TestName = "FourWorkers_ChangeFiltered_EveryVersionedWriteLands")]
    [VerifiesRule("PS-05a")]
    public void EveryVersionedWriteLands(int workers, bool changeFiltered)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PvwTag>();
        dbe.RegisterComponentFromAccessor<PvwGold>();
        dbe.InitializeArchetypes();
        var ids = new EntityId[Entities];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Entities; i++)
            {
                ids[i] = tx.Spawn<PvwUnit>(PvwUnit.Tag.Set(new PvwTag { Value = i }), PvwUnit.Gold.Set(new PvwGold()));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        using (dbe)
        {
            var errors = new ConcurrentBag<string>();
            var walks = 0;
            using (var txView = dbe.CreateQuickTransaction())
            using (var view = txView.Query<PvwUnit>().ToView())
            {
                using var runtime = TyphonRuntime.Create(dbe, schedule =>
                {
                    var dag = schedule.PublicTrack.DeclareDag("Test");

                    // Path 4 needs a change: every entity's tag moves every tick, so the filtered list is the whole archetype.
                    dag.CallbackSystem("Touch", ctx =>
                    {
                        if (changeFiltered)
                        {
                            foreach (var id in ids)
                            {
                                var target = ctx.Transaction.OpenMut(id);
                                var tagCopy = target.Read(PvwUnit.Tag);
                                tagCopy.Value++;
                                target.Set(PvwUnit.Tag, tagCopy);
                            }
                        }
                    });
                    dag.QuerySystem("Pay", ctx =>
                    {
                        try
                        {
                            foreach (var id in ctx.Entities)
                            {
                                var opened = ctx.Transaction.OpenMut(id);
                                var goldCopy = opened.Read(PvwUnit.Gold);
                                goldCopy.Value++;
                                opened.Set(PvwUnit.Gold, goldCopy);
                            }

                            if (ctx.ChunkIndex == 0)
                            {
                                Interlocked.Increment(ref walks);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex.ToString());
                            throw;
                        }
                    }, input: () => view, parallel: true, writesVersioned: true, chunksPerWorker: 4f, minChunkSize: 16, after: "Touch",
                        changeFilter: changeFiltered ? [typeof(PvwTag)] : null);
                }, new RuntimeOptions { WorkerCount = workers, BaseTickRate = 1000, CostBasedChunking = false });

                runtime.Start();
                var advanced = SpinWait.SpinUntil(() => Volatile.Read(ref walks) >= Ticks || !errors.IsEmpty, TimeSpan.FromSeconds(10));
                runtime.Shutdown();
                Assert.That(errors, Is.Empty, () => errors.First());
                Assert.That(advanced, Is.True, "precondition: the system ran");
            }

            // Every chunk of every dispatch committed: each entity was paid once per dispatch, so all hold the same amount, at least Ticks.
            using var check = dbe.CreateQuickTransaction();
            var gold = ids.Select(id => check.Open(id).Read(PvwUnit.Gold).Value).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(gold.Min(), Is.GreaterThanOrEqualTo(Ticks), "every entity was paid every tick");
                Assert.That(gold.Distinct().Count(), Is.EqualTo(1), $"every entity paid the same number of times (min {gold.Min()}, max {gold.Max()})");
            });
        }
    }
}
