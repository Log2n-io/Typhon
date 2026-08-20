using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// CHARACTERIZATION — what does a ClusterDurability.Checkpoint archetype actually return after a HARD CRASH?
//
// ClusterDurability.cs:38-42 promises: "A crash loses up to one checkpoint interval of value updates... the entities survive, their values are as of the
// last checkpoint." That sentence is only meaningful for an entity that HAD a last-checkpoint value. An entity SPAWNED inside the crash window has none:
// its Spawn lifecycle record IS written to the WAL (Transaction.cs:2504 is unconditional — ClusterDurability has exactly one branch site, in the tick
// fence), but the fence that would have carried its VALUES is suppressed by the mode. So recovery replays "this entity exists" with nothing to fill it.
//
// The existing ClusterDurabilityTests only covers a CLEAN reopen. Nothing in the suite crashes a Checkpoint archetype and asserts what comes back. This
// fixture establishes that baseline before any change to lifecycle emission is contemplated.
//
// The FenceWal control is load-bearing: it runs the identical harness and MUST return window-spawned entities with their real values. Without it, a
// broken crash harness (nothing durable at all) would produce the same all-zero reading and be mistaken for the finding.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.CkptCrash.Ckpt", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CkptCrashCkptData
{
    [Field]
    public int Value;
}

