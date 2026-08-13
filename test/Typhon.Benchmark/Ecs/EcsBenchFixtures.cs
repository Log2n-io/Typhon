using System.Numerics;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Shared ECS benchmark fixtures — the component/archetype library every ECS benchmark class builds on.
//
// These were previously declared inside ArchetypeAccessorBenchmark.cs and consumed cross-file by
// ClusterRegressionBenchmarks and CommittedDisciplineBenchmarks. They live here so that no benchmark class owns the
// fixtures it shares, and so a new op-matrix class can reuse them instead of declaring yet another near-identical
// {int Value; long Timestamp} component.
//
// The set deliberately spans the storage-mode × storage-shape matrix, because op cost depends on BOTH:
//   • StorageMode  — Versioned (MVCC chain) / SingleVersion (in-place) / Transient (heap, non-persisted).
//   • Storage SHAPE — cluster (SoA) vs legacy/flat, branched on `_clusterBase != null`. An archetype is
//     cluster-eligible unless it is pure-Versioned — that is the whole rule since #655 removed the exclusion on
//     archetypes carrying an indexed Transient component. So a pure-Versioned archetype takes the LEGACY path while
//     a Versioned component inside a mixed cluster takes the CLUSTER path — same StorageMode, different code.
//     Benchmarks must state which shape they run.
//
// Component names are globally unique and StorageMode is pinned per (name, revision) — never re-register the same
// name with a different mode; add a distinctly-named twin instead.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

