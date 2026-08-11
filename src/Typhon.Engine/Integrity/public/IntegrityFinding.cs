using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// How bad a finding is. Ordered most-severe first so a numeric comparison ranks findings.
/// </summary>
[PublicAPI]
public enum IntegritySeverity
{
    /// <summary>The database cannot be safely opened or traversed. Scanning may have stopped early.</summary>
    Fatal = 0,

    /// <summary>Primary data is unreadable. Something the user stored is gone and cannot be regenerated from within the database.</summary>
    DataLoss = 1,

    /// <summary>A derived structure disagrees with primary data. Repairable by regeneration; no user data is lost.</summary>
    Divergence = 2,

    /// <summary>Space is allocated but unreachable. Wastes capacity; costs no correctness.</summary>
    Leak = 3,

    /// <summary>Informational — a condition worth surfacing that is not itself a defect.</summary>
    Advisory = 4
}

/// <summary>
/// Whether a finding was observed on a source that could not have been mutating underneath the scan.
/// </summary>
/// <remarks>
/// A live scan sees a consistent <i>page</i>, never a consistent <i>database</i>, so a cross-structure disagreement observed
/// online may simply be a mutation in flight. Repair acts only on <see cref="Confirmed"/> findings — collapsing these two
/// into one boolean is how a tool ends up repairing a database that was healthy.
/// </remarks>
[PublicAPI]
public enum IntegrityConfidence
{
    /// <summary>Observed on a quiescent source (offline, or online behind a tick fence and checkpoint barrier).</summary>
    Confirmed = 0,

    /// <summary>Observed on a live, mutating source. Never sufficient to justify a repair — re-run offline to confirm.</summary>
    Suspected = 1
}

/// <summary>What, if anything, can be done about a finding from within the database.</summary>
[PublicAPI]
public enum Repairability
{
    /// <summary>Regenerable from primary data. Loss is zero by construction.</summary>
    Lossless = 0,

    /// <summary>Repairable only by excising a reference to data that is already unreadable. Requires explicit consent.</summary>
    Lossy = 1,

    /// <summary>No repair primitive applies. The report is the deliverable; escalation is a restore.</summary>
    NotRepairable = 2
}

/// <summary>The unit in which a repair would lose something.</summary>
[PublicAPI]
public enum LossKind
{
    /// <summary>Nothing is lost.</summary>
    None = 0,

    /// <summary>Component values are lost; the owning entities survive with their other components.</summary>
    Values = 1,

    /// <summary>Whole entities are lost.</summary>
    Entities = 2,

    /// <summary>Elements of a component collection are lost.</summary>
    Collection = 3,

    /// <summary>Interned string payloads are lost.</summary>
    Strings = 4,

    /// <summary>Something is lost but the scan cannot characterise it. Always paired with a bound.</summary>
    Unknown = 5
}

