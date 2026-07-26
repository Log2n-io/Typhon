using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Samples.Swg.Shard;

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// SWG Light — the minimal slice: one planet shard of the galaxy, alive with characters.
//
// A single archetype, Character, is any faction-aligned being in the world — an NPC here, since the tick loop drives
// them by AI, but the same shape a player character has. It roams the planet, regenerates its HAM pools, senses its
// surroundings, and trades credits with other characters in atomic, snapshot-isolated transactions. (The Full tier's
// Player is this plus an account, a guild membership and a session — a Character with an identity attached.)
//
// The point of this sample is JUDGMENT, not a feature checklist: each component sits in the storage mode its access
// pattern actually calls for. That single choice is the whole difference between a fast Typhon schema and a slow one:
//
//   • SingleVersion (Transform, Bounds, Ham, Faction) — hot, per-tick, loss-tolerant state. Written lock-free by
//     parallel systems through the per-worker accessor, laid out SoA for cache-friendly scans. ~40 ns/write.
//   • Versioned     (Wallet) — the ECONOMY: full MVCC + WAL. Snapshot-isolated, durable, transactional (rolls back
//     cleanly). Touched ONLY when an economic event fires (a trade or a reward), NEVER every tick. ~250 ns/write.
//   • Transient     (Intent) — per-tick AI scratch (the wander target); heap-only, dropped on restart by design.
//
// Note what is DELIBERATELY absent: no Versioned component in the tick loop (that is a ~6× write tax for data that
// needs neither isolation nor history), and no ComponentCollection / EntityLink indirection in a minimal seed. The
// index on Faction lives on a SingleVersion component — a secondary index does NOT require Versioned storage.
//
// This slice is standalone (it references no other sample type), so it compiles on its own. It is exactly the source
// `typhon new` emits and what the getting-started guide runs. The Full tier (Full/*.cs) adds the relational surface
// the harvesting/crafting economy needs — deposits, harvesters, factories, schematics, items — while the seed stays lean.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The galaxy's standing factions. <see cref="Faction.Value"/> is a plain indexed int, so a faction scan is an
/// index seek. <see cref="Hutt"/> stands in for the unaligned criminal underworld — the guide uses it to tag a single
/// tracked character that no shard-deployed being collides with.</summary>
public static class Factions
{
    /// <summary>Unaligned civilian — the default allegiance.</summary>
    public const int Neutral = 0;

    /// <summary>Rebel Alliance.</summary>
    public const int Rebel = 1;

    /// <summary>Galactic Empire.</summary>
    public const int Imperial = 2;

    /// <summary>Hutt cartel / underworld — neither Rebel nor Imperial.</summary>
    public const int Hutt = 3;
}

/// <summary>SingleVersion pose: the character's position and velocity on the planet. SingleVersion is the hot, durable,
/// no-isolation mode — a movement system integrates it lock-free through the per-worker accessor every tick, with no
/// MVCC revision history and no per-write WAL record (it recovers to the last tick fence). This is the default mode for
/// game state.</summary>
[Component("Swg.Shard.Transform", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Transform
{
    [Field] public Point2F Pos;
    [Field] public Point2F Vel;
}

/// <summary>SingleVersion spatial footprint — the R-Tree mirror of <see cref="Transform"/>'s position, used for
/// area-of-interest queries (<c>WhereNearby</c>): who is in radar range, who hears this shout. A spatial field must be
/// written through the spatial barrier (<c>WriteSpatial</c>), which is what keeps the index coherent after the character
/// moves.</summary>
[Component("Swg.Shard.Bounds", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Bounds
{
    [Field] [SpatialIndex(2.0f, Mode = SpatialMode.Dynamic)] public AABB2F Box;
}

/// <summary>SingleVersion HAM — Star Wars Galaxies' signature stat model: three parallel pools, Health (physical),
/// Action (stamina) and Mind (mental), each drained by combat or exertion and regenerated over time. Hot,
/// high-frequency, loss-tolerant — exactly what SingleVersion is for. Losing at most the last tick's regen to a crash
/// is acceptable, and that is what buys the ~40 ns in-place write instead of a Versioned revision. Three pools in one
/// component (rather than three components) keeps the regen system's SoA sweep to a single cluster slot.</summary>
[Component("Swg.Shard.Ham", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Ham
{
    [Field] public int Health;
    [Field] public int Action;
    [Field] public int Mind;
    [Field] public int MaxHealth;
    [Field] public int MaxAction;
    [Field] public int MaxMind;
}

/// <summary>SingleVersion faction allegiance (see <see cref="Factions"/>). <see cref="Value"/> is a non-unique index,
/// so "every Imperial on the planet" is an index scan — and it demonstrates that a secondary index works fine on a
/// SingleVersion component: indexing does NOT force Versioned storage. A standing allegiance changes rarely and never
/// needs MVCC history.</summary>
[Component("Swg.Shard.Faction", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Faction
{
    [Field] [Index(AllowMultiple = true)] public int Value;
}

/// <summary>Versioned wallet: the character's credits. This is the ONE component that earns MVCC — it is transactional
/// economy state, so reads see a consistent snapshot, writes are ACID and roll back cleanly, and it survives a crash
/// with zero loss. It is written only when an economic event fires (a trade or a reward), NEVER every tick — reaching
/// for Versioned on per-tick state is the classic mistake this sample is built to avoid. <see cref="Credits"/> is
/// deliberately NOT indexed: it changes constantly, and a secondary index on a churning field costs more to maintain
/// than a scan saves — so wealth queries are plain scans (<c>Where</c>). Contrast <see cref="Faction"/>, a stable
/// classification, which IS indexed. Index what's stable; scan what churns.</summary>
[Component("Swg.Shard.Wallet", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Wallet
{
    [Field] public long Credits;   // 8 bytes: currency wants the range, and a Versioned chunk segment needs >= 8 bytes anyway
}

/// <summary>Transient AI intent: where this character is currently headed. Transient components are heap-only (zero
/// page-cache footprint) and dropped on reopen by design — perfect for per-tick scratch that is recomputed each frame
/// and never needs to survive a restart. On reopen every character simply picks a new destination.</summary>
[Component("Swg.Shard.Intent", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct Intent
{
    [Field] public Point2F Target;
}

/// <summary>The one World-Shard archetype: a character — an AI-driven NPC here, the same shape a player character has —
/// combining a SingleVersion pose + spatial footprint + HAM pools + faction (the hot, parallel, SoA sim tier), a
/// Versioned wallet (the ACID economy), and a Transient wander intent. Having SingleVersion slots makes it
/// cluster-eligible, so the engine stores it SoA and the tick systems iterate it lock-free. Its durable identity is its
/// type name ("Character"); the engine assigns the catalog + routing ids automatically (feature #514 — no author-set id).</summary>
[Archetype]
public sealed partial class Character : Archetype<Character>
{
    public static readonly Comp<Transform> Transform = Register<Transform>();
    public static readonly Comp<Bounds> Bounds = Register<Bounds>();
    public static readonly Comp<Ham> Ham = Register<Ham>();
    public static readonly Comp<Faction> Faction = Register<Faction>();
    public static readonly Comp<Wallet> Wallet = Register<Wallet>();
    public static readonly Comp<Intent> Intent = Register<Intent>();
}