// ── SV components matching the AntHill pattern ──────────────────────────────────────────────────────────────────────
[Component("Typhon.Benchmark.AA.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchPosition
{
    public float X, Y;
    public AaBenchPosition(float x, float y) { X = x; Y = y; }
}

[Component("Typhon.Benchmark.AA.Movement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchMovement
{
    public float VX, VY;
    public AaBenchMovement(float vx, float vy) { VX = vx; VY = vy; }
}

/// <summary>Pure-SV cluster archetype (SoA path). The baseline shape for iteration and in-place write benchmarks.</summary>
[Archetype]
partial class AaBenchAnt : Archetype<AaBenchAnt>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchMovement> Movement = Register<AaBenchMovement>();
}

// ── Spatial SV components ───────────────────────────────────────────────────────────────────────────────────────────
[Component("Typhon.Benchmark.AA.SpatialPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchSpatialPos
{
    [Field]
    [SpatialIndex(5.0f)]
    public AABB3F Bounds;
    [Field]
    public float Speed;
}

[Component("Typhon.Benchmark.AA.SpatialMeta", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchSpatialMeta
{
    [Field]
    public long Tag;
}

[Archetype]
partial class AaBenchSpatialUnit : Archetype<AaBenchSpatialUnit>
{
    public static readonly Comp<AaBenchSpatialPos> Pos = Register<AaBenchSpatialPos>();
    public static readonly Comp<AaBenchSpatialMeta> Meta = Register<AaBenchSpatialMeta>();
}

// ── Indexed SV component ────────────────────────────────────────────────────────────────────────────────────────────
[Component("Typhon.Benchmark.AA.IdxData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchIdxData
{
    [Index]
    public int Score;
    public int Flags;
    public AaBenchIdxData(int score, int flags) { Score = score; Flags = flags; }
}

/// <summary>Indexed SV archetype — carries the shadow-capture + B+Tree maintenance cost on write/tick-fence.</summary>
[Archetype]
partial class AaBenchIdxUnit : Archetype<AaBenchIdxUnit>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchIdxData> Data = Register<AaBenchIdxData>();
}

// ── Mixed SV + Versioned cluster archetype ──────────────────────────────────────────────────────────────────────────
[Component("Typhon.Bench.AA.VcHealth", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct AaVcHealth
{
    public int Current, Max;
}

/// <summary>
/// Mixed SV + Versioned cluster archetype. The Versioned <c>Health</c> slot here takes the CLUSTER path (HEAD cached in
/// the cluster slot, chain in the revision table) — contrast with a pure-Versioned archetype, which takes the legacy path.
/// </summary>
[Archetype]
partial class AaBenchMixedCluster : Archetype<AaBenchMixedCluster>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();  // SV
    public static readonly Comp<AaBenchMovement> Movement = Register<AaBenchMovement>();  // SV
    public static readonly Comp<AaVcHealth> Health = Register<AaVcHealth>();              // Versioned
}

// ── Indexed VERSIONED component — the commit-time index path ────────────────────────────────────────────────────────
// AaVcHealth above carries no index, so nothing in the suite exercised ReconcileClusterIndexAndViews' field loop: the per-archetype B+Tree maintenance that
// runs at COMMIT rather than at the tick fence. That is the path #665 guards, and the path Phase 4 of the index-ownership consolidation migrates the rest of
// the population onto. AllowMultiple + a low-cardinality Tier is the shape the unchanged-field guard exists for: index the classification, write the value
// that churns.
[Component("Typhon.Bench.AA.VcRanked", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct AaVcRanked
{
    [Index(AllowMultiple = true)]
    public int Tier;
    public int Score;
    public AaVcRanked(int tier, int score) { Tier = tier; Score = score; }
}

/// <summary>
/// Mixed SV + <b>indexed</b> Versioned cluster archetype. The SV <c>Position</c> makes it cluster-eligible, which is what moves <c>AaVcRanked</c>'s index onto
/// the archetype; writes to it reconcile the B+Tree at commit.
/// </summary>
[Archetype]
partial class AaBenchIdxVersionedCluster : Archetype<AaBenchIdxVersionedCluster>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();  // SV — makes it cluster-eligible
    public static readonly Comp<AaVcRanked> Ranked = Register<AaVcRanked>();              // Versioned, indexed
}

// ── Transient components — completes the StorageMode axis ───────────────────────────────────────────────────────────
// Transient data is heap-resident and never persisted: writes are in-place with no dirty tracking and no durable commit.
// A Transient component with NO indexed field keeps the archetype cluster-eligible, which is the shape we want to
// measure (dual-segment PS + TS SoA read, exercising the TransientSlotMask branch).
[Component("Typhon.Benchmark.AA.TransientData", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchTransientData
{
    public int Value;
    public long Timestamp;
    public AaBenchTransientData(int value, long timestamp) { Value = value; Timestamp = timestamp; }
}

/// <summary>Pure-Transient cluster archetype — the Transient rung of the storage-mode cost ladder.</summary>
[Archetype]
partial class AaBenchTransientUnit : Archetype<AaBenchTransientUnit>
{
    public static readonly Comp<AaBenchTransientData> Data = Register<AaBenchTransientData>();
}

/// <summary>Mixed SV + Transient archetype — exercises the dual-segment (persistent + transient) cluster read path.</summary>
[Archetype]
partial class AaBenchSvTransientUnit : Archetype<AaBenchSvTransientUnit>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();      // SV  (persistent segment)
    public static readonly Comp<AaBenchTransientData> Data = Register<AaBenchTransientData>(); // Transient (transient segment)
}

/// <summary>
/// Pure-Versioned archetype — carries NO SV/Transient slot, so it is NOT cluster-eligible and takes the LEGACY (flat) path.
/// This is the deliberate counterpart to <see cref="AaBenchMixedCluster"/>: same <c>Versioned</c> StorageMode, different code
/// path. Benchmarking both is the only way to see the cluster-vs-legacy shape cost, which the old suite never measured.
/// </summary>
[Archetype]
partial class AaBenchVersionedUnit : Archetype<AaBenchVersionedUnit>
{
    public static readonly Comp<AaVcHealth> Health = Register<AaVcHealth>();
}

// ── Additional indexed archetypes for the ordered/K-way-merge query benchmarks ──────────────────────────────────────
[Archetype]
partial class AaBenchIdxUnit2 : Archetype<AaBenchIdxUnit2>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchIdxData> Data = Register<AaBenchIdxData>();
}

[Archetype]
partial class AaBenchIdxUnit3 : Archetype<AaBenchIdxUnit3>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchIdxData> Data = Register<AaBenchIdxData>();
}

// ── High-fan-out AllowMultiple index — the shape the selective scan is SELECTED for ─────────────────────────────────
// Every other indexed fixture here is either unique (AaBenchIdxData, fan-out 1) or low fan-out (AaVcRanked, 12.5 over
// 100 rows), so none of them reaches EcsQuery.HasFanOutForSelectiveScan's threshold and the tracked suite had no
// coverage at all of the path the planner chooses above it. A regression there would have been invisible.
//
// Fan-out 200 over 10 000 rows with keys assigned `i % 50`, i.e. DECORRELATED from insert order: equal keys land in
// every cluster, so zone maps prune nothing and Path B pays a 64-slot pass per cluster. That is the case the sweep
// measured Path A winning 1.18-1.67x. The correlated variant (equal keys adjacent) is where it is a wash, and is not
// what this benchmark holds — one shape per fixture, named for what it is.
[Component("Typhon.Bench.AA.FanOut", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AaBenchFanOutData
{
    [Index(AllowMultiple = true)] public int Bucket;
    public int Payload;

    public AaBenchFanOutData(int bucket, int payload)
    {
        Bucket = bucket;
        Payload = payload;
    }
}

[Archetype]
partial class AaBenchFanOutUnit : Archetype<AaBenchFanOutUnit>
{
    public static readonly Comp<AaBenchPosition> Position = Register<AaBenchPosition>();
    public static readonly Comp<AaBenchFanOutData> Data = Register<AaBenchFanOutData>();
}
