using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The archetype component-set migration on a clean open (#1394 slice 3, claude/design/Schema/07-archetype-set-evolution.md §11). Revision pairs share
// [Archetype(Name = ...)] (D3: the higher revision of a name is consumed, the lower one superseded); each open registers only the components of the revision
// it consumes, so the next revision does not resolve before its open. Every scenario owns its components: an archetype class is process-wide, and a
// component shared with another scenario would make that scenario's opens consume, and migrate, these archetypes too.
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region A SingleVersion component added to a SingleVersion + Versioned archetype

[Component("Typhon.Schema.UnitTest.AsmSvKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmSvKeep
{
    public int A;
    public float B;
    public AsmSvKeep(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.AsmSvVer", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmSvVer
{
    public long V;
    public AsmSvVer(long v) => V = v;
}

[Component("Typhon.Schema.UnitTest.AsmSvAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmSvAdded
{
    public long C;
    public int D;
    public int Pad;
    public AsmSvAdded(long c, int d) { C = c; D = d; Pad = 0; }
}

[Archetype(1, Name = "Asm.SvAdd")]
class AsmSvV1Arch : Archetype<AsmSvV1Arch>
{
    public static readonly Comp<AsmSvKeep> Keep = Register<AsmSvKeep>();
    public static readonly Comp<AsmSvVer> Ver = Register<AsmSvVer>();
}

[Archetype(2, Name = "Asm.SvAdd")]
class AsmSvV2Arch : Archetype<AsmSvV2Arch>
{
    public static readonly Comp<AsmSvKeep> Keep = Register<AsmSvKeep>();
    public static readonly Comp<AsmSvVer> Ver = Register<AsmSvVer>();
    public static readonly Comp<AsmSvAdded> Added = Register<AsmSvAdded>();
}

#endregion

#region A Versioned component added to a pure-Versioned archetype

[Component("Typhon.Schema.UnitTest.AsmVKey", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmVKey
{
    public long K;
    public AsmVKey(long k) => K = k;
}

[Component("Typhon.Schema.UnitTest.AsmVAdded", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmVAdded
{
    public long X;
    public int Y;
    public int Pad;
    public AsmVAdded(long x, int y) { X = x; Y = y; Pad = 0; }
}

[Archetype(1, Name = "Asm.VAdd")]
class AsmVV1Arch : Archetype<AsmVV1Arch>
{
    public static readonly Comp<AsmVKey> Key = Register<AsmVKey>();
}

[Archetype(2, Name = "Asm.VAdd")]
class AsmVV2Arch : Archetype<AsmVV2Arch>
{
    public static readonly Comp<AsmVKey> Key = Register<AsmVKey>();
    public static readonly Comp<AsmVAdded> Added = Register<AsmVAdded>();
}

#endregion

#region R9: an added Versioned component whose chains already carry the archetype's routing id

[Component("Typhon.Schema.UnitTest.AsmStKey", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmStKey
{
    public long K;
    public AsmStKey(long k) => K = k;
}

/// <summary>Same size and mode as <see cref="AsmStAdded"/>: the stored list is tampered to name it in Added's slot, so the old geometry stays exact.</summary>
[Component("Typhon.Schema.UnitTest.AsmStGhost", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmStGhost
{
    public long G;
}

[Component("Typhon.Schema.UnitTest.AsmStAdded", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmStAdded
{
    public long A;
    public AsmStAdded(long a) => A = a;
}

[Archetype(1, Name = "Asm.Stale")]
class AsmStV1Arch : Archetype<AsmStV1Arch>
{
    public static readonly Comp<AsmStKey> Key = Register<AsmStKey>();
    public static readonly Comp<AsmStAdded> Added = Register<AsmStAdded>();
}

[Archetype(2, Name = "Asm.Stale")]
class AsmStV2Arch : Archetype<AsmStV2Arch>
{
    public static readonly Comp<AsmStKey> Key = Register<AsmStKey>();
    public static readonly Comp<AsmStGhost> Ghost = Register<AsmStGhost>();
    public static readonly Comp<AsmStAdded> Added = Register<AsmStAdded>();
}

#endregion

#region A Transient component added

[Component("Typhon.Schema.UnitTest.AsmTrKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmTrKeep
{
    public int V;
    public int Pad;
    public AsmTrKeep(int v) { V = v; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmTrAdded", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct AsmTrAdded
{
    public long T;
    public AsmTrAdded(long t) => T = t;
}

[Archetype(1, Name = "Asm.TrAdd")]
class AsmTrV1Arch : Archetype<AsmTrV1Arch>
{
    public static readonly Comp<AsmTrKeep> Keep = Register<AsmTrKeep>();
}

[Archetype(2, Name = "Asm.TrAdd")]
class AsmTrV2Arch : Archetype<AsmTrV2Arch>
{
    public static readonly Comp<AsmTrKeep> Keep = Register<AsmTrKeep>();
    public static readonly Comp<AsmTrAdded> Added = Register<AsmTrAdded>();
}

#endregion

#region A Versioned component inserted mid-list: every later slot shifts, the Versioned count goes 1 -> 2

[Component("Typhon.Schema.UnitTest.AsmMidA", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmMidA
{
    public int A;
    public int Pad;
    public AsmMidA(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmMidV", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmMidV
{
    public long V;
    public AsmMidV(long v) => V = v;
}

[Component("Typhon.Schema.UnitTest.AsmMidB", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmMidB
{
    public long B;
    public AsmMidB(long b) => B = b;
}

[Component("Typhon.Schema.UnitTest.AsmMidIns", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmMidIns
{
    public long I;
    public AsmMidIns(long i) => I = i;
}

[Archetype(1, Name = "Asm.Mid")]
class AsmMidV1Arch : Archetype<AsmMidV1Arch>
{
    public static readonly Comp<AsmMidA> A = Register<AsmMidA>();
    public static readonly Comp<AsmMidV> V = Register<AsmMidV>();
    public static readonly Comp<AsmMidB> B = Register<AsmMidB>();
}

[Archetype(2, Name = "Asm.Mid")]
class AsmMidV2Arch : Archetype<AsmMidV2Arch>
{
    public static readonly Comp<AsmMidA> A = Register<AsmMidA>();
    public static readonly Comp<AsmMidIns> Ins = Register<AsmMidIns>();
    public static readonly Comp<AsmMidV> V = Register<AsmMidV>();
    public static readonly Comp<AsmMidB> B = Register<AsmMidB>();
}

#endregion

#region A parent gains a component: the parent's slots and every child slot shift

[Component("Typhon.Schema.UnitTest.AsmParP1", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmParP1
{
    public int P;
    public int Pad;
    public AsmParP1(int p) { P = p; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmParP2", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmParP2
{
    public long Q;
}

[Component("Typhon.Schema.UnitTest.AsmParC1", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmParC1
{
    public long C;
    public AsmParC1(long c) => C = c;
}

[Archetype(1, Name = "Asm.Parent")]
class AsmParentV1Arch : Archetype<AsmParentV1Arch>
{
    public static readonly Comp<AsmParP1> P1 = Register<AsmParP1>();
}

[Archetype(1, Name = "Asm.Child")]
class AsmChildV1Arch : Archetype<AsmChildV1Arch, AsmParentV1Arch>
{
    public static readonly Comp<AsmParC1> C1 = Register<AsmParC1>();
}

[Archetype(2, Name = "Asm.Parent")]
class AsmParentV2Arch : Archetype<AsmParentV2Arch>
{
    public static readonly Comp<AsmParP1> P1 = Register<AsmParP1>();
    public static readonly Comp<AsmParP2> P2 = Register<AsmParP2>();
}

[Archetype(2, Name = "Asm.Child")]
class AsmChildV2Arch : Archetype<AsmChildV2Arch, AsmParentV2Arch>
{
    public static readonly Comp<AsmParC1> C1 = Register<AsmParC1>();
}

#endregion

#region EntityIds, links from another archetype, and the key counter

[Component("Typhon.Schema.UnitTest.AsmLnkT", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmLnkT
{
    public int T;
    public int Pad;
    public AsmLnkT(int t) { T = t; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmLnkAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmLnkAdded
{
    public long A;
}

[Component("Typhon.Schema.UnitTest.AsmLnkRef", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmLnkRef
{
    public EntityLink<AsmLnkV1Arch> Target;
}

[Archetype(1, Name = "Asm.LinkTarget")]
class AsmLnkV1Arch : Archetype<AsmLnkV1Arch>
{
    public static readonly Comp<AsmLnkT> T = Register<AsmLnkT>();
}

[Archetype(2, Name = "Asm.LinkTarget")]
class AsmLnkV2Arch : Archetype<AsmLnkV2Arch>
{
    public static readonly Comp<AsmLnkT> T = Register<AsmLnkT>();
    public static readonly Comp<AsmLnkAdded> Added = Register<AsmLnkAdded>();
}

[Archetype(Name = "Asm.LinkHolder")]
class AsmLnkHolderArch : Archetype<AsmLnkHolderArch>
{
    public static readonly Comp<AsmLnkRef> Ref = Register<AsmLnkRef>();
}

#endregion

#region Two adds: in one open (1 -> 3), and in two successive opens (1 -> 2 -> 3)

[Component("Typhon.Schema.UnitTest.AsmTwoK", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmTwoK
{
    public int K;
    public int Pad;
    public AsmTwoK(int k) { K = k; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmTwoX", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmTwoX
{
    public long X;
}

[Component("Typhon.Schema.UnitTest.AsmTwoY", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmTwoY
{
    public long Y;
}

[Archetype(1, Name = "Asm.Two")]
class AsmTwoV1Arch : Archetype<AsmTwoV1Arch>
{
    public static readonly Comp<AsmTwoK> K = Register<AsmTwoK>();
}

[Archetype(3, Name = "Asm.Two")]
class AsmTwoV3Arch : Archetype<AsmTwoV3Arch>
{
    public static readonly Comp<AsmTwoK> K = Register<AsmTwoK>();
    public static readonly Comp<AsmTwoX> X = Register<AsmTwoX>();
    public static readonly Comp<AsmTwoY> Y = Register<AsmTwoY>();
}

[Component("Typhon.Schema.UnitTest.AsmSucK", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmSucK
{
    public int K;
    public int Pad;
    public AsmSucK(int k) { K = k; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmSucX", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmSucX
{
    public long X;
    public AsmSucX(long x) => X = x;
}

[Component("Typhon.Schema.UnitTest.AsmSucY", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmSucY
{
    public long Y;
}

[Archetype(1, Name = "Asm.Successive")]
class AsmSucV1Arch : Archetype<AsmSucV1Arch>
{
    public static readonly Comp<AsmSucK> K = Register<AsmSucK>();
}

[Archetype(2, Name = "Asm.Successive")]
class AsmSucV2Arch : Archetype<AsmSucV2Arch>
{
    public static readonly Comp<AsmSucK> K = Register<AsmSucK>();
    public static readonly Comp<AsmSucX> X = Register<AsmSucX>();
}

[Archetype(3, Name = "Asm.Successive")]
class AsmSucV3Arch : Archetype<AsmSucV3Arch>
{
    public static readonly Comp<AsmSucK> K = Register<AsmSucK>();
    public static readonly Comp<AsmSucX> X = Register<AsmSucX>();
    public static readonly Comp<AsmSucY> Y = Register<AsmSucY>();
}

#endregion

#region A field migration and a set add in one open

[Component("Typhon.Schema.UnitTest.AsmCfW", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmCfWV1
{
    public int A;
    public int Pad;
    public AsmCfWV1(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmCfW", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmCfWV2
{
    public int A;
    public int Pad;
    public long B;
}

[Component("Typhon.Schema.UnitTest.AsmCfAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmCfAdded
{
    public long V;
}

[Archetype(1, Name = "Asm.CombinedWiden")]
class AsmCfV1Arch : Archetype<AsmCfV1Arch>
{
    public static readonly Comp<AsmCfWV1> W = Register<AsmCfWV1>();
}

[Archetype(2, Name = "Asm.CombinedWiden")]
class AsmCfV2Arch : Archetype<AsmCfV2Arch>
{
    public static readonly Comp<AsmCfWV2> W = Register<AsmCfWV2>();
    public static readonly Comp<AsmCfAdded> Added = Register<AsmCfAdded>();
}

[Component("Typhon.Schema.UnitTest.AsmCuP", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmCuPV1
{
    public int Health;
    public int Mana;
    public AsmCuPV1(int health, int mana) { Health = health; Mana = mana; }
}

[Component("Typhon.Schema.UnitTest.AsmCuP", 2, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmCuPV2
{
    public float Health;
    public int Mana;
    public int Shield;
    public int Pad;
}

[Component("Typhon.Schema.UnitTest.AsmCuAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmCuAdded
{
    public long V;
}

[Archetype(1, Name = "Asm.CombinedChain")]
class AsmCuV1Arch : Archetype<AsmCuV1Arch>
{
    public static readonly Comp<AsmCuPV1> P = Register<AsmCuPV1>();
}

[Archetype(2, Name = "Asm.CombinedChain")]
class AsmCuV2Arch : Archetype<AsmCuV2Arch>
{
    public static readonly Comp<AsmCuAdded> Added = Register<AsmCuAdded>();
    public static readonly Comp<AsmCuPV2> P = Register<AsmCuPV2>();
}

#endregion

#region Indexes

[Component("Typhon.Schema.UnitTest.AsmIxK", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmIxK
{
    [Index(AllowMultiple = true)]
    public int Group;
    public int Pad;
    public AsmIxK(int group) { Group = group; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmIxAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmIxAdded
{
    public long V;
}

[Archetype(1, Name = "Asm.IndexSurvives")]
class AsmIxV1Arch : Archetype<AsmIxV1Arch>
{
    public static readonly Comp<AsmIxK> K = Register<AsmIxK>();
}

/// <summary>The added component goes FIRST, so the indexed one moves to slot 1 and every index entry must point at a new position.</summary>
[Archetype(2, Name = "Asm.IndexSurvives")]
class AsmIxV2Arch : Archetype<AsmIxV2Arch>
{
    public static readonly Comp<AsmIxAdded> Added = Register<AsmIxAdded>();
    public static readonly Comp<AsmIxK> K = Register<AsmIxK>();
}

[Component("Typhon.Schema.UnitTest.AsmIaK", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmIaK
{
    public int K;
    public int Pad;
    public AsmIaK(int k) { K = k; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmIaAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmIaAdded
{
    [Index(AllowMultiple = true)]
    public int Tag;
    public int Pad;
    public AsmIaAdded(int tag) { Tag = tag; Pad = 0; }
}

[Archetype(1, Name = "Asm.IndexAdded")]
class AsmIaV1Arch : Archetype<AsmIaV1Arch>
{
    public static readonly Comp<AsmIaK> K = Register<AsmIaK>();
}

[Archetype(2, Name = "Asm.IndexAdded")]
class AsmIaV2Arch : Archetype<AsmIaV2Arch>
{
    public static readonly Comp<AsmIaK> K = Register<AsmIaK>();
    public static readonly Comp<AsmIaAdded> Added = Register<AsmIaAdded>();
}

[Component("Typhon.Schema.UnitTest.AsmIuK", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmIuK
{
    public int K;
    public int Pad;
    public AsmIuK(int k) { K = k; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmIuAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmIuAdded
{
    [Index]
    public int Code;
    public int Pad;
}

[Archetype(1, Name = "Asm.IndexUnique")]
class AsmIuV1Arch : Archetype<AsmIuV1Arch>
{
    public static readonly Comp<AsmIuK> K = Register<AsmIuK>();
}

[Archetype(2, Name = "Asm.IndexUnique")]
class AsmIuV2Arch : Archetype<AsmIuV2Arch>
{
    public static readonly Comp<AsmIuK> K = Register<AsmIuK>();
    public static readonly Comp<AsmIuAdded> Added = Register<AsmIuAdded>();
}

[Component("Typhon.Schema.UnitTest.AsmIsK", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmIsK
{
    public int K;
    public int Pad;
    public AsmIsK(int k) { K = k; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmIsAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmIsAdded
{
    [Index(Sparse = true)]
    public int Code;
    public int Pad;
    public AsmIsAdded(int code) { Code = code; Pad = 0; }
}

[Archetype(1, Name = "Asm.IndexSparse")]
class AsmIsV1Arch : Archetype<AsmIsV1Arch>
{
    public static readonly Comp<AsmIsK> K = Register<AsmIsK>();
}

[Archetype(2, Name = "Asm.IndexSparse")]
class AsmIsV2Arch : Archetype<AsmIsV2Arch>
{
    public static readonly Comp<AsmIsK> K = Register<AsmIsK>();
    public static readonly Comp<AsmIsAdded> Added = Register<AsmIsAdded>();
}

#endregion

/// <summary>
/// The archetype component-set migration on a clean open (#1394, design 07 §11): every surviving component of every pre-existing entity read back by NAME
/// (bytes, enabled bit in the record and the cluster word, presence), every added component ABSENT in its STAGE-02 state, and the commit's row, journal,
/// names buffer and refusals.
/// </summary>
[NonParallelizable]
class ArchetypeSetMigrationTests : TestBase<ArchetypeSetMigrationTests>
{
    private const int Population = 240;

    private WarningCapture _warnings;

    private string Bundle => ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value.BundleDirectory;

    public override void Setup()
    {
        base.Setup();

        // Rebuilt with a capturing provider beside the fixture's own, so the engine's ILogger<DatabaseEngine> reaches it.
        _warnings = new WarningCapture();
        ServiceCollection.AddSingleton<ILoggerProvider>(_warnings);
        (ServiceProvider as IDisposable)?.Dispose();
        ServiceProvider = ServiceCollection.BuildServiceProvider();
    }

    // ── SingleVersion add ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawns <see cref="Population"/> entities of revision 1, then destroys every seventh so the old clusters have holes. Returns the survivors.
    /// </summary>
    private List<EntityId> SeedSv()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsmSvKeep>();
        dbe.RegisterComponentFromAccessor<AsmSvVer>();
        dbe.InitializeArchetypes();

        var all = new EntityId[Population];
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < Population; i++)
            {
                all[i] = t.Spawn<AsmSvV1Arch>(AsmSvV1Arch.Keep.Set(new AsmSvKeep(i, i * 0.5f)), AsmSvV1Arch.Ver.Set(new AsmSvVer(i * 1000L + 1)));
            }

            Assert.That(t.Commit(), Is.True);
        }

        var survivors = new List<EntityId>();
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < Population; i++)
            {
                if (i % 7 == 3)
                {
                    t.Destroy(all[i]);
                }
                else
                {
                    survivors.Add(all[i]);
                }
            }

            Assert.That(t.Commit(), Is.True);
        }

        return survivors;
    }

    private static DatabaseEngine OpenSv(IServiceScope scope, bool v2)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RecordRebuildPlacementForTest = true;
        dbe.RegisterComponentFromAccessor<AsmSvKeep>();
        dbe.RegisterComponentFromAccessor<AsmSvVer>();
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<AsmSvAdded>();
        }

        dbe.InitializeArchetypes();
        return dbe;
    }

    private Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> SnapshotSvV1(List<EntityId> ids)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenSv(scope, false);
        return ArchetypeOracle.Snapshot(dbe, Archetype<AsmSvV1Arch>.Metadata.ArchetypeId, ids);
    }

    // The added component is appended with every old slot in place, so this proves the absence of the added slot (STAGE-02's set-migration note), not
    // the by-name match: an index mapping would pass it too. InsertMidList_* and EnabledStates_SurviveBySlotName prove SCHEMA-11.
    [Test]
    [VerifiesRule("STAGE-02")]
    public void AddSingleVersion_OldValuesIntact_AddedZeroAndDisabled()
    {
        var ids = SeedSv();
        var before = SnapshotSvV1(ids);

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenSv(scope, true);
        var after = ArchetypeOracle.Snapshot(dbe, Archetype<AsmSvV2Arch>.Metadata.ArchetypeId, ids);

        ArchetypeOracle.AssertSurvivorsEqual(before, after);
        ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmSvAdded");
        Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1));
        var report = dbe.LastOpenArchetypeSetMigrations[0];
        Assert.Multiple(() =>
        {
            Assert.That(report.ArchetypeName, Is.EqualTo("Asm.SvAdd"));
            Assert.That(report.FromRevision, Is.EqualTo(1));
            Assert.That(report.ToRevision, Is.EqualTo(2));
            Assert.That(report.Changes, Is.EqualTo("+Typhon.Schema.UnitTest.AsmSvAdded at slot 2"));
            Assert.That(report.EntitiesMigrated, Is.EqualTo(ids.Count));
        });
    }

    [Test]
    public void AddSingleVersion_EnableReadsZero_EnableWithValueStores()
    {
        var ids = SeedSv();

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenSv(scope, true);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            var plain = t.OpenMut(ids[0]);
            plain.Enable(AsmSvV2Arch.Added);
            var supplied = t.OpenMut(ids[1]);
            supplied.Enable(AsmSvV2Arch.Added, new AsmSvAdded(0x5EED, 7));
            Assert.That(t.Commit(), Is.True);
        }

        // Read back in an open of its own: the values are durable, and the second open of the new schema migrates nothing.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenSv(scope, true);
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty, "the committed set is the runtime set now");
            using var t = dbe.CreateQuickTransaction();
            var e0 = t.Open(ids[0]);
            var zeroEnabled = e0.IsEnabled(AsmSvV2Arch.Added);
            var zero = e0.Read(AsmSvV2Arch.Added);
            var e1 = t.Open(ids[1]);
            var stored = e1.Read(AsmSvV2Arch.Added);
            var keep = e1.Read(AsmSvV2Arch.Keep);
            Assert.Multiple(() =>
            {
                Assert.That(zeroEnabled, Is.True, "Enable(comp) on an absent SingleVersion component succeeds");
                Assert.That(zero.C, Is.Zero, "and reads zero, as an unsupplied spawn does");
                Assert.That(zero.D, Is.Zero);
                Assert.That(stored.C, Is.EqualTo(0x5EED), "Enable(comp, value) stores the value");
                Assert.That(stored.D, Is.EqualTo(7));
                Assert.That(keep.A, Is.EqualTo(1), "the entity's own component is untouched");
            });
        }
    }

    [Test]
    [VerifiesRule("SCHEMA-09")]
    public void SetMigration_JournalsAnArchetypeRow()
    {
        var ids = SeedSv();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenSv(scope, true);
        var rows = new List<SchemaHistoryR1>();
        foreach (var row in dbe.GetSchemaHistory())
        {
            if (row.Target == SchemaObjectKind.Archetype && row.ComponentName.AsString == "Asm.SvAdd")
            {
                rows.Add(row);
            }
        }

        Assert.That(rows, Has.Count.EqualTo(1), "one archetype row per migrated archetype");
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Kind, Is.EqualTo(SchemaChangeKind.Migration));
            Assert.That(rows[0].FromRevision, Is.EqualTo(1));
            Assert.That(rows[0].ToRevision, Is.EqualTo(2));
            Assert.That(rows[0].FieldsAdded, Is.EqualTo(1), "components added");
            Assert.That(rows[0].FieldsRemoved, Is.Zero, "components removed");
            Assert.That(rows[0].EntitiesMigrated, Is.EqualTo(ids.Count));
        });
    }

    // A repeat InitializeArchetypes (#790) while the set change is staged: the first call stops after staging it and after the archetype's fresh EntityMap
    // is allocated (the cluster-init probe throws once), the second completes. One migration, one user-schema-version bump, one journal row, and every
    // entity carried across as by a single open.
    [Test]
    public void RepeatInitializeArchetypes_WhileTheSetChangeIsStaged_MigratesOnce()
    {
        var ids = SeedSv();
        var before = SnapshotSvV1(ids);
        int versionBefore;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenSv(scope, false);
            versionBefore = dbe.MMF.Bootstrap.GetInt(DatabaseEngine.BK_UserSchemaVersion);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmSvKeep>();
            dbe.RegisterComponentFromAccessor<AsmSvVer>();
            dbe.RegisterComponentFromAccessor<AsmSvAdded>();
            var target = Archetype<AsmSvV2Arch>.Metadata.ArchetypeId;
            var thrown = false;
            dbe.ArchetypeClusterInitProbe = id =>
            {
                if (id == target && !thrown)
                {
                    thrown = true;
                    throw new InvalidOperationException("injected: the first InitializeArchetypes stops after staging the set change");
                }
            };

            Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes(), "premise: the first call stops");
            dbe.InitializeArchetypes();

            var after = ArchetypeOracle.Snapshot(dbe, target, ids);
            ArchetypeOracle.AssertSurvivorsEqual(before, after);
            ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmSvAdded");
            var rows = 0;
            foreach (var row in dbe.GetSchemaHistory())
            {
                rows += row.Target == SchemaObjectKind.Archetype && row.ComponentName.AsString == "Asm.SvAdd" ? 1 : 0;
            }

            Assert.Multiple(() =>
            {
                Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1));
                Assert.That(dbe.LastOpenArchetypeSetMigrations[0].EntitiesMigrated, Is.EqualTo(ids.Count));
                Assert.That(rows, Is.EqualTo(1), "one journal row");
                Assert.That(dbe.MMF.Bootstrap.GetInt(DatabaseEngine.BK_UserSchemaVersion), Is.EqualTo(versionBefore + 1), "one version bump");
            });
        }

        // And the file opens as a migrated one: nothing left to migrate, every entity there.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenSv(scope, true);
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty);
            ArchetypeOracle.AssertSurvivorsEqual(before, ArchetypeOracle.Snapshot(dbe, Archetype<AsmSvV2Arch>.Metadata.ArchetypeId, ids));
        }
    }

    [Test]
    public void IntegrityScan_CleanAfterSetMigration()
    {
        var ids = SeedSv();
        ArchetypeOracle.AssertIntegrityClean(Bundle, "premise: the revision-1 file", excuseFreedSegmentRoots: false);

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenSv(scope, true);
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the open migrated the set");
        }

        // The scanner rebuilds the cluster layout from the stored names and the catalog (ClusterLayoutReader) and cross-checks the names against the row
        // (CrossStructureChecks): both read the rewritten row.
        ArchetypeOracle.AssertIntegrityClean(Bundle, "after the set migration");

        // And a plain reopen of the migrated file changes nothing it would report.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenSv(scope, true);
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Open(ids[^1]).Read(AsmSvV2Arch.Keep).A, Is.EqualTo(Population - 1));
        }

        ArchetypeOracle.AssertIntegrityClean(Bundle, "after a plain reopen of the migrated file");
    }

    [Test]
    public void SegmentsAndNamesBuffer_Released()
    {
        SeedSv();

        int oldNamesBuffer;
        int namesChunksBefore;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenSv(scope, false);
            oldNamesBuffer = dbe._persistedArchetypes["Asm.SvAdd"].Arch.ComponentNames._bufferId;
            namesChunksBefore = dbe.GetComponentCollectionSegment<String64>().AllocatedChunkCount;
            AssertNoLeakedPages(dbe, "premise: the revision-1 file");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenSv(scope, true);
            var names = dbe.GetComponentCollectionSegment<String64>();
            var newNamesBuffer = dbe._persistedArchetypes["Asm.SvAdd"].Arch.ComponentNames._bufferId;
            Assert.Multiple(() =>
            {
                Assert.That(newNamesBuffer, Is.Not.EqualTo(oldNamesBuffer), "the row names a new buffer");
                Assert.That(names.IsChunkAllocated(oldNamesBuffer), Is.False, "the replaced names buffer is freed after the commit");
                Assert.That(names.AllocatedChunkCount, Is.EqualTo(namesChunksBefore), "one names buffer per archetype, as before");
            });
            AssertNoLeakedPages(dbe, "the migrating open");
        }

        // The pre-migration cluster is LOADED to be read, which registers it: a leak of it only shows one open later (MigrationSegmentReclaimTests).
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenSv(scope, true);
            AssertNoLeakedPages(dbe, "the open after the migration");
        }
    }

    [Test]
    [VerifiesRule("SCHEMA-12")]
    public void CleanOpen_HasNoChainOnlyEntities()
    {
        var ids = SeedSv();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenSv(scope, true);
        var id = Archetype<AsmSvV2Arch>.Metadata.ArchetypeId;
        Assert.That(dbe.LastOpenRebuildPlacementMap, Is.Not.Null.And.ContainKey(id), "premise: the archetype was re-placed");
        foreach (var (_, sources) in dbe.LastOpenRebuildPlacementMap[id])
        {
            Assert.That(sources, Has.None.EqualTo(-1), "no cluster of chain-only entities on a clean open");
        }

        Assert.Multiple(() =>
        {
            Assert.That(dbe.LastOpenMigrationOrphanChainEntities, Is.Zero);
            Assert.That(ClusterPlacementProbe.Locations(dbe, id), Has.Count.EqualTo(ids.Count));
        });
    }

    // ── Versioned add ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private List<EntityId> SeedVersioned()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsmVKey>();
        dbe.InitializeArchetypes();
        var ids = new List<EntityId>();
        using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = 0; i < Population; i++)
        {
            ids.Add(t.Spawn<AsmVV1Arch>(AsmVV1Arch.Key.Set(new AsmVKey(i * 3L))));
        }

        Assert.That(t.Commit(), Is.True);
        return ids;
    }

    private static DatabaseEngine OpenVersioned(IServiceScope scope, bool v2)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RecordRebuildPlacementForTest = true;
        dbe.RegisterComponentFromAccessor<AsmVKey>();
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<AsmVAdded>();
        }

        dbe.InitializeArchetypes();
        return dbe;
    }

    // Appended, as above: the absence of the added Versioned slot (root 0, bit clear, Enable refuses — STAGE-02), not the by-name match.
    [Test]
    [VerifiesRule("STAGE-02")]
    public void AddVersioned_AddedAbsent_EnableRefuses()
    {
        var ids = SeedVersioned();
        Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> before;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenVersioned(scope, false);
            before = ArchetypeOracle.Snapshot(dbe, Archetype<AsmVV1Arch>.Metadata.ArchetypeId, ids);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenVersioned(scope, true);
            var archetypeId = Archetype<AsmVV2Arch>.Metadata.ArchetypeId;
            var after = ArchetypeOracle.Snapshot(dbe, archetypeId, ids);
            ArchetypeOracle.AssertSurvivorsEqual(before, after);
            ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmVAdded");

            // A pure-Versioned archetype's pre-migration cluster is loaded and walked too (AC-17): every new cluster names an old one.
            foreach (var (_, sources) in dbe.LastOpenRebuildPlacementMap[archetypeId])
            {
                Assert.That(sources, Has.All.GreaterThan(0), "placed out of the pre-migration cluster, in its order");
            }

            using var t = dbe.CreateQuickTransaction();
            var ex = Assert.Throws<InvalidOperationException>(() => t.OpenMut(ids[0]).Enable(AsmVV2Arch.Added));
            Assert.That(ex!.Message, Does.Contain("was never supplied for this entity, so it has no value to enable"), "the STAGE-02 refusal");
        }
    }

    /// <summary>
    /// The added Versioned slot has no chain root on any carried entity, by design (D5). The migration's HEAD rebuild runs untrusted, where a rootless slot
    /// is otherwise counted as unclassifiable and warned about: the added slot is classified as absent instead, and no "left un-rebuilt" warning fires.
    /// </summary>
    [Test]
    public void AddVersioned_NoUnrebuiltHeadWarning()
    {
        var ids = SeedVersioned();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenVersioned(scope, true);
        Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the set migrated");
        Assert.Multiple(() =>
        {
            Assert.That(_warnings.Matching("left un-rebuilt"), Is.Zero, "no un-rebuilt HEAD warning for the added slot");
            Assert.That(dbe.LastOpenVersionedHeadRebuildSkips.Total, Is.Zero);
            Assert.That(dbe.LastOpenVersionedHeadRebuildSkips.AbsentByDesign, Is.EqualTo(ids.Count), "one absent slot per carried entity");
        });
    }

    [Test]
    public void AddVersioned_EnableWithValue_SurvivesReopen()
    {
        var ids = SeedVersioned();

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenVersioned(scope, true);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.OpenMut(ids[5]).Enable(AsmVV2Arch.Added, new AsmVAdded(0xABCDL, 12));
            Assert.That(t.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenVersioned(scope, true);
            using var t = dbe.CreateQuickTransaction();
            var e5 = t.Open(ids[5]);
            var enabled = e5.IsEnabled(AsmVV2Arch.Added);
            var added = e5.Read(AsmVV2Arch.Added);
            var key = e5.Read(AsmVV2Arch.Key).K;
            var otherEnabled = t.Open(ids[6]).IsEnabled(AsmVV2Arch.Added);
            Assert.Multiple(() =>
            {
                Assert.That(enabled, Is.True);
                Assert.That(added.X, Is.EqualTo(0xABCDL));
                Assert.That(added.Y, Is.EqualTo(12));
                Assert.That(key, Is.EqualTo(15L));
                Assert.That(otherEnabled, Is.False, "the others stay absent");
            });
        }
    }

    // R9 (design 07 §8). The stored list names AsmStGhost where AsmStAdded's data sits (same size and mode, so the old geometry is exact): to this open,
    // AsmStAdded is ADDED, yet its chains already carry the archetype's routing id — the file an earlier build that dropped the component would leave.
    [Test]
    public void AddVersioned_WithStaleChains_IsRefused()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmStKey>();
            dbe.RegisterComponentFromAccessor<AsmStAdded>();
            dbe.RegisterComponentFromAccessor<AsmStGhost>();
            dbe.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(AsmStV2Arch);
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < 20; i++)
            {
                t.Spawn<AsmStV1Arch>(AsmStV1Arch.Key.Set(new AsmStKey(i)), AsmStV1Arch.Added.Set(new AsmStAdded(i + 100)));
            }

            Assert.That(t.Commit(), Is.True);
        }

        RewriteStoredNames("Asm.Stale", "Typhon.Schema.UnitTest.AsmStKey", "Typhon.Schema.UnitTest.AsmStGhost");

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmStKey>();
            dbe.RegisterComponentFromAccessor<AsmStAdded>();
            dbe.RegisterComponentFromAccessor<AsmStGhost>();
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(dbe.CleanShutdownAtOpenForTest, Is.False, "premise: the tamper session (its open never completed) did not close clean");
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.StaleChains));
                Assert.That(ex.Message, Does.StartWith(
                    "Archetype 'Asm.Stale' gains Versioned component 'Typhon.Schema.UnitTest.AsmStAdded', but 20 revision chains of it already carry this "
                    + "archetype's routing id "));
                Assert.That(ex.Message, Does.EndWith("Opening would revive those values as present. Nothing of this archetype was modified."));
            });
        }
    }

    // ── Transient add ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AddTransient_AddedIsDisabled()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmTrKeep>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Population; i++)
            {
                ids.Add(t.Spawn<AsmTrV1Arch>(AsmTrV1Arch.Keep.Set(new AsmTrKeep(i + 1))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmTrKeep>();
            dbe.RegisterComponentFromAccessor<AsmTrAdded>();
            dbe.InitializeArchetypes();
            var after = ArchetypeOracle.Snapshot(dbe, Archetype<AsmTrV2Arch>.Metadata.ArchetypeId, ids);
            ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmTrAdded");

            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i++)
            {
                var keep = t.Open(ids[i]).Read(AsmTrV2Arch.Keep).V;
                Assert.That(keep, Is.EqualTo(i + 1));
            }
        }
    }

    // ── Mid-list insert: slots shift, the Versioned count changes ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Revision 1 {A, V, B}, with every enabled state staged by name: V disabled (i % 5 == 1), B disabled (== 2), V never supplied (== 3), all enabled
    /// otherwise.
    /// </summary>
    private List<EntityId> SeedMid()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsmMidA>();
        dbe.RegisterComponentFromAccessor<AsmMidV>();
        dbe.RegisterComponentFromAccessor<AsmMidB>();
        dbe.InitializeArchetypes();

        var ids = new List<EntityId>();
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < Population; i++)
            {
                ids.Add(i % 5 == 3
                    ? t.Spawn<AsmMidV1Arch>(AsmMidV1Arch.A.Set(new AsmMidA(i)), AsmMidV1Arch.B.Set(new AsmMidB(i * 11L)))
                    : t.Spawn<AsmMidV1Arch>(AsmMidV1Arch.A.Set(new AsmMidA(i)), AsmMidV1Arch.V.Set(new AsmMidV(i * 7L)),
                        AsmMidV1Arch.B.Set(new AsmMidB(i * 11L))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < Population; i++)
            {
                if (i % 5 == 1)
                {
                    t.OpenMut(ids[i]).Disable(AsmMidV1Arch.V);
                }
                else if (i % 5 == 2)
                {
                    t.OpenMut(ids[i]).Disable(AsmMidV1Arch.B);
                }
            }

            Assert.That(t.Commit(), Is.True);
        }

        return ids;
    }

    private static DatabaseEngine OpenMid(IServiceScope scope, bool v2)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsmMidA>();
        dbe.RegisterComponentFromAccessor<AsmMidV>();
        dbe.RegisterComponentFromAccessor<AsmMidB>();
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<AsmMidIns>();
        }

        dbe.InitializeArchetypes();
        return dbe;
    }

    private Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> SnapshotMidV1(List<EntityId> ids)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenMid(scope, false);
        return ArchetypeOracle.Snapshot(dbe, Archetype<AsmMidV1Arch>.Metadata.ArchetypeId, ids);
    }

    [Test]
    [VerifiesRule("SCHEMA-11")]
    public void InsertMidList_EveryComponentReadsBackByName()
    {
        var ids = SeedMid();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenMid(scope, true);
        Assert.That(dbe.LastOpenArchetypeSetMigrations[0].Changes,
            Is.EqualTo("+Typhon.Schema.UnitTest.AsmMidIns at slot 1; Typhon.Schema.UnitTest.AsmMidV 1->2; Typhon.Schema.UnitTest.AsmMidB 2->3"));

        // Raw reads: a typed read refuses a disabled component, and the disabled ones must have kept their bytes too.
        var meta = Archetype<AsmMidV2Arch>.Metadata;
        var slotV = meta.GetSlot(AsmMidV2Arch.V._componentTypeId);
        var slotB = meta.GetSlot(AsmMidV2Arch.B._componentTypeId);
        Assert.That((slotV, slotB), Is.EqualTo((2, 3)), "premise: both moved");
        using var t = dbe.CreateQuickTransaction();
        for (var i = 0; i < ids.Count; i++)
        {
            var e = t.Open(ids[i]);
            var a = e.Read(AsmMidV2Arch.A).A;
            var b = MemoryMarshal.Read<long>(e.ReadRaw(slotB));
            Assert.That(a, Is.EqualTo(i), $"entity {i}: A");
            Assert.That(b, Is.EqualTo(i * 11L), $"entity {i}: B, two slots later than it was");
            if (i % 5 != 3)
            {
                var v = MemoryMarshal.Read<long>(e.ReadRaw(slotV));
                Assert.That(v, Is.EqualTo(i * 7L), $"entity {i}: V, one slot later, its chain root at the new Versioned index");
            }
        }
    }

    [Test]
    [VerifiesRule("SCHEMA-11")]
    public void EnabledStates_SurviveBySlotName()
    {
        var ids = SeedMid();
        var before = SnapshotMidV1(ids);

        // Premise: the three states are all there, by name.
        var disabledV = 0;
        var disabledB = 0;
        var absentV = 0;
        foreach (var (_, components) in before)
        {
            var v = components["Typhon.Schema.UnitTest.AsmMidV"];
            disabledV += v.Present && !v.Enabled ? 1 : 0;
            absentV += !v.Present ? 1 : 0;
            disabledB += !components["Typhon.Schema.UnitTest.AsmMidB"].Enabled ? 1 : 0;
        }

        Assert.That(new[] { disabledV, disabledB, absentV }, Has.All.GreaterThan(0), "premise: present-disabled V, disabled B and absent V all staged");

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenMid(scope, true);
        var after = ArchetypeOracle.Snapshot(dbe, Archetype<AsmMidV2Arch>.Metadata.ArchetypeId, ids);
        ArchetypeOracle.AssertSurvivorsEqual(before, after);
        ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmMidIns");
    }

    // The record grows from 19 + 4 (one Versioned slot) to 19 + 8: the pre-migration map must be read at the OLD size for the disabled bits to come from the
    // record, the one authority for enabled state (#846).
    [Test]
    public void VersionedCountChanges_EnabledBitsReadAtOldRecordSize()
    {
        var ids = SeedMid();
        var before = SnapshotMidV1(ids);
        Assert.That(Archetype<AsmMidV1Arch>.Metadata.ComponentCount, Is.EqualTo(3), "premise");

        using var scope = ServiceProvider.CreateScope();
        using var dbe = OpenMid(scope, true);
        Assert.Multiple(() =>
        {
            Assert.That(Archetype<AsmMidV2Arch>.Metadata.VersionedSlotCount, Is.EqualTo(2), "premise: the Versioned count went from 1 to 2");
            Assert.That(Archetype<AsmMidV2Arch>.Metadata._entityRecordSize, Is.EqualTo(ClusterEntityRecordAccessor.RecordSize(2)));
        });

        var after = ArchetypeOracle.Snapshot(dbe, Archetype<AsmMidV2Arch>.Metadata.ArchetypeId, ids);
        var failures = new List<string>();
        for (var i = 0; i < ids.Count; i++)
        {
            var raw = (long)ids[i].RawValue;
            foreach (var name in new[] { "Typhon.Schema.UnitTest.AsmMidV", "Typhon.Schema.UnitTest.AsmMidB" })
            {
                if (after[raw][name].Enabled != before[raw][name].Enabled)
                {
                    failures.Add($"entity {i} {name}: enabled {before[raw][name].Enabled} -> {after[raw][name].Enabled}");
                }
            }
        }

        Assert.That(failures, Is.Empty);
    }

    // ── Parent ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ParentGainsComponent_ChildSlotsShift_BothMigrate()
    {
        var parents = new List<EntityId>();
        var children = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmParP1>();
            dbe.RegisterComponentFromAccessor<AsmParC1>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < 210; i++)
            {
                parents.Add(t.Spawn<AsmParentV1Arch>(AsmParentV1Arch.P1.Set(new AsmParP1(i))));
                children.Add(t.Spawn<AsmChildV1Arch>(AsmParentV1Arch.P1.Set(new AsmParP1(-i)), AsmChildV1Arch.C1.Set(new AsmParC1(i * 13L))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmParP1>();
            dbe.RegisterComponentFromAccessor<AsmParP2>();
            dbe.RegisterComponentFromAccessor<AsmParC1>();
            dbe.InitializeArchetypes();

            var changes = new Dictionary<string, string>();
            foreach (var report in dbe.LastOpenArchetypeSetMigrations)
            {
                changes[report.ArchetypeName] = report.Changes;
            }

            Assert.Multiple(() =>
            {
                Assert.That(changes, Has.Count.EqualTo(2), "the parent and the child both migrate");
                Assert.That(changes["Asm.Parent"], Is.EqualTo("+Typhon.Schema.UnitTest.AsmParP2 at slot 1"));
                Assert.That(changes["Asm.Child"], Is.EqualTo("+Typhon.Schema.UnitTest.AsmParP2 at slot 1; Typhon.Schema.UnitTest.AsmParC1 1->2"));
            });

            var parentAfter = ArchetypeOracle.Snapshot(dbe, Archetype<AsmParentV2Arch>.Metadata.ArchetypeId, parents);
            var childAfter = ArchetypeOracle.Snapshot(dbe, Archetype<AsmChildV2Arch>.Metadata.ArchetypeId, children);
            ArchetypeOracle.AssertAbsent(parentAfter, "Typhon.Schema.UnitTest.AsmParP2");
            ArchetypeOracle.AssertAbsent(childAfter, "Typhon.Schema.UnitTest.AsmParP2");

            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < parents.Count; i++)
            {
                var p = t.Open(parents[i]).Read(AsmParentV2Arch.P1).P;
                var child = t.Open(children[i]);
                var cp = child.Read(AsmParentV2Arch.P1).P;
                var cc = child.Read(AsmChildV2Arch.C1).C;
                Assert.That(p, Is.EqualTo(i));
                Assert.That(cp, Is.EqualTo(-i));
                Assert.That(cc, Is.EqualTo(i * 13L), $"child {i}: its own component, two slots after the parent's");
            }
        }
    }

    // ── Identity ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void EntityIdsAndLinks_Unchanged_NextKeyResumes()
    {
        var targets = new List<EntityId>();
        var holders = new List<EntityId>();
        long highestKey;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmLnkT>();
            dbe.RegisterComponentFromAccessor<AsmLnkRef>();
            dbe.InitializeArchetypes();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 220; i++)
                {
                    var target = t.Spawn<AsmLnkV1Arch>(AsmLnkV1Arch.T.Set(new AsmLnkT(i)));
                    targets.Add(target);
                    holders.Add(t.Spawn<AsmLnkHolderArch>(AsmLnkHolderArch.Ref.Set(new AsmLnkRef { Target = target })));
                }

                Assert.That(t.Commit(), Is.True);
            }

            // The highest key is destroyed: its key was handed out, and the next spawn must still not reuse it.
            highestKey = targets[^1].EntityKey;
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                t.Destroy(targets[^1]);
                Assert.That(t.Commit(), Is.True);
            }

            targets.RemoveAt(targets.Count - 1);
            holders.RemoveAt(holders.Count - 1);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmLnkT>();
            dbe.RegisterComponentFromAccessor<AsmLnkRef>();
            dbe.RegisterComponentFromAccessor<AsmLnkAdded>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the target archetype migrated");

            EntityId fresh;
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                fresh = t.Spawn<AsmLnkV2Arch>(AsmLnkV2Arch.T.Set(new AsmLnkT(-1)));
                Assert.That(t.Commit(), Is.True);
            }

            using var read = dbe.CreateQuickTransaction();
            for (var i = 0; i < holders.Count; i++)
            {
                EntityId link = read.Open(holders[i]).Read(AsmLnkHolderArch.Ref).Target;
                Assert.That(link, Is.EqualTo(targets[i]), $"holder {i}: the link it stores is the target's id, unchanged");
                var value = read.Open(link).Read(AsmLnkV2Arch.T).T;
                Assert.That(value, Is.EqualTo(i), $"holder {i}: and it resolves to the migrated target");
            }

            Assert.Multiple(() =>
            {
                Assert.That(fresh.ArchetypeId, Is.EqualTo(targets[0].ArchetypeId), "the routing id is preserved");
                Assert.That(fresh.EntityKey, Is.GreaterThan(highestKey), "the next key exceeds every key handed out, the destroyed one's included");
            });
        }
    }

    // ── Two adds ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void TwoAddsInOneOpen()
    {
        var ids = new List<EntityId>();
        Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> before;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmTwoK>();
            dbe.InitializeArchetypes();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Population; i++)
                {
                    ids.Add(t.Spawn<AsmTwoV1Arch>(AsmTwoV1Arch.K.Set(new AsmTwoK(i))));
                }

                Assert.That(t.Commit(), Is.True);
            }

            before = ArchetypeOracle.Snapshot(dbe, Archetype<AsmTwoV1Arch>.Metadata.ArchetypeId, ids);
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe2.RegisterComponentFromAccessor<AsmTwoK>();
        dbe2.RegisterComponentFromAccessor<AsmTwoX>();
        dbe2.RegisterComponentFromAccessor<AsmTwoY>();
        dbe2.InitializeArchetypes();

        var report = dbe2.LastOpenArchetypeSetMigrations[0];
        Assert.Multiple(() =>
        {
            Assert.That(report.FromRevision, Is.EqualTo(1));
            Assert.That(report.ToRevision, Is.EqualTo(3));
            Assert.That(report.Changes, Is.EqualTo("+Typhon.Schema.UnitTest.AsmTwoX at slot 1; +Typhon.Schema.UnitTest.AsmTwoY at slot 2"));
        });

        var after = ArchetypeOracle.Snapshot(dbe2, Archetype<AsmTwoV3Arch>.Metadata.ArchetypeId, ids);
        ArchetypeOracle.AssertSurvivorsEqual(before, after);
        ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmTwoX");
        ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmTwoY");
    }

    [Test]
    public void TwoSuccessiveSetChanges()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmSucK>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Population; i++)
            {
                ids.Add(t.Spawn<AsmSucV1Arch>(AsmSucV1Arch.K.Set(new AsmSucK(i))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        // Revision 2 adds X, and every third entity gets a value for it.
        Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> atRevision2;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmSucK>();
            dbe.RegisterComponentFromAccessor<AsmSucX>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations[0].ToRevision, Is.EqualTo(2));
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < ids.Count; i += 3)
                {
                    t.OpenMut(ids[i]).Enable(AsmSucV2Arch.X, new AsmSucX(i * 5L + 1));
                }

                Assert.That(t.Commit(), Is.True);
            }

            atRevision2 = ArchetypeOracle.Snapshot(dbe, Archetype<AsmSucV2Arch>.Metadata.ArchetypeId, ids);
        }

        // Revision 3 adds Y: X's values, present and absent alike, carry across a second geometry change.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmSucK>();
            dbe.RegisterComponentFromAccessor<AsmSucX>();
            dbe.RegisterComponentFromAccessor<AsmSucY>();
            dbe.InitializeArchetypes();
            var report = dbe.LastOpenArchetypeSetMigrations[0];
            Assert.That((report.FromRevision, report.ToRevision), Is.EqualTo((2, 3)));
            var atRevision3 = ArchetypeOracle.Snapshot(dbe, Archetype<AsmSucV3Arch>.Metadata.ArchetypeId, ids);
            ArchetypeOracle.AssertSurvivorsEqual(atRevision2, atRevision3);
            ArchetypeOracle.AssertAbsent(atRevision3, "Typhon.Schema.UnitTest.AsmSucY");

            using var t = dbe.CreateQuickTransaction();
            var x = t.Open(ids[3]).Read(AsmSucV3Arch.X).X;
            Assert.That(x, Is.EqualTo(16L));
        }
    }

    // ── A field migration and a set add in one open ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void CombinedFieldAndSetChange_SvWiden()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmCfWV1>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Population; i++)
            {
                ids.Add(t.Spawn<AsmCfV1Arch>(AsmCfV1Arch.W.Set(new AsmCfWV1(i + 10))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmCfWV2>();
            dbe.RegisterComponentFromAccessor<AsmCfAdded>();
            Assert.That(dbe.HasComponentMigrations, Is.True, "premise: the component's fields migrate too");
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1));

            var after = ArchetypeOracle.Snapshot(dbe, Archetype<AsmCfV2Arch>.Metadata.ArchetypeId, ids);
            ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmCfAdded");
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i++)
            {
                var w = t.Open(ids[i]).Read(AsmCfV2Arch.W);
                Assert.That(w.A, Is.EqualTo(i + 10), $"entity {i}: the widened component converted");
                Assert.That(w.B, Is.Zero, $"entity {i}: its added field zero");
            }
        }
    }

    [Test]
    public void CombinedFieldAndSetChange_UserMigrationChain()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmCuPV1>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Population; i++)
            {
                ids.Add(t.Spawn<AsmCuV1Arch>(AsmCuV1Arch.P.Set(new AsmCuPV1(i * 100, i))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterMigration<AsmCuPV1, AsmCuPV2>((ref AsmCuPV1 old, out AsmCuPV2 converted) =>
            {
                converted = new AsmCuPV2 { Health = old.Health / 100f, Mana = old.Mana, Shield = 1 };
            });
            dbe.RegisterComponentFromAccessor<AsmCuPV2>();
            dbe.RegisterComponentFromAccessor<AsmCuAdded>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations[0].Changes,
                Is.EqualTo("+Typhon.Schema.UnitTest.AsmCuAdded at slot 0; Typhon.Schema.UnitTest.AsmCuP 0->1"));

            var after = ArchetypeOracle.Snapshot(dbe, Archetype<AsmCuV2Arch>.Metadata.ArchetypeId, ids);
            ArchetypeOracle.AssertAbsent(after, "Typhon.Schema.UnitTest.AsmCuAdded");
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i++)
            {
                var p = t.Open(ids[i]).Read(AsmCuV2Arch.P);
                Assert.That(p.Health, Is.EqualTo((float)i), $"entity {i}: converted by the user's function");
                Assert.That(p.Mana, Is.EqualTo(i));
                Assert.That(p.Shield, Is.EqualTo(1));
            }
        }
    }

    // ── Indexes ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Index_SurvivingIndexedField_QueriesAtNewPositions()
    {
        var byGroup = new Dictionary<int, HashSet<EntityId>>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIxK>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Population; i++)
            {
                var group = i % 6;
                var id = t.Spawn<AsmIxV1Arch>(AsmIxV1Arch.K.Set(new AsmIxK(group)));
                if (!byGroup.TryGetValue(group, out var set))
                {
                    byGroup[group] = set = [];
                }

                set.Add(id);
            }

            Assert.That(t.Commit(), Is.True);
        }

        // Every fourth entity destroyed, so the old clusters have holes and the dense re-placement moves the survivors; their positions are read back on a
        // plain reopen, as the migrating open finds them.
        Dictionary<long, (int Chunk, int Slot)> before;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIxK>();
            dbe.InitializeArchetypes();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                var i = 0;
                foreach (var (_, set) in byGroup)
                {
                    var destroyed = new List<EntityId>();
                    foreach (var id in set)
                    {
                        if (i++ % 4 == 0)
                        {
                            t.Destroy(id);
                            destroyed.Add(id);
                        }
                    }

                    set.ExceptWith(destroyed);
                }

                Assert.That(t.Commit(), Is.True);
            }
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIxK>();
            dbe.InitializeArchetypes();
            before = ClusterPlacementProbe.Locations(dbe, Archetype<AsmIxV1Arch>.Metadata.ArchetypeId);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIxK>();
            dbe.RegisterComponentFromAccessor<AsmIxAdded>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: migrated");

            // Premise: the index entries ARE cluster positions, so the queries below prove something only if positions changed.
            var after = ClusterPlacementProbe.Locations(dbe, Archetype<AsmIxV2Arch>.Metadata.ArchetypeId);
            var moved = 0;
            foreach (var (raw, location) in after)
            {
                moved += before[raw] != location ? 1 : 0;
            }

            Assert.That(after, Has.Count.EqualTo(before.Count), "premise: every survivor placed");
            Assert.That(moved, Is.GreaterThan(0), "premise: the re-placement moved entities to other (chunk, slot) positions");

            using var t = dbe.CreateQuickTransaction();
            for (var g = 0; g < 6; g++)
            {
                var group = g;
                var found = t.Query<AsmIxV2Arch>().WhereField<AsmIxK>(c => c.Group == group).Execute();
                Assert.That(found, Is.EquivalentTo(byGroup[g]), $"group {g}: every entity, at its new position");
            }
        }
    }

    [Test]
    public void Index_AddedIndexedComponent_NeverMatchesAbsent()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIaK>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Population; i++)
            {
                ids.Add(t.Spawn<AsmIaV1Arch>(AsmIaV1Arch.K.Set(new AsmIaK(i))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe2.RegisterComponentFromAccessor<AsmIaK>();
        dbe2.RegisterComponentFromAccessor<AsmIaAdded>();
        dbe2.InitializeArchetypes();

        // Every pre-existing entity holds zero bytes in the added component, so key 0 is indexed for all of them — and none is matched (QENABLE-01).
        using (var t = dbe2.CreateQuickTransaction())
        {
            Assert.That(t.Query<AsmIaV2Arch>().WhereField<AsmIaAdded>(c => c.Tag == 0).Execute(), Is.Empty, "an absent component never matches");
        }

        using (var t = dbe2.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            t.OpenMut(ids[4]).Enable(AsmIaV2Arch.Added, new AsmIaAdded(0));
            t.OpenMut(ids[9]).Enable(AsmIaV2Arch.Added, new AsmIaAdded(77));
            Assert.That(t.Commit(), Is.True);
        }

        // A SingleVersion write reaches the per-archetype index at the tick fence (deferred-fence indexing), migrated archetype or not.
        dbe2.WriteTickFence(1);

        using (var t = dbe2.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.Query<AsmIaV2Arch>().WhereField<AsmIaAdded>(c => c.Tag == 0).Execute(), Is.EquivalentTo(new[] { ids[4] }),
                    "supplied, it matches");
                Assert.That(t.Query<AsmIaV2Arch>().WhereField<AsmIaAdded>(c => c.Tag == 77).Execute(), Is.EquivalentTo(new[] { ids[9] }));
            });
        }
    }

    [Test]
    public void Index_AddedUniqueDense_PopulatedArchetype_IsRefused()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIuK>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < 12; i++)
            {
                ids.Add(t.Spawn<AsmIuV1Arch>(AsmIuV1Arch.K.Set(new AsmIuK(i))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIuK>();
            dbe.RegisterComponentFromAccessor<AsmIuAdded>();
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(dbe.CleanShutdownAtOpenForTest, Is.True, "premise: the clean-open refusal path (the seed session completed its open)");
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.UniqueIndexOnAddedComponent));
                Assert.That(ex.Message, Is.EqualTo(
                    "Archetype 'Asm.IndexUnique' gains component 'Typhon.Schema.UnitTest.AsmIuAdded', whose field 'Code' carries a unique index. Its 12 "
                    + "existing entities have no value for it and would all share key 0. Declare the index Sparse, or add it in a later revision once every "
                    + "entity has a value; until then, open the database with the build that wrote it. Nothing of this archetype was modified."));
            });
        }

        // Refused before the commit: the revision-1 schema still opens, its data intact.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIuK>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty);
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i++)
            {
                var k = t.Open(ids[i]).Read(AsmIuV1Arch.K).K;
                Assert.That(k, Is.EqualTo(i));
            }
        }
    }

    // R7 needs the entity count, which only the open reads: the preview lists the set change and says, for this archetype, that R7 is not checked — it
    // never presents a change the open refuses as one that would migrate without a word. An archetype that never held an entity passes all three.
    [Test]
    public void Preview_SaysWhichDataDependentRefusalsItCannotCheck()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIuK>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < 12; i++)
            {
                t.Spawn<AsmIuV1Arch>(AsmIuV1Arch.K.Set(new AsmIuK(i)));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIuK>();
            dbe.RegisterComponentFromAccessor<AsmIuAdded>();
            var preview = dbe.PreviewArchetypeSetChanges();
            Assert.Multiple(() =>
            {
                Assert.That(preview.Changes, Has.Count.EqualTo(1));
                Assert.That(preview.Changes[0].ArchetypeName, Is.EqualTo("Asm.IndexUnique"));
                Assert.That(preview.Refusals, Is.Empty, "R7 needs the entity count: the preview cannot throw it");
                Assert.That(preview.NotChecked, Has.Count.EqualTo(1));
                Assert.That(preview.NotChecked[0], Does.StartWith("Archetype 'Asm.IndexUnique' gains components whose refusal depends on its entities "
                                                                  + "('Typhon.Schema.UnitTest.AsmIuAdded.Code' is a unique, non-sparse index)"));
                Assert.That(preview.NotChecked[0], Does.EndWith("not checked by this preview."));
            });
        }

        // And the open does refuse it: what the preview flagged is real.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIuK>();
            dbe.RegisterComponentFromAccessor<AsmIuAdded>();
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.UniqueIndexOnAddedComponent));
        }
    }

    [Test]
    public void Index_AddedUniqueSparse_IsAccepted()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsmIsK>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < 40; i++)
            {
                ids.Add(t.Spawn<AsmIsV1Arch>(AsmIsV1Arch.K.Set(new AsmIsK(i))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe2.RegisterComponentFromAccessor<AsmIsK>();
        dbe2.RegisterComponentFromAccessor<AsmIsAdded>();
        dbe2.InitializeArchetypes();
        Assert.That(dbe2.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "a sparse unique index skips key 0, so the absent entities do not collide");

        using (var t = dbe2.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            t.OpenMut(ids[7]).Enable(AsmIsV2Arch.Added, new AsmIsAdded(5));
            Assert.That(t.Commit(), Is.True);
        }

        dbe2.WriteTickFence(1);

        using (var t = dbe2.CreateQuickTransaction())
        {
            Assert.That(t.Query<AsmIsV2Arch>().WhereField<AsmIsAdded>(c => c.Code == 5).Execute(), Is.EquivalentTo(new[] { ids[7] }));
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertNoLeakedPages(DatabaseEngine dbe, string when)
    {
        var report = dbe.RunStorageIntegrityCheck();
        var detail = new System.Text.StringBuilder();
        foreach (var issue in report.Issues)
        {
            detail.Append(detail.Length == 0 ? "" : " | ").Append(issue.Kind).Append(": ").Append(issue.Detail);
        }

        Assert.That(report.IsHealthy, Is.True,
            $"{when}: storage integrity must be clean — {report.OrphanPageCount} orphan page(s), {report.PhantomPageCount} phantom. {detail}");
    }

    /// <summary>
    /// Rewrites an archetype row's stored component names to <paramref name="names"/>, in a session that never initialises its archetypes. Its open never
    /// completes, so it does NOT close clean (measured: the next open reads the clean flag false) and leaves the failed-open marker set: the next open is the
    /// open after a failed one, where no torn page is possible — the staging of a file an earlier build left.
    /// </summary>
    private void RewriteStoredNames(string archetype, params string[] names)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        if (dbe.GetComponentTable<ArchetypeR1>() == null)
        {
            dbe.RegisterComponentFromAccessor<ArchetypeR1>();
        }

        var table = dbe.GetComponentTable<ArchetypeR1>();
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var cs = dbe.MMF.CreateChangeSet();
        var found = false;
        var segment = table.ComponentSegment;
        for (var chunkId = 1; chunkId < segment.ChunkCapacity && !found; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId) || !SystemCrud.Read(table, chunkId, out ArchetypeR1 row, dbe.EpochManager) || row.Name.AsString != archetype)
            {
                continue;
            }

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
            found = true;
        }

        cs.SaveChanges();
        Assert.That(found, Is.True, $"premise: a row named '{archetype}' is persisted");
    }
}