/// <summary>
/// Where a finding is, in every addressing scheme that applies to it.
/// </summary>
/// <remarks>
/// Deliberately over-specified. A page index is what a storage engineer needs; an archetype and entity id is what the
/// application owner needs; the Workbench File Map addresses cells by physical page index. Reporting only one forces every
/// consumer to re-derive the others — and re-derivation on a <i>damaged</i> database is exactly what cannot be trusted.
/// </remarks>
[PublicAPI]
public readonly struct Locus : IEquatable<Locus>
{
    /// <summary>Sentinel for an addressing component that does not apply to a finding.</summary>
    public const int None = -1;

    /// <summary>
    /// A locus that names the database as a whole. Use this rather than <c>default</c>: a default-initialised struct
    /// reads every component as <c>0</c>, which renders as "page 0" and points a reader at the meta page.
    /// </summary>
    public static Locus Database => new(None, None, StorageSegmentKind.Other, null, null, 0, None);

    /// <summary>Creates a locus. Pass <see cref="None"/> / <c>null</c> / <c>0</c> for components that do not apply.</summary>
    /// <param name="filePageIndex">Physical file-page index, or <see cref="None"/>.</param>
    /// <param name="segmentRootPage">Root page of the owning logical segment, or <see cref="None"/>.</param>
    /// <param name="kind">Kind of the owning segment.</param>
    /// <param name="archetypeName">Schema-level archetype name when resolvable.</param>
    /// <param name="componentName">Schema-level component name when resolvable.</param>
    /// <param name="chunkId">Chunk id within the owning chunk-based segment, or <c>0</c>.</param>
    /// <param name="slot">Slot index within a cluster chunk, or <see cref="None"/>.</param>
    /// <param name="entityId">Raw entity id when the finding is entity-scoped, or <c>0</c>.</param>
    public Locus(int filePageIndex = None, int segmentRootPage = None, StorageSegmentKind kind = StorageSegmentKind.Other,
        string archetypeName = null, string componentName = null, long chunkId = 0, int slot = None, ulong entityId = 0)
    {
        FilePageIndex = filePageIndex;
        SegmentRootPage = segmentRootPage;
        Kind = kind;
        ArchetypeName = archetypeName;
        ComponentName = componentName;
        ChunkId = chunkId;
        Slot = slot;
        EntityId = entityId;
    }

    /// <summary>Physical file-page index, or <see cref="None"/> when the finding is not page-scoped.</summary>
    public int FilePageIndex { get; }

    /// <summary>Root page of the owning logical segment, or <see cref="None"/>.</summary>
    public int SegmentRootPage { get; }

    /// <summary>Kind of the owning logical segment.</summary>
    public StorageSegmentKind Kind { get; }

    /// <summary>Schema-level archetype name, when the scan could resolve one.</summary>
    public string ArchetypeName { get; }

    /// <summary>Schema-level component name, when the scan could resolve one.</summary>
    public string ComponentName { get; }

    /// <summary>Chunk id within the owning chunk-based segment, or <c>0</c>.</summary>
    public long ChunkId { get; }

    /// <summary>Slot index within a cluster chunk, or <see cref="None"/>.</summary>
    public int Slot { get; }

    /// <summary>Raw entity id when the finding is entity-scoped, or <c>0</c>.</summary>
    public ulong EntityId { get; }

    /// <summary>Renders the locus as a compact, human-readable path — only the components that apply.</summary>
    public override string ToString()
    {
        var parts = new List<string>(6);
        if (Kind != StorageSegmentKind.Other)
        {
            parts.Add(ArchetypeName != null ? $"{Kind}/{ArchetypeName}" : Kind.ToString());
        }
        else if (ArchetypeName != null)
        {
            parts.Add(ArchetypeName);
        }

        if (ComponentName != null)
        {
            parts.Add($".{ComponentName}");
        }

        if (FilePageIndex != None)
        {
            parts.Add($"page {FilePageIndex}");
        }

        if (ChunkId != 0)
        {
            parts.Add($"chunk {ChunkId}");
        }

        if (Slot != None)
        {
            parts.Add($"slot {Slot}");
        }

        if (EntityId != 0)
        {
            parts.Add($"entity {EntityId}");
        }

        return parts.Count == 0 ? "database" : string.Join(" ", parts);
    }

    /// <inheritdoc />
    public bool Equals(Locus other) =>
        FilePageIndex == other.FilePageIndex && SegmentRootPage == other.SegmentRootPage && Kind == other.Kind
        && ArchetypeName == other.ArchetypeName && ComponentName == other.ComponentName && ChunkId == other.ChunkId
        && Slot == other.Slot && EntityId == other.EntityId;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is Locus other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(FilePageIndex, SegmentRootPage, (int)Kind, ArchetypeName, ComponentName, ChunkId, Slot);

    /// <summary>Equality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator ==(Locus left, Locus right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator !=(Locus left, Locus right) => !left.Equals(right);
}

