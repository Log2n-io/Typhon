using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>No indexed field: a dirty tick makes the change filter fall back to the whole view.</summary>
[Component("Typhon.Test.Cf841.Plain", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct Cf841Plain
{
    public int Value;
}

/// <summary>Indexed: a dirty tick makes the change filter resolve the dirty set entity by entity.</summary>
[Component("Typhon.Test.Cf841.Indexed", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct Cf841Indexed
{
    [Index(AllowMultiple = true)]
    public int Value;
}

[Archetype]
partial class Cf841PlainUnit : Archetype<Cf841PlainUnit>
{
    public static readonly Comp<Cf841Plain> Plain = Register<Cf841Plain>();
}

[Archetype]
partial class Cf841IndexedUnit : Archetype<Cf841IndexedUnit>
{
    public static readonly Comp<Cf841Indexed> Indexed = Register<Cf841Indexed>();
}

/// <summary>
/// #841: a change-filtered system's entity set is documented as <c>dirtySet ∪ Added</c> — the View's new entities plus those whose filtered components
/// were written — but a spawn sets no dirty bit (DIRTY-01) and the runtime built the set from the dirty bitmaps alone, so a change-filtered system never
/// saw an entity spawned while the runtime ran. Every <see cref="ChangeFilterTests"/> case spawns before <c>Start</c>, which is why nothing went red.
/// </summary>
[TestFixture]
[NonParallelizable]
class ChangeFilterSpawnTests : TestBase<ChangeFilterSpawnTests>
{
    // The spawner's third run: past the first tick, which a change filter runs over the whole view for want of a previous-tick snapshot.
    private const int SpawnRun = 3;
    private const int RunsAfterSpawn = 4;

    public enum Write
    {
        /// <summary>The spawn alone.</summary>
        None,

        /// <summary>The spawning transaction also writes the entity — still no dirty bit (DIRTY-01).</summary>
        InTheSpawnTransaction,

        /// <summary>A later system of the spawn's tick writes it — dirty AND added, so the two halves of the union overlap.</summary>
        ByALaterSystem
    }

    [Test]
    public void ASpawnedEntity_ReachesTheChangeFilteredSystem_OnceOnTheTickThePlainSystemSeesIt(
        [Values(false, true)] bool indexed,
        [Values] Write write)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<Cf841Plain>();
        dbe.RegisterComponentFromAccessor<Cf841Indexed>();
        dbe.InitializeArchetypes();
        using var _ = dbe;

        // One entity before the start, so the view is not empty and the filter has something to leave out.
        long seed;
        using (var tx = dbe.CreateQuickTransaction())
        {
            seed = (long)Spawn(tx, indexed, 1).RawValue;
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = indexed ? (ViewBase)txView.Query<Cf841IndexedUnit>().ToView() : txView.Query<Cf841PlainUnit>().ToView();

        var spawnerRuns = 0;
        long spawned = 0;
        long spawnTick = -1;
        var plain = new Sightings();
        var reactive = new Sightings();
        using var settled = new ManualResetEventSlim();
        var filterType = indexed ? typeof(Cf841Indexed) : typeof(Cf841Plain);

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Spawner", ctx =>
            {
                var run = Interlocked.Increment(ref spawnerRuns);
                if (run == SpawnRun)
                {
                    var id = Spawn(ctx.Transaction, indexed, 2);
                    if (write == Write.InTheSpawnTransaction)
                    {
                        WriteValue(ctx.Transaction, id, indexed, 3);
                    }

                    Volatile.Write(ref spawnTick, ctx.TickNumber);
                    Volatile.Write(ref spawned, (long)id.RawValue);
                }
                else if (run == SpawnRun + RunsAfterSpawn)
                {
                    settled.Set();
                }
            });

            dag.CallbackSystem("LaterWriter", ctx =>
            {
                if (write == Write.ByALaterSystem && ctx.TickNumber == Volatile.Read(ref spawnTick))
                {
                    WriteValue(ctx.Transaction, EntityId.FromRaw(Volatile.Read(ref spawned)), indexed, 4);
                }
            }, after: "Spawner");

            dag.QuerySystem("Reactive", ctx => reactive.Record(ctx, Volatile.Read(ref spawned), seed),
                input: () => view, changeFilter: [filterType], after: "LaterWriter");
            dag.QuerySystem("Plain", ctx => plain.Record(ctx, Volatile.Read(ref spawned), seed), input: () => view, after: "LaterWriter");
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        runtime.Start();
        var completed = settled.Wait(TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Only a dirty non-indexed table falls back to the whole view, the seed included; every other arm's set is exact.
        var seedMayFollow = write == Write.ByALaterSystem && !indexed;
        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.True, "PREMISE: the runtime ticked past the spawn");
            Assert.That(plain.FirstTick, Is.GreaterThan(spawnTick), "PREMISE: the plain system over the same view sees the spawn, from a later tick");
            Assert.That(reactive.FirstTick, Is.EqualTo(plain.FirstTick),
                "the change-filtered system receives dirtySet ∪ Added, so the new entity reaches it on the tick it enters the view");
            Assert.That(reactive.TicksWithSpawned, Is.EqualTo(1), "Added is this tick's: the entity is delivered once, not on every later tick");
            Assert.That(reactive.MaxOccurrences, Is.EqualTo(1), "an entity both written and added is delivered once");
            if (!seedMayFollow)
            {
                Assert.That(reactive.SeedAfterFirstRun, Is.EqualTo(0), "the filter still leaves out the entity nothing wrote and nothing added");
            }
        });
    }

    public enum Narrowing
    {
        /// <summary><c>tier: Tier0</c>; the outside spawn lands in a Tier1 cell.</summary>
        Tier,

        /// <summary>One cluster asleep; the outside spawn lands in it, the inside one in a fresh cell.</summary>
        SleepingCluster,

        /// <summary><c>realms: [1]</c>; the outside spawn is in realm 0.</summary>
        InRealms,

        /// <summary>No narrowing declared; the outside spawn is in realm 3, which sleeps.</summary>
        DormantRealm
    }

    /// <summary>
    /// A narrowed run — a tier, a sleeping cluster, <c>InRealms</c>, a dormant realm — delivers an Added entity on the dirty scans' own per-cluster terms:
    /// the change-filtered system sees a new entity exactly when the plain system with the same narrowing does, and on the same tick.
    /// </summary>
    [Test]
    public void ASpawnedEntity_ReachesANarrowedChangeFilteredSystem_ExactlyWhenThePlainOneSeesIt([Values] Narrowing narrowing)
    {
        var grid = SpatialGridConfig.Flat(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(100, 100), 10);
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<Realms.RealmPos>();
        dbe.ConfigureRealms(5);
        dbe.ConfigureSpatialGrid(grid);
        dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(grid));
        dbe.Realms.Register(new RealmId(3),
            new RealmConfig { Grid = grid, WhenUnobserved = RealmUnobserved.Sleep, SleepAfterTicks = 1, UnobservedTickDivisor = 1 });
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<Realms.RealmUnit>(Realms.RealmUnit.Pos.Set(At(5, 5, 0)));
            tx.Spawn<Realms.RealmUnit>(Realms.RealmUnit.Pos.Set(At(5, 5, 1)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var cs = dbe._archetypeStates[Archetype<Realms.RealmUnit>.Metadata.ArchetypeId].ClusterState;
        var (inside, outside) = narrowing switch
        {
            Narrowing.Tier => (At(25, 5, 0), At(35, 5, 0)),
            Narrowing.SleepingCluster => (At(45, 5, 0), At(5, 5, 0)),
            Narrowing.InRealms => (At(15, 5, 1), At(15, 5, 0)),
            _ => (At(15, 5, 0), At(15, 5, 3))
        };
        if (narrowing == Narrowing.Tier)
        {
            dbe.Realm0Grid.SetCellTier(dbe.Realm0Grid.WorldToCellKey(25, 5, 0f), SimTier.Tier0);
            dbe.Realm0Grid.SetCellTier(dbe.Realm0Grid.WorldToCellKey(35, 5, 0f), SimTier.Tier1);
        }
        else if (narrowing == Narrowing.SleepingCluster)
        {
            // Asleep for good: a zero threshold keeps the fence's sweep from ever running, so nothing wakes it.
            var chunk = ChunkAt(cs, dbe.Realm0Grid.WorldToCellKey(5, 5, 0f), realm: 0);
            cs.SleepThresholdTicks = 0;
            cs.SleepStates[chunk] = ClusterSleepState.Sleeping;
            cs.SleepingClusterCount++;
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<Realms.RealmUnit>().ToView();
        var tier = narrowing == Narrowing.Tier ? SimTier.Tier0 : SimTier.All;
        RealmId[] realms = narrowing == Narrowing.InRealms ? [new RealmId(1)] : null;

        var spawnerRuns = 0;
        long insideId = 0;
        long outsideId = 0;
        long spawnTick = -1;
        var plainIn = new Sightings();
        var plainOut = new Sightings();
        var reactiveIn = new Sightings();
        var reactiveOut = new Sightings();
        using var settled = new ManualResetEventSlim();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Spawner", ctx =>
            {
                var run = Interlocked.Increment(ref spawnerRuns);
                if (run == SpawnRun)
                {
                    var a = ctx.Transaction.Spawn<Realms.RealmUnit>(Realms.RealmUnit.Pos.Set(inside));
                    var b = ctx.Transaction.Spawn<Realms.RealmUnit>(Realms.RealmUnit.Pos.Set(outside));
                    Volatile.Write(ref spawnTick, ctx.TickNumber);
                    Volatile.Write(ref insideId, (long)a.RawValue);
                    Volatile.Write(ref outsideId, (long)b.RawValue);
                }
                else if (run == SpawnRun + RunsAfterSpawn)
                {
                    settled.Set();
                }
            });

            dag.QuerySystem("Reactive", ctx =>
            {
                reactiveIn.Record(ctx, Volatile.Read(ref insideId), 0);
                reactiveOut.Record(ctx, Volatile.Read(ref outsideId), 0);
            }, input: () => view, changeFilter: [typeof(Realms.RealmPos)], tier: tier, realms: realms, after: "Spawner");
            dag.QuerySystem("Plain", ctx =>
            {
                plainIn.Record(ctx, Volatile.Read(ref insideId), 0);
                plainOut.Record(ctx, Volatile.Read(ref outsideId), 0);
            }, input: () => view, tier: tier, realms: realms, after: "Spawner");
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        runtime.Start();
        var completed = settled.Wait(TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.True, "PREMISE: the runtime ticked past the spawn");
            Assert.That(plainIn.FirstTick, Is.GreaterThan(spawnTick), "PREMISE: the plain system sees the inside spawn");
            Assert.That(plainOut.FirstTick, Is.EqualTo(-1), "PREMISE: the narrowing keeps the outside spawn from the plain system");
            Assert.That(reactiveIn.FirstTick, Is.EqualTo(plainIn.FirstTick), "the inside spawn reaches the change-filtered system on the same tick");
            Assert.That(reactiveIn.TicksWithSpawned, Is.EqualTo(1), "and once");
            Assert.That(reactiveOut.FirstTick, Is.EqualTo(-1), "the narrowing applies to Added as it does to the dirty set (DSEL-01)");
        });
    }

    private static int ChunkAt(Internals.ArchetypeClusterState cs, int cellKey, ushort realm)
    {
        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            var cid = cs.ActiveClusterIds[i];
            if (cs.ClusterCellMap[cid] == cellKey && cs.ClusterRealmMap[cid] == realm)
            {
                return cid;
            }
        }

        throw new InvalidOperationException("no cluster in that cell and realm");
    }

    private static Realms.RealmPos At(float x, float y, ushort realm) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm };

    private sealed class Sightings
    {
        public long FirstTick = -1;
        public int TicksWithSpawned;
        public int MaxOccurrences;
        public int SeedAfterFirstRun;
        private int _runs;

        public void Record(TickContext ctx, long spawned, long seed)
        {
            var run = ++_runs;
            var occurrences = 0;
            var sawSeed = false;
            foreach (var id in ctx.Entities)
            {
                var raw = (long)id.RawValue;
                occurrences += spawned != 0 && raw == spawned ? 1 : 0;
                sawSeed |= raw == seed;
            }

            if (occurrences > 0)
            {
                TicksWithSpawned++;
                MaxOccurrences = Math.Max(MaxOccurrences, occurrences);
                if (FirstTick < 0)
                {
                    FirstTick = ctx.TickNumber;
                }
            }

            if (sawSeed && run > 1)
            {
                SeedAfterFirstRun++;
            }
        }
    }

    private static EntityId Spawn(Transaction tx, bool indexed, int value)
    {
        if (indexed)
        {
            var c = new Cf841Indexed { Value = value };
            return tx.Spawn<Cf841IndexedUnit>(Cf841IndexedUnit.Indexed.Set(in c));
        }

        var p = new Cf841Plain { Value = value };
        return tx.Spawn<Cf841PlainUnit>(Cf841PlainUnit.Plain.Set(in p));
    }

    private static void WriteValue(Transaction tx, EntityId id, bool indexed, int value)
    {
        if (indexed)
        {
            tx.OpenMut(id).Set(Cf841IndexedUnit.Indexed, new Cf841Indexed { Value = value });
        }
        else
        {
            tx.OpenMut(id).Set(Cf841PlainUnit.Plain, new Cf841Plain { Value = value });
        }
    }
}
