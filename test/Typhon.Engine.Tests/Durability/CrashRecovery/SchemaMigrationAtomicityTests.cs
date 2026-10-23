using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Schema.UnitTest.WalSvMigration", 3, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = CommitDiscipline.Commit)]
[StructLayout(LayoutKind.Sequential)]
struct WalSvMigrationConvertedV3
{
    public float A;
    public int B;
}

[Archetype]
class WalSvMigrationConvertedArch : Archetype<WalSvMigrationConvertedArch>
{
    public static readonly Comp<WalSvMigrationConvertedV3> Value = Register<WalSvMigrationConvertedV3>();
}

/// <summary>
/// A schema migration commits atomically (SCHEMA-09): an open that stops anywhere before the commit leaves the pre-migration image — catalog, segments and WAL
/// window — for the next open to migrate again, and one that stops after it leaves the migrated image (#1469, #1473).
/// </summary>
[TestFixture]
[NonParallelizable]
class SchemaMigrationAtomicityTests : TestBase<SchemaMigrationAtomicityTests>
{
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Wal.WalDirectory = null;
    }

    private sealed class InjectedFault : Exception
    {
        public InjectedFault(string message) : base(message)
        {
        }
    }

    // #1473: the migrating open of a cleanly closed database crashes before its first checkpoint. The entity used to be gone at the next open.
    [TestCase(StorageMode.Versioned)]
    [TestCase(StorageMode.SingleVersion)]
    [VerifiesRule("SCHEMA-09")]
    public void CleanMigratingOpen_CrashingBeforeItsFirstCheckpoint_KeepsTheEntity(StorageMode mode)
    {
        var id = SpawnV1(mode, crash: false, updateAfterCheckpoint: false);

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterV2(dbe, mode);

            // The commit relies on it: nothing written in the new layout reaches the WAL before the catalog flips, or a crash before the flip would leave
            // new-layout records over the old catalog.
            var lsnAtRegistration = dbe.WalManager.LastPublishedLsn;
            var lsnAtStage = -1L;
            dbe.SchemaCommitProbe = s =>
            {
                if (s == SchemaCommitStage.Staged)
                {
                    lsnAtStage = dbe.WalManager.LastPublishedLsn;
                }
            };

            dbe.InitializeArchetypes();
            Assert.That(lsnAtStage, Is.EqualTo(lsnAtRegistration), "the migrating open appended WAL records before its commit");
            dbe.SimulateHardCrash();
        }

        AssertMigratedEntityReadsBack(mode, id);
    }

    // The genuineness proof: with the component rows written ahead of the flip — the pre-SCHEMA-09 order — an open stopped before the flip leaves a catalog
    // that names the new component segments over archetype rows that still name the old cluster, and the verifier must see it.
    [Test]
    [RuleMutant("SCHEMA-09")]
    public void Mutant_ComponentRowsWrittenAheadOfTheFlip_AreDetected()
    {
        var id = SpawnV1(StorageMode.Versioned, crash: false, updateAfterCheckpoint: false);

        Assert.Throws<InjectedFault>(() =>
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            dbe.WriteComponentRowsBeforeTheFlipForTest = true;
            dbe.SchemaCommitProbe = s =>
            {
                if (s == SchemaCommitStage.Staged)
                {
                    throw new InjectedFault("stopped before the flip");
                }
            };
            dbe.InitializeArchetypes();
        });

        RuleMutants.AssertDetects("SCHEMA-09", "SCHEMA-09", () => AssertMigratedEntityReadsBack(StorageMode.Versioned, id));
    }

    // #1469: a migrating crash-open that fails during the replay — here a user function that chokes on the WAL value only — used to leave the new catalog
    // over the old-layout window, so the next open copied the window in raw, silently.
    [Test]
    [VerifiesRule("SCHEMA-09")]
    public void MigratingCrashOpen_FailingDuringTheReplay_LeavesTheWindowForTheRetry()
    {
        var id = SpawnV1(StorageMode.Versioned, crash: true, updateAfterCheckpoint: true);

        Assert.Throws<InvalidOperationException>(() =>
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterMigration<WalMigrationV1, WalMigrationConvertedV5>((ref WalMigrationV1 old, out WalMigrationConvertedV5 value) =>
            {
                if (old.A == 1111)
                {
                    throw new InvalidOperationException("a bug in the user's migration that only the WAL value reaches");
                }
                value = new WalMigrationConvertedV5 { A = old.A, B = old.B };
            });
            dbe.RegisterComponentFromAccessor<WalMigrationConvertedV5>();
            dbe.InitializeArchetypes();
        });

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterMigration<WalMigrationV1, WalMigrationConvertedV5>((ref WalMigrationV1 old, out WalMigrationConvertedV5 value) =>
            {
                value = new WalMigrationConvertedV5 { A = old.A, B = old.B };
            });
            dbe.RegisterComponentFromAccessor<WalMigrationConvertedV5>();
            Assert.That(dbe.HasComponentMigrations, Is.True, "the failed open committed nothing, so this one migrates again");
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            var value = tx.Open(id).Read(WalMigrationConvertedArch.Value);
            Assert.That((value.A, value.B), Is.EqualTo((1111f, 2222)), "the window converted through the function, not copied in raw");
        }
    }

    // An open stopped at each stage of the commit, on both paths. Before the flip the next open migrates again; after it the migration is done, the next open
    // writes whatever rows were left, and the key is gone either way.
    [Test]
    [VerifiesRule("SCHEMA-09")]
    public void CommitInterruptedAtAnyStage_LeavesExactlyOneImage(
        [Values(SchemaCommitStage.Staged, SchemaCommitStage.Committed, SchemaCommitStage.Applied)] SchemaCommitStage stage,
        [Values(false, true)] bool crashOpen)
    {
        var id = SpawnV1(StorageMode.Versioned, crash: crashOpen, updateAfterCheckpoint: crashOpen);

        Assert.Throws<InjectedFault>(() =>
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            dbe.SchemaCommitProbe = s =>
            {
                if (s == stage)
                {
                    throw new InjectedFault($"stopped at {s}");
                }
            };
            dbe.InitializeArchetypes();
        });

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Assert.That(dbe.MMF.Bootstrap.ContainsKey(DatabaseEngine.BK_SchemaCommit), Is.False,
                "a committed migration's rows are written before anything reads them");
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            Assert.That(dbe.HasComponentMigrations, Is.EqualTo(stage == SchemaCommitStage.Staged),
                stage == SchemaCommitStage.Staged ? "not committed: the migration runs again" : "committed: nothing left to migrate");
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            var value = tx.Open(id).Read(WalMigrationV2Arch.Value);
            Assert.That((value.A, value.B, value.C), Is.EqualTo((1111, 2222, 0L)));
        }
    }

    // The checkpoint that would commit fails for good: the open fails, and the database is still the one before it.
    [Test]
    [VerifiesRule("SCHEMA-09")]
    public void CommitCheckpointThatNeverCompletes_FailsTheOpen_AndTheNextOneMigratesAgain()
    {
        var id = SpawnV1(StorageMode.Versioned, crash: false, updateAfterCheckpoint: false);

        var failure = Assert.Throws<InvalidOperationException>(() =>
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            dbe.CheckpointManager.CycleFaultInjector = () => throw new InvalidOperationException("injected fatal checkpoint failure");
            dbe.InitializeArchetypes();
        });
        Assert.That(failure.Message, Does.Contain("could not be committed"));

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            Assert.That(dbe.HasComponentMigrations, Is.True);
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            var value = tx.Open(id).Read(WalMigrationV2Arch.Value);
            Assert.That((value.A, value.B, value.C), Is.EqualTo((1111, 2222, 0L)));
        }
    }

    // A cycle that takes the staged commit and then fails hands it to the next cycle, which commits it.
    [Test]
    [VerifiesRule("SCHEMA-09")]
    public void CommitCycleFailingAfterTakingTheCommit_HandsItToTheNextCycle()
    {
        var id = SpawnV1(StorageMode.Versioned, crash: false, updateAfterCheckpoint: false);

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            var failed = false;
            dbe.SchemaCommitProbe = s =>
            {
                if (s == SchemaCommitStage.Staged)
                {
                    dbe.CheckpointManager.BeforeSettleProbe = () =>
                    {
                        if (!failed)
                        {
                            failed = true;
                            throw new TyphonTimeoutException(TyphonErrorCode.TransactionTimeout, "injected transient checkpoint failure", TimeSpan.Zero);
                        }
                    };
                }
            };
            dbe.InitializeArchetypes();
            Assert.That(failed, Is.True, "precondition: the first commit cycle failed after taking the commit");
            Assert.That(dbe.HasPendingSchemaCommit, Is.False);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            Assert.That(dbe.HasComponentMigrations, Is.False, "the migration committed in the session's second cycle");
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Open(id).Read(WalMigrationV2Arch.Value).A, Is.EqualTo(1111));
        }
    }

    // Before this change a SingleVersion component's cluster bytes were copied verbatim when its migration ran a user function — the function was skipped.
    [Test]
    public void SingleVersionBreakingMigration_RunsTheUserFunctionOverTheCluster()
    {
        var id = SpawnV1(StorageMode.SingleVersion, crash: false, updateAfterCheckpoint: false);

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterMigration<WalSvMigrationV1, WalSvMigrationConvertedV3>((ref WalSvMigrationV1 old, out WalSvMigrationConvertedV3 value) =>
        {
            value = new WalSvMigrationConvertedV3 { A = old.A / 8f, B = old.B + 1 };
        });
        dbe.RegisterComponentFromAccessor<WalSvMigrationConvertedV3>();
        dbe.InitializeArchetypes();

        using var tx = dbe.CreateQuickTransaction();
        var value = tx.Open(id).Read(WalSvMigrationConvertedArch.Value);
        Assert.That((value.A, value.B), Is.EqualTo((1111f / 8f, 2223)));
    }

    [Test]
    public void WalValueOfAnotherSize_IsRefusedRatherThanReinterpreted()
    {
        var migration = new MigrationResult { OldCompSize = 8, FieldMap = [] };
        var ex = Assert.Throws<InvalidDataException>(() => SchemaEvolutionEngine.MigrateWalPayload(in migration, new byte[16], 16, "Probe"));
        Assert.That(ex!.Message, Does.Contain("16 bytes").And.Contain("8-byte"));
    }

    /// <summary>
    /// One V1 entity <c>(1111, 2222)</c>. <paramref name="updateAfterCheckpoint"/>: spawned as (10, 20) and checkpointed, then set to (1111, 2222) — so the
    /// value is only in the WAL window. <paramref name="crash"/>: the session ends in a hard crash rather than a clean close.
    /// </summary>
    private EntityId SpawnV1(StorageMode mode, bool crash, bool updateAfterCheckpoint)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        if (mode == StorageMode.Versioned)
        {
            dbe.RegisterComponentFromAccessor<WalMigrationV1>();
        }
        else
        {
            dbe.RegisterComponentFromAccessor<WalSvMigrationV1>();
        }
        dbe.InitializeArchetypes();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var (a, b) = updateAfterCheckpoint ? (10, 20) : (1111, 2222);
            id = mode == StorageMode.Versioned
                ? tx.Spawn<WalMigrationArch>(WalMigrationArch.Value.Set(new WalMigrationV1 { A = a, B = b }))
                : tx.Spawn<WalSvMigrationArch>(WalSvMigrationArch.Value.Set(new WalSvMigrationV1 { A = a, B = b }));
            tx.Commit();
        }

        if (updateAfterCheckpoint)
        {
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            if (mode == StorageMode.Versioned)
            {
                tx.OpenMut(id).Set(WalMigrationArch.Value, new WalMigrationV1 { A = 1111, B = 2222 });
            }
            else
            {
                tx.OpenMut(id).Set(WalSvMigrationArch.Value, new WalSvMigrationV1 { A = 1111, B = 2222 });
            }
            tx.Commit();
        }

        if (crash)
        {
            dbe.SimulateHardCrash();
        }
        return id;
    }

    private static void RegisterV2(DatabaseEngine dbe, StorageMode mode)
    {
        if (mode == StorageMode.Versioned)
        {
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
        }
        else
        {
            dbe.RegisterComponentFromAccessor<WalSvMigrationV2>();
        }
    }

    /// <summary>The verifier: the migrated database reads the entity back as <c>(1111, 2222, 0)</c>.</summary>
    private void AssertMigratedEntityReadsBack(StorageMode mode, EntityId id)
    {
        (int A, int B, long C) value;
        try
        {
            value = ReadV2(mode, id);
        }
        catch (Exception e)
        {
            Assert.Fail($"SCHEMA-09: the migrated database does not read the entity back: {e.GetType().Name}: {e.Message}");
            return;
        }

        Assert.That(value, Is.EqualTo((1111, 2222, 0L)), "SCHEMA-09: the migrated database reads the entity back wrong");
    }

    private (int A, int B, long C) ReadV2(StorageMode mode, EntityId id)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterV2(dbe, mode);
        dbe.InitializeArchetypes();
        using var tx = dbe.CreateQuickTransaction();
        if (mode == StorageMode.Versioned)
        {
            var v = tx.Open(id).Read(WalMigrationV2Arch.Value);
            return (v.A, v.B, v.C);
        }

        var sv = tx.Open(id).Read(WalSvMigrationV2Arch.Value);
        return (sv.A, sv.B, sv.C);
    }
}
