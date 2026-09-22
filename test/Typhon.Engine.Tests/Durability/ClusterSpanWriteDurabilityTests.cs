using System;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;

namespace Typhon.Engine.Tests;

/// <summary>
/// Reproducer for <a href="https://github.com/Log2n-io/Typhon/issues/983">#983</a>: a clustered archetype's reopen result depends on the order its tests
/// ran in.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this fixture does NOT establish.</b> It was written to answer one question — does a durable column written through <c>ClusterRef.GetSpan</c>
/// or <c>WriteSpatial</c> and then not marked survive a reopen — and it cannot answer it, because the answer inverts when the test methods are renamed
/// and reordered. Nothing about the marking logic changes between the two arrangements:
/// </para>
/// <code>
/// marking          as first written      after renaming/reordering
/// none             write lost            write CARRIED
/// position only    write carried         write LOST
/// movement only    write carried         write carried
/// both             write carried         write carried
/// </code>
/// <para>
/// Five consecutive runs of each arrangement reproduce that arrangement's column every time, so this is deterministic and order-dependent rather than a
/// race. Each test builds its own <c>ServiceProvider</c> over a fresh WAL directory and a fresh database directory, so nothing travels through the
/// filesystem. <c>[NonParallelizable]</c> fixed a separate cross-fixture contradiction with <c>ClusterStorageTests</c> — which declares <c>ClAnt</c> and
/// carries the same attribute — and did not fix this.
/// </para>
/// <para>
/// <b>Quarantined, not deleted.</b> The assertions state what the engine's own contracts say should happen, so whichever arm fails on a given day names
/// a real disagreement between the contract and the behaviour. Deleting it would throw away the only reproducer; leaving it un-quarantined would redden
/// the gate for a defect that is filed. A hypothesis for the cause is in #983 and is explicitly unverified.
/// </para>
/// <para>
/// <b>Do not read a durability conclusion out of a green run of this fixture.</b> Green here means the ordering happened to favour the assertions.
/// </para>
/// </remarks>
[TestFixture]
[Category("WAL")]
[Category("Quarantine")]
// ClAnt is declared by ClusterStorageTests, which is [NonParallelizable] for the archetype-registry statics it shares.
// Without the same marking this fixture ran beside it and produced a reopen that contradicted itself run to run.
[NonParallelizable]
class ClusterSpanWriteDurabilityTests
{
    private ServiceProvider _serviceProvider;
    private string _walDir;
    private string _dbDir;

    [SetUp]
    public void Setup()
    {
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_{Guid.NewGuid():N}");
        _dbDir = Path.Combine(Path.GetTempPath(), $"typhon_db_{Guid.NewGuid():N}");
        CleanupDirectories();
        Directory.CreateDirectory(_walDir);
        Directory.CreateDirectory(_dbDir);

        var services = new ServiceCollection();
        services
            .AddLogging(builder =>
            {
                builder.AddSerilog();
                builder.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddSingleton<IWalFileIO>(new WalFileIO())
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = "ClusterSpanWriteDurability";
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        CleanupDirectories();
        Log.CloseAndFlush();
    }

    private void CleanupDirectories()
    {
        try { if (Directory.Exists(_walDir)) Directory.Delete(_walDir, true); }
        catch { /* ignored */ }
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, true); }
        catch { /* ignored */ }
    }

    private static DatabaseEngine CreateEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>The position X every slot is given by the span write. Distinct from any seeded value, which are 0..AntCount-1.</summary>
    private const float WrittenPositionX = 100f;

    /// <summary>The movement VX every slot is given by the span write.</summary>
    private const float WrittenMovementVx = 300f;

    /// <summary>How many ants the run seeds. Two clusters' worth is not needed; the column union is per ARCHETYPE, not per cluster.</summary>
    private const int AntCount = 4;

