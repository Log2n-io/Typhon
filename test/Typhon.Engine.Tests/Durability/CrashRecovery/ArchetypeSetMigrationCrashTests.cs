using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The archetype component-set migration on the crash path (#1394 slice 4, claude/design/Schema/07-archetype-set-evolution.md §7, §11). A WAL window written
// under the old set is in the COMMITTED slot numbering (SCHEMA-09): the replay translates every record to the runtime numbering. The commit's stages leave
// exactly one image, the names buffers survive (or are reclaimed by) the crash open's sweep, and the migration's realm hints reach the recovery rebuild (D12).
// Every scenario owns its components, as in ArchetypeSetMigrationTests: an archetype class is process-wide.
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region Every component shifts: Ins is inserted at slot 0

/// <summary>SingleVersion under the tick-fence discipline: its window values travel in FenceBlock records.</summary>
[Component("Typhon.Schema.UnitTest.AsmcA", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcA
{
    public int A;
    public int Pad;
    public AsmcA(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmcV", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcV
{
    public long V;
    public AsmcV(long v) => V = v;
}

/// <summary>SingleVersion under the commit discipline: its window values travel in Slot records.</summary>
[Component("Typhon.Schema.UnitTest.AsmcB", 1, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = CommitDiscipline.Commit)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcB
{
    public long B;
    public AsmcB(long b) => B = b;
}

/// <summary>Versioned with a collection: its window content travels in CollectionDelta records.</summary>
[Component("Typhon.Schema.UnitTest.AsmcC", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct AsmcC
{
    [Field]
    public ComponentCollection<int> Items;

    public int Seq;
}

[Component("Typhon.Schema.UnitTest.AsmcIns", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcIns
{
    public long I;
}

[Archetype(1, Name = "Asmc.Wal")]
class AsmcV1Arch : Archetype<AsmcV1Arch>
{
    public static readonly Comp<AsmcA> A = Register<AsmcA>();
    public static readonly Comp<AsmcV> V = Register<AsmcV>();
    public static readonly Comp<AsmcB> B = Register<AsmcB>();
    public static readonly Comp<AsmcC> C = Register<AsmcC>();
}

[Archetype(2, Name = "Asmc.Wal")]
class AsmcV2Arch : Archetype<AsmcV2Arch>
{
    public static readonly Comp<AsmcIns> Ins = Register<AsmcIns>();
    public static readonly Comp<AsmcA> A = Register<AsmcA>();
    public static readonly Comp<AsmcV> V = Register<AsmcV>();
    public static readonly Comp<AsmcB> B = Register<AsmcB>();
    public static readonly Comp<AsmcC> C = Register<AsmcC>();
}

#endregion

#region R9 on a crash open: a destroyed entity's chain of the added component

[Component("Typhon.Schema.UnitTest.AsmcR9Key", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcR9Key
{
    public long K;
    public AsmcR9Key(long k) => K = k;
}

/// <summary>Same size and mode as <see cref="AsmcR9Added"/>: the stored list is tampered to name it in Added's slot, so the old geometry stays exact.</summary>
[Component("Typhon.Schema.UnitTest.AsmcR9Ghost", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcR9Ghost
{
    public long G;
}

[Component("Typhon.Schema.UnitTest.AsmcR9Added", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcR9Added
{
    public long A;
    public AsmcR9Added(long a) => A = a;
}

[Archetype(1, Name = "Asmc.R9")]
class AsmcR9V1Arch : Archetype<AsmcR9V1Arch>
{
    public static readonly Comp<AsmcR9Key> Key = Register<AsmcR9Key>();
    public static readonly Comp<AsmcR9Added> Added = Register<AsmcR9Added>();
}

[Archetype(2, Name = "Asmc.R9")]
class AsmcR9V2Arch : Archetype<AsmcR9V2Arch>
{
    public static readonly Comp<AsmcR9Key> Key = Register<AsmcR9Key>();
    public static readonly Comp<AsmcR9Ghost> Ghost = Register<AsmcR9Ghost>();
    public static readonly Comp<AsmcR9Added> Added = Register<AsmcR9Added>();
}

#endregion

#region Realm-keyed spatial archetype whose added component shrinks the cluster (D12)

[Component("Typhon.Schema.UnitTest.AsmcHintPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcHintPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public ushort Realm;

    [Field]
    public ushort Pad;

    [Field]
    public int Tag;
}

[Component("Typhon.Schema.UnitTest.AsmcHintStats", 1, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = CommitDiscipline.Commit)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcHintStats
{
    public int Hp;
    public int Pad;
    public AsmcHintStats(int hp) { Hp = hp; Pad = 0; }
}

/// <summary>With <see cref="AsmcHintWide"/>, wide enough to shrink the cluster size: every old cluster splits, a child starts in its middle.</summary>
[Component("Typhon.Schema.UnitTest.AsmcHintBig", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcHintBig
{
    public long B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10, B11, B12, B13;
}

[Component("Typhon.Schema.UnitTest.AsmcHintWide", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcHintWide
{
    public long W0, W1, W2, W3, W4, W5, W6;
}

[Archetype(1, Name = "Asmc.Hint", ClusterDurability = ClusterDurability.Checkpoint)]
partial class AsmcHintV1Arch : Archetype<AsmcHintV1Arch>
{
    public static readonly Comp<AsmcHintPos> Pos = Register<AsmcHintPos>();
    public static readonly Comp<AsmcHintStats> Stats = Register<AsmcHintStats>();
}

[Archetype(2, Name = "Asmc.Hint", ClusterDurability = ClusterDurability.Checkpoint)]
partial class AsmcHintV2Arch : Archetype<AsmcHintV2Arch>
{
    public static readonly Comp<AsmcHintPos> Pos = Register<AsmcHintPos>();
    public static readonly Comp<AsmcHintStats> Stats = Register<AsmcHintStats>();
    public static readonly Comp<AsmcHintBig> Big = Register<AsmcHintBig>();
    public static readonly Comp<AsmcHintWide> Wide = Register<AsmcHintWide>();
}

#endregion

#region R12 over the WAL window: an archetype empty at the checkpoint gains its spatial component, its spawns are in the window

[Component("Typhon.Schema.UnitTest.AsmcR12Stats", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcR12Stats
{
    public int Hp;
    public int Pad;
    public AsmcR12Stats(int hp) { Hp = hp; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmcR12Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcR12Pos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public int Tag;

    [Field]
    public int Pad;
}

[Archetype(1, Name = "Asmc.R12")]
class AsmcR12V1Arch : Archetype<AsmcR12V1Arch>
{
    public static readonly Comp<AsmcR12Stats> Stats = Register<AsmcR12Stats>();
}

[Archetype(2, Name = "Asmc.R12")]
class AsmcR12V2Arch : Archetype<AsmcR12V2Arch>
{
    public static readonly Comp<AsmcR12Stats> Stats = Register<AsmcR12Stats>();
    public static readonly Comp<AsmcR12Pos> Pos = Register<AsmcR12Pos>();
}

#endregion

#region R7 over the WAL window: one entity at the checkpoint, more spawned in the window, an added unique dense index

[Component("Typhon.Schema.UnitTest.AsmcR7Key", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcR7Key
{
    public long K;
    public AsmcR7Key(long k) => K = k;
}

[Component("Typhon.Schema.UnitTest.AsmcR7Added", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcR7Added
{
    [Index]
    public int Code;
    public int Pad;
}

[Archetype(1, Name = "Asmc.R7")]
class AsmcR7V1Arch : Archetype<AsmcR7V1Arch>
{
    public static readonly Comp<AsmcR7Key> Key = Register<AsmcR7Key>();
}

[Archetype(2, Name = "Asmc.R7")]
class AsmcR7V2Arch : Archetype<AsmcR7V2Arch>
{
    public static readonly Comp<AsmcR7Key> Key = Register<AsmcR7Key>();
    public static readonly Comp<AsmcR7Added> Added = Register<AsmcR7Added>();
}

#endregion

#region R7 over chain-only entities: one entity in the old cluster, the others found through their chains

[Component("Typhon.Schema.UnitTest.AsmcR7cKey", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcR7cKey
{
    public long K;
    public AsmcR7cKey(long k) => K = k;
}

[Component("Typhon.Schema.UnitTest.AsmcR7cAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmcR7cAdded
{
    [Index]
    public int Code;
    public int Pad;
}

[Archetype(1, Name = "Asmc.R7c")]
class AsmcR7cV1Arch : Archetype<AsmcR7cV1Arch>
{
    public static readonly Comp<AsmcR7cKey> Key = Register<AsmcR7cKey>();
}

[Archetype(2, Name = "Asmc.R7c")]
class AsmcR7cV2Arch : Archetype<AsmcR7cV2Arch>
{
    public static readonly Comp<AsmcR7cKey> Key = Register<AsmcR7cKey>();
    public static readonly Comp<AsmcR7cAdded> Added = Register<AsmcR7cAdded>();
}

#endregion

/// <summary>
/// The archetype component-set migration on the crash path (#1394, design 07 §7, §11): WAL translation, the commit's stages, the names buffers across a
/// crash sweep, chain-only entities, R9 on a crash open, and the realm hints of the recovery rebuild.
/// </summary>
[TestFixture]
[NonParallelizable]
class ArchetypeSetMigrationCrashTests : TestBase<ArchetypeSetMigrationCrashTests>
{
    private const int Population = 200;
    private const string Ins = "Typhon.Schema.UnitTest.AsmcIns";

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

    private static ushort WalArchetypeId => Archetype<AsmcV2Arch>.Metadata.ArchetypeId;

    // ── The "Asmc.Wal" scenario ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void RegisterWal(DatabaseEngine dbe, bool v2)
    {
        dbe.RegisterComponentFromAccessor<AsmcA>();
        dbe.RegisterComponentFromAccessor<AsmcV>();
        dbe.RegisterComponentFromAccessor<AsmcB>();
        dbe.RegisterComponentFromAccessor<AsmcC>();
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<AsmcIns>();
        }
    }

    private static DatabaseEngine OpenWal(IServiceScope scope, bool v2)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RecordRebuildPlacementForTest = true;
        RegisterWal(dbe, v2);
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>The values entity <paramref name="i"/> holds: <paramref name="window"/> selects those the WAL window wrote over the seed's.</summary>
    private static (int A, long V, long B) Expected(int i, bool window) => window ? (i + 1000, i * 10L + 7, i * 100L + 3) : (i, i * 10L, i * 100L);

    /// <summary>
    /// Spawns <see cref="Population"/> entities of revision 1, checkpoints them, then — <paramref name="window"/> — writes every value again above the
    /// checkpoint: A through a tick fence (FenceBlock records), V and B at commit (Slot records). <paramref name="crash"/>: the session ends in a hard crash.
    /// </summary>
    private List<EntityId> SeedWal(bool window, bool crash)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenWal(scope, false);
        var ids = new List<EntityId>();
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < Population; i++)
            {
                var (a, v, b) = Expected(i, false);
                ids.Add(t.Spawn<AsmcV1Arch>(AsmcV1Arch.A.Set(new AsmcA(a)), AsmcV1Arch.V.Set(new AsmcV(v)), AsmcV1Arch.B.Set(new AsmcB(b)),
                    AsmcV1Arch.C.Set(new AsmcC { Seq = i })));
            }

            Assert.That(t.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);

        if (window)
        {
            // Two transactions: a Commit-discipline write escalates its whole transaction (CM-02), which would carry A at commit too.
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    var (a, v, _) = Expected(i, true);
                    var e = t.OpenMut(ids[i]);
                    e.Set(AsmcV1Arch.A, new AsmcA(a));
                    e.Set(AsmcV1Arch.V, new AsmcV(v));
                }

                Assert.That(t.Commit(), Is.True);
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    t.OpenMut(ids[i]).Set(AsmcV1Arch.B, new AsmcB(Expected(i, true).B));
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.WriteTickFence(2);
        }

        if (crash)
        {
            dbe.SimulateHardCrash();
        }

        return ids;
    }

    private static void AssertWalValues(DatabaseEngine dbe, List<EntityId> ids, bool v2, bool window)
    {
        using var t = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var e = t.Open(ids[i]);
                var actual = v2
                    ? (e.Read(AsmcV2Arch.A).A, e.Read(AsmcV2Arch.V).V, e.Read(AsmcV2Arch.B).B)
                    : (e.Read(AsmcV1Arch.A).A, e.Read(AsmcV1Arch.V).V, e.Read(AsmcV1Arch.B).B);
                Assert.That(actual, Is.EqualTo(Expected(i, window)), $"entity {i}");
            }
        });
    }

    /// <summary>The persisted row of "Asmc.Wal": its component count, revision and names buffer.</summary>
    private static (byte Count, int Revision, int NamesBuffer) WalRow(DatabaseEngine dbe)
    {
        var arch = dbe._persistedArchetypes["Asmc.Wal"].Arch;
        return (arch.ComponentCount, arch.Revision, arch.ComponentNames._bufferId);
    }

    // ── WAL translation (AC-19) ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("SCHEMA-11")]
    [VerifiesRule("LOG-06")]
    [CancelAfter(20_000)]
    public void CrashOpen_WindowUnderOldSet_SlotAndFenceBlockReplayByName()
    {
        var ids = SeedWal(window: true, crash: true);

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenWal(scope, true);
        Assert.Multiple(() =>
        {
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the open migrated the set");
            Assert.That(dbe.LastWalV2RecoveryResult.FenceBlockRecordsExpanded, Is.GreaterThan(0), "premise: A's window values came as FenceBlock records");
            Assert.That(dbe.LastWalV2RecoveryResult.RecordsApplied, Is.GreaterThan(0), "premise: the window was replayed");
        });

        // Every component moved one slot up: replayed in the committed numbering, A's values would have landed on Ins, V's on A, and so on.
        AssertWalValues(dbe, ids, v2: true, window: true);
        ArchetypeOracle.AssertAbsent(ArchetypeOracle.Snapshot(dbe, WalArchetypeId, ids), Ins);
    }

    [Test]
    [VerifiesRule("SCHEMA-11")]
    [CancelAfter(20_000)]
    public void CrashOpen_WindowUnderOldSet_SpawnAndSetEnabledBitsRemapped()
    {
        var ids = SeedWal(window: false, crash: false);
        var spawned = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, false);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                // Above the checkpoint: B disabled on every third existing entity, V on every fifth (SetEnabledBits records).
                for (var i = 0; i < ids.Count; i++)
                {
                    if (i % 3 == 0)
                    {
                        t.OpenMut(ids[i]).Disable(AsmcV1Arch.B);
                    }

                    if (i % 5 == 0)
                    {
                        t.OpenMut(ids[i]).Disable(AsmcV1Arch.V);
                    }
                }

                // And spawns (Spawn records), every other one with V never supplied: its spawn mask has V's bit clear.
                for (var i = 0; i < 40; i++)
                {
                    spawned.Add(i % 2 == 0
                        ? t.Spawn<AsmcV1Arch>(AsmcV1Arch.A.Set(new AsmcA(5000 + i)), AsmcV1Arch.B.Set(new AsmcB(6000 + i)), AsmcV1Arch.C.Set(new AsmcC()))
                        : t.Spawn<AsmcV1Arch>(AsmcV1Arch.A.Set(new AsmcA(5000 + i)), AsmcV1Arch.V.Set(new AsmcV(7000 + i)),
                            AsmcV1Arch.B.Set(new AsmcB(6000 + i)), AsmcV1Arch.C.Set(new AsmcC())));
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, true);
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
            var all = new List<EntityId>(ids);
            all.AddRange(spawned);
            var after = ArchetypeOracle.Snapshot(dbe, WalArchetypeId, all);
            ArchetypeOracle.AssertAbsent(after, Ins);

            var failures = new List<string>();
            void Check(EntityId id, string name, bool enabled, bool present)
            {
                var entry = after[(long)id.RawValue][name];
                if (entry.Enabled != enabled || entry.ClusterBit != enabled || entry.Present != present)
                {
                    failures.Add($"0x{id.RawValue:X} {name}: enabled {entry.Enabled} / cluster {entry.ClusterBit} / present {entry.Present}, expected "
                                 + $"{enabled} / {enabled} / {present}");
                }
            }

            for (var i = 0; i < ids.Count; i++)
            {
                Check(ids[i], "Typhon.Schema.UnitTest.AsmcA", true, true);
                Check(ids[i], "Typhon.Schema.UnitTest.AsmcV", i % 5 != 0, true);
                Check(ids[i], "Typhon.Schema.UnitTest.AsmcB", i % 3 != 0, true);
            }

            for (var i = 0; i < spawned.Count; i++)
            {
                Check(spawned[i], "Typhon.Schema.UnitTest.AsmcA", true, true);
                Check(spawned[i], "Typhon.Schema.UnitTest.AsmcV", i % 2 != 0, i % 2 != 0);
                Check(spawned[i], "Typhon.Schema.UnitTest.AsmcB", true, true);
            }

            Assert.That(failures, Is.Empty, $"{failures.Count} enabled state(s) not remapped by name; first: {(failures.Count > 0 ? failures[0] : "")}");

            // The spawned entities' Versioned values: their Slot records were translated with their Spawn records. (Their SingleVersion values are not
            // asserted: in this shape they recover as zero with or without a migration — a pre-existing recovery behaviour, outside this design.)
            using var t = dbe.CreateQuickTransaction();
            for (var i = 1; i < spawned.Count; i += 2)
            {
                Assert.That(t.Open(spawned[i]).Read(AsmcV2Arch.V).V, Is.EqualTo(7000L + i), $"spawned {i}: V");
            }
        }
    }

    [Test]
    [VerifiesRule("SCHEMA-11")]
    [CancelAfter(20_000)]
    public void CrashOpen_WindowUnderOldSet_CollectionDeltaRemapped()
    {
        var ids = SeedWal(window: false, crash: false);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, false);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    var e = t.OpenMut(ids[i]);
                    var c = e.Read(AsmcV1Arch.C);
                    using (var cca = e.CreateComponentCollectionAccessor(AsmcV1Arch.C, ref c, ref c.Items))
                    {
                        cca.Replace(ContentOf(i));
                    }
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, true);
            Assert.Multiple(() =>
            {
                Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
                Assert.That(dbe.LastWalV2RecoveryResult.CollectionFoldsFlushed, Is.GreaterThan(0), "premise: the CollectionDelta records were folded");
            });

            // C moved from slot 3 to slot 4: in the committed numbering its deltas name slot 3, which is B now — no collection field, content dropped.
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i++)
            {
                var c = t.Open(ids[i]).Read(AsmcV2Arch.C);
                using var cca = t.CreateComponentCollectionAccessor(ref c.Items);
                var actual = new int[cca.ElementCount];
                cca.GetAllElements(actual);
                Assert.That(actual, Is.EqualTo(ContentOf(i)), $"entity {i}");
                Assert.That(c.Seq, Is.EqualTo(i), $"entity {i}: Seq");
            }
        }
    }

    private static int[] ContentOf(int i)
    {
        var content = new int[i % 4 + 1];
        for (var k = 0; k < content.Length; k++)
        {
            content[k] = i * 10 + k;
        }

        return content;
    }

    // The window holds values of the committed layout only (SCHEMA-09); a value of another size cannot be one, and the open is refused rather than placing
    // it — end to end, through the replay. A component's committed size is what the persisted catalog records, so the test states a catalog whose size
    // for A (FenceBlock records) or B (Slot records) is not the one the window was written at (AdjustPersistedLayoutSizesForTest). The archetype is empty
    // at the checkpoint, so the pre-migration cluster has nothing a wrong size could misread: the window's records are the only thing that can refuse.
    [Test]
    [VerifiesRule("SCHEMA-09")]
    [CancelAfter(20_000)]
    public void CrashOpen_PayloadOfAnotherSize_IsRefused([Values(false, true)] bool fenceBlock)
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, false);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the empty archetype's row reaches the file");

            var ids = new List<EntityId>();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 20; i++)
                {
                    // B is Commit discipline and escalates its transaction (CM-02): spawned without it, A's values travel only in the fence's FenceBlock.
                    ids.Add(fenceBlock
                        ? t.Spawn<AsmcV1Arch>(AsmcV1Arch.A.Set(new AsmcA(i)), AsmcV1Arch.V.Set(new AsmcV(i)))
                        : t.Spawn<AsmcV1Arch>(AsmcV1Arch.A.Set(new AsmcA(i)), AsmcV1Arch.V.Set(new AsmcV(i)), AsmcV1Arch.B.Set(new AsmcB(i))));
                }

                Assert.That(t.Commit(), Is.True);
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                foreach (var id in ids)
                {
                    if (fenceBlock)
                    {
                        t.OpenMut(id).Set(AsmcV1Arch.A, new AsmcA(7));
                    }
                    else
                    {
                        t.OpenMut(id).Set(AsmcV1Arch.B, new AsmcB(7));
                    }
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.WriteTickFence(1);
            dbe.SimulateHardCrash();
        }

        var committedSlot = fenceBlock ? 0 : 2;   // A, or B, in the committed numbering {A, V, B, C}
        var component = fenceBlock ? "Typhon.Schema.UnitTest.AsmcA" : "Typhon.Schema.UnitTest.AsmcB";
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterWal(dbe, true);
            dbe.AdjustPersistedLayoutSizesForTest = (archetype, sizes) =>
            {
                if (archetype == "Asmc.Wal")
                {
                    sizes[committedSlot] += 8;
                }
            };

            var thrown = Assert.Catch(() => dbe.InitializeArchetypes(), "the open must refuse a window value of another size");
            var refusal = thrown;
            while (refusal != null && refusal is not InvalidDataException)
            {
                refusal = refusal.InnerException;
            }

            Assert.Multiple(() =>
            {
                Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
                Assert.That(refusal, Is.Not.Null, $"refused for the value's size, not for anything else: {thrown}");
                Assert.That(refusal?.Message, Does.StartWith(
                    $"A WAL value of component '{component}' of archetype 'Asmc.Wal' is 8 bytes, but the committed component set stores it as 16 bytes."));
            });
        }
    }

    // The translation hooks themselves, called while the crash open's set migration is live (its commit's Staged probe runs after the replay, before
    // CompleteSchemaCommit retires the translation): the committed → runtime slot and bit maps, a slot beyond the committed set refused, and the size check
    // on both sides of the committed size.
    [Test]
    [CancelAfter(20_000)]
    public void CrashOpen_TranslationHooks_MapAndRefuseByTheCommittedSet()
    {
        SeedWal(window: true, crash: true);

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterWal(dbe, true);
        var routing = (ushort)0;
        Exception wrongSize = null;
        Exception removed = null;
        Exception rightSize = null;
        var translated = (ushort)0xFFFF;
        var enabled = (ushort)0;
        dbe.SchemaCommitProbe = s =>
        {
            if (s != SchemaCommitStage.Staged)
            {
                return;
            }

            routing = dbe._routingByCatalog[WalArchetypeId];
            wrongSize = Capture(() => dbe.ThrowIfWalPayloadIsNotCommittedSize(routing, 2, 16));
            rightSize = Capture(() => dbe.ThrowIfWalPayloadIsNotCommittedSize(routing, 2, 8));
            removed = Capture(() => dbe.TranslateWalSlot(routing, 4, out _));
            dbe.TranslateWalSlot(routing, 2, out translated);
            enabled = dbe.TranslateWalEnabledBits(routing, 0b1111);
        };
        dbe.InitializeArchetypes();

        Assert.Multiple(() =>
        {
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
            Assert.That(wrongSize, Is.InstanceOf<InvalidDataException>());
            Assert.That(rightSize, Is.Null, "a value of the committed size passes");
            Assert.That(removed, Is.InstanceOf<InvalidDataException>(), "a slot beyond the committed set is refused, never placed");
            Assert.That(translated, Is.EqualTo(3), "committed slot 2 (B) is runtime slot 3");
            Assert.That(enabled, Is.EqualTo(0b11110), "each committed bit moves up one slot; the added slot 0 is clear");
        });
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    // ── The commit (AC-18, AC-20) ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("SCHEMA-09")]
    public void SetMigration_AppendsNoWalBeforeCommit()
    {
        var ids = SeedWal(window: false, crash: false);

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterWal(dbe, true);
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
        Assert.Multiple(() =>
        {
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the open migrated the set");
            Assert.That(lsnAtStage, Is.EqualTo(lsnAtRegistration), "the migrating open appended WAL records before its commit");
        });

        dbe.SimulateHardCrash();
        using var reopen = ServiceProvider.CreateScope();
        using var next = OpenWal(reopen, true);
        Assert.That(next.LastOpenArchetypeSetMigrations, Is.Empty, "the commit was durable: nothing left to migrate");
        AssertWalValues(next, ids, v2: true, window: false);
    }

    // An open stopped at every stage of the set commit, on both paths, and at both stages of the next open's own write of a committed commit. Before the flip
    // the file is the old image, which opens under the old schema and migrates again under the new one; after it, the new image.
    [Test]
    [VerifiesRule("SCHEMA-09")]
    [CancelAfter(30_000)]
    public void SetCommitInterruptedAtAnyStage_LeavesExactlyOneImage(
        [Values(SchemaCommitStage.Staged, SchemaCommitStage.Committed, SchemaCommitStage.Applied, SchemaCommitStage.ComponentRowsAppliedAtOpen,
            SchemaCommitStage.AppliedAtOpen)] SchemaCommitStage stage,
        [Values(false, true)] bool crashOpen)
    {
        var ids = SeedWal(window: crashOpen, crash: crashOpen);
        var atOpen = stage is SchemaCommitStage.ComponentRowsAppliedAtOpen or SchemaCommitStage.AppliedAtOpen;
        var stopAt = atOpen ? SchemaCommitStage.Committed : stage;

        Assert.Throws<InjectedFault>(() =>
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterWal(dbe, true);
            dbe.SchemaCommitProbe = s =>
            {
                if (s == stopAt)
                {
                    throw new InjectedFault($"stopped at {s}");
                }
            };
            dbe.InitializeArchetypes();
        });

        if (atOpen)
        {
            DatabaseEngine.SchemaCommitAtOpenProbeForTest = s =>
            {
                if (s == stage)
                {
                    throw new InjectedFault($"stopped at {s}");
                }
            };
            try
            {
                var stopped = Assert.Catch(() =>
                {
                    using var scope = ServiceProvider.CreateScope();
                    using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
                });
                Assert.That(stopped, Is.InstanceOf<InjectedFault>().Or.Property(nameof(Exception.InnerException)).InstanceOf<InjectedFault>());
            }
            finally
            {
                DatabaseEngine.SchemaCommitAtOpenProbeForTest = null;
            }
        }

        if (stage == SchemaCommitStage.Staged)
        {
            // Not committed: the old image, under the old schema.
            using (var scope = ServiceProvider.CreateScope())
            {
                using var dbe = OpenWal(scope, false);
                Assert.Multiple(() =>
                {
                    Assert.That(WalRow(dbe).Count, Is.EqualTo(4), "the old row");
                    Assert.That(WalRow(dbe).Revision, Is.EqualTo(1));
                    Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty);
                });
                AssertWalValues(dbe, ids, v2: false, window: crashOpen);
            }
        }

        // The new schema: the migration runs (again) when it had not committed, and is done otherwise.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, true);
            Assert.Multiple(() =>
            {
                Assert.That(dbe.MMF.Bootstrap.ContainsKey(DatabaseEngine.BK_SchemaCommit), Is.False,
                    "a committed commit's rows are written before anything reads them");
                Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(stage == SchemaCommitStage.Staged ? 1 : 0),
                    stage == SchemaCommitStage.Staged ? "not committed: the migration runs again" : "committed: nothing left to migrate");
                Assert.That(WalRow(dbe).Count, Is.EqualTo(5), "the new row");
                Assert.That(WalRow(dbe).Revision, Is.EqualTo(2));
                Assert.That(dbe.ReadPersistedComponentNames(dbe._persistedArchetypes["Asmc.Wal"].Arch, "Asmc.Wal")[0], Is.EqualTo(Ins),
                    "the new names buffer, in runtime slot order");
            });
            AssertWalValues(dbe, ids, v2: true, window: crashOpen);
            ArchetypeOracle.AssertAbsent(ArchetypeOracle.Snapshot(dbe, WalArchetypeId, ids), Ins);
        }
    }

    // A crash open with a set migration allocates the new names buffer after its collection sweep (design 07 §5.5), and the sweep of the next crash open
    // keeps it: the row it reads names it.
    [Test]
    [VerifiesRule("SCHEMA-09")]
    [CancelAfter(20_000)]
    public void CrashSweep_KeepsTheNewNamesBuffer()
    {
        var ids = SeedWal(window: true, crash: true);

        int newBuffer;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, true);
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the open migrated the set");
            newBuffer = WalRow(dbe).NamesBuffer;
            Assert.That(dbe.GetComponentCollectionSegment<String64>().IsChunkAllocated(newBuffer), Is.True, "the committed row names a live buffer");
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, true);
            Assert.Multiple(() =>
            {
                Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open, whose sweep runs");
                Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty);
                Assert.That(WalRow(dbe).NamesBuffer, Is.EqualTo(newBuffer));
                Assert.That(dbe.GetComponentCollectionSegment<String64>().IsChunkAllocated(newBuffer), Is.True, "the sweep kept the named buffer");
                Assert.That(dbe.ReadPersistedComponentNames(dbe._persistedArchetypes["Asmc.Wal"].Arch, "Asmc.Wal"),
                    Is.EqualTo(new[] { Ins, "Typhon.Schema.UnitTest.AsmcA", "Typhon.Schema.UnitTest.AsmcV", "Typhon.Schema.UnitTest.AsmcB",
                        "Typhon.Schema.UnitTest.AsmcC" }));
            });
            AssertWalValues(dbe, ids, v2: true, window: true);
        }
    }

    // A clean open stopped after its rows are applied and before the replaced names buffer is freed: the next open is a crash open (the failed-open marker),
    // and its sweep reclaims the old buffer, named by nothing (design 07 §5.5, R-6).
    [Test]
    [VerifiesRule("SCHEMA-09")]
    [CancelAfter(20_000)]
    public void OldNamesBuffer_ReclaimedAfterCrashBetweenFlipAndFree()
    {
        var ids = SeedWal(window: false, crash: false);
        int oldBuffer;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, false);
            oldBuffer = WalRow(dbe).NamesBuffer;
        }

        var allocatedAtApplied = false;
        Assert.Throws<InjectedFault>(() =>
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterWal(dbe, true);
            dbe.SchemaCommitProbe = s =>
            {
                if (s == SchemaCommitStage.Applied)
                {
                    allocatedAtApplied = dbe.GetComponentCollectionSegment<String64>().IsChunkAllocated(oldBuffer);
                    throw new InjectedFault("stopped after the rows, before the frees");
                }
            };
            dbe.InitializeArchetypes();
        });
        Assert.That(allocatedAtApplied, Is.True, "premise: the old names buffer is still allocated when the open stops");

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, true);
            var names = dbe.GetComponentCollectionSegment<String64>();
            var newBuffer = WalRow(dbe).NamesBuffer;
            Assert.Multiple(() =>
            {
                Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: the open after a failed one takes the crash path, whose sweep runs");
                Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty, "premise: the migration had committed");
                Assert.That(newBuffer, Is.Not.EqualTo(oldBuffer));
                Assert.That(names.IsChunkAllocated(oldBuffer), Is.False, "the sweep reclaimed the old names buffer");
                Assert.That(names.IsChunkAllocated(newBuffer), Is.True, "and kept the new one");
            });
            AssertWalValues(dbe, ids, v2: true, window: false);
        }
    }

    // ── Chain-only entities (AC-16, crash) ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    // A checkpoint is not page-consistent across segments (design 07 §6.2): the revision chains of an entity can reach the file while its cluster slot does
    // not. Staged here by clearing the entity's occupancy bit in the cluster page before the last checkpoint.
    [Test]
    [VerifiesRule("SCHEMA-12")]
    [CancelAfter(20_000)]
    public void CrashOpen_ChainOnlyEntities_ArePlacedNotLost()
    {
        var ids = SeedWal(window: false, crash: false);
        var lost = ids[17];
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, false);
            ClearOccupancyBits(dbe, Archetype<AsmcV1Arch>.Metadata.ArchetypeId, lost);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, true);
            Assert.Multiple(() =>
            {
                Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
                Assert.That(dbe.LastOpenMigrationOrphanChainEntities, Is.EqualTo(1), "the entity found through its chains alone is counted");
                Assert.That(dbe.LastOpenRebuildPlacementMap[WalArchetypeId].Values, Has.Some.Contains(-1), "and placed after every old cluster");
            });

            using var t = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                Assert.That(t.IsAlive(lost), Is.True, "never lost");
                Assert.That(t.Open(lost).Read(AsmcV2Arch.V).V, Is.EqualTo(Expected(17, false).V), "its Versioned value comes from its chain");
                for (var i = 0; i < ids.Count; i++)
                {
                    Assert.That(t.IsAlive(ids[i]), Is.True, $"entity {i}");
                }
            });
        }
    }

    // ── R9 on a crash open (owner, 2026-11-02) ────────────────────────────────────────────────────────────────────────────────────────────────────────

    // An entity destroyed before the checkpoint, while a reader held its cleanup back: the file holds its chains, tombstoned, and a free cluster slot. A crash
    // open does not drop tombstoned heads, so R9 must count only the chains of entities the migration places — and the destroyed one is not placed.
    [Test]
    [CancelAfter(20_000)]
    public void CrashOpen_DestroyedEntityChain_DoesNotTripR9()
    {
        var survivors = new List<EntityId>();
        var destroyed = new List<EntityId>();
        ushort routingId;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterR9(dbe);
            dbe.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(AsmcR9V2Arch);
            dbe.InitializeArchetypes();
            var all = new List<EntityId>();
            // Only the entities about to be destroyed carry Added: after the tamper below, the live ones have no chain of it, so R9 has nothing live to count.
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 20; i++)
                {
                    all.Add(i % 4 == 1
                        ? t.Spawn<AsmcR9V1Arch>(AsmcR9V1Arch.Key.Set(new AsmcR9Key(i)), AsmcR9V1Arch.Added.Set(new AsmcR9Added(i + 100)))
                        : t.Spawn<AsmcR9V1Arch>(AsmcR9V1Arch.Key.Set(new AsmcR9Key(i))));
                }

                Assert.That(t.Commit(), Is.True);
            }

            routingId = dbe.RoutingIdOf(Archetype<AsmcR9V1Arch>.Metadata);

            // The spawns' row (NextEntityKey) is written by this checkpoint; the tampered names below are then left alone by the next one.
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            RewriteStoredNames(dbe, "Asmc.R9", "Typhon.Schema.UnitTest.AsmcR9Key", "Typhon.Schema.UnitTest.AsmcR9Ghost");

            using (var reader = dbe.CreateQuickTransaction())
            {
                using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
                {
                    for (var i = 0; i < all.Count; i++)
                    {
                        if (i % 4 == 1)
                        {
                            t.Destroy(all[i]);
                            destroyed.Add(all[i]);
                        }
                        else
                        {
                            survivors.Add(all[i]);
                        }
                    }

                    Assert.That(t.Commit(), Is.True);
                }

                Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            }

            // The reader's release runs the cleanup that frees the chains, in memory only: the crash loses it, the file keeps the checkpoint's image.
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterR9(dbe);

            // Premise: the file holds the destroyed entities' chains of Added, carrying the archetype's routing id, and a crash open keeps their heads.
            Dictionary<long, int> heads;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                heads = ComponentRevisionManager.EnumerateVersionedChainHeads(dbe.GetComponentTable<AsmcR9Added>(), routingId);
            }

            Assert.That(heads.Keys, Is.EquivalentTo(Array.ConvertAll(destroyed.ToArray(), id => (long)id.RawValue)),
                "premise: exactly the destroyed entities' chains of Added are in the file");

            Assert.DoesNotThrow(() => dbe.InitializeArchetypes(), "the destroyed entities' chains revive nothing: R9 must not count them");
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");

            var after = ArchetypeOracle.Snapshot(dbe, Archetype<AsmcR9V2Arch>.Metadata.ArchetypeId, survivors);
            ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmcR9Added");
            using var t = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                foreach (var id in destroyed)
                {
                    Assert.That(t.IsAlive(id), Is.False, $"0x{id.RawValue:X}: destroyed before the checkpoint, not resurrected by the chain-only pass");
                }

                for (var i = 0; i < survivors.Count; i++)
                {
                    Assert.That(t.IsAlive(survivors[i]), Is.True);
                }
            });
        }
    }

    /// <summary>Clears each entity's occupancy bit in its cluster page, through the running engine (a test-only tamper): its record still names it.</summary>
    private static unsafe void ClearOccupancyBits(DatabaseEngine dbe, ushort archetypeId, params EntityId[] ids)
    {
        var locations = ClusterPlacementProbe.Locations(dbe, archetypeId);
        var cluster = dbe._archetypeStates[archetypeId].ClusterState.ClusterSegment;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var cs = dbe.MMF.CreateChangeSet();
            var accessor = cluster.CreateChunkAccessor(cs);
            try
            {
                foreach (var id in ids)
                {
                    var location = locations[(long)id.RawValue];
                    *(ulong*)accessor.GetChunkAddress(location.Chunk, true) &= ~(1UL << location.Slot);
                }
            }
            finally
            {
                accessor.Dispose();
            }

            cs.SaveChanges();
        }
    }

    // ── R11 is not single-shot (the open after a refused one) ───────────────────────────────────────────────────────────────────────────────────────

    // A clean open refuses the migration over the §6.4 cross-check (the cluster walk and the pre-migration map disagree: an entity's occupancy bit is
    // cleared under its record, staged before a clean close). That refusal leaves the failed-open marker, so the next open takes the crash path, on the very
    // image the last clean close left: no torn page is possible, and it must refuse again. Gated on CrashRecoveryAtOpen, the cross-check was skipped there
    // and the walk passed on routing bits alone, the entity left to the chain-only pass with its SingleVersion bytes zeroed.
    [Test]
    [VerifiesRule("SCHEMA-12")]
    [CancelAfter(20_000)]
    public void RefusedOpen_TheCrashPathOpenAfterIt_IsRefusedAgain()
    {
        var ids = SeedWal(window: false, crash: false);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, false);
            ClearOccupancyBits(dbe, Archetype<AsmcV1Arch>.Metadata.ArchetypeId, ids[17]);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterWal(dbe, true);
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes(), $"open {attempt}");
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.OldClusterUnreadable), $"open {attempt}");
                Assert.That(ex.Message, Does.Contain("live entities its pre-migration entity map records"), $"open {attempt}: the cross-check refused it");
                if (attempt == 2)
                {
                    Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: the open after the refused one takes the crash path");
                    Assert.That(dbe.TolerateTornSegmentsAtOpen, Is.False, "premise: on an image no torn page can be in");
                }
            });
        }
    }

    // R11 when the pre-migration map itself cannot be loaded, where no torn page is possible: the cross-check has nothing to check the walk against, and the
    // open used to go on without it, silently.
    [Test]
    [CancelAfter(20_000)]
    public void CleanOpen_PreMigrationMapUnloadable_IsRefused()
    {
        SeedWal(window: false, crash: false);
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterWal(dbe, true);
        dbe.FailPreMigrationEntityMapLoadForTest = true;
        var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
        Assert.Multiple(() =>
        {
            Assert.That(dbe.TolerateTornSegmentsAtOpen, Is.False, "premise: no torn page is possible on this open");
            Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.OldClusterUnreadable));
            Assert.That(ex.Message, Does.StartWith("Archetype 'Asmc.Wal': its pre-migration entity map (SPI "));
        });
    }

    // ── R7 and R12 count every entity the open places: the window's spawns and the chain-only ones ───────────────────────────────────────────────────

    // Empty at the checkpoint, so the re-placement has nothing to refuse; the window spawns three entities, each absent in the added spatial component (the
    // window was written under the committed set). Replayed, all three would sit at position 0.
    [Test]
    [CancelAfter(20_000)]
    public void CrashOpen_ReplayedSpawnsOfAnArchetypeGainingItsSpatialField_AreRefused()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR12Stats>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the empty archetype's row reaches the file");
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 3; i++)
                {
                    t.Spawn<AsmcR12V1Arch>(AsmcR12V1Arch.Stats.Set(new AsmcR12Stats(i + 1)));
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR12Stats>();
            dbe.RegisterComponentFromAccessor<AsmcR12Pos>();
            dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), CellSize));
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.PlacementOnAddedComponent));
                Assert.That(ex.Message, Is.EqualTo(
                    "Archetype 'Asmc.R12' gains component 'Typhon.Schema.UnitTest.AsmcR12Pos', which carries its spatial field. Its 3 entities (0 placed from "
                    + "the checkpoint and alive at the end of the WAL window, 3 spawned in that window) have no value for it, so they would all be placed at "
                    + "position 0. Add it in a revision that recreates these entities, or add it before the archetype is populated; until then, open the "
                    + "database with the build that wrote it. " + CrashRemedy + " Nothing of this archetype was modified."));
            });
        }
    }

    /// <summary>The remedy a crash open refused by R7 or R12 states: its WAL window was not replayed.</summary>
    private const string CrashRemedy = "This open was also a crash recovery, refused before its WAL window was replayed: open the database once with the "
                                       + "build that wrote it to finish the recovery, then migrate.";

    // The population R12 counts is the one at the END of the window: spawned and destroyed in it, the entities leave the archetype empty, and it migrates.
    [Test]
    [CancelAfter(20_000)]
    public void CrashOpen_SpawnedThenDestroyedInTheWindow_IsNotCounted()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR12Stats>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the empty archetype's row reaches the file");
            var ids = new List<EntityId>();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 3; i++)
                {
                    ids.Add(t.Spawn<AsmcR12V1Arch>(AsmcR12V1Arch.Stats.Set(new AsmcR12Stats(i + 1))));
                }

                Assert.That(t.Commit(), Is.True);
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                foreach (var id in ids)
                {
                    t.Destroy(id);
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR12Stats>();
            dbe.RegisterComponentFromAccessor<AsmcR12Pos>();
            dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), CellSize));
            Assert.DoesNotThrow(() => dbe.InitializeArchetypes(), "every spawn of the window is destroyed in it: the archetype is empty at its end");
            Assert.Multiple(() =>
            {
                Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
                Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "the empty archetype migrated");
                Assert.That(dbe._persistedArchetypes["Asmc.R12"].Arch.Revision, Is.EqualTo(2));
            });
        }
    }

    // Two entities at the checkpoint, one destroyed in the window: one left at its end, which R7 lets through. Counting the checkpoint alone refused it.
    [Test]
    [CancelAfter(20_000)]
    public void CrashOpen_DestroyInTheWindow_IsSubtracted()
    {
        EntityId kept;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR7Key>();
            dbe.InitializeArchetypes();
            EntityId gone;
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                kept = t.Spawn<AsmcR7V1Arch>(AsmcR7V1Arch.Key.Set(new AsmcR7Key(1)));
                gone = t.Spawn<AsmcR7V1Arch>(AsmcR7V1Arch.Key.Set(new AsmcR7Key(2)));
                Assert.That(t.Commit(), Is.True);
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                t.Destroy(gone);
                Assert.That(t.Commit(), Is.True);
            }

            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR7Key>();
            dbe.RegisterComponentFromAccessor<AsmcR7Added>();
            Assert.DoesNotThrow(() => dbe.InitializeArchetypes(), "one entity is left at the end of the window: no key-0 collision");
            using var t = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
                Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "the archetype migrated");
                Assert.That(t.Open(kept).Read(AsmcR7V2Arch.Key).K, Is.EqualTo(1L));
            });
        }
    }

    // One entity at the checkpoint passes R7 alone; with the window's two spawns the archetype holds three, all on key 0 of the added unique index.
    [Test]
    [CancelAfter(20_000)]
    public void CrashOpen_ReplayedSpawnsCountForAnAddedUniqueIndex()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR7Key>();
            dbe.InitializeArchetypes();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                t.Spawn<AsmcR7V1Arch>(AsmcR7V1Arch.Key.Set(new AsmcR7Key(1)));
                Assert.That(t.Commit(), Is.True);
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                t.Spawn<AsmcR7V1Arch>(AsmcR7V1Arch.Key.Set(new AsmcR7Key(2)));
                t.Spawn<AsmcR7V1Arch>(AsmcR7V1Arch.Key.Set(new AsmcR7Key(3)));
                Assert.That(t.Commit(), Is.True);
            }

            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR7Key>();
            dbe.RegisterComponentFromAccessor<AsmcR7Added>();
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.UniqueIndexOnAddedComponent));
                Assert.That(ex.Message, Does.Contain(
                    "Its 3 entities (1 placed from the checkpoint and alive at the end of the WAL window, 2 spawned in that window) have no value for it and "
                    + "would all share key 0."));
                Assert.That(ex.Message, Does.Contain("Declare the index Sparse, or add it in a later revision once every entity has a value"),
                    "the remedy");
                Assert.That(ex.Message, Does.Contain(CrashRemedy), "and how to finish the crash recovery the refusal left undone");
            });
        }
    }

    // One entity in the old cluster and two found only through their Versioned chains (their occupancy bits cleared before the last checkpoint, §6.2):
    // three entities, absent in the added uniquely indexed component. Counting the old cluster alone, R7 saw one and let them through.
    [Test]
    [CancelAfter(20_000)]
    public void CrashOpen_ChainOnlyEntitiesCountForAnAddedUniqueIndex()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR7cKey>();
            dbe.InitializeArchetypes();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 3; i++)
                {
                    ids.Add(t.Spawn<AsmcR7cV1Arch>(AsmcR7cV1Arch.Key.Set(new AsmcR7cKey(i + 1))));
                }

                Assert.That(t.Commit(), Is.True);
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            ClearOccupancyBits(dbe, Archetype<AsmcR7cV1Arch>.Metadata.ArchetypeId, ids[1], ids[2]);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmcR7cKey>();
            dbe.RegisterComponentFromAccessor<AsmcR7cAdded>();
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.UniqueIndexOnAddedComponent));
                Assert.That(ex.Message, Does.Contain("Its 3 existing entities have no value for it"), "the chain-only entities are counted");
                Assert.That(ex.Message, Does.Contain(CrashRemedy), "the remedy of a crash open");
            });
        }
    }

    // ── Refuse: a window record of an archetype the policy skipped says so, not "register it" ────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(20_000)]
    public void CrashOpen_UnderRefuse_WindowOfASkippedArchetype_NamesThePolicy()
    {
        var ids = SeedWal(window: true, crash: true);
        var options = ServiceProvider.GetRequiredService<IOptions<DatabaseEngineOptions>>().Value;
        options.ArchetypeSetMigration = ArchetypeSetMigrationPolicy.Refuse;
        try
        {
            using (var scope = ServiceProvider.CreateScope())
            {
                using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
                RegisterWal(dbe, true);
                var ex = Assert.Throws<DurabilityException>(() => dbe.InitializeArchetypes());
                Assert.Multiple(() =>
                {
                    Assert.That(ex!.ErrorCode, Is.EqualTo(TyphonErrorCode.RecoveryArchetypeSetChangeRefused), "not 7012: the archetype is registered");
                    Assert.That(ex.Message, Does.Contain("records of 'Asmc.Wal'"));
                    Assert.That(ex.Message, Does.Contain("ArchetypeSetMigration = Refuse"), "the cause");
                    Assert.That(ex.Message, Does.Contain("Open the database with ArchetypeSetMigration = Apply"), "and the fix");
                    Assert.That(ex.Message, Does.Not.Contain("does not register"));
                });
            }
        }
        finally
        {
            options.ArchetypeSetMigration = ArchetypeSetMigrationPolicy.Apply;
        }

        // Nothing was applied: an Apply open migrates and replays the whole window.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenWal(scope, true);
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: still a crash open");
            AssertWalValues(dbe, ids, v2: true, window: true);
        }
    }

    // ── D12 with a window that moves a cluster out of its hinted realm ───────────────────────────────────────────────────────────────────────────────

    // Every entity of one old cluster changes realm in the window (escalated to its commit by the Commit-discipline Stats write in the same transaction, so
    // the change is a Slot record). The migration hints that cluster's new cluster with the OLD realm; after the replay no key of it names that realm any
    // more. A hint no valid key matches is ignored: the cluster is filed where its entities are, not in the old realm with every entity a crossing.
    [Test]
    [VerifiesRule("SCHEMA-12")]
    [CancelAfter(30_000)]
    public void CrashOpen_ReplayMovesAClusterOutOfItsHintedRealm_FiledWhereItsEntitiesAre()
    {
        var archetypeId = Archetype<AsmcHintV1Arch>.Metadata.ArchetypeId;
        var moved = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureHint(dbe, false);
            const int perHome = 10;
            var homes = RealmIds.Length * CellCount;
            var ids = new List<EntityId>();
            using (var t = dbe.CreateQuickTransaction())
            {
                for (var k = 0; k < homes * perHome; k++)
                {
                    var home = k % homes;
                    var i = k / homes;
                    var x = (FirstCellX + home % CellCount) * CellSize + 15 + i % 5 * 4f;
                    var y = CellY * CellSize + 15 + i / 5 * 4f;
                    ids.Add(t.Spawn<AsmcHintV1Arch>(AsmcHintV1Arch.Pos.Set(HintPos(RealmIds[home / CellCount], x, y, k)),
                        AsmcHintV1Arch.Stats.Set(new AsmcHintStats(k))));
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.WriteTickFence(1);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);

            // The old cluster of entity 0 (realm RealmIds[0]): every entity of it moves to the other realm, in place.
            var locations = ClusterPlacementProbe.Locations(dbe, archetypeId);
            var chunk = locations[(long)ids[0].RawValue].Chunk;
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                foreach (var id in ids)
                {
                    if (locations[(long)id.RawValue].Chunk != chunk)
                    {
                        continue;
                    }

                    var pos = t.Open(id).Read(AsmcHintV1Arch.Pos);
                    var e = t.OpenMut(id);
                    e.Set(AsmcHintV1Arch.Stats, new AsmcHintStats(-1));
                    e.Set(AsmcHintV1Arch.Pos, HintPos(RealmIds[1], pos.Bounds.MinX, pos.Bounds.MinY, pos.Tag));
                    moved.Add(id);
                }

                Assert.That(t.Commit(), Is.True);
            }

            Assert.That(moved, Has.Count.EqualTo(perHome), "premise: the old cluster held exactly its home");
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureHint(dbe, true);
            var cs = dbe._archetypeStates[Archetype<AsmcHintV2Arch>.Metadata.ArchetypeId].ClusterState;
            using var t = dbe.CreateQuickTransaction();
            var realms = new HashSet<ushort>();
            foreach (var id in moved)
            {
                realms.Add(t.Open(id).Read(AsmcHintV2Arch.Pos).Realm);
            }

            Assert.Multiple(() =>
            {
                Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
                Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the open migrated the set");
                Assert.That(realms, Is.EquivalentTo(new[] { RealmIds[1] }), "premise: the window's realm change was replayed onto every moved entity");
                Assert.That(cs.LastRebuildForeignRealmSlots, Is.Zero, "the cluster is filed in its entities' realm, not its hint's");
                Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero);
            });
        }
    }

    private static void RegisterR9(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<AsmcR9Key>();
        dbe.RegisterComponentFromAccessor<AsmcR9Added>();
        dbe.RegisterComponentFromAccessor<AsmcR9Ghost>();
    }

    /// <summary>Rewrites the stored component list of <paramref name="archetype"/>'s row in place, through the running engine (a test-only tamper).</summary>
    private static void RewriteStoredNames(DatabaseEngine dbe, string archetype, params string[] names)
    {
        var table = dbe.GetComponentTable<ArchetypeR1>();
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var cs = dbe.MMF.CreateChangeSet();
        var chunkId = dbe._persistedArchetypes[archetype].ChunkId;
        Assert.That(SystemCrud.Read(table, chunkId, out ArchetypeR1 row, dbe.EpochManager), Is.True);
        row.ComponentNames = default;
        using (var cca = new ComponentCollectionAccessor<String64>(cs, dbe.GetComponentCollectionVSBS<String64>(), ref row.ComponentNames))
        {
            foreach (var name in names)
            {
                cca.Add((String64)name);
            }
        }

        row.ComponentCount = (byte)names.Length;
        SystemCrud.Update(table, chunkId, ref row, dbe.EpochManager, cs);
        cs.SaveChanges();
    }

    // ── D12: the recovery rebuild keeps the migration's realm hints (AC-31) ──────────────────────────────────────────────────────────────────────────

    private const float World = 1000f;
    private const float CellSize = 50f;
    private const float HysteresisRatio = 0.2f;
    private const float Band = CellSize * HysteresisRatio;
    private const int CellY = 5;
    private const int FirstCellX = 2;
    private const int CellCount = 6;
    private static readonly ushort[] RealmIds = [1, 2];

    private static void ConfigureHint(DatabaseEngine dbe, bool v2)
    {
        dbe.RegisterComponentFromAccessor<AsmcHintPos>();
        dbe.RegisterComponentFromAccessor<AsmcHintStats>();
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<AsmcHintBig>();
            dbe.RegisterComponentFromAccessor<AsmcHintWide>();
        }

        dbe.ConfigureRealms(4);
        foreach (var realm in RealmIds)
        {
            dbe.Realms.Register(new RealmId(realm), RealmConfig.SimulatedAlways(
                SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), CellSize, migrationHysteresisRatio: HysteresisRatio, reclusterBudgetMs: 0f,
                    batchSpawnSortThreshold: 0)));
        }

        dbe.InitializeArchetypes();
    }

    private static AsmcHintPos HintPos(ushort realm, float x, float y, int tag)
        => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm, Tag = tag };

    // Realm-keyed, every home (realm, cell) one old cluster, slot 0 of each in the hysteresis band of the next cell (D11), and a realm stray in each at the
    // old slot where the added component's split starts a new cluster. That child's first entity is the stray: the first-valid-key rule would file the child
    // in the stray's realm and every other entity of it as a realm crossing. The migration's hint keeps the old cluster's realm, and the recovery rebuild —
    // which runs again after the replay — must keep it too (D12).
    [Test]
    [VerifiesRule("SCHEMA-12")]
    [CancelAfter(30_000)]
    public void CrashOpen_SetMigration_RecoveryRebuildKeepsHints()
    {
        var newN = ArchetypeClusterInfo.Compute(4,
            [Marshal.SizeOf<AsmcHintPos>(), Marshal.SizeOf<AsmcHintStats>(), Marshal.SizeOf<AsmcHintBig>(), Marshal.SizeOf<AsmcHintWide>()]).ClusterSize;
        var archetypeId = Archetype<AsmcHintV1Arch>.Metadata.ArchetypeId;
        var strays = new List<EntityId>();
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureHint(dbe, false);
            var oldN = dbe._archetypeStates[archetypeId].ClusterState.Layout.ClusterSize;
            var perHome = Math.Min(oldN, newN + 8);
            Assert.That(newN, Is.LessThan(perHome), $"premise: the added component shrinks the cluster ({oldN} -> {newN}) so every old cluster splits");

            var homes = RealmIds.Length * CellCount;
            var realmOf = new Dictionary<EntityId, ushort>();
            using (var t = dbe.CreateQuickTransaction())
            {
                for (var k = 0; k < homes * perHome; k++)
                {
                    var home = k % homes;
                    var i = k / homes;
                    var realm = RealmIds[home / CellCount];
                    var x = (FirstCellX + home % CellCount) * CellSize + 15 + i % 5 * 4f;
                    var y = CellY * CellSize + 15 + i / 5 % 5 * 4f;
                    var id = t.Spawn<AsmcHintV1Arch>(AsmcHintV1Arch.Pos.Set(HintPos(realm, x, y, k)), AsmcHintV1Arch.Stats.Set(new AsmcHintStats(k)));
                    ids.Add(id);
                    realmOf[id] = realm;
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.WriteTickFence(1);

            // Slot 0 of every cluster into the band of the next cell, then a fence: it stays (CC-02's dead zone). The entity at old slot newN of every cluster
            // then changes realm after the last fence: a realm stray the checkpoint image holds.
            var locations = ClusterPlacementProbe.Locations(dbe, archetypeId);
            using (var t = dbe.CreateQuickTransaction())
            {
                foreach (var id in ids)
                {
                    if (locations[(long)id.RawValue].Slot == 0)
                    {
                        var pos = t.Open(id).Read(AsmcHintV1Arch.Pos);
                        var x = MathF.Floor(pos.Bounds.MinX / CellSize) * CellSize + CellSize + Band / 2;
                        t.OpenMut(id).Set(AsmcHintV1Arch.Pos, HintPos(pos.Realm, x, pos.Bounds.MinY, pos.Tag));
                    }
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.WriteTickFence(2);
            Assert.That(dbe._archetypeStates[archetypeId].ClusterState.LastTickMigrationCount, Is.Zero, "premise: band residents stay in their clusters");

            using (var t = dbe.CreateQuickTransaction())
            {
                foreach (var id in ids)
                {
                    if (locations[(long)id.RawValue].Slot == newN)
                    {
                        var pos = t.Open(id).Read(AsmcHintV1Arch.Pos);
                        var other = pos.Realm == RealmIds[0] ? RealmIds[1] : RealmIds[0];
                        t.OpenMut(id).Set(AsmcHintV1Arch.Pos, HintPos(other, pos.Bounds.MinX, pos.Bounds.MinY, pos.Tag));
                        strays.Add(id);
                    }
                }

                Assert.That(t.Commit(), Is.True);
            }

            Assert.That(strays, Has.Count.EqualTo(homes), "premise: one realm stray per old cluster");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);

            // The WAL window: Slot records, no spawn (D6 keeps replayed spawns out of scope).
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < ids.Count; i += 3)
                {
                    t.OpenMut(ids[i]).Set(AsmcHintV1Arch.Stats, new AsmcHintStats(100_000 + i));
                }

                Assert.That(t.Commit(), Is.True);
            }

            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureHint(dbe, true);
            var cs = dbe._archetypeStates[Archetype<AsmcHintV2Arch>.Metadata.ArchetypeId].ClusterState;
            var locations = ClusterPlacementProbe.Locations(dbe, Archetype<AsmcHintV2Arch>.Metadata.ArchetypeId);
            Assert.That(locations, Has.Count.EqualTo(ids.Count), "premise: every entity is placed");
            var straysFirst = 0;
            foreach (var id in strays)
            {
                straysFirst += locations[(long)id.RawValue].Slot == 0 ? 1 : 0;
            }

            Assert.Multiple(() =>
            {
                Assert.That(dbe.LastOpenRanCrashRecovery, Is.True, "premise: a crash open");
                Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the open migrated the set");
                Assert.That(dbe.LastWalV2RecoveryResult.RecordsApplied, Is.GreaterThan(0), "premise: the window was replayed");
                Assert.That(straysFirst, Is.EqualTo(strays.Count), "premise: every stray is the first entity of a new cluster");
                Assert.That(cs.LastRebuildForeignRealmSlots, Is.EqualTo(strays.Count),
                    "the recovery rebuild files each carried stray alone: no child flipped to its stray's realm");
                Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero, "and no cell stray (D11)");
            });

            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i += 3)
            {
                Assert.That(t.Open(ids[i]).Read(AsmcHintV2Arch.Stats).Hp, Is.EqualTo(100_000 + i), $"entity {i}: its window value");
            }
        }
    }
}
