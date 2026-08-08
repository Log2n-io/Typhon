using System;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The component kit. Nine structs: three storage modes × three index kinds.
//
// Every struct in a mode has the SAME field set in the SAME order, differing ONLY in which field carries an [Index] attribute. That is deliberate: a fixture
// asserting "the value round-trips" must assert the identical thing in all three, so a difference in outcome is attributable to the index kind and to nothing
// else. Differing layouts would confound the axis with the payload.
//
//   Key     — distinctive per entity (i*7+1). The identity the assertions check, and the UNIQUE index key.
//   Bucket  — deliberately duplicated (i % 4). The AllowMultiple index key; a multi-index over distinct values would never exercise the duplicate path.
//   Weight  — float, distinctive (i*1.5f). Catches a field-offset error that an int-only payload would read as plausible.
//   Tag     — long, distinctive (i*1000+3). Catches a truncation or a 4-vs-8 byte stride mistake.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region SingleVersion

[Component("Typhon.Test.Axis.SvCore", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AxSvCore
{
    public int Key;
    public int Bucket;
    public float Weight;
    public long Tag;
}

[Component("Typhon.Test.Axis.SvUniq", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AxSvUniq
{
    [Index]
    public int Key;

    public int Bucket;
    public float Weight;
    public long Tag;
}

[Component("Typhon.Test.Axis.SvMulti", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AxSvMulti
{
    public int Key;

    [Index(AllowMultiple = true)]
    public int Bucket;

    public float Weight;
    public long Tag;
}

#endregion

#region Versioned

[Component("Typhon.Test.Axis.VerCore", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AxVerCore
{
    public int Key;
    public int Bucket;
    public float Weight;
    public long Tag;
}

[Component("Typhon.Test.Axis.VerUniq", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AxVerUniq
{
    [Index]
    public int Key;

    public int Bucket;
    public float Weight;
    public long Tag;
}

[Component("Typhon.Test.Axis.VerMulti", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AxVerMulti
{
    public int Key;

    [Index(AllowMultiple = true)]
    public int Bucket;

    public float Weight;
    public long Tag;
}

#endregion

#region Transient

[Component("Typhon.Test.Axis.TrCore", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct AxTrCore
{
    public int Key;
    public int Bucket;
    public float Weight;
    public long Tag;
}

[Component("Typhon.Test.Axis.TrUniq", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct AxTrUniq
{
    [Index]
    public int Key;

    public int Bucket;
    public float Weight;
    public long Tag;
}

[Component("Typhon.Test.Axis.TrMulti", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct AxTrMulti
{
    public int Key;

    [Index(AllowMultiple = true)]
    public int Bucket;

    public float Weight;
    public long Tag;
}

#endregion

#region Collection carriers

// The collection axis (#704). One carrier per persistable storage mode — Transient is excluded because a Transient component declaring a
// ComponentCollection field is rejected at registration (DatabaseEngine.cs:2411-2422): its buffers would live in a persistent VSBS while the component is
// heap-volatile, orphaning them on restart. That is why EngineAxes.IsValid refuses PureTransient + Collection.

[Component("Typhon.Test.Axis.SvColl", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AxSvColl
{
    [Field]
    public int Key;

    [Field]
    public ComponentCollection<int> Items;
}

[Component("Typhon.Test.Axis.VerColl", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AxVerColl
{
    [Field]
    public int Key;

    [Field]
    public ComponentCollection<int> Items;
}

#endregion

#region Spatial carriers

// The spatial axis (#704). One carrier per persistable storage mode — Transient is excluded because [SpatialIndex] on a Transient component is rejected at
// registration (DatabaseDefinitions.cs:357-360), which is why EngineAxes.IsValid refuses PureTransient + Spatial.
//
// Margin 0, deliberately. A non-zero [SpatialIndex] margin expands the indexed box, so a box query can legitimately return an entity whose true bounds lie
// outside the query region — which would make the closed-form model in SpatialMatrixTests approximate rather than exact. The margin's own behaviour is
// ClusterSpatialTests' subject; here it would only blur the axis under test.

[Component("Typhon.Test.Axis.SvSpatial", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AxSvSpatial
{
    [Field]
    [SpatialIndex(0.0f)]
    public AABB3F Bounds;

    [Field]
    public int Key;
}

[Component("Typhon.Test.Axis.VerSpatial", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AxVerSpatial
{
    [Field]
    [SpatialIndex(0.0f)]
    public AABB3F Bounds;

    [Field]
    public int Key;
}

#endregion

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The archetype kit: 6 storage shapes × 3 index kinds = 18 compositions.
//
// One CLR class per composition is not a style choice. `Archetype<TSelf>` keys its metadata on the concrete type and `StorageMode` is fixed by the [Component]
// attribute, so a cell genuinely needs a distinct type — which is exactly WHY no fixture in the suite was parameterised over storage mode before #704. This
// block is the scaffolding whose absence made the axis unreachable, written once here instead of privately in each of ~45 fixtures.
//
// In a mixed shape the PRIMARY (first-listed) component is the one that carries the index; the secondary is always a plain core. That convention keeps
// "which component is under test" answerable without the fixture having to ask.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region PureSv

[Archetype]
class AxPureSvNone : Archetype<AxPureSvNone>
{
    public static readonly Comp<AxSvCore> P = Register<AxSvCore>();
}

[Archetype]
class AxPureSvUniq : Archetype<AxPureSvUniq>
{
    public static readonly Comp<AxSvUniq> P = Register<AxSvUniq>();
}

[Archetype]
class AxPureSvMulti : Archetype<AxPureSvMulti>
{
    public static readonly Comp<AxSvMulti> P = Register<AxSvMulti>();
}

#endregion

#region PureVersioned

[Archetype]
class AxPureVerNone : Archetype<AxPureVerNone>
{
    public static readonly Comp<AxVerCore> P = Register<AxVerCore>();
}

[Archetype]
class AxPureVerUniq : Archetype<AxPureVerUniq>
{
    public static readonly Comp<AxVerUniq> P = Register<AxVerUniq>();
}

[Archetype]
class AxPureVerMulti : Archetype<AxPureVerMulti>
{
    public static readonly Comp<AxVerMulti> P = Register<AxVerMulti>();
}

#endregion

#region PureTransient

[Archetype]
class AxPureTrNone : Archetype<AxPureTrNone>
{
    public static readonly Comp<AxTrCore> P = Register<AxTrCore>();
}

[Archetype]
class AxPureTrUniq : Archetype<AxPureTrUniq>
{
    public static readonly Comp<AxTrUniq> P = Register<AxTrUniq>();
}

[Archetype]
class AxPureTrMulti : Archetype<AxPureTrMulti>
{
    public static readonly Comp<AxTrMulti> P = Register<AxTrMulti>();
}

#endregion

#region SvPlusVersioned

[Archetype]
class AxSvVerNone : Archetype<AxSvVerNone>
{
    public static readonly Comp<AxSvCore> P = Register<AxSvCore>();
    public static readonly Comp<AxVerCore> S = Register<AxVerCore>();
}

[Archetype]
class AxSvVerUniq : Archetype<AxSvVerUniq>
{
    public static readonly Comp<AxSvUniq> P = Register<AxSvUniq>();
    public static readonly Comp<AxVerCore> S = Register<AxVerCore>();
}

[Archetype]
class AxSvVerMulti : Archetype<AxSvVerMulti>
{
    public static readonly Comp<AxSvMulti> P = Register<AxSvMulti>();
    public static readonly Comp<AxVerCore> S = Register<AxVerCore>();
}

#endregion

#region SvPlusTransient

[Archetype]
class AxSvTrNone : Archetype<AxSvTrNone>
{
    public static readonly Comp<AxSvCore> P = Register<AxSvCore>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
}

[Archetype]
class AxSvTrUniq : Archetype<AxSvTrUniq>
{
    public static readonly Comp<AxSvUniq> P = Register<AxSvUniq>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
}

[Archetype]
class AxSvTrMulti : Archetype<AxSvTrMulti>
{
    public static readonly Comp<AxSvMulti> P = Register<AxSvMulti>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
}

#endregion

#region VerPlusTransient

[Archetype]
class AxVerTrNone : Archetype<AxVerTrNone>
{
    public static readonly Comp<AxVerCore> P = Register<AxVerCore>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
}

[Archetype]
class AxVerTrUniq : Archetype<AxVerTrUniq>
{
    public static readonly Comp<AxVerUniq> P = Register<AxVerUniq>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
}

[Archetype]
class AxVerTrMulti : Archetype<AxVerTrMulti>
{
    public static readonly Comp<AxVerMulti> P = Register<AxVerMulti>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
}

#endregion

#region Collection compositions

// Five shapes, index-free — the same narrowing as the spatial compositions and for the same reason: no collection defect on record implicates the index
// flavour, and crossing both would triple the archetype count for a dimension nothing points at.

[Archetype]
class AxCoPureSv : Archetype<AxCoPureSv>
{
    public static readonly Comp<AxSvCore> P = Register<AxSvCore>();
    public static readonly Comp<AxSvColl> Co = Register<AxSvColl>();
}

[Archetype]
class AxCoPureVer : Archetype<AxCoPureVer>
{
    public static readonly Comp<AxVerCore> P = Register<AxVerCore>();
    public static readonly Comp<AxVerColl> Co = Register<AxVerColl>();
}

[Archetype]
class AxCoSvVer : Archetype<AxCoSvVer>
{
    public static readonly Comp<AxSvCore> P = Register<AxSvCore>();
    public static readonly Comp<AxVerCore> S = Register<AxVerCore>();
    public static readonly Comp<AxSvColl> Co = Register<AxSvColl>();
}

[Archetype]
class AxCoSvTr : Archetype<AxCoSvTr>
{
    public static readonly Comp<AxSvCore> P = Register<AxSvCore>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
    public static readonly Comp<AxSvColl> Co = Register<AxSvColl>();
}

[Archetype]
class AxCoVerTr : Archetype<AxCoVerTr>
{
    public static readonly Comp<AxVerCore> P = Register<AxVerCore>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
    public static readonly Comp<AxVerColl> Co = Register<AxVerColl>();
}

#endregion

#region Spatial compositions

// Five shapes, index-free. The spatial axis is crossed with the STORAGE SHAPE, not with the index kind: #548's shape was a Versioned [SpatialIndex] update
// double-inserting, i.e. a storage-mode interaction, and adding the index axis here would triple the archetype count for a dimension no spatial bug has
// implicated. AxisArchetypes.Supports states that narrowing rather than leaving it implicit.

[Archetype]
class AxSpPureSv : Archetype<AxSpPureSv>
{
    public static readonly Comp<AxSvCore> P = Register<AxSvCore>();
    public static readonly Comp<AxSvSpatial> Sp = Register<AxSvSpatial>();
}

[Archetype]
class AxSpPureVer : Archetype<AxSpPureVer>
{
    public static readonly Comp<AxVerCore> P = Register<AxVerCore>();
    public static readonly Comp<AxVerSpatial> Sp = Register<AxVerSpatial>();
}

[Archetype]
class AxSpSvVer : Archetype<AxSpSvVer>
{
    public static readonly Comp<AxSvCore> P = Register<AxSvCore>();
    public static readonly Comp<AxVerCore> S = Register<AxVerCore>();
    public static readonly Comp<AxSvSpatial> Sp = Register<AxSvSpatial>();
}

[Archetype]
class AxSpSvTr : Archetype<AxSpSvTr>
{
    public static readonly Comp<AxSvCore> P = Register<AxSvCore>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
    public static readonly Comp<AxSvSpatial> Sp = Register<AxSvSpatial>();
}

[Archetype]
class AxSpVerTr : Archetype<AxSpVerTr>
{
    public static readonly Comp<AxVerCore> P = Register<AxVerCore>();
    public static readonly Comp<AxTrCore> S = Register<AxTrCore>();
    public static readonly Comp<AxVerSpatial> Sp = Register<AxVerSpatial>();
}

#endregion

/// <summary>
/// The one-shot query terminals, as an axis.
/// </summary>
/// <remarks>
/// This is a FIXTURE-LOCAL axis, crossed in via <see cref="EngineAxes.PairwiseWith{T}"/> rather than added to <see cref="Cell"/>: a query terminal means
/// nothing to a schema-migration fixture, and putting it in the shared cell would inflate every other fixture's candidate set. It still has to be covered —
/// #590/#592 were exactly a terminal × predicate-shape interaction, three cells of a 2×2 tested and the bug in the fourth.
/// </remarks>
public enum QueryTerminal
{
    /// <summary>Materialises the matching set.</summary>
    Execute,

    /// <summary>Counts without materialising.</summary>
    Count,

    /// <summary>Short-circuits on the first match — collapses to 0/1, so it is checked against "any at all" rather than the exact count.</summary>
    Any,

    /// <summary>The incremental view path, which #590/#592 left working while every one-shot terminal was wrong.</summary>
    ToView,
}

/// <summary>
/// Recovers a cell's static archetype type. See <see cref="AxisArchetypes.Dispatch{TArg,TResult}"/>.
/// </summary>
/// <remarks>
/// The visit takes its input as an ARGUMENT rather than as visitor state on purpose: the common argument is a <c>Transaction</c>, and a field of disposable
/// type in a non-disposable class is exactly what the TYPHON005 analyzer rejects — an owned-lifetime mistake waiting to become an uncommitted change.
/// </remarks>
/// <typeparam name="TArg">What the visit needs — usually the transaction to run against.</typeparam>
/// <typeparam name="TResult">What the visit produces.</typeparam>
public interface ICellVisitor<in TArg, out TResult>
{
    /// <summary>Called with the archetype type the cell resolves to.</summary>
    TResult Visit<TArch>(TArg arg) where TArch : Archetype<TArch>;
}

/// <summary>
/// The shape kit behind <see cref="EngineAxes"/>: turns a <see cref="Cell"/> into a registered, spawnable, assertable archetype.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Before #704 the suite had 131 components declaring a non-default <c>StorageMode</c> — not zero, but 131 PRIVATE ones, each written
/// by one fixture and used on one axis value. Nothing was parameterised over storage mode because a storage-mode cell needs a distinct CLR type and nobody had
/// written the types. This class is that scaffolding, written once.
/// </para>
/// <para>
/// <b>An unsupported cell throws; it never skips.</b> The kit deliberately does not cover the whole valid matrix yet (see <see cref="Supports"/>), and a
/// fixture must narrow to what the kit has. What it must NOT do is receive a cell and quietly return — a skipped cell counts as coverage in the test count
/// and is not, which is the illusion this whole epic exists to remove. <c>AxisArchetypesTests</c> reports and ratchets the kit's coverage of the valid matrix,
/// so the gaps are a number that can only shrink rather than a silence.
/// </para>
/// </remarks>
public static class AxisArchetypes
{
    /// <summary>The distinctive per-entity payload. Every field differs for every <paramref name="i"/>, and differs from every other field.</summary>
    private static (int Key, int Bucket, float Weight, long Tag) Payload(int i) => (i * 7 + 1, i % 4, i * 1.5f, i * 1000L + 3);

    /// <summary>How many distinct values the AllowMultiple index key takes, i.e. how many entities share a bucket in a run of N.</summary>
    public const int BucketCount = 4;

    /// <summary>
    /// Whether the kit can build this cell. A fixture narrows with <c>EngineAxes.PairwiseWhere(AxisArchetypes.Supports)</c> — combined with its own
    /// restrictions — rather than discovering the gap at run time.
    /// </summary>
    /// <remarks>
    /// The collection and spatial axes are not built yet: both need extra carrier components and, for spatial, a configured grid, and no fixture converted so
    /// far varies them. They are gaps in the KIT, counted by <c>AxisArchetypesTests</c>, not claims about the engine — <see cref="EngineAxes.IsValid"/> is
    /// where genuine impossibility lives, and it accepts both on any non-pure-Transient shape.
    /// </remarks>
    public static bool Supports(in Cell c) =>
        EngineAxes.IsValid(c)
        && (c.Collection == CollectionShape.None || SupportsCollection(c))
        && (c.Spatial == SpatialShape.None || SupportsSpatial(c));

    /// <summary>
    /// The BASE compositions: storage shape × index kind, with neither payload carrier. What a fixture should narrow to unless it is specifically about a
    /// payload axis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because <see cref="Supports"/> being the only predicate broke downstream fixtures TWICE. Adding the spatial carrier silently handed spatial
    /// cells to <c>ClusterStorageMatrixTests</c>, whose <see cref="Update"/> had no branch for them; adding the collection carrier did it again, this time
    /// hitting a deliberate refusal. Both times the cause was a fixture inheriting a composition it never asked for, because its narrowing said "whatever the
    /// kit supports" and the kit grew.
    /// </para>
    /// <para>
    /// The lesson generalises past this kit: <b>a predicate that means "everything currently possible" is a dependency on a moving target.</b> A fixture
    /// should name the region it is about. The payload axes have dedicated fixtures — <c>SpatialMatrixTests</c>, <c>ComponentCollectionMatrixTests</c> — and
    /// everything else narrows here.
    /// </para>
    /// </remarks>
    public static bool SupportsBase(in Cell c) =>
        EngineAxes.IsValid(c)
        && c.Collection == CollectionShape.None
        && c.Spatial == SpatialShape.None;

    /// <summary>
    /// Whether the kit has a COLLECTION composition for this cell. Crossed with the storage shape only, at <c>Index=None</c> and without spatial.
    /// </summary>
    public static bool SupportsCollection(in Cell c) =>
        c.Collection == CollectionShape.Present
        && c.Spatial == SpatialShape.None
        && c.Index == IndexShape.None
        && c.Shape != StorageShape.PureTransient;

    /// <summary>
    /// Whether the kit has a SPATIAL composition for this cell. Crossed with the storage shape only, not with the index kind.
    /// </summary>
    /// <remarks>
    /// #548's shape was a Versioned <c>[SpatialIndex]</c> update double-inserting — a storage-mode interaction. No spatial defect on record implicates the
    /// index flavour, and crossing both would triple the archetype count for a dimension nothing points at. Stated here rather than left implicit, because a
    /// narrowing nobody can see is indistinguishable from a gap nobody noticed.
    /// </remarks>
    public static bool SupportsSpatial(in Cell c) =>
        c.Spatial == SpatialShape.Present
        && c.Index == IndexShape.None
        && c.Collection == CollectionShape.None
        && c.Shape != StorageShape.PureTransient;

    /// <summary>
    /// Whether this cell's SingleVersion component VALUES are expected to survive a HARD CRASH. They are not, unless the writing transaction ran under
    /// <see cref="DurabilityDiscipline.Commit"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the storage contract, not a workaround. A SingleVersion component is durable at the tick fence (≤1 tick of loss); its per-commit WAL record is
    /// what <see cref="DurabilityDiscipline.Commit"/> buys, and <c>WalCrashSweepTests</c> states the same thing at its <c>:334-336</c>. Entity LIFECYCLE
    /// records are durable either way, so after a crash the entities are all still there — with zeroed values.
    /// </para>
    /// <para>
    /// A converted fixture must consult this before asserting a post-crash value, or it will assert the opposite of the contract and read as a red. That
    /// happened while building this kit: 12 of 18 apparent failures were the contract working, and only separating them exposed the 6 that were not (#710).
    /// </para>
    /// </remarks>
    public static bool SvValuesAreCrashDurable(in Cell c) => !c.HasSingleVersion || c.Discipline == DurabilityDiscipline.Commit;

    /// <summary>How many elements the kit puts in entity <paramref name="i"/>'s collection — 1..4, so the count itself varies per entity.</summary>
    /// <remarks>
    /// A constant length would let a bug that writes every entity the same buffer pass unnoticed; varying it means a shared or mis-keyed buffer shows up as
    /// the wrong ELEMENT COUNT, not merely the wrong values.
    /// </remarks>
    public static int ElementCountOf(int i) => (i % 4) + 1;

    /// <summary>The value of element <paramref name="e"/> of entity <paramref name="i"/> — distinctive across both indices.</summary>
    public static int ElementValue(int i, int e) => i * 1000 + e;

    /// <summary>
    /// Entity <paramref name="i"/> sits at (i·Spacing, i·Spacing, i·Spacing) as a point box — so "how many are inside this region" is arithmetic.
    /// </summary>
    public const float Spacing = 10f;

    /// <summary>The world half-extent the kit's grid is configured over. Entities must stay well inside it.</summary>
    private const float WorldExtent = 10_000f;

    private static AABB3F BoundsOf(int i)
    {
        var v = i * Spacing;
        return new AABB3F { MinX = v, MinY = v, MinZ = v, MaxX = v, MaxY = v, MaxZ = v };
    }

    /// <summary>
    /// How many of the first <paramref name="count"/> entities lie inside the box [0, <paramref name="maxCoord"/>] — the model a spatial query is checked
    /// against.
    /// </summary>
    /// <remarks>
    /// Point boxes on a fixed lattice, and a zero <c>[SpatialIndex]</c> margin, are what make this exact rather than approximate. Entity <c>i</c> is inside iff
    /// <c>i * Spacing &lt;= maxCoord</c>.
    /// </remarks>
    public static int ExpectedInBox(int count, float maxCoord)
    {
        var n = (int)(maxCoord / Spacing) + 1;
        return n < 0 ? 0 : n > count ? count : n;
    }

    /// <summary>Registers the components this cell needs. Call before <c>InitializeArchetypes()</c>.</summary>
    public static void Register(DatabaseEngine dbe, in Cell c)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        RequireSupported(c);

        if (c.Spatial == SpatialShape.Present)
        {
            RegisterSpatial(dbe, c);
            return;
        }

        if (c.Collection == CollectionShape.Present)
        {
            RegisterCollection(dbe, c);
            return;
        }

        switch (c.Shape)
        {
            case StorageShape.PureSv:
                RegisterPrimarySv(dbe, c.Index);
                break;
            case StorageShape.PureVersioned:
                RegisterPrimaryVer(dbe, c.Index);
                break;
            case StorageShape.PureTransient:
                RegisterPrimaryTr(dbe, c.Index);
                break;
            case StorageShape.SvPlusVersioned:
                RegisterPrimarySv(dbe, c.Index);
                dbe.RegisterComponentFromAccessor<AxVerCore>();
                break;
            case StorageShape.SvPlusTransient:
                RegisterPrimarySv(dbe, c.Index);
                dbe.RegisterComponentFromAccessor<AxTrCore>();
                break;
            case StorageShape.VerPlusTransient:
                RegisterPrimaryVer(dbe, c.Index);
                dbe.RegisterComponentFromAccessor<AxTrCore>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(c), c.Shape, "unhandled storage shape");
        }
    }

    /// <summary>
    /// Registers a spatial composition and configures the engine-wide grid. The grid must be configured BEFORE <c>InitializeArchetypes()</c> — a spatial
    /// archetype opened without one leaves its cluster/entity-map segments unattributed, so a fixture that forgot would be testing a different thing.
    /// </summary>
    private static void RegisterCollection(DatabaseEngine dbe, in Cell c)
    {
        switch (c.Shape)
        {
            case StorageShape.PureSv:
                dbe.RegisterComponentFromAccessor<AxSvCore>();
                dbe.RegisterComponentFromAccessor<AxSvColl>();
                break;
            case StorageShape.PureVersioned:
                dbe.RegisterComponentFromAccessor<AxVerCore>();
                dbe.RegisterComponentFromAccessor<AxVerColl>();
                break;
            case StorageShape.SvPlusVersioned:
                dbe.RegisterComponentFromAccessor<AxSvCore>();
                dbe.RegisterComponentFromAccessor<AxVerCore>();
                dbe.RegisterComponentFromAccessor<AxSvColl>();
                break;
            case StorageShape.SvPlusTransient:
                dbe.RegisterComponentFromAccessor<AxSvCore>();
                dbe.RegisterComponentFromAccessor<AxTrCore>();
                dbe.RegisterComponentFromAccessor<AxSvColl>();
                break;
            default:
                dbe.RegisterComponentFromAccessor<AxVerCore>();
                dbe.RegisterComponentFromAccessor<AxTrCore>();
                dbe.RegisterComponentFromAccessor<AxVerColl>();
                break;
        }
    }

    private static void RegisterSpatial(DatabaseEngine dbe, in Cell c)
    {
        switch (c.Shape)
        {
            case StorageShape.PureSv:
                dbe.RegisterComponentFromAccessor<AxSvCore>();
                dbe.RegisterComponentFromAccessor<AxSvSpatial>();
                break;
            case StorageShape.PureVersioned:
                dbe.RegisterComponentFromAccessor<AxVerCore>();
                dbe.RegisterComponentFromAccessor<AxVerSpatial>();
                break;
            case StorageShape.SvPlusVersioned:
                dbe.RegisterComponentFromAccessor<AxSvCore>();
                dbe.RegisterComponentFromAccessor<AxVerCore>();
                dbe.RegisterComponentFromAccessor<AxSvSpatial>();
                break;
            case StorageShape.SvPlusTransient:
                dbe.RegisterComponentFromAccessor<AxSvCore>();
                dbe.RegisterComponentFromAccessor<AxTrCore>();
                dbe.RegisterComponentFromAccessor<AxSvSpatial>();
                break;
            default:
                dbe.RegisterComponentFromAccessor<AxVerCore>();
                dbe.RegisterComponentFromAccessor<AxTrCore>();
                dbe.RegisterComponentFromAccessor<AxVerSpatial>();
                break;
        }

        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(-WorldExtent, -WorldExtent),
            worldMax: new Vector2(WorldExtent, WorldExtent),
            cellSize: 100f));
    }

    private static void RegisterPrimarySv(DatabaseEngine dbe, IndexShape index)
    {
        switch (index)
        {
            case IndexShape.None:
                dbe.RegisterComponentFromAccessor<AxSvCore>();
                break;
            case IndexShape.Unique:
                dbe.RegisterComponentFromAccessor<AxSvUniq>();
                break;
            default:
                dbe.RegisterComponentFromAccessor<AxSvMulti>();
                break;
        }
    }

    private static void RegisterPrimaryVer(DatabaseEngine dbe, IndexShape index)
    {
        switch (index)
        {
            case IndexShape.None:
                dbe.RegisterComponentFromAccessor<AxVerCore>();
                break;
            case IndexShape.Unique:
                dbe.RegisterComponentFromAccessor<AxVerUniq>();
                break;
            default:
                dbe.RegisterComponentFromAccessor<AxVerMulti>();
                break;
        }
    }

    private static void RegisterPrimaryTr(DatabaseEngine dbe, IndexShape index)
    {
        switch (index)
        {
            case IndexShape.None:
                dbe.RegisterComponentFromAccessor<AxTrCore>();
                break;
            case IndexShape.Unique:
                dbe.RegisterComponentFromAccessor<AxTrUniq>();
                break;
            default:
                dbe.RegisterComponentFromAccessor<AxTrMulti>();
                break;
        }
    }

    /// <summary>Spawns entity <paramref name="i"/> of this cell's archetype, with the distinctive payload the assertions expect.</summary>
    public static EntityId Spawn(Transaction t, in Cell c, int i)
    {
        ArgumentNullException.ThrowIfNull(t);
        RequireSupported(c);
        var (key, bucket, weight, tag) = Payload(i);

        if (c.Spatial == SpatialShape.Present)
        {
            return SpawnSpatial(t, c, i);
        }

        if (c.Collection == CollectionShape.Present)
        {
            return SpawnCollection(t, c, i);
        }

        var sv = new AxSvCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var svU = new AxSvUniq { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var svM = new AxSvMulti { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var ver = new AxVerCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var verU = new AxVerUniq { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var verM = new AxVerMulti { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var tr = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var trU = new AxTrUniq { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var trM = new AxTrMulti { Key = key, Bucket = bucket, Weight = weight, Tag = tag };

        return (c.Shape, c.Index) switch
        {
            (StorageShape.PureSv, IndexShape.None) => t.Spawn<AxPureSvNone>(AxPureSvNone.P.Set(in sv)),
            (StorageShape.PureSv, IndexShape.Unique) => t.Spawn<AxPureSvUniq>(AxPureSvUniq.P.Set(in svU)),
            (StorageShape.PureSv, _) => t.Spawn<AxPureSvMulti>(AxPureSvMulti.P.Set(in svM)),

            (StorageShape.PureVersioned, IndexShape.None) => t.Spawn<AxPureVerNone>(AxPureVerNone.P.Set(in ver)),
            (StorageShape.PureVersioned, IndexShape.Unique) => t.Spawn<AxPureVerUniq>(AxPureVerUniq.P.Set(in verU)),
            (StorageShape.PureVersioned, _) => t.Spawn<AxPureVerMulti>(AxPureVerMulti.P.Set(in verM)),

            (StorageShape.PureTransient, IndexShape.None) => t.Spawn<AxPureTrNone>(AxPureTrNone.P.Set(in tr)),
            (StorageShape.PureTransient, IndexShape.Unique) => t.Spawn<AxPureTrUniq>(AxPureTrUniq.P.Set(in trU)),
            (StorageShape.PureTransient, _) => t.Spawn<AxPureTrMulti>(AxPureTrMulti.P.Set(in trM)),

            (StorageShape.SvPlusVersioned, IndexShape.None) => t.Spawn<AxSvVerNone>(AxSvVerNone.P.Set(in sv), AxSvVerNone.S.Set(in ver)),
            (StorageShape.SvPlusVersioned, IndexShape.Unique) => t.Spawn<AxSvVerUniq>(AxSvVerUniq.P.Set(in svU), AxSvVerUniq.S.Set(in ver)),
            (StorageShape.SvPlusVersioned, _) => t.Spawn<AxSvVerMulti>(AxSvVerMulti.P.Set(in svM), AxSvVerMulti.S.Set(in ver)),

            (StorageShape.SvPlusTransient, IndexShape.None) => t.Spawn<AxSvTrNone>(AxSvTrNone.P.Set(in sv), AxSvTrNone.S.Set(in tr)),
            (StorageShape.SvPlusTransient, IndexShape.Unique) => t.Spawn<AxSvTrUniq>(AxSvTrUniq.P.Set(in svU), AxSvTrUniq.S.Set(in tr)),
            (StorageShape.SvPlusTransient, _) => t.Spawn<AxSvTrMulti>(AxSvTrMulti.P.Set(in svM), AxSvTrMulti.S.Set(in tr)),

            (StorageShape.VerPlusTransient, IndexShape.None) => t.Spawn<AxVerTrNone>(AxVerTrNone.P.Set(in ver), AxVerTrNone.S.Set(in tr)),
            (StorageShape.VerPlusTransient, IndexShape.Unique) => t.Spawn<AxVerTrUniq>(AxVerTrUniq.P.Set(in verU), AxVerTrUniq.S.Set(in tr)),
            _ => t.Spawn<AxVerTrMulti>(AxVerTrMulti.P.Set(in verM), AxVerTrMulti.S.Set(in tr)),
        };
    }

    /// <summary>
    /// The spatial compositions' counterpart of <see cref="Update"/> — writes the core payload AND the spatial carrier, so a fixture that updates a spatial
    /// cell and then asserts the round trip sees what it wrote.
    /// </summary>
    /// <remarks>
    /// This method exists because its absence was a live defect for the length of one build. Widening <see cref="Supports"/> to admit spatial cells
    /// immediately handed them to <c>ClusterStorageMatrixTests</c>, whose write behaviours call <see cref="Update"/>; without a spatial branch the call fell
    /// through to the (shape, index) switch and wrote a DIFFERENT archetype's component, leaving the assertion to read stale values. Fifteen cases failed.
    /// The lesson generalises: adding a carrier to the kit means auditing EVERY kit method, because the ones that were not updated do not refuse — they
    /// quietly do something else.
    /// </remarks>
    private static void UpdateSpatial(Transaction t, in Cell c, EntityId id, int i)
    {
        var (key, bucket, weight, tag) = Payload(i);
        var e = t.OpenMut(id);
        var core = new AxSvCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var vcore = new AxVerCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var tr = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var svSp = new AxSvSpatial { Bounds = BoundsOf(i), Key = key };
        var verSp = new AxVerSpatial { Bounds = BoundsOf(i), Key = key };

        switch (c.Shape)
        {
            case StorageShape.PureSv:
                e.Write(AxSpPureSv.P) = core;
                e.Write(AxSpPureSv.Sp) = svSp;
                break;
            case StorageShape.PureVersioned:
                e.Write(AxSpPureVer.P) = vcore;
                e.Write(AxSpPureVer.Sp) = verSp;
                break;
            case StorageShape.SvPlusVersioned:
                e.Write(AxSpSvVer.P) = core;
                e.Write(AxSpSvVer.S) = vcore;
                e.Write(AxSpSvVer.Sp) = svSp;
                break;
            case StorageShape.SvPlusTransient:
                e.Write(AxSpSvTr.P) = core;
                e.Write(AxSpSvTr.S) = tr;
                e.Write(AxSpSvTr.Sp) = svSp;
                break;
            default:
                e.Write(AxSpVerTr.P) = vcore;
                e.Write(AxSpVerTr.S) = tr;
                e.Write(AxSpVerTr.Sp) = verSp;
                break;
        }
    }

    private static EntityId SpawnCollection(Transaction t, in Cell c, int i)
    {
        var (key, bucket, weight, tag) = Payload(i);
        var core = new AxSvCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var vcore = new AxVerCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var tr = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };

        var svColl = new AxSvColl { Key = key };
        var verColl = new AxVerColl { Key = key };
        var n = ElementCountOf(i);

        // The buffer has to be filled BEFORE the spawn: Set() copies the component by value, and the collection field is a descriptor into a VSBS whose
        // contents must already exist for that descriptor to mean anything.
        if (c.Shape is StorageShape.PureVersioned or StorageShape.VerPlusTransient)
        {
            using var cca = t.CreateComponentCollectionAccessor(ref verColl.Items);
            for (var e = 0; e < n; e++)
            {
                cca.Add(ElementValue(i, e));
            }
        }
        else
        {
            using var cca = t.CreateComponentCollectionAccessor(ref svColl.Items);
            for (var e = 0; e < n; e++)
            {
                cca.Add(ElementValue(i, e));
            }
        }

        return c.Shape switch
        {
            StorageShape.PureSv => t.Spawn<AxCoPureSv>(AxCoPureSv.P.Set(in core), AxCoPureSv.Co.Set(in svColl)),
            StorageShape.PureVersioned => t.Spawn<AxCoPureVer>(AxCoPureVer.P.Set(in vcore), AxCoPureVer.Co.Set(in verColl)),
            StorageShape.SvPlusVersioned => t.Spawn<AxCoSvVer>(AxCoSvVer.P.Set(in core), AxCoSvVer.S.Set(in vcore), AxCoSvVer.Co.Set(in svColl)),
            StorageShape.SvPlusTransient => t.Spawn<AxCoSvTr>(AxCoSvTr.P.Set(in core), AxCoSvTr.S.Set(in tr), AxCoSvTr.Co.Set(in svColl)),
            _ => t.Spawn<AxCoVerTr>(AxCoVerTr.P.Set(in vcore), AxCoVerTr.S.Set(in tr), AxCoVerTr.Co.Set(in verColl)),
        };
    }

    /// <summary>Reads entity <paramref name="i"/>'s collection back and asserts every element survived, in order and in the right number.</summary>
    public static void AssertCollectionRoundTrip(Transaction t, in Cell c, EntityId id, int i)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (!SupportsCollection(c))
        {
            throw new NotSupportedException($"Cell '{c}' has no collection composition; narrow with AxisArchetypes.SupportsCollection.");
        }

        var e = t.Open(id);
        var expectedKey = Payload(i).Key;
        var expectedCount = ElementCountOf(i);

        int gotKey;
        int[] items;
        if (c.Shape is StorageShape.PureVersioned or StorageShape.VerPlusTransient)
        {
            var v = c.Shape == StorageShape.PureVersioned ? e.Read(AxCoPureVer.Co) : e.Read(AxCoVerTr.Co);
            gotKey = v.Key;
            using var cca = t.CreateComponentCollectionAccessor(ref v.Items);
            items = new int[cca.ElementCount];
            cca.GetAllElements(items);
        }
        else
        {
            var v = c.Shape switch
            {
                StorageShape.PureSv => e.Read(AxCoPureSv.Co),
                StorageShape.SvPlusVersioned => e.Read(AxCoSvVer.Co),
                _ => e.Read(AxCoSvTr.Co),
            };
            gotKey = v.Key;
            using var cca = t.CreateComponentCollectionAccessor(ref v.Items);
            items = new int[cca.ElementCount];
            cca.GetAllElements(items);
        }

        var where = $"{c} entity {i}: collection";
        Assert.Multiple(() =>
        {
            Assert.That(gotKey, Is.EqualTo(expectedKey), $"{where} Key");
            Assert.That(items, Has.Length.EqualTo(expectedCount),
                $"{where} element count — a shared or mis-keyed buffer shows up here before it shows up in the values");
            for (var el = 0; el < System.Math.Min(items.Length, expectedCount); el++)
            {
                Assert.That(items[el], Is.EqualTo(ElementValue(i, el)), $"{where} element {el}");
            }
        });
    }

    private static EntityId SpawnSpatial(Transaction t, in Cell c, int i)
    {
        var (key, bucket, weight, tag) = Payload(i);
        var core = new AxSvCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var vcore = new AxVerCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var tr = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
        var svSp = new AxSvSpatial { Bounds = BoundsOf(i), Key = key };
        var verSp = new AxVerSpatial { Bounds = BoundsOf(i), Key = key };

        return c.Shape switch
        {
            StorageShape.PureSv => t.Spawn<AxSpPureSv>(AxSpPureSv.P.Set(in core), AxSpPureSv.Sp.Set(in svSp)),
            StorageShape.PureVersioned => t.Spawn<AxSpPureVer>(AxSpPureVer.P.Set(in vcore), AxSpPureVer.Sp.Set(in verSp)),
            StorageShape.SvPlusVersioned => t.Spawn<AxSpSvVer>(AxSpSvVer.P.Set(in core), AxSpSvVer.S.Set(in vcore), AxSpSvVer.Sp.Set(in svSp)),
            StorageShape.SvPlusTransient => t.Spawn<AxSpSvTr>(AxSpSvTr.P.Set(in core), AxSpSvTr.S.Set(in tr), AxSpSvTr.Sp.Set(in svSp)),
            _ => t.Spawn<AxSpVerTr>(AxSpVerTr.P.Set(in vcore), AxSpVerTr.S.Set(in tr), AxSpVerTr.Sp.Set(in verSp)),
        };
    }

    /// <summary>Moves an entity spatial bounds to the lattice point of <paramref name="i"/> — the operation #548 class of defect breaks.</summary>
    public static void MoveSpatial(Transaction t, in Cell c, EntityId id, int i)
    {
        ArgumentNullException.ThrowIfNull(t);
        RequireSpatial(c);

        var e = t.OpenMut(id);
        var svSp = new AxSvSpatial { Bounds = BoundsOf(i), Key = Payload(i).Key };
        var verSp = new AxVerSpatial { Bounds = BoundsOf(i), Key = Payload(i).Key };

        switch (c.Shape)
        {
            case StorageShape.PureSv:
                e.Write(AxSpPureSv.Sp) = svSp;
                break;
            case StorageShape.PureVersioned:
                e.Write(AxSpPureVer.Sp) = verSp;
                break;
            case StorageShape.SvPlusVersioned:
                e.Write(AxSpSvVer.Sp) = svSp;
                break;
            case StorageShape.SvPlusTransient:
                e.Write(AxSpSvTr.Sp) = svSp;
                break;
            default:
                e.Write(AxSpVerTr.Sp) = verSp;
                break;
        }
    }

    /// <summary>Counts the entities of this cell whose bounds intersect the box [-1,-1,-1]..[max,max,max], through one terminal.</summary>
    public static int QueryInBox(Transaction t, in Cell c, float max, QueryTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(t);
        RequireSpatial(c);

        const float lo = -1f;
        return c.Shape switch
        {
            StorageShape.PureSv => ApplyTerminal(t.Query<AxSpPureSv>().WhereInAABB<AxSvSpatial>(lo, lo, lo, max, max, max), terminal),
            StorageShape.PureVersioned => ApplyTerminal(t.Query<AxSpPureVer>().WhereInAABB<AxVerSpatial>(lo, lo, lo, max, max, max), terminal),
            StorageShape.SvPlusVersioned => ApplyTerminal(t.Query<AxSpSvVer>().WhereInAABB<AxSvSpatial>(lo, lo, lo, max, max, max), terminal),
            StorageShape.SvPlusTransient => ApplyTerminal(t.Query<AxSpSvTr>().WhereInAABB<AxSvSpatial>(lo, lo, lo, max, max, max), terminal),
            _ => ApplyTerminal(t.Query<AxSpVerTr>().WhereInAABB<AxVerSpatial>(lo, lo, lo, max, max, max), terminal),
        };
    }

    private static void RequireSpatial(in Cell c)
    {
        if (!SupportsSpatial(c))
        {
            throw new NotSupportedException(
                $"Cell '{c}' has no spatial composition. Narrow the fixture source with AxisArchetypes.SupportsSpatial — the kit crosses the spatial axis "
                + "with the storage shape only, at Index=None.");
        }
    }

    /// <summary>
    /// Overwrites an existing entity's components with the payload of <paramref name="i"/>, so a fixture can spawn as <c>i</c>, update to <c>j</c> and then
    /// assert with <c>j</c>. Writes BOTH members of a mixed shape — an update path that silently drops the neighbour is a shape bug worth catching.
    /// </summary>
    public static void Update(Transaction t, in Cell c, EntityId id, int i)
    {
        ArgumentNullException.ThrowIfNull(t);
        RequireSupported(c);

        if (c.Spatial == SpatialShape.Present)
        {
            UpdateSpatial(t, c, id, i);
            return;
        }

        // Collection cells REFUSE rather than fall through. Rewriting a ComponentCollection in place means allocating a new VSBS buffer and releasing the old
        // one, which is a different operation from overwriting a blittable payload — and the spatial carrier taught the cost of letting an unhandled
        // composition fall through to the (shape, index) switch: it does not fail, it silently writes another archetype's component.
        if (c.Collection == CollectionShape.Present)
        {
            throw new NotSupportedException(
                $"Cell '{c}' carries a ComponentCollection; AxisArchetypes.Update does not rewrite collection buffers. Spawn a fresh entity, or extend the "
                + "kit with a collection-aware update if a fixture genuinely needs one.");
        }

        var (key, bucket, weight, tag) = Payload(i);
        var e = t.OpenMut(id);

        switch (c.Shape, c.Index)
        {
            case (StorageShape.PureSv, IndexShape.None):
                e.Write(AxPureSvNone.P) = new AxSvCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.PureSv, IndexShape.Unique):
                e.Write(AxPureSvUniq.P) = new AxSvUniq { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.PureSv, _):
                e.Write(AxPureSvMulti.P) = new AxSvMulti { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;

            case (StorageShape.PureVersioned, IndexShape.None):
                e.Write(AxPureVerNone.P) = new AxVerCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.PureVersioned, IndexShape.Unique):
                e.Write(AxPureVerUniq.P) = new AxVerUniq { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.PureVersioned, _):
                e.Write(AxPureVerMulti.P) = new AxVerMulti { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;

            case (StorageShape.PureTransient, IndexShape.None):
                e.Write(AxPureTrNone.P) = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.PureTransient, IndexShape.Unique):
                e.Write(AxPureTrUniq.P) = new AxTrUniq { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.PureTransient, _):
                e.Write(AxPureTrMulti.P) = new AxTrMulti { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;

            case (StorageShape.SvPlusVersioned, IndexShape.None):
                e.Write(AxSvVerNone.P) = new AxSvCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                e.Write(AxSvVerNone.S) = new AxVerCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.SvPlusVersioned, IndexShape.Unique):
                e.Write(AxSvVerUniq.P) = new AxSvUniq { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                e.Write(AxSvVerUniq.S) = new AxVerCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.SvPlusVersioned, _):
                e.Write(AxSvVerMulti.P) = new AxSvMulti { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                e.Write(AxSvVerMulti.S) = new AxVerCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;

            case (StorageShape.SvPlusTransient, IndexShape.None):
                e.Write(AxSvTrNone.P) = new AxSvCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                e.Write(AxSvTrNone.S) = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.SvPlusTransient, IndexShape.Unique):
                e.Write(AxSvTrUniq.P) = new AxSvUniq { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                e.Write(AxSvTrUniq.S) = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.SvPlusTransient, _):
                e.Write(AxSvTrMulti.P) = new AxSvMulti { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                e.Write(AxSvTrMulti.S) = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;

            case (StorageShape.VerPlusTransient, IndexShape.None):
                e.Write(AxVerTrNone.P) = new AxVerCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                e.Write(AxVerTrNone.S) = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            case (StorageShape.VerPlusTransient, IndexShape.Unique):
                e.Write(AxVerTrUniq.P) = new AxVerUniq { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                e.Write(AxVerTrUniq.S) = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
            default:
                e.Write(AxVerTrMulti.P) = new AxVerMulti { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                e.Write(AxVerTrMulti.S) = new AxTrCore { Key = key, Bucket = bucket, Weight = weight, Tag = tag };
                break;
        }
    }

    /// <summary>
    /// Reads entity <paramref name="i"/> back through the primary component and asserts every field survived. The secondary component of a mixed shape is
    /// asserted too — a shape bug that corrupts the neighbour while preserving the component under test is exactly the kind #704 is looking for.
    /// </summary>
    /// <param name="includeTransient">
    /// Pass <c>false</c> after a reopen. A Transient component is heap-only by definition, so its values are legitimately gone on the far side of any reopen —
    /// asserting them there would test the storage contract backwards. The DURABLE members are still asserted, which is the point: a mixed shape must carry
    /// its SV/Versioned payload across the reopen even though its Transient neighbour resets.
    /// </param>
    public static void AssertRoundTrip(Transaction t, in Cell c, EntityId id, int i, bool includeTransient = true)
    {
        ArgumentNullException.ThrowIfNull(t);
        RequireSupported(c);

        if (c.Spatial == SpatialShape.Present)
        {
            AssertSpatialRoundTrip(t, c, id, i, includeTransient);
            return;
        }

        if (c.Collection == CollectionShape.Present)
        {
            AssertCollectionRoundTrip(t, c, id, i);
            return;
        }

        var (key, bucket, weight, tag) = Payload(i);
        var e = t.Open(id);

        switch (c.Shape, c.Index)
        {
            case (StorageShape.PureSv, IndexShape.None):
                AssertSv(e.Read(AxPureSvNone.P), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.PureSv, IndexShape.Unique):
                AssertSvU(e.Read(AxPureSvUniq.P), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.PureSv, _):
                AssertSvM(e.Read(AxPureSvMulti.P), key, bucket, weight, tag, c, i);
                break;

            case (StorageShape.PureVersioned, IndexShape.None):
                AssertVer(e.Read(AxPureVerNone.P), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.PureVersioned, IndexShape.Unique):
                AssertVerU(e.Read(AxPureVerUniq.P), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.PureVersioned, _):
                AssertVerM(e.Read(AxPureVerMulti.P), key, bucket, weight, tag, c, i);
                break;

            case (StorageShape.PureTransient, IndexShape.None):
                AssertTr(e.Read(AxPureTrNone.P), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.PureTransient, IndexShape.Unique):
                AssertTrU(e.Read(AxPureTrUniq.P), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.PureTransient, _):
                AssertTrM(e.Read(AxPureTrMulti.P), key, bucket, weight, tag, c, i);
                break;

            case (StorageShape.SvPlusVersioned, IndexShape.None):
                AssertSv(e.Read(AxSvVerNone.P), key, bucket, weight, tag, c, i);
                AssertVer(e.Read(AxSvVerNone.S), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.SvPlusVersioned, IndexShape.Unique):
                AssertSvU(e.Read(AxSvVerUniq.P), key, bucket, weight, tag, c, i);
                AssertVer(e.Read(AxSvVerUniq.S), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.SvPlusVersioned, _):
                AssertSvM(e.Read(AxSvVerMulti.P), key, bucket, weight, tag, c, i);
                AssertVer(e.Read(AxSvVerMulti.S), key, bucket, weight, tag, c, i);
                break;

            case (StorageShape.SvPlusTransient, IndexShape.None):
                AssertSv(e.Read(AxSvTrNone.P), key, bucket, weight, tag, c, i);
                AssertTrSecondary(includeTransient, e.Read(AxSvTrNone.S), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.SvPlusTransient, IndexShape.Unique):
                AssertSvU(e.Read(AxSvTrUniq.P), key, bucket, weight, tag, c, i);
                AssertTrSecondary(includeTransient, e.Read(AxSvTrUniq.S), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.SvPlusTransient, _):
                AssertSvM(e.Read(AxSvTrMulti.P), key, bucket, weight, tag, c, i);
                AssertTrSecondary(includeTransient, e.Read(AxSvTrMulti.S), key, bucket, weight, tag, c, i);
                break;

            case (StorageShape.VerPlusTransient, IndexShape.None):
                AssertVer(e.Read(AxVerTrNone.P), key, bucket, weight, tag, c, i);
                AssertTrSecondary(includeTransient, e.Read(AxVerTrNone.S), key, bucket, weight, tag, c, i);
                break;
            case (StorageShape.VerPlusTransient, IndexShape.Unique):
                AssertVerU(e.Read(AxVerTrUniq.P), key, bucket, weight, tag, c, i);
                AssertTrSecondary(includeTransient, e.Read(AxVerTrUniq.S), key, bucket, weight, tag, c, i);
                break;
            default:
                AssertVerM(e.Read(AxVerTrMulti.P), key, bucket, weight, tag, c, i);
                AssertTrSecondary(includeTransient, e.Read(AxVerTrMulti.S), key, bucket, weight, tag, c, i);
                break;
        }
    }

    /// <summary>
    /// Runs an equality query on the cell's AllowMultiple index key (<c>Bucket</c>) through one terminal, and returns how many entities it reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the AllowMultiple compositions.</b> <c>WhereField</c> rejects a predicate on a non-indexed field — <i>"Field 'Bucket' is not indexed. View
    /// predicates require indexed fields."</i> — and <c>Bucket</c> carries the index only on the <c>*Multi</c> variants. A caller must therefore narrow to
    /// <see cref="IndexShape.AllowMultiple"/>; anything else throws here rather than silently querying something else.
    /// </para>
    /// <para>
    /// <b>Why the query lives in the kit.</b> <c>WhereField</c> takes an expression over a statically-known component type, and a generic <c>TComp</c>
    /// constrained to an interface would resolve <c>f.Bucket</c> to an interface MEMBER rather than the field the expression parser needs. So the predicate
    /// cannot be written generically over a cell — but it can be written once per component here, where the type is concrete. Applying the TERMINAL is generic
    /// (<see cref="ApplyTerminal{TArch}"/>), because every terminal hangs off <c>EcsQuery&lt;TArchetype&gt;</c> and none of them mentions the component type.
    /// </para>
    /// </remarks>
    public static int QueryByBucket(Transaction t, in Cell c, int bucket, QueryTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(t);
        RequireIndexed(c, IndexShape.AllowMultiple, "Bucket");

        return c.Shape switch
        {
            StorageShape.PureSv => ApplyTerminal(t.Query<AxPureSvMulti>().WhereField<AxSvMulti>(f => f.Bucket == bucket), terminal),
            StorageShape.PureVersioned => ApplyTerminal(t.Query<AxPureVerMulti>().WhereField<AxVerMulti>(f => f.Bucket == bucket), terminal),
            StorageShape.PureTransient => ApplyTerminal(t.Query<AxPureTrMulti>().WhereField<AxTrMulti>(f => f.Bucket == bucket), terminal),
            StorageShape.SvPlusVersioned => ApplyTerminal(t.Query<AxSvVerMulti>().WhereField<AxSvMulti>(f => f.Bucket == bucket), terminal),
            StorageShape.SvPlusTransient => ApplyTerminal(t.Query<AxSvTrMulti>().WhereField<AxSvMulti>(f => f.Bucket == bucket), terminal),
            _ => ApplyTerminal(t.Query<AxVerTrMulti>().WhereField<AxVerMulti>(f => f.Bucket == bucket), terminal),
        };
    }

    /// <summary>
    /// Runs an equality query on the cell's UNIQUE index key (<c>Key</c>) through one terminal, and returns how many entities it reported — 0 or 1.
    /// </summary>
    /// <remarks>
    /// The unique counterpart of <see cref="QueryByBucket"/>, and not redundant with it: a unique index and an AllowMultiple index take different plan paths
    /// (point lookup versus a multi-value element buffer), so a terminal can be right on one and wrong on the other. That is the 2×2 #590/#592 lived in.
    /// </remarks>
    public static int QueryByKey(Transaction t, in Cell c, int key, QueryTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(t);
        RequireIndexed(c, IndexShape.Unique, "Key");

        return c.Shape switch
        {
            StorageShape.PureSv => ApplyTerminal(t.Query<AxPureSvUniq>().WhereField<AxSvUniq>(f => f.Key == key), terminal),
            StorageShape.PureVersioned => ApplyTerminal(t.Query<AxPureVerUniq>().WhereField<AxVerUniq>(f => f.Key == key), terminal),
            StorageShape.PureTransient => ApplyTerminal(t.Query<AxPureTrUniq>().WhereField<AxTrUniq>(f => f.Key == key), terminal),
            StorageShape.SvPlusVersioned => ApplyTerminal(t.Query<AxSvVerUniq>().WhereField<AxSvUniq>(f => f.Key == key), terminal),
            StorageShape.SvPlusTransient => ApplyTerminal(t.Query<AxSvTrUniq>().WhereField<AxSvUniq>(f => f.Key == key), terminal),
            _ => ApplyTerminal(t.Query<AxVerTrUniq>().WhereField<AxVerUniq>(f => f.Key == key), terminal),
        };
    }

    /// <summary>
    /// The unique <c>Key</c> the kit gives entity <paramref name="i"/> — so a fixture can query for a known entity without re-deriving the payload.
    /// </summary>
    public static int KeyOf(int i) => Payload(i).Key;

    /// <summary>
    /// How many of the first <paramref name="count"/> entities the kit spawns fall in <paramref name="bucket"/> — the model the query is checked against.
    /// </summary>
    /// <remarks>
    /// A closed form, not a scan. The kit's payload puts entity <c>i</c> in bucket <c>i % BucketCount</c>, so the expected answer is arithmetic — which means
    /// the assertion never has to re-derive "the right answer" per cell. Re-deriving it per cell is exactly how the suite ended up pinned to one cell.
    /// </remarks>
    public static int ExpectedInBucket(int count, int bucket) => bucket >= BucketCount ? 0 : (count - bucket + BucketCount - 1) / BucketCount;

    /// <summary>
    /// Refuses a query whose predicate field is not indexed in this cell. Loud, because the alternative is a fixture that silently queries a different field
    /// (or falls back to a scan) and reports coverage of a plan path it never took.
    /// </summary>
    private static void RequireIndexed(in Cell c, IndexShape required, string field)
    {
        RequireSupported(c);
        if (c.Index == required)
        {
            return;
        }

        throw new NotSupportedException(
            $"Cell '{c}' has Index={c.Index}, so '{field}' is not indexed and WhereField would reject the predicate. Narrow the fixture's source to "
            + $"Index={required}. The kit indexes Key only on the unique variants and Bucket only on the AllowMultiple ones, by design: that is what makes "
            + "the index kind an axis rather than a label.");
    }

    private static int ApplyTerminal<TArch>(EcsQuery<TArch> query, QueryTerminal terminal)
        where TArch : Archetype<TArch>
    {
        switch (terminal)
        {
            case QueryTerminal.Count:
                return query.Count();
            case QueryTerminal.Any:
                return query.Any() ? 1 : 0;
            case QueryTerminal.ToView:
            {
                using var view = query.ToView();
                return view.Count;
            }
            default:
                return query.Execute().Count;
        }
    }

    /// <summary>
    /// Recovers the cell's static archetype type and hands it to <paramref name="visitor"/>. The escape hatch for anything the four kit methods cannot express
    /// generically — most importantly a query, since <c>Transaction.Query&lt;TArchetype&gt;()</c> is generic on the archetype and there is no non-generic form.
    /// </summary>
    public static TResult Dispatch<TArg, TResult>(in Cell c, TArg arg, ICellVisitor<TArg, TResult> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        RequireSupported(c);

        if (c.Collection == CollectionShape.Present)
        {
            return c.Shape switch
            {
                StorageShape.PureSv => visitor.Visit<AxCoPureSv>(arg),
                StorageShape.PureVersioned => visitor.Visit<AxCoPureVer>(arg),
                StorageShape.SvPlusVersioned => visitor.Visit<AxCoSvVer>(arg),
                StorageShape.SvPlusTransient => visitor.Visit<AxCoSvTr>(arg),
                _ => visitor.Visit<AxCoVerTr>(arg),
            };
        }

        if (c.Spatial == SpatialShape.Present)
        {
            return c.Shape switch
            {
                StorageShape.PureSv => visitor.Visit<AxSpPureSv>(arg),
                StorageShape.PureVersioned => visitor.Visit<AxSpPureVer>(arg),
                StorageShape.SvPlusVersioned => visitor.Visit<AxSpSvVer>(arg),
                StorageShape.SvPlusTransient => visitor.Visit<AxSpSvTr>(arg),
                _ => visitor.Visit<AxSpVerTr>(arg),
            };
        }

        return (c.Shape, c.Index) switch
        {
            (StorageShape.PureSv, IndexShape.None) => visitor.Visit<AxPureSvNone>(arg),
            (StorageShape.PureSv, IndexShape.Unique) => visitor.Visit<AxPureSvUniq>(arg),
            (StorageShape.PureSv, _) => visitor.Visit<AxPureSvMulti>(arg),

            (StorageShape.PureVersioned, IndexShape.None) => visitor.Visit<AxPureVerNone>(arg),
            (StorageShape.PureVersioned, IndexShape.Unique) => visitor.Visit<AxPureVerUniq>(arg),
            (StorageShape.PureVersioned, _) => visitor.Visit<AxPureVerMulti>(arg),

            (StorageShape.PureTransient, IndexShape.None) => visitor.Visit<AxPureTrNone>(arg),
            (StorageShape.PureTransient, IndexShape.Unique) => visitor.Visit<AxPureTrUniq>(arg),
            (StorageShape.PureTransient, _) => visitor.Visit<AxPureTrMulti>(arg),

            (StorageShape.SvPlusVersioned, IndexShape.None) => visitor.Visit<AxSvVerNone>(arg),
            (StorageShape.SvPlusVersioned, IndexShape.Unique) => visitor.Visit<AxSvVerUniq>(arg),
            (StorageShape.SvPlusVersioned, _) => visitor.Visit<AxSvVerMulti>(arg),

            (StorageShape.SvPlusTransient, IndexShape.None) => visitor.Visit<AxSvTrNone>(arg),
            (StorageShape.SvPlusTransient, IndexShape.Unique) => visitor.Visit<AxSvTrUniq>(arg),
            (StorageShape.SvPlusTransient, _) => visitor.Visit<AxSvTrMulti>(arg),

            (StorageShape.VerPlusTransient, IndexShape.None) => visitor.Visit<AxVerTrNone>(arg),
            (StorageShape.VerPlusTransient, IndexShape.Unique) => visitor.Visit<AxVerTrUniq>(arg),
            _ => visitor.Visit<AxVerTrMulti>(arg),
        };
    }

    /// <summary>
    /// The load-bearing refusal. A cell the kit cannot build must stop the test, not be quietly ignored — see the class remarks. The message names the cell and
    /// what to do about it, because the caller's next move is either to narrow with <see cref="Supports"/> or to add the missing composition here.
    /// </summary>
    private static void RequireSupported(in Cell c)
    {
        if (Supports(c))
        {
            return;
        }

        var why = EngineAxes.IsValid(c)
            ? "the kit has no composition for it yet (the collection and spatial carriers are not built)"
            : "EngineAxes.IsValid rejects it — the engine cannot express this combination at all";

        throw new NotSupportedException(
            $"AxisArchetypes has no archetype for cell '{c}': {why}. Narrow the fixture's source with AxisArchetypes.Supports, or add the composition to "
            + "AxisArchetypes.cs. Do NOT skip the cell at run time — a skipped cell counts as coverage in the test count and is not.");
    }

    private static void AssertSpatialRoundTrip(Transaction t, in Cell c, EntityId id, int i, bool includeTransient)
    {
        var (key, bucket, weight, tag) = Payload(i);
        var e = t.Open(id);
        var expected = BoundsOf(i);

        AABB3F bounds;
        int spatialKey;
        switch (c.Shape)
        {
            case StorageShape.PureSv:
            {
                AssertSv(e.Read(AxSpPureSv.P), key, bucket, weight, tag, c, i);
                var v = e.Read(AxSpPureSv.Sp);
                bounds = v.Bounds;
                spatialKey = v.Key;
                break;
            }

            case StorageShape.PureVersioned:
            {
                AssertVer(e.Read(AxSpPureVer.P), key, bucket, weight, tag, c, i);
                var v = e.Read(AxSpPureVer.Sp);
                bounds = v.Bounds;
                spatialKey = v.Key;
                break;
            }

            case StorageShape.SvPlusVersioned:
            {
                AssertSv(e.Read(AxSpSvVer.P), key, bucket, weight, tag, c, i);
                AssertVer(e.Read(AxSpSvVer.S), key, bucket, weight, tag, c, i);
                var v = e.Read(AxSpSvVer.Sp);
                bounds = v.Bounds;
                spatialKey = v.Key;
                break;
            }

            case StorageShape.SvPlusTransient:
            {
                AssertSv(e.Read(AxSpSvTr.P), key, bucket, weight, tag, c, i);
                AssertTrSecondary(includeTransient, e.Read(AxSpSvTr.S), key, bucket, weight, tag, c, i);
                var v = e.Read(AxSpSvTr.Sp);
                bounds = v.Bounds;
                spatialKey = v.Key;
                break;
            }

            default:
            {
                AssertVer(e.Read(AxSpVerTr.P), key, bucket, weight, tag, c, i);
                AssertTrSecondary(includeTransient, e.Read(AxSpVerTr.S), key, bucket, weight, tag, c, i);
                var v = e.Read(AxSpVerTr.Sp);
                bounds = v.Bounds;
                spatialKey = v.Key;
                break;
            }
        }

        var where = $"{c} entity {i}: spatial";
        Assert.Multiple(() =>
        {
            Assert.That(spatialKey, Is.EqualTo(key), $"{where} Key");
            Assert.That(bounds.MinX, Is.EqualTo(expected.MinX).Within(0.0001f), $"{where} MinX");
            Assert.That(bounds.MaxZ, Is.EqualTo(expected.MaxZ).Within(0.0001f), $"{where} MaxZ");
        });
    }

    // ── Field-by-field assertions. Each names the cell and the entity, because a covering-array failure is only useful if it says which cell failed. ────────

    private static void AssertSv(in AxSvCore v, int key, int bucket, float weight, long tag, in Cell c, int i) =>
        AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "SV");

    private static void AssertSvU(in AxSvUniq v, int key, int bucket, float weight, long tag, in Cell c, int i) =>
        AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "SV+unique");

    private static void AssertSvM(in AxSvMulti v, int key, int bucket, float weight, long tag, in Cell c, int i) =>
        AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "SV+multi");

    private static void AssertVer(in AxVerCore v, int key, int bucket, float weight, long tag, in Cell c, int i) =>
        AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "Versioned");

    private static void AssertVerU(in AxVerUniq v, int key, int bucket, float weight, long tag, in Cell c, int i) =>
        AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "Versioned+unique");

    private static void AssertVerM(in AxVerMulti v, int key, int bucket, float weight, long tag, in Cell c, int i) =>
        AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "Versioned+multi");

    private static void AssertTr(in AxTrCore v, int key, int bucket, float weight, long tag, in Cell c, int i) =>
        AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "Transient");

    private static void AssertTrU(in AxTrUniq v, int key, int bucket, float weight, long tag, in Cell c, int i) =>
        AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "Transient+unique");

    private static void AssertTrM(in AxTrMulti v, int key, int bucket, float weight, long tag, in Cell c, int i) =>
        AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "Transient+multi");

    /// <summary>
    /// The Transient neighbour of a mixed shape, asserted only when the caller says the values should still be there. Skipping it after a reopen is not
    /// leniency — a Transient component is defined as heap-only, so asserting its survival would be asserting the opposite of the storage contract.
    /// </summary>
    private static void AssertTrSecondary(bool include, in AxTrCore v, int key, int bucket, float weight, long tag, in Cell c, int i)
    {
        if (include)
        {
            AssertFields(v.Key, v.Bucket, v.Weight, v.Tag, key, bucket, weight, tag, c, i, "Transient");
        }
    }

    // `Cell` is taken by value, not `in`: the assertion label is built inside an Assert.Multiple lambda, and a `ref`-like parameter cannot be captured.
    private static void AssertFields(int gotKey, int gotBucket, float gotWeight, long gotTag,
        int key, int bucket, float weight, long tag, Cell c, int i, string which)
    {
        var where = $"{c} entity {i}: {which}";
        Assert.Multiple(() =>
        {
            Assert.That(gotKey, Is.EqualTo(key), $"{where} Key");
            Assert.That(gotBucket, Is.EqualTo(bucket), $"{where} Bucket");
            Assert.That(gotWeight, Is.EqualTo(weight).Within(0.0001f), $"{where} Weight");
            Assert.That(gotTag, Is.EqualTo(tag), $"{where} Tag");
        });
    }
}
