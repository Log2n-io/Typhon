using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Every scenario owns its components. The archetype classes below share durable names across revisions, and D3 (SCHEMA-10) refuses two consumed
// classes of one name at one revision: a component shared with another fixture would make that fixture's opens consume them too.

#region Same-count swap: {A, B} -> {A, C}, same revision

[Component("Typhon.Schema.UnitTest.AsvSwapA", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvSwapA
{
    public int A;
    public int Pad;
    public AsvSwapA(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvSwapB", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvSwapB
{
    public long B;
    public AsvSwapB(long b) => B = b;
}

[Component("Typhon.Schema.UnitTest.AsvSwapC", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvSwapC
{
    public long C;
    public AsvSwapC(long c) => C = c;
}

[Archetype(1, Name = "Asv.Swap")]
class AsvSwapV1Arch : Archetype<AsvSwapV1Arch>
{
    public static readonly Comp<AsvSwapA> A = Register<AsvSwapA>();
    public static readonly Comp<AsvSwapB> B = Register<AsvSwapB>();
}

[Archetype(1, Name = "Asv.Swap")]
class AsvSwapV2Arch : Archetype<AsvSwapV2Arch>
{
    public static readonly Comp<AsvSwapA> A = Register<AsvSwapA>();
    public static readonly Comp<AsvSwapC> C = Register<AsvSwapC>();
}

#endregion

#region Set change without a revision increase

[Component("Typhon.Schema.UnitTest.AsvScKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvScKeep
{
    public int V;
    public int Pad;
    public AsvScKeep(int v) { V = v; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvScAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvScAdded
{
    public long V;
}

[Archetype(1, Name = "Asv.SetChange")]
class AsvScV1Arch : Archetype<AsvScV1Arch>
{
    public static readonly Comp<AsvScKeep> Keep = Register<AsvScKeep>();
}

[Archetype(2, Name = "Asv.SetChange")]
class AsvScV2Arch : Archetype<AsvScV2Arch>
{
    public static readonly Comp<AsvScKeep> Keep = Register<AsvScKeep>();
    public static readonly Comp<AsvScAdded> Added = Register<AsvScAdded>();
}

#endregion

#region Revision downgrade

[Component("Typhon.Schema.UnitTest.AsvDgKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvDgKeep
{
    public int V;
    public int Pad;
    public AsvDgKeep(int v) { V = v; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvDgMark", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvDgMark
{
    public long V;
}

[Archetype(2, Name = "Asv.Downgrade")]
class AsvDgNewArch : Archetype<AsvDgNewArch>
{
    public static readonly Comp<AsvDgKeep> Keep = Register<AsvDgKeep>();
    public static readonly Comp<AsvDgMark> Mark = Register<AsvDgMark>();
}

[Archetype(1, Name = "Asv.Downgrade")]
class AsvDgOldArch : Archetype<AsvDgOldArch>
{
    public static readonly Comp<AsvDgKeep> Keep = Register<AsvDgKeep>();
}

#endregion

#region Component removed

[Component("Typhon.Schema.UnitTest.AsvRmKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRmKeep
{
    public int V;
    public int Pad;
    public AsvRmKeep(int v) { V = v; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvRmGone", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRmGone
{
    public long V;
    public AsvRmGone(long v) => V = v;
}

[Component("Typhon.Schema.UnitTest.AsvRmNew", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRmNew
{
    public long V;
}

[Archetype(1, Name = "Asv.Remove")]
class AsvRmV1Arch : Archetype<AsvRmV1Arch>
{
    public static readonly Comp<AsvRmKeep> Keep = Register<AsvRmKeep>();
    public static readonly Comp<AsvRmGone> Gone = Register<AsvRmGone>();
}

/// <summary>Drops <see cref="AsvRmGone"/>. Adding <see cref="AsvRmNew"/> only keeps it from resolving, and superseding V1, in the first open.</summary>
[Archetype(2, Name = "Asv.Remove")]
class AsvRmV2Arch : Archetype<AsvRmV2Arch>
{
    public static readonly Comp<AsvRmKeep> Keep = Register<AsvRmKeep>();
    public static readonly Comp<AsvRmNew> New = Register<AsvRmNew>();
}

#endregion

#region Names buffer missing

[Component("Typhon.Schema.UnitTest.AsvNbKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvNbKeep
{
    public int V;
    public int Pad;
    public AsvNbKeep(int v) { V = v; Pad = 0; }
}

[Archetype(Name = "Asv.NamesMissing")]
class AsvNbArch : Archetype<AsvNbArch>
{
    public static readonly Comp<AsvNbKeep> Keep = Register<AsvNbKeep>();
}

#endregion

#region Duplicate durable names

[Component("Typhon.Schema.UnitTest.AsvDupKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvDupKeep
{
    public int V;
    public int Pad;
    public AsvDupKeep(int v) { V = v; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvDupExtra", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvDupExtra
{
    public long V;
}

[Archetype(1, Name = "Asv.Dup")]
class AsvDupLowArch : Archetype<AsvDupLowArch>
{
    public static readonly Comp<AsvDupKeep> Keep = Register<AsvDupKeep>();
}

[Archetype(2, Name = "Asv.Dup")]
class AsvDupHighArch : Archetype<AsvDupHighArch>
{
    public static readonly Comp<AsvDupKeep> Keep = Register<AsvDupKeep>();
    public static readonly Comp<AsvDupExtra> Extra = Register<AsvDupExtra>();
}

[Component("Typhon.Schema.UnitTest.AsvDeqKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvDeqKeep
{
    public int V;
    public int Pad;
}

[Archetype(1, Name = "Asv.DupEqual")]
class AsvDeqAArch : Archetype<AsvDeqAArch>
{
    public static readonly Comp<AsvDeqKeep> Keep = Register<AsvDeqKeep>();
}

[Archetype(1, Name = "Asv.DupEqual")]
class AsvDeqBArch : Archetype<AsvDeqBArch>
{
    public static readonly Comp<AsvDeqKeep> Keep = Register<AsvDeqKeep>();
}

#endregion

#region R10: a component migrated while an archetype holding it is not opened

[Component("Typhon.Schema.UnitTest.AsvR10Shared", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvR10SharedV1
{
    public int A;
    public int Pad;
    public AsvR10SharedV1(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvR10Shared", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvR10SharedV2
{
    public int A;
    public int Pad;
    public long B;
}

[Component("Typhon.Schema.UnitTest.AsvR10Other", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvR10Other
{
    public long D;
    public AsvR10Other(long d) => D = d;
}

[Archetype(Name = "AsvR10.X")]
class AsvR10XArch : Archetype<AsvR10XArch>
{
    public static readonly Comp<AsvR10SharedV1> Shared = Register<AsvR10SharedV1>();
}

[Archetype(Name = "AsvR10.Y")]
class AsvR10YArch : Archetype<AsvR10YArch>
{
    public static readonly Comp<AsvR10SharedV1> Shared = Register<AsvR10SharedV1>();
    public static readonly Comp<AsvR10Other> Other = Register<AsvR10Other>();
}

/// <summary>Reads the shared component at its V2 layout. Never spawned into.</summary>
[Archetype(Name = "AsvR10.Reader")]
class AsvR10ReaderArch : Archetype<AsvR10ReaderArch>
{
    public static readonly Comp<AsvR10SharedV2> Shared = Register<AsvR10SharedV2>();
}

#endregion

#region A component renamed in an earlier open, then a component added

[Component("Typhon.Schema.UnitTest.AsvRnOld", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRnOld
{
    public int V;
    public int Pad;
    public AsvRnOld(int v) { V = v; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvRnNew", 1, PreviousName = "Typhon.Schema.UnitTest.AsvRnOld", StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRnNew
{
    public int V;
    public int Pad;
}

[Component("Typhon.Schema.UnitTest.AsvRnAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRnAdded
{
    public long V;
}

[Archetype(1, Name = "Asv.Rename")]
class AsvRnV1Arch : Archetype<AsvRnV1Arch>
{
    public static readonly Comp<AsvRnOld> Value = Register<AsvRnOld>();
}

/// <summary>The same revision once the component is renamed: what the source looks like after the rename alone.</summary>
[Archetype(1, Name = "Asv.Rename")]
class AsvRnRenamedArch : Archetype<AsvRnRenamedArch>
{
    public static readonly Comp<AsvRnNew> Value = Register<AsvRnNew>();
}

[Archetype(2, Name = "Asv.Rename")]
class AsvRnGrowArch : Archetype<AsvRnGrowArch>
{
    public static readonly Comp<AsvRnNew> Value = Register<AsvRnNew>();
    public static readonly Comp<AsvRnAdded> Added = Register<AsvRnAdded>();
}

#endregion

#region Reorder with a revision bump: {A, B} -> {B, A}

[Component("Typhon.Schema.UnitTest.AsvRoA", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRoA
{
    public int V;
    public int Pad;
    public AsvRoA(int v) { V = v; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvRoB", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRoB
{
    public long V;
    public AsvRoB(long v) => V = v;
}

[Archetype(1, Name = "Asv.Reorder")]
class AsvRoV1Arch : Archetype<AsvRoV1Arch>
{
    public static readonly Comp<AsvRoA> A = Register<AsvRoA>();
    public static readonly Comp<AsvRoB> B = Register<AsvRoB>();
}

[Archetype(2, Name = "Asv.Reorder")]
class AsvRoV2Arch : Archetype<AsvRoV2Arch>
{
    public static readonly Comp<AsvRoB> B = Register<AsvRoB>();
    public static readonly Comp<AsvRoA> A = Register<AsvRoA>();
}

#endregion

#region Revision-only bump

[Component("Typhon.Schema.UnitTest.AsvRvKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRvKeep
{
    public int V;
    public int Pad;
    public AsvRvKeep(int v) { V = v; Pad = 0; }
}

/// <summary>Spawned at revision 2; the test lowers the stored revision to 1, which is the file a revision-1 build wrote.</summary>
[Archetype(2, Name = "Asv.RevisionOnly")]
class AsvRvV1Arch : Archetype<AsvRvV1Arch>
{
    public static readonly Comp<AsvRvKeep> Keep = Register<AsvRvKeep>();
}

#endregion

#region ArchetypeSetMigrationPolicy.Refuse: a set add skipped, an unchanged archetype opened beside it, a field migration under the skipped one

[Component("Typhon.Schema.UnitTest.AsvRfKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRfKeep
{
    public int V;
    public int Pad;
    public AsvRfKeep(int v) { V = v; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvRfAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRfAdded
{
    public long A;
}

[Component("Typhon.Schema.UnitTest.AsvRfSteady", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRfSteady
{
    public int S;
    public int Pad;
    public AsvRfSteady(int s) { S = s; Pad = 0; }
}

[Archetype(1, Name = "Asv.Refuse")]
class AsvRfV1Arch : Archetype<AsvRfV1Arch>
{
    public static readonly Comp<AsvRfKeep> Keep = Register<AsvRfKeep>();
}

[Archetype(2, Name = "Asv.Refuse")]
class AsvRfV2Arch : Archetype<AsvRfV2Arch>
{
    public static readonly Comp<AsvRfKeep> Keep = Register<AsvRfKeep>();
    public static readonly Comp<AsvRfAdded> Added = Register<AsvRfAdded>();
}

/// <summary>Unchanged across the opens: the rest of the database a Refuse open must still serve.</summary>
[Archetype(1, Name = "Asv.RefuseSteady")]
class AsvRfSteadyArch : Archetype<AsvRfSteadyArch>
{
    public static readonly Comp<AsvRfSteady> Steady = Register<AsvRfSteady>();
}

[Component("Typhon.Schema.UnitTest.AsvRfField", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRfFieldV1
{
    [Field]
    public int A;
    [Field]
    public int Pad;
    public AsvRfFieldV1(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvRfField", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRfFieldV2
{
    [Field]
    public int A;
    [Field]
    public int Pad;
    [Field]
    public long B;
}

[Component("Typhon.Schema.UnitTest.AsvRfFieldAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvRfFieldAdded
{
    public long C;
}

[Archetype(1, Name = "Asv.RefuseField")]
class AsvRfFieldV1Arch : Archetype<AsvRfFieldV1Arch>
{
    public static readonly Comp<AsvRfFieldV1> Field = Register<AsvRfFieldV1>();
}

[Archetype(2, Name = "Asv.RefuseField")]
class AsvRfFieldV2Arch : Archetype<AsvRfFieldV2Arch>
{
    public static readonly Comp<AsvRfFieldV2> Field = Register<AsvRfFieldV2>();
    public static readonly Comp<AsvRfFieldAdded> Added = Register<AsvRfFieldAdded>();
}

#endregion

#region D1: a parent's revision-only bump does not excuse a child's reorder

[Component("Typhon.Schema.UnitTest.AsvD1RoP", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvD1RoP
{
    public int P;
    public int Pad;
    public AsvD1RoP(int p) { P = p; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvD1RoA", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvD1RoA
{
    public long A;
    public AsvD1RoA(long a) => A = a;
}

[Component("Typhon.Schema.UnitTest.AsvD1RoB", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvD1RoB
{
    public long B;
    public AsvD1RoB(long b) => B = b;
}

[Archetype(1, Name = "Asv.D1RoParent")]
class AsvD1RoParentV1 : Archetype<AsvD1RoParentV1>
{
    public static readonly Comp<AsvD1RoP> P = Register<AsvD1RoP>();
}

[Archetype(1, Name = "Asv.D1RoChild")]
class AsvD1RoChildV1 : Archetype<AsvD1RoChildV1, AsvD1RoParentV1>
{
    public static readonly Comp<AsvD1RoA> A = Register<AsvD1RoA>();
    public static readonly Comp<AsvD1RoB> B = Register<AsvD1RoB>();
}

/// <summary>Revision 2 with the same set: a revision-only bump.</summary>
[Archetype(2, Name = "Asv.D1RoParent")]
class AsvD1RoParentV2 : Archetype<AsvD1RoParentV2>
{
    public static readonly Comp<AsvD1RoP> P = Register<AsvD1RoP>();
}

/// <summary>Still revision 1, its own components swapped.</summary>
[Archetype(1, Name = "Asv.D1RoChild")]
class AsvD1RoChildV2 : Archetype<AsvD1RoChildV2, AsvD1RoParentV2>
{
    public static readonly Comp<AsvD1RoB> B = Register<AsvD1RoB>();
    public static readonly Comp<AsvD1RoA> A = Register<AsvD1RoA>();
}

#endregion

#region D1: a parent's set change with a bump migrates its child at the child's own revision

[Component("Typhon.Schema.UnitTest.AsvD1GrP", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvD1GrP
{
    public int P;
    public int Pad;
    public AsvD1GrP(int p) { P = p; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvD1GrQ", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvD1GrQ
{
    public long Q;
}

[Component("Typhon.Schema.UnitTest.AsvD1GrA", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvD1GrA
{
    public long A;
    public AsvD1GrA(long a) => A = a;
}

[Archetype(1, Name = "Asv.D1GrParent")]
class AsvD1GrParentV1 : Archetype<AsvD1GrParentV1>
{
    public static readonly Comp<AsvD1GrP> P = Register<AsvD1GrP>();
}

[Archetype(1, Name = "Asv.D1GrChild")]
class AsvD1GrChildV1 : Archetype<AsvD1GrChildV1, AsvD1GrParentV1>
{
    public static readonly Comp<AsvD1GrA> A = Register<AsvD1GrA>();
}

[Archetype(2, Name = "Asv.D1GrParent")]
class AsvD1GrParentV2 : Archetype<AsvD1GrParentV2>
{
    public static readonly Comp<AsvD1GrP> P = Register<AsvD1GrP>();
    public static readonly Comp<AsvD1GrQ> Q = Register<AsvD1GrQ>();
}

[Archetype(1, Name = "Asv.D1GrChild")]
class AsvD1GrChildV2 : Archetype<AsvD1GrChildV2, AsvD1GrParentV2>
{
    public static readonly Comp<AsvD1GrA> A = Register<AsvD1GrA>();
}

#endregion

#region D1: a parent's set change committed by an open that left the child unopened

[Component("Typhon.Schema.UnitTest.AsvA3P", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvA3P
{
    public int P;
    public int Pad;
    public AsvA3P(int p) { P = p; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsvA3Q", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvA3Q
{
    public long Q;
}

[Component("Typhon.Schema.UnitTest.AsvA3A", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvA3A
{
    public long A;
    public AsvA3A(long a) => A = a;
}

[Archetype(1, Name = "Asv.A3Parent")]
class AsvA3ParentV1 : Archetype<AsvA3ParentV1>
{
    public static readonly Comp<AsvA3P> P = Register<AsvA3P>();
}

[Archetype(1, Name = "Asv.A3Child")]
class AsvA3ChildV1 : Archetype<AsvA3ChildV1, AsvA3ParentV1>
{
    public static readonly Comp<AsvA3A> A = Register<AsvA3A>();
}

[Archetype(2, Name = "Asv.A3Parent")]
class AsvA3ParentV2 : Archetype<AsvA3ParentV2>
{
    public static readonly Comp<AsvA3P> P = Register<AsvA3P>();
    public static readonly Comp<AsvA3Q> Q = Register<AsvA3Q>();
}

[Archetype(1, Name = "Asv.A3Child")]
class AsvA3ChildV2 : Archetype<AsvA3ChildV2, AsvA3ParentV2>
{
    public static readonly Comp<AsvA3A> A = Register<AsvA3A>();
}

#endregion

#region Every refusal precedes the first allocation (Apply)

/// <summary>Declared before the refused archetype, so the build loop reaches it first.</summary>
[Component("Typhon.Schema.UnitTest.AsvM6Good", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvM6Good
{
    public long G;
}

[Archetype(1, Name = "Asv.M6Good")]
class AsvM6AGoodArch : Archetype<AsvM6AGoodArch>
{
    public static readonly Comp<AsvM6Good> Good = Register<AsvM6Good>();
}

[Component("Typhon.Schema.UnitTest.AsvM6Keep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvM6Keep
{
    public long K;
    public AsvM6Keep(long k) => K = k;
}

[Component("Typhon.Schema.UnitTest.AsvM6Added", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsvM6Added
{
    public long A;
}

[Archetype(1, Name = "Asv.M6Bad")]
class AsvM6BadV1 : Archetype<AsvM6BadV1>
{
    public static readonly Comp<AsvM6Keep> Keep = Register<AsvM6Keep>();
}

/// <summary>A set change at the same revision: R1.</summary>
[Archetype(1, Name = "Asv.M6Bad")]
class AsvM6BadV2 : Archetype<AsvM6BadV2>
{
    public static readonly Comp<AsvM6Keep> Keep = Register<AsvM6Keep>();
    public static readonly Comp<AsvM6Added> Added = Register<AsvM6Added>();
}

#endregion

/// <summary>
/// The validation half of archetype-set evolution (#1394, <c>claude/design/Schema/07-archetype-set-evolution.md</c>): slots matched by component name,
/// the revision rules (refusals, and the reorder and revision-only changes that commit), component renames, duplicate durable names, and the guard against
/// migrating a component under an archetype the open does not consume.
/// </summary>
/// <remarks>
/// Revision pairs share <c>[Archetype(Name = ...)]</c>, which D3 makes legal. Each open registers only the components of the revision it means to consume,
/// so the other one is skipped for an unresolved slot rather than competing. Non-parallelizable like its four siblings: the archetype registry is
/// process-wide, and these tests hide classes from it per engine (<see cref="DatabaseEngine.HideArchetypeForTest"/>) and stage files other fixtures'
/// archetypes would also consume.
/// </remarks>
[TestFixture]
[NonParallelizable]
class ArchetypeSchemaValidationTests : TestBase<ArchetypeSchemaValidationTests>
{
    private WarningCapture _warnings;

    public override void Setup()
    {
        base.Setup();

        // Rebuilt with a capturing provider beside the fixture's own, so the engine's ILogger<DatabaseEngine> reaches it (SmartTeleportationTests' pattern).
        _warnings = new WarningCapture();
        ServiceCollection.AddSingleton<ILoggerProvider>(_warnings);
        (ServiceProvider as IDisposable)?.Dispose();
        ServiceProvider = ServiceCollection.BuildServiceProvider();
    }

    /// <summary>The open policy of the engines this test creates from here on (the fixture's options object is one per service provider).</summary>
    private void SetPolicy(ArchetypeSetMigrationPolicy policy) =>
        ServiceProvider.GetRequiredService<IOptions<DatabaseEngineOptions>>().Value.ArchetypeSetMigration = policy;

    // ── ArchetypeSetMigrationPolicy.Refuse (D4): an inspector never rewrites a file ───────────────────────────────────────────────────────────────────

    private (List<EntityId> Set, EntityId Steady) SpawnRefuseV1()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvRfKeep>();
        dbe.RegisterComponentFromAccessor<AsvRfSteady>();
        dbe.InitializeArchetypes();
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = 0; i < 100; i++)
        {
            ids.Add(tx.Spawn<AsvRfV1Arch>(AsvRfV1Arch.Keep.Set(new AsvRfKeep(i + 1))));
        }

        var steady = tx.Spawn<AsvRfSteadyArch>(AsvRfSteadyArch.Steady.Set(new AsvRfSteady(77)));
        Assert.That(tx.Commit(), Is.True);
        return (ids, steady);
    }

    private static DatabaseEngine OpenRefuseV2(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvRfKeep>();
        dbe.RegisterComponentFromAccessor<AsvRfAdded>();
        dbe.RegisterComponentFromAccessor<AsvRfSteady>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void RefusePolicy_SetChange_SkipsTheArchetype_RowUntouched_RestOpens()
    {
        var (ids, steady) = SpawnRefuseV1();
        var before = ReadRow("Asv.Refuse");

        SetPolicy(ArchetypeSetMigrationPolicy.Refuse);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenRefuseV2(scope);
            Assert.That(dbe.RefusedArchetypeSetChanges, Has.Count.EqualTo(1));
            var refusal = dbe.RefusedArchetypeSetChanges[0];
            Assert.Multiple(() =>
            {
                Assert.That((refusal.ArchetypeName, refusal.FromRevision, refusal.ToRevision, refusal.Kind),
                    Is.EqualTo(("Asv.Refuse", 1, 2, ArchetypeSetChangeKind.SetChange)));
                Assert.That(refusal.Changes, Does.Contain("+Typhon.Schema.UnitTest.AsvRfAdded"));
                Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty, "nothing migrated");
                Assert.That(_warnings.Matching("This engine does not migrate archetype sets"), Is.EqualTo(1), "the skip is logged once");
            });

            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Open(steady).Read(AsvRfSteadyArch.Steady).S, Is.EqualTo(77), "the rest of the database opens");
            var ex = Assert.Throws<InvalidOperationException>(() => tx.Spawn<AsvRfV2Arch>(AsvRfV2Arch.Keep.Set(new AsvRfKeep(1))));
            Assert.That(ex!.Message, Does.Contain("ArchetypeSetMigration = Refuse"), "a use of the skipped archetype says why it is not open");
        }

        AssertRowUnchanged(before, ReadRow("Asv.Refuse"));

        // The file is the pre-migration image: an Apply open migrates it, and every entity reads back.
        SetPolicy(ArchetypeSetMigrationPolicy.Apply);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = OpenRefuseV2(scope);
            Assert.That(dbe.RefusedArchetypeSetChanges, Is.Empty);
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1));
            Assert.That(dbe.LastOpenArchetypeSetMigrations[0].EntitiesMigrated, Is.EqualTo(ids.Count));
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i++)
            {
                Assert.That(tx.Open(ids[i]).Read(AsvRfV2Arch.Keep).V, Is.EqualTo(i + 1));
            }
        }
    }

    /// <summary>
    /// An inspector registers only the components the file holds, so the component a set change adds is unregistered and the new revision is skipped for
    /// that unresolved slot before it is classified. It is reported all the same, from the names: the most common set change must not reach an inspector
    /// as nothing at all.
    /// </summary>
    [Test]
    public void RefusePolicy_AddedComponentNotRegistered_IsReported()
    {
        var (_, steady) = SpawnRefuseV1();
        var before = ReadRow("Asv.Refuse");

        SetPolicy(ArchetypeSetMigrationPolicy.Refuse);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(AsvRfV1Arch);   // the inspected build declares revision 2 only
            dbe.RegisterComponentFromAccessor<AsvRfKeep>();
            dbe.RegisterComponentFromAccessor<AsvRfSteady>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.RefusedArchetypeSetChanges, Has.Count.EqualTo(1));
            var refusal = dbe.RefusedArchetypeSetChanges[0];
            Assert.Multiple(() =>
            {
                Assert.That((refusal.ArchetypeName, refusal.FromRevision, refusal.ToRevision, refusal.Kind),
                    Is.EqualTo(("Asv.Refuse", 1, 2, ArchetypeSetChangeKind.SetChange)));
                Assert.That(refusal.Changes, Does.Contain("+Typhon.Schema.UnitTest.AsvRfAdded"));
            });

            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Open(steady).Read(AsvRfSteadyArch.Steady).S, Is.EqualTo(77));
        }

        AssertRowUnchanged(before, ReadRow("Asv.Refuse"));

        // Under Apply the same unresolved archetype is a plain skip, as it always was: nothing is reported.
        SetPolicy(ArchetypeSetMigrationPolicy.Apply);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(AsvRfV1Arch);
            dbe.RegisterComponentFromAccessor<AsvRfKeep>();
            dbe.RegisterComponentFromAccessor<AsvRfSteady>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.RefusedArchetypeSetChanges, Is.Empty);
        }
    }

    [Test]
    public void RefusePolicy_RevisionOnlyBump_IsSkipped()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRvKeep>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = tx.Spawn<AsvRvV1Arch>(AsvRvV1Arch.Keep.Set(new AsvRvKeep(9)));
            Assert.That(tx.Commit(), Is.True);
        }

        TamperRow("Asv.RevisionOnly", (ref ArchetypeR1 row) => row.Revision = 1);
        var before = ReadRow("Asv.RevisionOnly");

        SetPolicy(ArchetypeSetMigrationPolicy.Refuse);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRvKeep>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.RefusedArchetypeSetChanges, Has.Count.EqualTo(1));
            var refusal = dbe.RefusedArchetypeSetChanges[0];
            Assert.That((refusal.Kind, refusal.Changes), Is.EqualTo((ArchetypeSetChangeKind.RevisionOnly, "")));
        }

        AssertRowUnchanged(before, ReadRow("Asv.RevisionOnly"));

        SetPolicy(ArchetypeSetMigrationPolicy.Apply);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRvKeep>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Open(id).Read(AsvRvV1Arch.Keep).V, Is.EqualTo(9));
        }

        Assert.That(ReadRow("Asv.RevisionOnly").Revision, Is.EqualTo(2), "the Apply open committed the revision");
    }

    /// <summary>
    /// R10 still applies under Refuse (D4): a component whose layout migrates in this open, held by an archetype the policy skips, would leave that
    /// archetype's cluster in the old layout under the new catalog.
    /// </summary>
    [Test]
    public void RefusePolicy_FieldMigrationUnderTheSkippedArchetype_IsRefused()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRfFieldV1>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < 20; i++)
            {
                tx.Spawn<AsvRfFieldV1Arch>(AsvRfFieldV1Arch.Field.Set(new AsvRfFieldV1(i)));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var before = ReadRow("Asv.RefuseField");
        SetPolicy(ArchetypeSetMigrationPolicy.Refuse);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRfFieldV2>();
            dbe.RegisterComponentFromAccessor<AsvRfFieldAdded>();
            Assert.That(dbe.HasComponentMigrations, Is.True, "premise: the shared component widens in this open");
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.UnopenedHolder));
                Assert.That(ex.Message, Does.Contain("archetype 'Asv.RefuseField' stores it and is not opened by this process"));
                Assert.That(ex.Message, Does.Contain("ArchetypeSetMigration = Refuse"), "the reason names the policy");
            });
        }

        AssertRowUnchanged(before, ReadRow("Asv.RefuseField"));
    }

    // ── SCHEMA-11: names, not indexes ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("SCHEMA-11")]
    public void SameCountSwapWithoutRevision_IsRefused()
    {
        var id = SpawnSwapV1();
        var before = ReadRow("Asv.Swap");

        AssertSwapRefused();

        AssertRowUnchanged(before, ReadRow("Asv.Swap"));
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvSwapA>();
        dbe.RegisterComponentFromAccessor<AsvSwapB>();
        dbe.InitializeArchetypes();
        using var tx = dbe.CreateQuickTransaction();
        var e = tx.Open(id);
        var a = e.Read(AsvSwapV1Arch.A);
        var b = e.Read(AsvSwapV1Arch.B);
        Assert.Multiple(() =>
        {
            Assert.That(a.A, Is.EqualTo(5), "the old schema still opens, its data intact");
            Assert.That(b.B, Is.EqualTo(0x600DL));
        });
    }

    // The genuineness proof: with the slot names not compared (the pre-#1394 count-and-revision check), the swap opens and the verifier must say so.
    [Test]
    [RuleMutant("SCHEMA-11")]
    public void Mutant_SlotNamesNotCompared_SwapIsDetected()
    {
        SpawnSwapV1();
        RuleMutants.AssertDetects("SCHEMA-11", SwapMarker, () => AssertSwapRefused(skipNameMatch: true));
    }

    private const string SwapMarker = "SCHEMA-11: a same-count swap at the same revision must be refused";

    private EntityId SpawnSwapV1()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvSwapA>();
        dbe.RegisterComponentFromAccessor<AsvSwapB>();
        dbe.InitializeArchetypes();

        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        var id = tx.Spawn<AsvSwapV1Arch>(AsvSwapV1Arch.A.Set(new AsvSwapA(5)), AsvSwapV1Arch.B.Set(new AsvSwapB(0x600D)));
        Assert.That(tx.Commit(), Is.True);
        return id;
    }

    private void AssertSwapRefused(bool skipNameMatch = false)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvSwapA>();
        dbe.RegisterComponentFromAccessor<AsvSwapC>();
        dbe.SkipArchetypeSlotNameMatchForTest = skipNameMatch;

        var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes(), SwapMarker);
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.RevisionNotBumped));
            Assert.That(ex.ArchetypeName, Is.EqualTo("Asv.Swap"));
            Assert.That(ex.Message, Is.EqualTo(
                "Archetype 'Asv.Swap' changed its component set (+Typhon.Schema.UnitTest.AsvSwapC at slot 1; -Typhon.Schema.UnitTest.AsvSwapB from slot 1) "
                + "but is still at revision 1. A component-set change is a schema change: increase [Archetype(Revision)] (or the revision of the ancestor "
                + "whose set changed) above 1 and open again. Nothing of this archetype was modified."));
        });
    }

    [Test]
    [VerifiesRule("SCHEMA-11")]
    public void SetChangeWithoutRevisionBump_IsRefused()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvScKeep>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<AsvScV1Arch>(AsvScV1Arch.Keep.Set(new AsvScKeep(3)));
            Assert.That(tx.Commit(), Is.True);
        }

        // A file written by a build whose revision 2 still declared {Keep}: revision 2 now declares {Keep, Added} without increasing it.
        TamperRow("Asv.SetChange", (ref ArchetypeR1 row) => row.Revision = 2);
        var before = ReadRow("Asv.SetChange");

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvScKeep>();
            dbe.RegisterComponentFromAccessor<AsvScAdded>();

            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(dbe.CleanShutdownAtOpenForTest, Is.False, "premise: the tamper session (its open never completed) did not close clean");
                Assert.That(dbe.TolerateTornSegmentsAtOpen, Is.False, "premise: the open after it reads one image, strictly");
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.RevisionNotBumped));
                Assert.That(ex.Message, Does.StartWith(
                    "Archetype 'Asv.SetChange' changed its component set (+Typhon.Schema.UnitTest.AsvScAdded at slot 1) but is still at revision 2."));
            });
        }

        AssertRowUnchanged(before, ReadRow("Asv.SetChange"));
    }

    [Test]
    public void RevisionDowngrade_IsRefused()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvDgKeep>();
            dbe.RegisterComponentFromAccessor<AsvDgMark>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<AsvDgNewArch>(AsvDgNewArch.Keep.Set(new AsvDgKeep(8)));
            Assert.That(tx.Commit(), Is.True);
        }
        var before = ReadRow("Asv.Downgrade");

        // ReadRow's session never completes its open, so it does not close clean: one that does, so the refusal below is the CLEAN-open path.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvDgKeep>();
            dbe.RegisterComponentFromAccessor<AsvDgMark>();
            dbe.InitializeArchetypes();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvDgKeep>();

            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(dbe.CleanShutdownAtOpenForTest, Is.True, "premise: the clean-open refusal path (the last session completed its open)");
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.Downgrade));
                Assert.That(ex.Message, Is.EqualTo(
                    "Archetype 'Asv.Downgrade' is stored at revision 2 and this build declares revision 1: the database was written by a newer build. Open "
                    + "it with that build. Nothing of this archetype was modified."));
            });
        }

        AssertRowUnchanged(before, ReadRow("Asv.Downgrade"));
    }

    [Test]
    public void ComponentRemoved_IsRefused_AndTheOldSchemaStillOpens()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRmKeep>();
            dbe.RegisterComponentFromAccessor<AsvRmGone>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = tx.Spawn<AsvRmV1Arch>(AsvRmV1Arch.Keep.Set(new AsvRmKeep(41)), AsvRmV1Arch.Gone.Set(new AsvRmGone(0xDEADL)));
            Assert.That(tx.Commit(), Is.True);
        }
        var before = ReadRow("Asv.Remove");

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRmKeep>();
            dbe.RegisterComponentFromAccessor<AsvRmNew>();

            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.ComponentRemoved));
                Assert.That(ex.Message, Does.StartWith(
                    "Archetype 'Asv.Remove' revision 2 no longer declares 'Typhon.Schema.UnitTest.AsvRmGone', which revision 1 stored. Removing a "
                    + "component deletes its data and is not supported yet"));
            });
        }

        AssertRowUnchanged(before, ReadRow("Asv.Remove"));

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRmKeep>();
            dbe.RegisterComponentFromAccessor<AsvRmGone>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            var e = tx.Open(id);
            var keep = e.Read(AsvRmV1Arch.Keep);
            var gone = e.Read(AsvRmV1Arch.Gone);
            Assert.Multiple(() =>
            {
                Assert.That(keep.V, Is.EqualTo(41));
                Assert.That(gone.V, Is.EqualTo(0xDEADL), "the component the refused revision dropped is still there");
            });
        }
    }

    [Test]
    public void NamesBufferMissing_IsRefused()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvNbKeep>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<AsvNbArch>(AsvNbArch.Keep.Set(new AsvNbKeep(1)));
            Assert.That(tx.Commit(), Is.True);
        }

        TamperRow("Asv.NamesMissing", (ref ArchetypeR1 row) => row.ComponentNames._bufferId = 0);

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvNbKeep>();

            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(dbe.CleanShutdownAtOpenForTest, Is.False, "premise: the tamper session (its open never completed) did not close clean");
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.NamesUnreadable));
                Assert.That(ex.Message, Is.EqualTo(
                    "Archetype 'Asv.NamesMissing': its stored component list (ArchetypeR1.ComponentNames) is missing (buffer id 0 on a row of 1 "
                    + "components), so its slots cannot be matched to this build's components. Nothing of this archetype was modified."));
            });
        }
    }

    // A file whose stored names are stale: a component renamed by a build that did not refresh them (every build before #1394; staged here through
    // SkipArchetypeNamesRefreshForTest). The next open's set change must read the stale name as the renamed component, not as a removal plus an addition.
    [Test]
    [VerifiesRule("SCHEMA-11")]
    public void RenamedComponent_InEarlierOpen_IsNotRemoveAdd()
    {
        var id = SpawnRenameV1(77);

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRnNew>();
            dbe.SkipArchetypeNamesRefreshForTest = true;
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Open(id).Read(AsvRnRenamedArch.Value).V, Is.EqualTo(77), "a rename alone opens, the data intact");
        }

        Assert.That(ReadNames("Asv.Rename"), Is.EqualTo(new[] { "Typhon.Schema.UnitTest.AsvRnOld" }),
            "premise: the stored list still names the component by its former name");

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRnNew>();
            dbe.RegisterComponentFromAccessor<AsvRnAdded>();
            dbe.InitializeArchetypes();

            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1));
            Assert.That(dbe.LastOpenArchetypeSetMigrations[0].Changes, Is.EqualTo("+Typhon.Schema.UnitTest.AsvRnAdded at slot 1"),
                "matched through the journal: only the add");
            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Open(id).Read(AsvRnGrowArch.Value).V, Is.EqualTo(77), "the renamed component carried across by its current name");
        }

        Assert.That(ReadNames("Asv.Rename"), Is.EqualTo(new[] { "Typhon.Schema.UnitTest.AsvRnNew", "Typhon.Schema.UnitTest.AsvRnAdded" }),
            "the migration's row names every component by its current name");
    }

    // AC-22: a rename and an add in one open. The journal row of the rename is written at registration, before the archetype is matched.
    [Test]
    [VerifiesRule("SCHEMA-11")]
    public void RenamedComponent_SameOpenAsSetAdd()
    {
        var ids = new List<EntityId>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(SpawnRenameV1(100 + i));
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRnNew>();
            dbe.RegisterComponentFromAccessor<AsvRnAdded>();
            dbe.InitializeArchetypes();

            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1));
            Assert.That(dbe.LastOpenArchetypeSetMigrations[0].Changes, Is.EqualTo("+Typhon.Schema.UnitTest.AsvRnAdded at slot 1"), "not a remove + add");
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i++)
            {
                var e = tx.Open(ids[i]);
                var value = e.Read(AsvRnGrowArch.Value).V;
                var addedEnabled = e.IsEnabled(AsvRnGrowArch.Added);
                Assert.That(value, Is.EqualTo(100 + i));
                Assert.That(addedEnabled, Is.False, "the added component is absent");
            }
        }

        Assert.That(ReadNames("Asv.Rename"), Is.EqualTo(new[] { "Typhon.Schema.UnitTest.AsvRnNew", "Typhon.Schema.UnitTest.AsvRnAdded" }));
    }

    // AC-22, AC-24: a rename alone refreshes the stored names in a metadata-only commit, and the integrity scanner (which looks the stored names up in the
    // catalog) finds nothing.
    [Test]
    public void RenamedComponent_OnlyRename_RefreshesNames_IntegrityClean()
    {
        var id = SpawnRenameV1(55);
        AssertIntegrityClean("premise: the file before the rename", afterSchemaCommit: false);

        // Every read in the opens themselves: a session that never initialises its archetypes (ReadRow's) never completes its open, so it does not close
        // clean (measured) and leaves the failed-open marker set, which the integrity scan would report.
        ArchetypeR1 before;
        ArchetypeR1 after;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRnNew>();
            before = FindRow(dbe, "Asv.Rename");
            dbe.InitializeArchetypes();
            after = FindRow(dbe, "Asv.Rename");
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty, "a metadata-only change is not a set migration");
            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Open(id).Read(AsvRnRenamedArch.Value).V, Is.EqualTo(55));
        }

        AssertIntegrityClean("after the names refresh");
        Assert.Multiple(() =>
        {
            Assert.That(ReadNames("Asv.Rename"), Is.EqualTo(new[] { "Typhon.Schema.UnitTest.AsvRnNew" }), "names refreshed");
            Assert.That(after.ComponentNames._bufferId, Is.Not.EqualTo(before.ComponentNames._bufferId), "in a new buffer");
            Assert.That(after.EntityMapSPI, Is.EqualTo(before.EntityMapSPI), "no data moved");
            Assert.That(after.ClusterSegmentSPI, Is.EqualTo(before.ClusterSegmentSPI), "no data moved");
            Assert.That(after.Revision, Is.EqualTo(before.Revision));
        });
    }

    // ── D1: revision rules that commit ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    // AC-6: {A, B} -> {B, A} with a revision bump migrates, every component read back by name.
    [Test]
    [VerifiesRule("SCHEMA-11")]
    public void ReorderOnly_WithRevisionBump_Migrates()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRoA>();
            dbe.RegisterComponentFromAccessor<AsvRoB>();
            dbe.HideArchetypeForTest = HideReorderV2;
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < 200; i++)
            {
                ids.Add(tx.Spawn<AsvRoV1Arch>(AsvRoV1Arch.A.Set(new AsvRoA(i)), AsvRoV1Arch.B.Set(new AsvRoB(i * 7L + 3))));
            }
            Assert.That(tx.Commit(), Is.True);
        }

        Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> oracle;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRoA>();
            dbe.RegisterComponentFromAccessor<AsvRoB>();
            dbe.HideArchetypeForTest = HideReorderV2;
            dbe.InitializeArchetypes();
            oracle = ArchetypeOracle.Snapshot(dbe, Archetype<AsvRoV1Arch>.Metadata.ArchetypeId, ids);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRoA>();
            dbe.RegisterComponentFromAccessor<AsvRoB>();
            // The revision-2 class was hidden from the opens above; here both resolve, and the revision-1 class is superseded (D3).
            dbe.InitializeArchetypes();

            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1));
            Assert.That(dbe.LastOpenArchetypeSetMigrations[0].Changes,
                Is.EqualTo("Typhon.Schema.UnitTest.AsvRoB 1->0; Typhon.Schema.UnitTest.AsvRoA 0->1"));
            var after = ArchetypeOracle.Snapshot(dbe, Archetype<AsvRoV2Arch>.Metadata.ArchetypeId, ids);
            ArchetypeOracle.AssertSurvivorsEqual(oracle, after);

            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i += 37)
            {
                var e = tx.Open(ids[i]);
                var a = e.Read(AsvRoV2Arch.A).V;
                var b = e.Read(AsvRoV2Arch.B).V;
                Assert.That(a, Is.EqualTo(i));
                Assert.That(b, Is.EqualTo(i * 7L + 3));
            }
        }

        Assert.That(ReadRow("Asv.Reorder").Revision, Is.EqualTo(2));
    }

    // AC-7: same set, higher revision: the row's revision moves, nothing else, and the journal says Compatible.
    [Test]
    public void RevisionOnlyBump_CommitsMetadataOnly()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRvKeep>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = tx.Spawn<AsvRvV1Arch>(AsvRvV1Arch.Keep.Set(new AsvRvKeep(9)));
            Assert.That(tx.Commit(), Is.True);
        }

        var before = ReadRow("Asv.RevisionOnly");
        TamperRow("Asv.RevisionOnly", (ref ArchetypeR1 row) => row.Revision = 1);
        before.Revision = 1;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRvKeep>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty);
            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Open(id).Read(AsvRvV1Arch.Keep).V, Is.EqualTo(9));

            var rows = new List<SchemaHistoryR1>();
            foreach (var row in dbe.GetSchemaHistory())
            {
                if (row.Target == SchemaObjectKind.Archetype && row.ComponentName.AsString == "Asv.RevisionOnly")
                {
                    rows.Add(row);
                }
            }

            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(rows[0].Kind, Is.EqualTo(SchemaChangeKind.Compatible));
                Assert.That(rows[0].FromRevision, Is.EqualTo(1));
                Assert.That(rows[0].ToRevision, Is.EqualTo(2));
                Assert.That(rows[0].EntitiesMigrated, Is.Zero);
            });
        }

        var after = ReadRow("Asv.RevisionOnly");
        Assert.Multiple(() =>
        {
            Assert.That(after.Revision, Is.EqualTo(2), "the row's revision is the runtime's");
            Assert.That(after.EntityMapSPI, Is.EqualTo(before.EntityMapSPI), "EntityMapSPI");
            Assert.That(after.ClusterSegmentSPI, Is.EqualTo(before.ClusterSegmentSPI), "ClusterSegmentSPI");
            Assert.That(after.ClusterIndexSPI, Is.EqualTo(before.ClusterIndexSPI), "ClusterIndexSPI");
            Assert.That(after.ComponentCount, Is.EqualTo(before.ComponentCount));
        });

        // And the next open finds nothing to do.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRvKeep>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.HasPendingSchemaCommit, Is.False);
        }

        Assert.That(ReadRow("Asv.RevisionOnly").ComponentNames._bufferId, Is.EqualTo(after.ComponentNames._bufferId), "an identical open rewrites nothing");
    }

    private static bool HideReorderV2(ArchetypeMetadata meta) => meta.ArchetypeType == typeof(AsvRoV2Arch);

    private EntityId SpawnRenameV1(int value)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvRnOld>();
        dbe.InitializeArchetypes();
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        var id = tx.Spawn<AsvRnV1Arch>(AsvRnV1Arch.Value.Set(new AsvRnOld(value)));
        Assert.That(tx.Commit(), Is.True);
        return id;
    }

    private void AssertIntegrityClean(string when, bool afterSchemaCommit = true)
        => ArchetypeOracle.AssertIntegrityClean(ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value.BundleDirectory, when,
            afterSchemaCommit);

    // ── SCHEMA-10 (D3): one archetype per durable name ───────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("SCHEMA-10")]
    public void DuplicateName_HigherRevisionWins_LowerLoggedOnce()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvDupKeep>();
        dbe.RegisterComponentFromAccessor<AsvDupExtra>();
        dbe.InitializeArchetypes();

        Assert.That(_warnings.Matching("Archetype 'Asv.Dup': 'AsvDupLowArch' (revision 1) is superseded by 'AsvDupHighArch' (revision 2) in this load "
                                       + "context and is not opened."), Is.EqualTo(1), "logged once per open");

        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<AsvDupHighArch>(AsvDupHighArch.Keep.Set(new AsvDupKeep(4)));
        Assert.That(tx.Commit(), Is.True);
        Assert.That(dbe.RoutingIdOf(Archetype<AsvDupHighArch>.Metadata), Is.EqualTo(id.ArchetypeId), "the higher revision is the one consumed");
    }

    [Test]
    [VerifiesRule("SCHEMA-10")]
    public void DuplicateName_EqualRevisions_IsRefused()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvDeqKeep>();

        var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.DuplicateName));
            Assert.That(ex.Message, Is.EqualTo(
                "Archetype name 'Asv.DupEqual' is declared by two classes in one load context, 'AsvDeqAArch' and 'AsvDeqBArch', both at revision 1. A load "
                + "context holds one archetype per name: give the newer declaration a higher revision, or remove one.")
                .Or.EqualTo(
                "Archetype name 'Asv.DupEqual' is declared by two classes in one load context, 'AsvDeqBArch' and 'AsvDeqAArch', both at revision 1. A load "
                + "context holds one archetype per name: give the newer declaration a higher revision, or remove one."));
        });
    }

    [Test]
    public void DuplicateName_SpawnThroughSuperseded_Throws()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvDupKeep>();
        dbe.RegisterComponentFromAccessor<AsvDupExtra>();
        dbe.InitializeArchetypes();

        using var tx = dbe.CreateQuickTransaction();
        var ex = Assert.Throws<InvalidOperationException>(() => tx.Spawn<AsvDupLowArch>(AsvDupLowArch.Keep.Set(new AsvDupKeep(1))));
        Assert.That(ex!.Message, Is.EqualTo(
            "Archetype 'AsvDupLowArch' is not open in this engine: it is superseded by 'AsvDupHighArch' (revision 2), which shares its name 'Asv.Dup'."));
    }

    // ── SCHEMA-13 (D10): no component migrates under an unopened holder ──────────────────────────────────────────────────────────────────────────────

    // Measured before the guard existed (the third open below, without the second one refusing): Y opened with no error and read Shared.A = 0 and
    // Other.D = 0 — its cluster, left at the old stride by the open that migrated Shared without consuming Y, read at the new one.
    [Test]
    [VerifiesRule("SCHEMA-13")]
    public void FieldMigrationWithAnUnopenedHolder_IsRefused()
    {
        var (x, y) = SpawnR10Holders();
        var beforeY = ReadRow("AsvR10.Y");

        AssertUnopenedHolderRefused();

        AssertRowUnchanged(beforeY, ReadRow("AsvR10.Y"));

        // With Y registered the migration runs for both holders, and both read back.
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvR10SharedV2>();
        dbe.RegisterComponentFromAccessor<AsvR10Other>();
        Assert.That(dbe.HasComponentMigrations, Is.True, "the refused open committed nothing, so this one migrates");
        dbe.InitializeArchetypes();

        using var tx = dbe.CreateQuickTransaction();
        var xShared = tx.Open(x).Read(AsvR10ReaderArch.Shared);
        var ye = tx.Open(y);
        var yShared = ye.Read(AsvR10ReaderArch.Shared);
        var yOther = ye.Read(AsvR10YArch.Other);
        Assert.Multiple(() =>
        {
            Assert.That(xShared.A, Is.EqualTo(11));
            Assert.That(yShared.A, Is.EqualTo(22));
            Assert.That(yShared.B, Is.EqualTo(0L), "the added field is zero");
            Assert.That(yOther.D, Is.EqualTo(0x0BADC0FFEEL));
        });
    }

    // The genuineness proof: with the guard off, the shared component migrates under the unopened holder and the verifier must say so.
    [Test]
    [RuleMutant("SCHEMA-13")]
    public void Mutant_GuardOff_UnopenedHolderIsDetected()
    {
        SpawnR10Holders();
        RuleMutants.AssertDetects("SCHEMA-13", UnopenedHolderMarker, () => AssertUnopenedHolderRefused(skipGuard: true));
    }

    private const string UnopenedHolderMarker = "SCHEMA-13: a component must not migrate while a persisted holder is not opened";

    private (EntityId X, EntityId Y) SpawnR10Holders()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvR10SharedV1>();
        dbe.RegisterComponentFromAccessor<AsvR10Other>();
        dbe.InitializeArchetypes();

        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        var x = tx.Spawn<AsvR10XArch>(AsvR10XArch.Shared.Set(new AsvR10SharedV1(11)));
        var y = tx.Spawn<AsvR10YArch>(AsvR10YArch.Shared.Set(new AsvR10SharedV1(22)), AsvR10YArch.Other.Set(new AsvR10Other(0x0BADC0FFEEL)));
        Assert.That(tx.Commit(), Is.True);
        return (x, y);
    }

    /// <summary>The shared component migrates (+8 bytes) and Y is not opened: AsvR10Other is not registered.</summary>
    private void AssertUnopenedHolderRefused(bool skipGuard = false)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvR10SharedV2>();
        dbe.SkipUnopenedHolderGuardForTest = skipGuard;

        var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes(), UnopenedHolderMarker);
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.UnopenedHolder));
            Assert.That(ex.ArchetypeName, Is.EqualTo("AsvR10.Y"));
            Assert.That(ex.Message, Is.EqualTo(
                "Component 'Typhon.Schema.UnitTest.AsvR10Shared' changes layout in this open, but archetype 'AsvR10.Y' stores it and is not opened by "
                + "this process (its component AsvR10Other is not registered with this engine (RegisterComponentFromAccessor<AsvR10Other>())). Its "
                + "cluster would be left in the old layout under the new catalog. Register every component of 'AsvR10.Y' (or declare its archetype "
                + "class) and open again. Nothing was modified."));
        });
    }

    // ── D1: the ancestor whose set changed ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool IsAny(ArchetypeMetadata meta, params Type[] types) => Array.IndexOf(types, meta.ArchetypeType) >= 0;

    // A parent bumped with its set unchanged, and a child that swapped its own components at its own unchanged revision: the child's set changed, and no
    // ancestor whose set changed raised its revision. R1. Accepting any ancestor's bump migrated it.
    [Test]
    public void ParentRevisionOnlyBump_ChildReorder_IsRefused()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvD1RoP>();
            dbe.RegisterComponentFromAccessor<AsvD1RoA>();
            dbe.RegisterComponentFromAccessor<AsvD1RoB>();
            dbe.HideArchetypeForTest = meta => IsAny(meta, typeof(AsvD1RoParentV2), typeof(AsvD1RoChildV2));
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<AsvD1RoChildV1>(AsvD1RoParentV1.P.Set(new AsvD1RoP(1)), AsvD1RoChildV1.A.Set(new AsvD1RoA(2)), AsvD1RoChildV1.B.Set(new AsvD1RoB(3)));
            Assert.That(tx.Commit(), Is.True);
        }

        var before = ReadRow("Asv.D1RoChild");
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvD1RoP>();
            dbe.RegisterComponentFromAccessor<AsvD1RoA>();
            dbe.RegisterComponentFromAccessor<AsvD1RoB>();
            dbe.HideArchetypeForTest = meta => IsAny(meta, typeof(AsvD1RoParentV1), typeof(AsvD1RoChildV1));
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.RevisionNotBumped));
                Assert.That(ex.ArchetypeName, Is.EqualTo("Asv.D1RoChild"));
                Assert.That(ex.Message, Does.StartWith("Archetype 'Asv.D1RoChild' changed its component set (").And.Contain("but is still at revision 1."));
            });
        }

        AssertRowUnchanged(before, ReadRow("Asv.D1RoChild"));
    }

    // The case D1 exists for: the parent gains a component and raises its revision, the child (whose set changed through it) keeps its own revision.
    [Test]
    public void ParentSetChangeWithBump_ChildMigratesAtItsOwnRevision()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvD1GrP>();
            dbe.RegisterComponentFromAccessor<AsvD1GrA>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = tx.Spawn<AsvD1GrChildV1>(AsvD1GrParentV1.P.Set(new AsvD1GrP(4)), AsvD1GrChildV1.A.Set(new AsvD1GrA(5)));
            Assert.That(tx.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvD1GrP>();
            dbe.RegisterComponentFromAccessor<AsvD1GrQ>();
            dbe.RegisterComponentFromAccessor<AsvD1GrA>();
            dbe.HideArchetypeForTest = meta => IsAny(meta, typeof(AsvD1GrParentV1), typeof(AsvD1GrChildV1));
            dbe.InitializeArchetypes();

            var migrated = new Dictionary<string, (int From, int To)>();
            foreach (var report in dbe.LastOpenArchetypeSetMigrations)
            {
                migrated[report.ArchetypeName] = (report.FromRevision, report.ToRevision);
            }

            using var tx = dbe.CreateQuickTransaction();
            var e = tx.Open(id);
            var p = e.Read(AsvD1GrParentV2.P).P;
            var a = e.Read(AsvD1GrChildV2.A).A;
            Assert.Multiple(() =>
            {
                Assert.That(migrated["Asv.D1GrParent"], Is.EqualTo((1, 2)));
                Assert.That(migrated["Asv.D1GrChild"], Is.EqualTo((1, 1)), "the child migrates at its own revision, through its parent's bump");
                Assert.That((p, a), Is.EqualTo((4, 5L)));
            });
        }

        // The rewritten child row names this build's parent and catalog id, not the creating build's.
        var child = ReadRow("Asv.D1GrChild");
        Assert.Multiple(() =>
        {
            Assert.That(child.ParentArchetypeId, Is.EqualTo(Archetype<AsvD1GrParentV2>.Metadata.ArchetypeId));
            Assert.That(child.ArchetypeId, Is.EqualTo(Archetype<AsvD1GrChildV2>.Metadata.ArchetypeId));
        });
    }

    // The parent's set change commits in an open that does not consume the child (the child's own component unregistered): the child's row keeps the
    // parent's old part, the parent's row carries the bump but no longer exceeds it. The child, opened later at its unchanged revision, migrates — every
    // difference lies in its parent's part — instead of being refused forever with a message pointing at a revision nobody can raise.
    [Test]
    public void ParentSetChangeCommittedWhileChildUnopened_ChildMigratesLater()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvA3P>();
            dbe.RegisterComponentFromAccessor<AsvA3A>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = tx.Spawn<AsvA3ChildV1>(AsvA3ParentV1.P.Set(new AsvA3P(6)), AsvA3ChildV1.A.Set(new AsvA3A(7)));
            tx.Spawn<AsvA3ParentV1>(AsvA3ParentV1.P.Set(new AsvA3P(8)));
            Assert.That(tx.Commit(), Is.True);
        }

        // The parent migrates (+Q, revision 2); the child is not opened (A unregistered) and its row is left as it was.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvA3P>();
            dbe.RegisterComponentFromAccessor<AsvA3Q>();
            dbe.HideArchetypeForTest = meta => IsAny(meta, typeof(AsvA3ParentV1), typeof(AsvA3ChildV1));
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the parent alone migrated");
            Assert.That(dbe.LastOpenArchetypeSetMigrations[0].ArchetypeName, Is.EqualTo("Asv.A3Parent"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(ReadRow("Asv.A3Parent").Revision, Is.EqualTo(2), "premise: the parent's bump is committed");
            Assert.That(ReadNames("Asv.A3Child"), Is.EqualTo(new[] { "Typhon.Schema.UnitTest.AsvA3P", "Typhon.Schema.UnitTest.AsvA3A" }),
                "premise: the child's row keeps the parent's old part");
        });

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvA3P>();
            dbe.RegisterComponentFromAccessor<AsvA3Q>();
            dbe.RegisterComponentFromAccessor<AsvA3A>();
            dbe.HideArchetypeForTest = meta => IsAny(meta, typeof(AsvA3ParentV1), typeof(AsvA3ChildV1));
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1));
            var report = dbe.LastOpenArchetypeSetMigrations[0];
            using var tx = dbe.CreateQuickTransaction();
            var e = tx.Open(id);
            var p = e.Read(AsvA3ParentV2.P).P;
            var a = e.Read(AsvA3ChildV2.A).A;
            Assert.Multiple(() =>
            {
                Assert.That((report.ArchetypeName, report.FromRevision, report.ToRevision), Is.EqualTo(("Asv.A3Child", 1, 1)));
                Assert.That((p, a), Is.EqualTo((6, 7L)));
            });
        }
    }

    // ── R5 on a corrupt row ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    // A stored list of seventeen names: no archetype holds that many, and the slot map must never be built from it.
    [Test]
    public void StoredListLongerThanAnArchetypeCanHold_IsR5()
    {
        SpawnNamesArchetype();
        var names = new string[17];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = $"Typhon.Schema.UnitTest.AsvNbGhost{i}";
        }

        TamperNames("Asv.NamesMissing", names);
        AssertNamesUnreadable("belongs to a row of 17 components, more than an archetype can hold (16)");
    }

    [Test]
    public void StoredNamesBufferNotAllocated_IsR5()
    {
        SpawnNamesArchetype();
        TamperRow("Asv.NamesMissing", (ref ArchetypeR1 row) => row.ComponentNames._bufferId = 0x00FF_FFFF);
        AssertNamesUnreadable("names buffer 16777215, which is not an allocated buffer of the component-name segment");
    }

    private void SpawnNamesArchetype()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<AsvNbKeep>();
        dbe.InitializeArchetypes();
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        tx.Spawn<AsvNbArch>(AsvNbArch.Keep.Set(new AsvNbKeep(1)));
        Assert.That(tx.Commit(), Is.True);
    }

    /// <summary>The open refuses with R5 carrying <paramref name="reason"/>, and the preview reports the same refusal rather than throwing.</summary>
    private void AssertNamesUnreadable(string reason)
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvNbKeep>();
            var preview = dbe.PreviewArchetypeSetChanges();
            Assert.That(preview.Refusals, Has.One.Contains(reason), "the preview reports the refusal");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvNbKeep>();
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.NamesUnreadable));
                Assert.That(ex.Message, Does.StartWith("Archetype 'Asv.NamesMissing': its stored component list (ArchetypeR1.ComponentNames) " + reason));
            });
        }
    }

    // ── Refuse: an unresolved archetype an open would refuse is reported as such ─────────────────────────────────────────────────────────────────────

    // An inspector that does not register the component revision 2 adds skips it for that slot; from the names, revision 2 drops a stored component, so an
    // open that consumed it would refuse it (R4). It is reported as Refused, with that refusal, not as a set change the application's build would migrate.
    [Test]
    public void RefusePolicy_UnresolvedArchetypeAnOpenWouldRefuse_IsReportedAsRefused()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRmKeep>();
            dbe.RegisterComponentFromAccessor<AsvRmGone>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<AsvRmV1Arch>(AsvRmV1Arch.Keep.Set(new AsvRmKeep(41)), AsvRmV1Arch.Gone.Set(new AsvRmGone(0xDEADL)));
            Assert.That(tx.Commit(), Is.True);
        }

        SetPolicy(ArchetypeSetMigrationPolicy.Refuse);
        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe2.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(AsvRmV1Arch);
        dbe2.RegisterComponentFromAccessor<AsvRmKeep>();
        dbe2.RegisterComponentFromAccessor<AsvRmGone>();
        dbe2.InitializeArchetypes();
        Assert.That(dbe2.RefusedArchetypeSetChanges, Has.Count.EqualTo(1));
        var refusal = dbe2.RefusedArchetypeSetChanges[0];
        Assert.Multiple(() =>
        {
            Assert.That((refusal.ArchetypeName, refusal.FromRevision, refusal.ToRevision, refusal.Kind),
                Is.EqualTo(("Asv.Remove", 1, 2, ArchetypeSetChangeKind.Refused)));
            Assert.That(refusal.Changes, Does.StartWith(
                "Archetype 'Asv.Remove' revision 2 no longer declares 'Typhon.Schema.UnitTest.AsvRmGone', which revision 1 stored."));
        });
    }

    // ── Refuse: a refused archetype is skipped, the rest of the database opens (D4) ──────────────────────────────────────────────────────────────────

    // Revision 2 drops a stored component (R4). Under Apply that refuses the open; an inspector under Refuse opens the rest of the database, and the
    // archetype is skipped and reported with the refusal.
    [Test]
    public void RefusePolicy_ConsumedArchetypeRefused_IsSkippedAndReported_TheRestOpens()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRmKeep>();
            dbe.RegisterComponentFromAccessor<AsvRmGone>();
            dbe.RegisterComponentFromAccessor<AsvRvKeep>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<AsvRmV1Arch>(AsvRmV1Arch.Keep.Set(new AsvRmKeep(41)), AsvRmV1Arch.Gone.Set(new AsvRmGone(0xDEADL)));
            id = tx.Spawn<AsvRvV1Arch>(AsvRvV1Arch.Keep.Set(new AsvRvKeep(9)));
            Assert.That(tx.Commit(), Is.True);
        }

        var before = ReadRow("Asv.Remove");
        SetPolicy(ArchetypeSetMigrationPolicy.Refuse);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRmKeep>();
            dbe.RegisterComponentFromAccessor<AsvRmNew>();
            dbe.RegisterComponentFromAccessor<AsvRvKeep>();
            Assert.DoesNotThrow(() => dbe.InitializeArchetypes(), "an inspector's open is not refused for one archetype");
            Assert.That(dbe.RefusedArchetypeSetChanges, Has.Count.EqualTo(1));
            var refusal = dbe.RefusedArchetypeSetChanges[0];
            using var tx = dbe.CreateQuickTransaction();
            var steady = tx.Open(id).Read(AsvRvV1Arch.Keep).V;
            var use = Assert.Throws<InvalidOperationException>(() => tx.Spawn<AsvRmV2Arch>(AsvRmV2Arch.Keep.Set(new AsvRmKeep(1))));
            Assert.Multiple(() =>
            {
                Assert.That((refusal.ArchetypeName, refusal.Kind), Is.EqualTo(("Asv.Remove", ArchetypeSetChangeKind.Refused)));
                Assert.That(refusal.Changes, Does.StartWith("Archetype 'Asv.Remove' revision 2 no longer declares 'Typhon.Schema.UnitTest.AsvRmGone'"));
                Assert.That(steady, Is.EqualTo(9), "the rest of the database opens");
                Assert.That(use!.Message, Does.Contain("no longer declares"), "a use of the skipped archetype says why it is not open");
            });
        }

        AssertRowUnchanged(before, ReadRow("Asv.Remove"));

        // Apply keeps refusing.
        SetPolicy(ArchetypeSetMigrationPolicy.Apply);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRmKeep>();
            dbe.RegisterComponentFromAccessor<AsvRmNew>();
            dbe.RegisterComponentFromAccessor<AsvRvKeep>();
            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.ComponentRemoved));
        }
    }

    // Stale stored names (a component renamed by a build that did not refresh them) are a NamesRefreshed verdict: under Refuse the archetype is skipped and
    // reported as such, and its stored names stay as they are.
    [Test]
    public void RefusePolicy_NamesRefreshed_IsSkippedAndReported()
    {
        SpawnRenameV1(31);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRnNew>();
            dbe.SkipArchetypeNamesRefreshForTest = true;
            dbe.InitializeArchetypes();
        }

        Assert.That(ReadNames("Asv.Rename"), Is.EqualTo(new[] { "Typhon.Schema.UnitTest.AsvRnOld" }), "premise: the stored names are stale");

        SetPolicy(ArchetypeSetMigrationPolicy.Refuse);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvRnNew>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.RefusedArchetypeSetChanges, Has.Count.EqualTo(1));
            var refusal = dbe.RefusedArchetypeSetChanges[0];
            Assert.That((refusal.ArchetypeName, refusal.Kind, refusal.Changes), Is.EqualTo(("Asv.Rename", ArchetypeSetChangeKind.NamesRefreshed, "")));
        }

        Assert.That(ReadNames("Asv.Rename"), Is.EqualTo(new[] { "Typhon.Schema.UnitTest.AsvRnOld" }), "the Refuse open rewrote nothing");
    }

    // ── The preview is side-effect free ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    // A preview, then the open, on the SAME engine: the preview leaves no skip reason behind and does not log the superseded warning — the open logs it,
    // once.
    [Test]
    public void Preview_ThenOpenOnTheSameEngine_LeavesNoTrace()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvDupKeep>();
            dbe.RegisterComponentFromAccessor<AsvDupExtra>();
            dbe.InitializeArchetypes();
        }

        const string superseded = "Archetype 'Asv.Dup': 'AsvDupLowArch' (revision 1) is superseded by 'AsvDupHighArch' (revision 2)";
        var logged = _warnings.Matching(superseded);
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvDupKeep>();
            dbe.RegisterComponentFromAccessor<AsvDupExtra>();
            var lowId = Archetype<AsvDupLowArch>.Metadata.ArchetypeId;
            var preview = dbe.PreviewArchetypeSetChanges();
            Assert.Multiple(() =>
            {
                Assert.That(preview.Refusals, Is.Empty);
                Assert.That(dbe.SkippedArchetypeReason(lowId), Is.Null, "the preview leaves no skip reason");
                Assert.That(_warnings.Matching(superseded), Is.EqualTo(logged), "and logs nothing");
            });

            dbe.InitializeArchetypes();
            Assert.Multiple(() =>
            {
                Assert.That(_warnings.Matching(superseded), Is.EqualTo(logged + 1), "the open logs the superseded class once");
                Assert.That(dbe.SkippedArchetypeReason(lowId), Does.Contain("superseded"));
            });
        }
    }

    // ── The preview agrees with the open on every refusal it can decide ─────────────────────────────────────────────────────────────────────────────

    // R10 (a component migrating under an unopened holder) and R1 (a same-count swap): the open throws them, so the preview must list them, word for word.
    [Test]
    public void Preview_ReportsTheRefusalsTheOpenThrows()
    {
        SpawnR10Holders();
        SpawnSwapV1();

        void Register(DatabaseEngine dbe)
        {
            dbe.RegisterComponentFromAccessor<AsvR10SharedV2>();
            dbe.RegisterComponentFromAccessor<AsvSwapA>();
            dbe.RegisterComponentFromAccessor<AsvSwapC>();
        }

        IReadOnlyList<string> previewed;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            previewed = dbe.PreviewArchetypeSetChanges().Refusals;
        }

        // The swap refuses first in the open (classification precedes the R10 guard); with it fixed, R10 is next.
        string swap;
        string holder;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            swap = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes())!.Message;
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvR10SharedV2>();
            holder = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes())!.Message;
        }

        Assert.Multiple(() =>
        {
            Assert.That(swap, Does.StartWith("Archetype 'Asv.Swap' changed its component set"), "premise: the open refuses the swap");
            Assert.That(holder, Does.StartWith("Component 'Typhon.Schema.UnitTest.AsvR10Shared' changes layout"), "premise: and then R10");
            Assert.That(previewed, Does.Contain(swap));
            Assert.That(previewed, Does.Contain(holder), "R10 is previewed, not only thrown");
        });
    }

    // ── Apply: every refusal precedes the first allocation ──────────────────────────────────────────────────────────────────────────────────────────

    // A new archetype the build loop reaches first, and a refused one after it. Classified in the loop, the refusal came after the new archetype had its
    // EntityMap allocated (orphaned until the failed-open sweep); classified before it, no archetype is built at all.
    [Test]
    public void ApplyPolicy_RefusalPrecedesEveryAllocation()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<AsvM6Keep>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<AsvM6BadV1>(AsvM6BadV1.Keep.Set(new AsvM6Keep(1)));
            Assert.That(tx.Commit(), Is.True);
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe2.RegisterComponentFromAccessor<AsvM6Good>();
        dbe2.RegisterComponentFromAccessor<AsvM6Keep>();
        dbe2.RegisterComponentFromAccessor<AsvM6Added>();
        dbe2.HideArchetypeForTest = meta => meta.ArchetypeType == typeof(AsvM6BadV1);
        var own = dbe2.OwnArchetypes();
        Assert.That(Array.IndexOf(own, Archetype<AsvM6AGoodArch>.Metadata), Is.LessThan(Array.IndexOf(own, Archetype<AsvM6BadV2>.Metadata)),
            "premise: the build loop reaches the new archetype before the refused one");

        var built = new List<ushort>();
        dbe2.ArchetypeClusterInitProbe = built.Add;
        var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe2.InitializeArchetypes());
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.RevisionNotBumped));
            Assert.That(built, Is.Empty, "no archetype got storage before the refusal");
        });
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rewrites one persisted row's stored names (and count), in a session that never calls InitializeArchetypes.</summary>
    private void TamperNames(string name, string[] names)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        var table = ArchetypeTable(dbe);
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var cs = dbe.MMF.CreateChangeSet();
        var found = false;
        var segment = table.ComponentSegment;
        for (var chunkId = 1; chunkId < segment.ChunkCapacity && !found; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId) || !SystemCrud.Read(table, chunkId, out ArchetypeR1 row, dbe.EpochManager) || row.Name.AsString != name)
            {
                continue;
            }

            row.ComponentNames = default;
            using (var cca = new ComponentCollectionAccessor<String64>(cs, dbe.GetComponentCollectionVSBS<String64>(), ref row.ComponentNames))
            {
                foreach (var n in names)
                {
                    cca.Add((String64)n);
                }
            }

            row.ComponentCount = (byte)names.Length;
            SystemCrud.Update(table, chunkId, ref row, dbe.EpochManager, cs);
            found = true;
        }

        cs.SaveChanges();
        Assert.That(found, Is.True, $"premise: a row named '{name}' is persisted");
    }

    private delegate void RowEdit(ref ArchetypeR1 row);

    /// <summary>Rewrites one persisted archetype row in place, in a session that never calls InitializeArchetypes (SchemaVersioningTests' technique).</summary>
    private void TamperRow(string name, RowEdit edit)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        var table = ArchetypeTable(dbe);
        var cs = dbe.MMF.CreateChangeSet();
        var found = false;
        var segment = table.ComponentSegment;
        for (var chunkId = 1; chunkId < segment.ChunkCapacity && !found; chunkId++)
        {
            if (segment.IsChunkAllocated(chunkId) && SystemCrud.Read(table, chunkId, out ArchetypeR1 row, dbe.EpochManager) && row.Name.AsString == name)
            {
                edit(ref row);
                SystemCrud.Update(table, chunkId, ref row, dbe.EpochManager, cs);
                found = true;
            }
        }
        cs.SaveChanges();
        Assert.That(found, Is.True, $"premise: a row named '{name}' is persisted");
    }

    private ArchetypeR1 ReadRow(string name)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        return FindRow(dbe, name);
    }

    private string[] ReadNames(string name)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        var row = FindRow(dbe, name);
        return dbe.ReadPersistedComponentNames(row, name);
    }

    private static ArchetypeR1 FindRow(DatabaseEngine dbe, string name)
    {
        var table = ArchetypeTable(dbe);
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var segment = table.ComponentSegment;
        for (var chunkId = 1; chunkId < segment.ChunkCapacity; chunkId++)
        {
            if (segment.IsChunkAllocated(chunkId) && SystemCrud.Read(table, chunkId, out ArchetypeR1 row, dbe.EpochManager) && row.Name.AsString == name)
            {
                return row;
            }
        }
        Assert.Fail($"premise: a row named '{name}' is persisted");
        return default;
    }

    private static ComponentTable ArchetypeTable(DatabaseEngine dbe)
    {
        if (dbe.GetComponentTable<ArchetypeR1>() == null)
        {
            dbe.RegisterComponentFromAccessor<ArchetypeR1>();
        }
        return dbe.GetComponentTable<ArchetypeR1>();
    }

    /// <summary>"Nothing of this archetype was modified": the row's identity, set, revision and every segment root.</summary>
    private static void AssertRowUnchanged(ArchetypeR1 before, ArchetypeR1 after) =>
        Assert.Multiple(() =>
        {
            Assert.That(after.RoutingId, Is.EqualTo(before.RoutingId), "RoutingId");
            Assert.That(after.ComponentCount, Is.EqualTo(before.ComponentCount), "ComponentCount");
            Assert.That(after.Revision, Is.EqualTo(before.Revision), "Revision");
            Assert.That(after.ComponentNames._bufferId, Is.EqualTo(before.ComponentNames._bufferId), "ComponentNames buffer");
            Assert.That(after.EntityMapSPI, Is.EqualTo(before.EntityMapSPI), "EntityMapSPI");
            Assert.That(after.ClusterSegmentSPI, Is.EqualTo(before.ClusterSegmentSPI), "ClusterSegmentSPI");
            Assert.That(after.ClusterIndexSPI, Is.EqualTo(before.ClusterIndexSPI), "ClusterIndexSPI");
            Assert.That(after.NextEntityKey, Is.EqualTo(before.NextEntityKey), "NextEntityKey");
        });
}
