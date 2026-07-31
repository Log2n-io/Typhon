namespace Typhon.Profiler;

/// <summary>
/// Describes an archetype registered with the engine's <c>ArchetypeRegistry</c>. Stored in a table near the start of a <c>.typhon-trace</c> file so
/// the viewer can map <c>ArchetypeId</c> numbers in typed events (<c>EcsSpawn</c>, <c>ClusterMigration</c>, etc.) back to human-readable names
/// without the wire format having to carry strings for every event.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>A trace carries two incompatible archetype id spaces, and nothing about their types distinguishes them.</b> <see cref="ArchetypeId"/> here — and in
/// every typed event — is the per-<i>process</i> catalog id: assigned in registration order, capped at 4095, and <b>never persisted</b>. The low 16 bits of
/// every <c>EntityId</c> in the same file are the per-<i>database</i> routing id, which <i>is</i> persisted. Both are <c>ushort</c>.
/// </para>
/// <para>
/// Comparing them directly (<c>entityId &amp; 0xFFFF == spawnEvent.ArchetypeId</c>, or joining a touch summary's archetype id against a routing id read from
/// the database) yields a plausible answer that is wrong for every archetype whose registration order differs from its persisted routing order — and it
/// <b>looks correct in a freshly-created database</b>, where the two coincide. That is a bug which passes every fixture and fails on real data.
/// </para>
/// <para>
/// <see cref="RoutingId"/> exists to close that gap: it is the bridge from the catalog id to the durable identity. Resolve through
/// <see cref="TraceArchetypeIdentity"/> rather than reading it directly — it is the one place that honours
/// <see cref="TraceHeaderFlags.MultipleEnginesObserved"/>. See claude/design/Apps/Workbench/10-database-and-profiles.md §5.3 and D-3.
/// </para>
/// </remarks>
public sealed class ArchetypeRecord
{
    /// <summary>Sentinel meaning "this trace does not know the routing id" — either no engine was attached, or D-9 degradation removed it.</summary>
    public const ushort UnknownRoutingId = 0xFFFF;

    /// <summary>Archetype ID — matches <c>ArchetypeMetadata.ArchetypeId</c> in the engine. Per-process, registration-ordered, never persisted.</summary>
    public ushort ArchetypeId { get; init; }

    /// <summary>Display name — typically the archetype class's <c>Type.Name</c>.</summary>
    public string Name { get; init; }

    /// <summary>
    /// The archetype's durable per-database routing id (<c>ArchetypeR1.RoutingId</c>) — the identity that persists across runs and that every
    /// <c>EntityId</c> embeds. <see cref="UnknownRoutingId"/> when unavailable: no engine attached, the archetype is unmapped in this database, or the capture
    /// observed more than one live engine and the value was withheld at close rather than written ambiguously (D-9).
    /// </summary>
    /// <remarks>Written once per capture, in this table only. Events are deliberately left alone — hot paths, many event kinds, no benefit over the table.</remarks>
    public ushort RoutingId { get; init; } = UnknownRoutingId;
}