[Component("Typhon.Test.CkptCrash.Walled", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CkptCrashWalledData
{
    [Field]
    public int Value;
}

/// <summary>The subject: checkpoint-granular durability.</summary>
[Archetype(ClusterDurability = ClusterDurability.Checkpoint)]
partial class CkptCrashCkptArch : Archetype<CkptCrashCkptArch>
{
    public static readonly Comp<CkptCrashCkptData> Data = Register<CkptCrashCkptData>();
}

/// <summary>The control: default (FenceWal) durability, identical shape, identical workload.</summary>
[Archetype]
partial class CkptCrashWalledArch : Archetype<CkptCrashWalledArch>
{
    public static readonly Comp<CkptCrashWalledData> Data = Register<CkptCrashWalledData>();
}

[TestFixture]
[NonParallelizable]
internal sealed class CheckpointDurabilityCrashTests
{
    private const int BaselineCount = 16;
    private const int WindowCount = 16;

    // Value bands chosen so every recovered value is attributable to exactly one write, and 0 means "never written".
    private const int BaselineBase = 1000;   // written before the checkpoint
    private const int WindowBase = 2000;     // written by a spawn INSIDE the crash window
    private const int WindowWrittenBase = 3000; // overwrites a WINDOW-SPAWNED entity, still inside the window
    private const int MutationBase = 9000;   // overwrites the baseline INSIDE the crash window

    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    private static string CurrentDatabaseName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }

            const int max = 63;
            const string prefix = "CkptCrash_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }

            return prefix + name;
        }
    }

    [SetUp]
    public void Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(CheckpointDurabilityCrashTests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
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
        var testRoot = Directory.GetParent(_dbDir)?.FullName;
        try
        {
            if (testRoot != null && Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CkptCrashCkptData>();
        dbe.RegisterComponentFromAccessor<CkptCrashWalledData>();
        dbe.InitializeArchetypes();
    }

    /// <summary>
    /// The whole point, in one run: baseline entities checkpointed, then a crash window containing BOTH new spawns and mutations of the baseline, then a
    /// power cut. Run for the Checkpoint archetype and for the FenceWal control through the identical harness.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("CM-07")]
    public void WindowSpawn_SurvivesHardCrash_ButWithWhatValues([Values] bool checkpointArchetype, [Values] bool writeAfterWindowSpawn)
    {
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);

            // Phase 1: baseline, made durable by a CHECKPOINT (not by the WAL).
            var baselineIds = Spawn(dbe, checkpointArchetype, BaselineCount, BaselineBase);
            dbe.WriteTickFence(1);
            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));

            // Phase 2: the crash window — everything from here on is post-checkpoint.
            var windowIds = Spawn(dbe, checkpointArchetype, WindowCount, WindowBase);
            if (writeAfterWindowSpawn)
            {
                // A plain WRITE to an already-spawned entity — the operation that DOES set the cluster dirty bit the fence emits from.
                Mutate(dbe, checkpointArchetype, windowIds, WindowWrittenBase);
            }

            Mutate(dbe, checkpointArchetype, baselineIds, MutationBase);
            dbe.WriteTickFence(2);

            // Phase 3: power cut. Dirty pages are discarded; only checkpoints + fsynced WAL survive.
            dbe.SimulateHardCrash();
        }

        // Phase 4: reopen and take the census.
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);

            var values = ReadAllValues(dbe, checkpointArchetype);
            var census = Census(values);

            var report = new StringBuilder();
            report.Append(checkpointArchetype ? "ClusterDurability.Checkpoint" : "ClusterDurability.FenceWal (control)");
            report.Append(writeAfterWindowSpawn ? ", window spawns WRITTEN after spawn" : ", window spawns NEVER written after spawn");
            report.Append($" — {values.Count} entities recovered of {BaselineCount + WindowCount} live at crash. ");
            report.Append($"zeroed={census.Zeroed} baselineAtCheckpoint={census.Baseline} windowSpawnValue={census.Window} ");
            report.Append($"windowWritten={census.WindowWritten} baselineMutated={census.Mutated} other={census.Other}");
            TestContext.Out.WriteLine(report.ToString());

            // The BASELINE survives in every cell — it was checkpointed, and the checkpoint is what both modes rely on.
            Assert.That(census.Baseline + census.Mutated, Is.EqualTo(BaselineCount),
                $"every checkpointed entity must survive in every cell — this is the floor both modes stand on. {report}");

            if (!checkpointArchetype)
            {
                // CONTROL — default FenceWal. The tick fence emits values for entities the cluster dirty bitmap marks. Spawn deliberately does NOT set that
                // bit (Transaction.ECS.cs:1561 — the bitmap tracks write mutations for change-filtered dispatch), so a spawn alone is invisible to the fence.
                // Only a subsequent WRITE makes the value durable. That is the whole difference between the two cells below, and it is NOT a Checkpoint
                // phenomenon — it is TickFence discipline, which FenceWal shares.
                Assert.Multiple(() =>
                {
                    Assert.That(values, Has.Count.EqualTo(BaselineCount + WindowCount),
                        $"FenceWal keeps its Spawn records, so every window-spawned entity still EXISTS — the gate must not touch this mode. {report}");
                    Assert.That(census.Mutated, Is.EqualTo(BaselineCount),
                        $"FenceWal fences the in-window mutation of a pre-existing entity — this is the mode working. {report}");

                    if (writeAfterWindowSpawn)
                    {
                        Assert.That(census.WindowWritten, Is.EqualTo(WindowCount),
                            $"a window spawn FOLLOWED BY A WRITE is fenced, so it recovers with its value. {report}");
                        Assert.That(census.Zeroed, Is.Zero, $"nothing should be valueless in this cell. {report}");
                    }
                    else
                    {
                        Assert.That(census.Zeroed, Is.EqualTo(WindowCount),
                            $"a window spawn NEVER written afterwards recovers ZEROED even under FenceWal — spawn does not dirty the bitmap the fence reads. {report}");
                        Assert.That(census.Window, Is.Zero, $"the spawn-time value reached the cluster page, not the WAL, and the page was never checkpointed. {report}");
                    }
                });

                return;
            }

            // SUBJECT — ClusterDurability.Checkpoint under CM-07: the Spawn record is suppressed, so a window spawn is ABSENT after a crash rather than
            // present-and-empty. That is precisely what D5's "checkpoint-durable only" says, and what the mode failed to deliver before CM-07 — the pre-rule
            // engine returned 32 entities of which 16 were zeroed phantoms (PreCm07Reading below preserves that measurement as the mutant).
            AssertCheckpointContract(values, census, report.ToString());
        }
    }

    /// <summary>
    /// CM-07's verifier, factored out so <see cref="Mutant_PreCm07Phantom_IsRejected"/> can drive it with a violating input. If this ever accepts the
    /// pre-CM-07 reading, the green result above is not evidence of anything.
    /// </summary>
    private static void AssertCheckpointContract(List<int> values, (int Zeroed, int Baseline, int Window, int WindowWritten, int Mutated, int Other) census,
        string report)
    {
        // Sequential, NOT Assert.Multiple: RuleMutants.AssertDetects requires a plain AssertionException as evidence the verifier rejected, and treats the
        // MultipleAssertException that Assert.Multiple throws as a CRASHED mutant. Nothing is lost — `report` already carries the full census, so the first
        // failure is as diagnostic as all five.
        Assert.That(values, Has.Count.EqualTo(BaselineCount),
            $"{PhantomMarker}: a window spawn must be ABSENT, not resurrected empty — its Spawn record is suppressed for this archetype. {report}");
        Assert.That(census.Zeroed, Is.Zero,
            $"{PhantomMarker}: no valueless phantom may survive — that outcome is the whole reason the record is suppressed. {report}");
        Assert.That(census.Baseline, Is.EqualTo(BaselineCount),
            $"the mode's documented promise: checkpointed entities return with their checkpoint values. {report}");
        Assert.That(census.Mutated, Is.Zero,
            $"the mode's documented trade: an in-window value update is lost. {report}");
        Assert.That(census.Window + census.WindowWritten, Is.Zero,
            $"no window-spawn value may appear — neither its spawn value nor a later write. {report}");
    }

    /// <summary>Distinctive substring of the verifier's own rejection message — <see cref="RuleMutants.AssertDetects"/> requires the failure to come from
    /// the assertion under test, not from scaffolding.</summary>
    private const string PhantomMarker = "CM-07 phantom";

    /// <summary>
    /// Genuineness proof for CM-07. The engine can no longer produce a phantom, so the violating input is the MEASURED pre-rule reading: 32 entities back
    /// from a 32-entity crash, of which the 16 window spawns are zeroed. The verifier must reject it.
    /// </summary>
    [Test]
    [RuleMutant("CM-07")]
    public void Mutant_PreCm07Phantom_IsRejected()
    {
        var preCm07 = new List<int>();
        for (var i = 0; i < BaselineCount; i++)
        {
            preCm07.Add(BaselineBase + i);   // baseline survived at its checkpoint value
        }

        for (var i = 0; i < WindowCount; i++)
        {
            preCm07.Add(0);                  // ...and the window spawns came back alive-but-empty
        }

        RuleMutants.AssertDetects("CM-07", PhantomMarker,
            () => AssertCheckpointContract(preCm07, Census(preCm07), "MUTANT: the pre-CM-07 engine's measured reading"));
    }

    /// <summary>
    /// HAZARD A (RB-05 / RB-06). Suppressing Spawn records LOWERS the max TSN recovery applies from the window, and the resumption floor is a max() over
    /// terms that include it. The argument that this is safe — a dropped Spawn removes both the TSN and everything that TSN produced, so no SURVIVING state
    /// references it — is reasoning, not evidence. This is the evidence: recover, then write, and check the allocator did not hand back an id or a slot that
    /// a survivor already owns. <see cref="RecoveryShadowModel"/>'s equivalent for the flat path is
    /// <c>DifferentialRecoveryOracleTests.PostRecoveryWrite_DoesNotReissueARecoveredEntityId</c>; that fixture has no Checkpoint archetype.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void PostRecoveryWrite_AfterSuppressedSpawns_ReissuesNothingAndStaysReadable()
    {
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);

            var baselineIds = Spawn(dbe, checkpointArchetype: true, BaselineCount, BaselineBase);
            dbe.WriteTickFence(1);
            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));

            // A window whose ONLY records are suppressed Spawns — the worst case for the watermark, since nothing else carries a TSN forward.
            Spawn(dbe, checkpointArchetype: true, WindowCount, WindowBase);
            Mutate(dbe, checkpointArchetype: true, baselineIds, MutationBase);
            dbe.WriteTickFence(2);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);

            var survivors = ReadAllEntityIds(dbe);
            Assert.That(survivors, Has.Count.EqualTo(BaselineCount), "precondition: the window spawns are gone, the checkpointed baseline is not");

            var fresh = Spawn(dbe, checkpointArchetype: true, WindowCount, WindowWrittenBase);

            foreach (var id in fresh)
            {
                Assert.That(survivors, Does.Not.Contain(id),
                    $"post-recovery spawn reissued EntityId {id.RawValue}, which a recovered entity already owns — the key watermark was restored below the "
                    + "population it was restored alongside (RB-06)");
            }

            // Everything must be readable and distinct: a reissued id or a re-claimed cluster slot shows up here as a lost or duplicated value.
            var all = ReadAllValues(dbe, checkpointArchetype: true);
            Assert.That(all, Has.Count.EqualTo(BaselineCount + WindowCount), "survivors + fresh spawns must all be live and enumerable");

            var distinct = new HashSet<int>(all);
            Assert.That(distinct, Has.Count.EqualTo(all.Count),
                "two entities read the same value — a post-recovery spawn landed on a slot a survivor still occupies");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static EntityId[] Spawn(DatabaseEngine dbe, bool checkpointArchetype, int count, int valueBase)
    {
        var ids = new EntityId[count];
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = 0; i < count; i++)
        {
            if (checkpointArchetype)
            {
                var v = new CkptCrashCkptData { Value = valueBase + i };
                ids[i] = tx.Spawn<CkptCrashCkptArch>(CkptCrashCkptArch.Data.Set(in v));
            }
            else
            {
                var v = new CkptCrashWalledData { Value = valueBase + i };
                ids[i] = tx.Spawn<CkptCrashWalledArch>(CkptCrashWalledArch.Data.Set(in v));
            }
        }

        tx.Commit();
        return ids;
    }

    private static void Mutate(DatabaseEngine dbe, bool checkpointArchetype, EntityId[] ids, int valueBase)
    {
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = 0; i < ids.Length; i++)
        {
            if (checkpointArchetype)
            {
                tx.OpenMut(ids[i]).Write(CkptCrashCkptArch.Data).Value = valueBase + i;
            }
            else
            {
                tx.OpenMut(ids[i]).Write(CkptCrashWalledArch.Data).Value = valueBase + i;
            }
        }

        tx.Commit();
    }

    private static List<EntityId> ReadAllEntityIds(DatabaseEngine dbe)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        using var view = tx.Query<CkptCrashCkptArch>().ToView();
        foreach (var id in view.GetEntityEnumerator())
        {
            ids.Add(id);
        }

        tx.Commit();
        return ids;
    }

    private static List<int> ReadAllValues(DatabaseEngine dbe, bool checkpointArchetype)
    {
        var values = new List<int>();
        using var tx = dbe.CreateQuickTransaction();
        if (checkpointArchetype)
        {
            using var view = tx.Query<CkptCrashCkptArch>().ToView();
            foreach (var id in view.GetEntityEnumerator())
            {
                values.Add(tx.Open(id).Read(CkptCrashCkptArch.Data).Value);
            }
        }
        else
        {
            using var view = tx.Query<CkptCrashWalledArch>().ToView();
            foreach (var id in view.GetEntityEnumerator())
            {
                values.Add(tx.Open(id).Read(CkptCrashWalledArch.Data).Value);
            }
        }

        tx.Commit();
        return values;
    }

    private static (int Zeroed, int Baseline, int Window, int WindowWritten, int Mutated, int Other) Census(List<int> values)
    {
        int zeroed = 0, baseline = 0, window = 0, windowWritten = 0, mutated = 0, other = 0;
        foreach (var v in values)
        {
            if (v == 0)
            {
                zeroed++;
            }
            else if (v >= BaselineBase && v < BaselineBase + BaselineCount)
            {
                baseline++;
            }
            else if (v >= WindowBase && v < WindowBase + WindowCount)
            {
                window++;
            }
            else if (v >= WindowWrittenBase && v < WindowWrittenBase + WindowCount)
            {
                windowWritten++;
            }
            else if (v >= MutationBase && v < MutationBase + BaselineCount)
            {
                mutated++;
            }
            else
            {
                other++;
            }
        }

        return (zeroed, baseline, window, windowWritten, mutated, other);
    }
}