/// <summary>
/// What a repair would cost, in user terms.
/// </summary>
/// <remarks>
/// <para>Three rules govern this type, and each exists because its violation is a specific way of lying:</para>
/// <list type="number">
/// <item><description>
/// <b>Never report <c>0</c> when the answer is unknown.</b> <see cref="LossKind.Unknown"/> with a bound is honest; zero is
/// a claim. Use <see cref="BoundedMin"/>/<see cref="BoundedMax"/> and leave <see cref="EntityCount"/> at <c>-1</c>.
/// </description></item>
/// <item><description>
/// <b>Enumerate when enumeration is possible.</b> A count of zero and a count that was never taken must not be
/// indistinguishable.
/// </description></item>
/// <item><description>
/// <b>Loss is measured in user terms, not storage terms.</b> "Page 41,208 is unrecoverable" is not a loss report;
/// "12 <c>Player</c> entities lose their <c>Inventory</c> values, the entities survive" is.
/// </description></item>
/// </list>
/// </remarks>
[PublicAPI]
public sealed class LossEstimate
{
    /// <summary>A shared, allocation-free "nothing is lost" instance.</summary>
    public static readonly LossEstimate None = new() { Kind = LossKind.None, EntityCount = 0, Explanation = "No data is lost." };

    /// <summary>What unit the loss is measured in.</summary>
    public LossKind Kind { get; init; }

    /// <summary>Exact number of entities affected when enumerable; <c>-1</c> when only a bound is known.</summary>
    public long EntityCount { get; init; } = -1;

    /// <summary>Lower bound on affected entities when <see cref="EntityCount"/> is <c>-1</c>.</summary>
    public long BoundedMin { get; init; }

    /// <summary>Upper bound on affected entities when <see cref="EntityCount"/> is <c>-1</c>.</summary>
    public long BoundedMax { get; init; }

    /// <summary>
    /// Affected entity ids, capped for report size. The complete set goes to the loss manifest file, never here.
    /// </summary>
    public IReadOnlyList<ulong> Sample { get; init; } = [];

    /// <summary>Archetype the loss falls in, when resolvable.</summary>
    public string Archetype { get; init; }

    /// <summary>Component the loss falls in, when resolvable.</summary>
    public string Component { get; init; }

    /// <summary>Plain-English statement of what the user no longer has.</summary>
    public string Explanation { get; init; } = "";

    /// <summary>Whether this estimate represents any loss at all.</summary>
    public bool IsNone => Kind == LossKind.None;

    /// <summary>Renders the count as text: an exact number, or an honest range.</summary>
    public string CountText => EntityCount >= 0 ? EntityCount.ToString("N0") : $"{BoundedMin:N0}–{BoundedMax:N0}";
}

/// <summary>
/// One thing that is wrong with a database, with everything needed to judge it: what rule it violates, where it is, how
/// confident the scan is, and what repairing it would cost.
/// </summary>
[PublicAPI]
public sealed class IntegrityFinding
{
    /// <summary>
    /// Stable check code, e.g. <c>"CHK-IDX-04"</c>. <b>A finding code is an API</b> — renaming one breaks somebody's alert.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>How bad this is.</summary>
    public required IntegritySeverity Severity { get; init; }

    /// <summary>Whether the source was quiescent when this was observed.</summary>
    public IntegrityConfidence Confidence { get; init; } = IntegrityConfidence.Confirmed;

    /// <summary>Where the problem is.</summary>
    public Locus Locus { get; init; }

    /// <summary>One sentence, no jargon — what is wrong.</summary>
    public required string Summary { get; init; }

    /// <summary>The evidence: expected versus found, with the numbers that support the conclusion.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Identifier of the <c>rules/</c> invariant this violates, e.g. <c>"RB-03"</c>. Empty only for structural pre-checks.</summary>
    public string RuleId { get; init; } = "";

    /// <summary>What can be done about it.</summary>
    public Repairability Repair { get; init; } = Repairability.NotRepairable;

    /// <summary>What repairing it would cost. Never <c>null</c> — use <see cref="LossEstimate.None"/> for lossless findings.</summary>
    public LossEstimate Loss { get; init; } = LossEstimate.None;

    /// <summary>Number of occurrences this finding aggregates, when the scan collapsed a repeated condition. <c>1</c> otherwise.</summary>
    public long Occurrences { get; init; } = 1;

    /// <inheritdoc />
    public override string ToString() => $"{Code} [{Severity}] {Locus}: {Summary}";
}