    /// <summary>
    /// Writes both of <see cref="ClAnt"/>'s columns through one span and marks <paramref name="mark"/> of them, then fences.
    /// </summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="mark">Which columns to mark, as a bitmask: 0 neither, 1 position, 2 movement, 3 both.</param>
    /// <param name="tick">The fence tick.</param>
    /// <remarks>
    /// The write and the fence are deliberately in a tick of their own, AFTER the spawn has been fenced. A spawn calls the three-argument
    /// <c>SetDirty</c> once per component, so a tick that also spawns has every column's bit set and the narrowing under test never engages — the bug
    /// would hide behind the seed. The union is exchanged to zero by each fence, so tick 2 starts clean.
    /// </remarks>
    private static void WriteBothColumnsThroughTheSpan(DatabaseEngine dbe, int mark, long tick)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClAnt>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                // Neither column is spatial, so TYPHON009 does not fire on either of these — which is the point the fixture's remarks make.
                var positions = cluster.GetSpan(ClAnt.Position);
                var movements = cluster.GetSpan(ClAnt.Movement);

                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    // ONE constant for every slot, not a function of the slot index. An entity spawned i-th does not necessarily occupy slot i, so
                    // a per-slot value would have to be read back through the slot the entity ended up in — a coupling that silently makes the
                    // assertion about placement rather than about durability. A constant is immune to where the entity landed.
                    positions[slot] = new ClPosition(WrittenPositionX, 200f);
                    movements[slot] = new ClMovement(WrittenMovementVx, 400f);
                }

                if (mark is 1 or 3)
                {
                    cluster.MarkDirty(ClAnt.Position);
                }

                if (mark is 2 or 3)
                {
                    cluster.MarkDirty(ClAnt.Movement);
                }
            }

            accessor.Dispose();
            tx.Commit();
        }

        dbe.WriteTickFence(tick);
        dbe.ForceCheckpoint();
    }

    /// <summary>Seeds the ants and fences the spawn, so the write under test lands in a tick of its own.</summary>
    private static EntityId[] Seed(DatabaseEngine dbe)
    {
        var ids = new EntityId[AntCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < AntCount; i++)
            {
                var p = new ClPosition(i, i);
                var m = new ClMovement(i, i);
                ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(in p), ClAnt.Movement.Set(in m));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        dbe.ForceCheckpoint();
        return ids;
    }

    /// <summary>
    /// The harness's own precondition: a clustered archetype's seeded values survive a reopen at all.
    /// </summary>
    /// <remarks>
    /// Written after the two tests below produced answers that contradicted each other — one marked column carrying BOTH, two marked columns carrying
    /// NEITHER. Neither result is a durability story, so the first thing to establish is whether this fixture observes durability in the first place. If
    /// this fails, the fixture is measuring something other than what it claims and the other two prove nothing.
    /// </remarks>
    [Test]
    [CancelAfter(15000)]
    public void TheHarnessObservesAReopenAtAll()
    {
        EntityId[] ids;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            ids = Seed(dbe);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            using var tx = dbe.CreateQuickTransaction();

            for (var i = 0; i < AntCount; i++)
            {
                var entity = tx.Open(ids[i]);
                var px = entity.Read(ClAnt.Position).X;
                var mvx = entity.Read(ClAnt.Movement).VX;

                Assert.Multiple(() =>
                {
                    Assert.That(px, Is.EqualTo((float)i).Within(0.001f), $"ant {i}: the SEEDED position must survive a clean reopen");
                    Assert.That(mvx, Is.EqualTo((float)i).Within(0.001f), $"ant {i}: the SEEDED movement must survive a clean reopen");
                });
            }
        }
    }

    /// <summary>
    /// Marking any one of an entity's columns carries every column that tick wrote to it — including the ones not named.
    /// </summary>
    /// <remarks>
    /// All three non-empty markings are run rather than one, because the interesting claim is that WHICH column is named does not matter. A single case
    /// would leave "position happens to be special" open, and the refuted version of this fixture is what that looks like when it goes wrong.
    /// </remarks>
    [Test]
    [CancelAfter(20000)]
    public void MarkingAnyOneColumn_CarriesEveryColumnTheTickWrote([Values(1, 2, 3)] int mark)
    {
        EntityId[] ids;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            ids = Seed(dbe);
            WriteBothColumnsThroughTheSpan(dbe, mark, tick: 2);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            using var tx = dbe.CreateQuickTransaction();

            var entity = tx.Open(ids[0]);
            var px = entity.Read(ClAnt.Position).X;
            var mvx = entity.Read(ClAnt.Movement).VX;

            Assert.Multiple(() =>
            {
                Assert.That(px, Is.EqualTo(WrittenPositionX).Within(0.001f), $"mark={mark}: position");
                Assert.That(mvx, Is.EqualTo(WrittenMovementVx).Within(0.001f), $"mark={mark}: movement");
            });
        }
    }


    /// <summary>
    /// Marking nothing loses every column the tick wrote — the entity is never selected, so the whole of its tick is gone.
    /// </summary>
    /// <remarks>
    /// This is the case that actually bites. It is what <c>ClusterRef.GetSpan</c> and <c>WriteSpatial</c> both leave behind when the caller forgets, it
    /// has no diagnostic, and it costs nothing at run time — the write succeeds, the value is readable for the rest of the session, and only a reopen
    /// says otherwise. The assertion names the SEEDED values so that a run which starts carrying unmarked writes fails here and says the contract moved.
    /// </remarks>
    [Test]
    [CancelAfter(20000)]
    public void MarkingNothing_LosesEveryColumnTheTickWrote()
    {
        EntityId[] ids;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            ids = Seed(dbe);
            WriteBothColumnsThroughTheSpan(dbe, mark: 0, tick: 2);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            using var tx = dbe.CreateQuickTransaction();

            for (var i = 0; i < AntCount; i++)
            {
                var entity = tx.Open(ids[i]);
                var px = entity.Read(ClAnt.Position).X;
                var mvx = entity.Read(ClAnt.Movement).VX;

                Assert.Multiple(() =>
                {
                    Assert.That(px, Is.EqualTo((float)i).Within(0.001f), $"ant {i}: position must still be the SEEDED value, the unmarked write lost");
                    Assert.That(mvx, Is.EqualTo((float)i).Within(0.001f), $"ant {i}: movement must still be the SEEDED value, the unmarked write lost");
                });
            }
        }
    }

}
