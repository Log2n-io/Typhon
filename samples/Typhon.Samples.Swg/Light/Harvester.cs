using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Samples.Swg;

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// SWG Light — the minimal slice: a roaming harvester drone.
//
// A single archetype, Harvester, is an autonomous drone that roams a resource field and accumulates yield into its
// cargo hold. Five components — one per storage mode Typhon offers, plus the spatial and index primitives — so the
// sample teaches the whole engine surface in the smallest coherent shape:
//
//   • SingleVersion  (Position)  — hot, durable, no isolation: the drone's location, written lock-free each tick.
//   • SingleVersion  (Footprint) — the spatial-index mirror of the position (the R-Tree lives here).
//   • Versioned      (Cargo)     — full MVCC + WAL: durable, snapshot-isolated ACID state (the accumulated yield).
//   • Transient      (Drift)     — heap-only per-tick scratch (the movement vector); lost on restart by design.
//   • Versioned      (Extractor) — an indexed classification field, so "every drone extracting kind K" is an index scan.
//
// This slice is standalone — it references no other sample type — so it compiles on its own. It is exactly what the
// `typhon new` scaffold emits as source and what the getting-started guide runs. SWG Full (Full/*.cs) extends the
// assembly with the exotic primitives; Light never depends on Full.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>SingleVersion drone location. SingleVersion is the hot, durable, no-isolation mode — a movement system
/// writes it lock-free through the per-worker accessor every tick, with no MVCC revision history.</summary>
[Component("Swg.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Position
{
    [Field] public Point2F P;
}

/// <summary>SingleVersion spatial footprint — the R-Tree mirror of <see cref="Position"/>. A spatial field must be
/// written through the spatial barrier (<c>WriteSpatial</c>), which is what keeps the index coherent after movement.</summary>
[Component("Swg.Footprint", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Footprint
{
    [Field] [SpatialIndex(2.0f, Mode = SpatialMode.Dynamic)] public AABB2F Box;
}

/// <summary>Versioned cargo hold: how much the drone has banked, and its cap. Plain ACID state — reads see a consistent
/// snapshot, writes are transactional and roll back cleanly.</summary>
[Component("Swg.Cargo", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Cargo
{
    [Field] public int Amount;
    [Field] public int Capacity;
}

/// <summary>Transient per-tick movement vector. Transient components are heap-only (zero page-cache footprint) and
/// dropped on reopen by design — perfect for scratch that is recomputed each tick and never needs to survive a restart.</summary>
[Component("Swg.Drift", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct Drift
{
    [Field] public float Dx;
    [Field] public float Dy;
}

/// <summary>Versioned extraction spec: which resource kind this drone pulls, and how fast. <see cref="ResourceKind"/>
/// is a non-unique index so "every drone harvesting kind K" is an index scan, not a table walk.</summary>
[Component("Swg.Extractor", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Extractor
{
    [Field] [Index(AllowMultiple = true)] public int ResourceKind;
    [Field] public int Rate;
}

/// <summary>The one SWG Light archetype: a roaming harvester drone combining a SingleVersion position + spatial
/// footprint, a Versioned cargo hold + extraction spec, and a Transient movement vector. Its durable identity is its
/// type name ("Harvester"); the engine assigns the catalog and routing ids automatically (feature #514 — no author-set id).</summary>
[Archetype]
public sealed partial class Harvester : Archetype<Harvester>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Footprint> Footprint = Register<Footprint>();
    public static readonly Comp<Cargo> Cargo = Register<Cargo>();
    public static readonly Comp<Drift> Drift = Register<Drift>();
    public static readonly Comp<Extractor> Extractor = Register<Extractor>();
}
