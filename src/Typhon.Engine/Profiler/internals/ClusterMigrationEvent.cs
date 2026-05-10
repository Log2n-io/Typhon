// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.ClusterMigration"/>. Two required fields, no optionals.
/// </summary>
[TraceEvent(TraceEventKind.ClusterMigration, EmitEncoder = true)]
internal ref partial struct ClusterMigrationEvent
{
    [BeginParam]
    public ushort ArchetypeId;
    [BeginParam]
    public int MigrationCount;
    /// <summary>
    /// Total component instances moved across this batch — set by the producer to <c>MigrationCount × archetype.componentCount</c>.
    /// Lets the viewer report data-movement cost (vs. just entity count). Optional at producer site; left at 0 when unset.
    /// </summary>
    [BeginParam]
    public int ComponentCount;

}

