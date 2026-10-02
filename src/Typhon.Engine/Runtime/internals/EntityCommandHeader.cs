using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Why an entity command was not queued (#1099). Each value is logged at most once per archetype per tick, and each is counted exactly.
/// </summary>
/// <remarks>
/// The split between the two groups is the one an application acts on. A REJECTION is an impossible request — the archetype is not registered, the realm
/// cannot hold the entity — and is almost always a bug to fix. An OVERFLOW is the engine out of room, which is a budget to raise. Collapsing them into one
/// counter would make a tick that lost commands indistinguishable from a tick that was asked for nonsense.
/// </remarks>
internal enum EntityCommandRefusal
{
    /// <summary>The archetype is not registered with this database, or has no entity map — nothing to spawn into. A rejection.</summary>
    ArchetypeNotRegistered = 0,

    /// <summary>The realm the values name is unregistered, closing, or cannot hold this archetype. A rejection.</summary>
    RealmRefused = 1,

    /// <summary>More values than one command may carry, or a destination span shorter than the requested run. A rejection.</summary>
    BadArguments = 2,

    /// <summary>This slot's header or payload pool is at its growth ceiling. An overflow.</summary>
    SegmentFull = 3,

    /// <summary>
    /// The archetype used its key-block generations for this tick, or the producer's chunk index fell outside the stride. An overflow — the engine
    /// declining, not the caller erring.
    /// </summary>
    KeyBlocksExhausted = 4,
}

/// <summary>What one entry in a worker's entity-command segment asks the apply phase to do (#1099).</summary>
internal enum EntityCommandKind : byte
{
    /// <summary>Unused slot. Never written; a zeroed header reads as this so a bug that skips the kind is visible rather than silently a spawn.</summary>
    None = 0,

    /// <summary>One entity, one value set. <c>Count</c> is 1.</summary>
    Spawn = 1,

    /// <summary>
    /// <c>Count</c> entities sharing one value set — one header and one payload copy for the whole run, which is the reason this kind exists separately
    /// from <c>Count</c> repetitions of <see cref="Spawn"/>.
    /// </summary>
    SpawnMany = 2,

    /// <summary>One destroy. <c>Count</c> is 0 and <c>PayloadOffset</c> is -1; the target is packed into <c>EntityKey</c>.</summary>
    Destroy = 3,
}

/// <summary>
/// One queued entity command, as the write side records it (#1099). Structure-of-arrays against the <c>ComponentValue</c> payload pool: the apply's
/// grouping pass walks headers only and never pulls a 128-byte payload into cache merely to sort it.
/// </summary>
/// <remarks>
/// <para>
/// <b>24 bytes, not the 16 the ENG-01 design specifies, and the reason is the Q1 decision taken the same day.</b> Once <c>Spawn</c> returns a real final
/// <see cref="EntityId"/> rather than a placeholder, the key it returned has to survive to the apply, and that is 8 bytes the 16-byte layout had no room
/// for. The alternative — recomputing the key at apply time from (archetype, tick, producer, ordinal) — was rejected: it is derivable, but it makes the
/// apply re-derive per-archetype ordinals that the write side already knew, and a defect there would hand out an id different from the one the caller was
/// given, silently. Eight bytes is the cheaper side of that trade. Three headers still share a cache line, and the SoA property the 16-byte figure was
/// chosen for is unaffected.
/// </para>
/// <para>
/// Explicit layout with an explicit size, so the width is a declared fact rather than whatever the field order happened to pack to.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct EntityCommandHeader
{
    /// <summary>Which operation this entry is.</summary>
    [FieldOffset(0)]
    public EntityCommandKind Kind;

    /// <summary>
    /// How many <c>ComponentValue</c>s this command's payload holds, starting at <see cref="PayloadOffset"/>. A byte because an archetype's component
    /// count is far below 255; the write side refuses a longer value span rather than truncating.
    /// </summary>
    [FieldOffset(1)]
    public byte ValueCount;

    /// <summary>The per-database archetype ROUTING id — what <see cref="EntityId.ArchetypeId"/> carries, so the apply needs no second lookup to rebuild one.</summary>
    [FieldOffset(2)]
    public ushort ArchetypeId;

    /// <summary>Entities this command creates: 1 for <see cref="EntityCommandKind.Spawn"/>, N for <see cref="EntityCommandKind.SpawnMany"/>, 0 for a destroy.</summary>
    [FieldOffset(4)]
    public int Count;

    /// <summary>Index of the first payload value in this slot's <c>ComponentValue</c> pool, or -1 when the command carries none.</summary>
    [FieldOffset(8)]
    public int PayloadOffset;

    /// <summary>
    /// The archetype's internal id, as distinct from the routing id in <see cref="ArchetypeId"/>. Both are carried because the apply needs the internal
    /// one to reach <c>_archetypeStates</c> and the routing one to rebuild the ids, and resolving either from the other costs a lookup per command.
    /// </summary>
    [FieldOffset(12)]
    public int InternalArchetypeId;

    /// <summary>
    /// For a spawn: the FIRST entity key this command was assigned; the run is <c>[EntityKey, EntityKey + Count)</c>. For a destroy: the raw packed
    /// <see cref="EntityId"/> of the target.
    /// </summary>
    [FieldOffset(16)]
    public long EntityKey;
}
