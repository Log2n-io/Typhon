using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Why a migration was queued — and therefore whether the throttle may refuse it (#872 step 11).
/// </summary>
/// <remarks>
/// <para><b>Carried, not inferred.</b> The obvious-looking test is <c>ClusterCellMap[SourceClusterChunkId] != DestCellKey</c> — a crossing changes cell,
/// a relocation does not. It reads state that mutates between the moment a request is filed and the moment the throttle classifies it: the repair planner
/// writes <c>ClusterCellMap</c> during the same Prep, and on a chunk id that may have been freed and RECYCLED into the destination cell. A stale crossing
/// whose source chunk was recycled that way reads as intra-cell and would be silently refused. It happens to be harmless today, because a recycled source
/// chunk implies the source slot is empty and <c>ExecuteMigrations</c>' stale-source guard skips the request anyway — but that safety is accidental, and a
/// classifier whose correctness rests on an accident is one refactor from being wrong.</para>
/// <para>A field settled at filing time is also what makes the throttle's partition ASSERTABLE: a test can count the classes, which an inferred
/// classification cannot offer.</para>
/// </remarks>
internal enum MigrationKind : byte
{
    /// <summary>
    /// The entity left its cell. A correctness move (§5.7): the throttle CHARGES its cost against the budget but must never refuse it.
    /// </summary>
    /// <remarks>
    /// Deliberately the zero value and the constructor's default. A producer that forgets to state its kind therefore files a request that is never
    /// dropped — the failure mode is doing too much work, not silently discarding a move the engine's correctness depends on.
    /// </remarks>
    CellCrossing = 0,

    /// <summary>An intra-cell drifter moving to the least-enlargement cluster (#872 step 10). Quality, not correctness — the throttle may refuse it.</summary>
    Relocation = 1,

    /// <summary>One entity of a repair unit's Morton re-pack (#872 step 12). Budgeted as a WHOLE UNIT at plan time, so the per-request throttle passes it
    /// through untouched — refusing half a re-pack is the one thing <c>RP-01</c> forbids.</summary>
    Repair = 2,
}

/// <summary>
/// A queued migration request: move the entity currently living at
/// <c>(SourceClusterChunkId, SourceSlotIndex)</c> into a cluster attached to <see cref="DestCellKey"/> — optionally
/// into one specific cluster, named by <see cref="DestClusterChunkId"/>, and optionally one specific slot in it.
/// </summary>
/// <remarks>
/// <para>Populated during cell-crossing detection inside <c>DatabaseEngine.DetectClusterMigrations</c>, by the outlier guard, by #872 step 10's intra-cell
/// drift detection and by step 12's repair planner; drained by <c>ArchetypeClusterState.ExecuteMigrations</c> at the tick fence.</para>
/// <para><b>20 bytes, five <see cref="int"/>s</b> — a 1K-entry queue is 20 KB. It was 12 bytes at <c>Pack = 4</c> before step 10 added the destination
/// cluster and step 12 the destination slot; five ints need no packing to sit end to end, and the natural alignment is what the sort and the slice scan
/// want. Step 11's <see cref="Kind"/> rides in the spare high bits of the source slot rather than adding a sixth field, which would have cost four bytes
/// after padding for two bits of information — see <see cref="SourceSlotIndex"/>.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct MigrationRequest
{
    /// <summary>"Any cluster in the destination cell will do" — the cell-crossing case, and the value every pre-step-10 caller means.</summary>
    public const int AnyCluster = -1;

    /// <summary>
    /// A relocation whose cell had candidates but no free slot left this pass (step 14's capacity ledger): the drain claims it in a cluster allocated
    /// for this cell during this Migrate slice, reused until full, rather than in whatever first-fit finds — which, once earlier drifters of the same
    /// cluster have drained, is the SOURCE cluster itself. The design's "allocate a new cluster if none qualifies", and step 17's split in embryo.
    /// </summary>
    public const int FreshCluster = -2;

    /// <summary>"Any free slot in the destination cluster will do" — what a step-10 relocation means, and the value every caller but repair passes.</summary>
    public const int AnySlot = -1;

    /// <summary>Bits of <see cref="_sourceSlotAndKind"/> holding the slot index. A cluster holds at most 64 slots, so six bits is the whole range.</summary>
    private const int SlotMask = 0x3F;

    /// <summary>Bit position of the two-bit <see cref="MigrationKind"/> inside <see cref="_sourceSlotAndKind"/>.</summary>
    private const int KindShift = 30;

    /// <summary>Cluster chunk id of the entity's current (pre-migration) slot.</summary>
    public readonly int SourceClusterChunkId;

    /// <summary>
    /// The source slot in bits 0-5 and the <see cref="MigrationKind"/> in bits 30-31.
    /// </summary>
    /// <remarks>
    /// Packed rather than given its own field because a cluster's capacity is 64 slots (<c>ArchetypeClusterInfo.FullMask</c>), so 26 of this int's bits
    /// were dead. A sixth field would have grown the struct to 24 bytes after alignment padding — a 20 % larger queue for two bits. The two accessors
    /// below are the only way in or out, so no consumer can read the raw value by accident.
    /// </remarks>
    private readonly int _sourceSlotAndKind;

    /// <summary>Target cell key the entity should land in after migration.</summary>
    public readonly int DestCellKey;

    /// <summary>
    /// The cluster to land in, or <see cref="AnyCluster"/> to let <c>ClaimSlotInCell</c> pick by first fit.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a cell key is not enough.</b> <c>ClaimSlotInCell</c> scans the destination cell's cluster list from a cursor and takes the first
    /// cluster with a free slot — it has no AABB awareness, and for an INTRA-cell relocation the source cluster is in that same list and usually has a
    /// free slot. So the claim would frequently hand the entity straight back to the cluster it was drifting out of. It cannot return the same SLOT (the
    /// source bit is still set), so nothing corrupts; the relocation is simply a no-op, and the least-enlargement choice that detection just computed is
    /// silently discarded. First-fit placement is the very thing #872 exists to repair, so leaving the destination to it would make step 10 a measurement
    /// of nothing.</para>
    /// <para><b>A pin is a preference, not a guarantee.</b> Between detection and the drain the pinned cluster can fill up, or be drained and freed.
    /// Execution falls back to <c>ClaimSlotInCell</c> in that case: a worse slot is a worse AABB, which costs selectivity, while failing the migration
    /// would strand the entity in a cluster it no longer belongs to.</para>
    /// </remarks>
    public readonly int DestClusterChunkId;

    /// <summary>
    /// The exact slot to land in within <see cref="DestClusterChunkId"/>, or <see cref="AnySlot"/> to take the cluster's first free slot.
    /// </summary>
    /// <remarks>
    /// <para><b>Only the repair path (#872 step 12) sets this.</b> A full cell re-sort computes the ENTIRE destination layout up front — sort order
    /// determines which cluster and which slot every entity lands in — so the placement is an output of the planner, not of the claim.</para>
    /// <para><b>What would have broken it is the SORT, not the slicing.</b> Before the Migrate phase dispatches, the drain prefix is ordered by
    /// <c>DestCellKey</c> alone — today by <c>ArchetypeClusterState.OrderDrainAndMeasureArrivals</c>, on both fences. Until #889 that was an
    /// <c>Array.Sort</c> — introsort, <b>unstable</b> — so every request a repair emits for one cell compared equal and the planner's emission order
    /// within that cell was permuted arbitrarily; first fit would then have assigned slots in the permuted order. Until #910 that sort ran only on the
    /// parallel path (<c>FenceExecSystem</c>), so the serial and parallel fences would have produced different packings from identical input — which is
    /// exactly what <c>AC-12.4</c> forbids. Pinning the slot made the packing independent of it.</para>
    /// <para><i>Slicing is NOT the reason, and an earlier version of this comment said it was.</i>
    /// <c>FenceWorkPlan.EmitMigrationApplyItems</c> advances each slice boundary until <c>DestCellKey</c> changes, so one cell's run is never split across
    /// workers and two workers can never claim into the same fresh cluster.</para>
    /// <para><b>#889 made the sort stable</b> (<c>ArchetypeClusterState.RadixSortByDestCellKey</c>), so the emission order now survives it and the pin is
    /// no longer what makes the packing deterministic. The field is kept for now: the fallback chain below is what every consumer is written against, and
    /// retiring it is a change to the planner's contract rather than to the sort. Whoever takes that on has this paragraph as the licence.</para>
    /// <para><b>Still a preference, like the cluster pin.</b> The planner allocates its fresh clusters during Prep and publishes them into
    /// <c>CellClusterPool</c> immediately, so a same-tick cell-crossing migration whose own pinned cluster is full can fall through to
    /// <c>ClaimSlotInCell</c>'s first fit and take a slot this plan reserved. A previously-queued request can also have emptied a source slot, in which case
    /// the repair request is skipped and leaves a hole. The fallback chain is exact slot → the pinned cluster's first free slot → <c>ClaimSlotInCell</c>,
    /// each step a worse layout and none of them wrong.</para>
    /// <para><b>Not the throttle's classifier.</b> <see cref="Kind"/> is, and it says so for a reason — see <see cref="MigrationKind"/>.</para>
    /// </remarks>
    public readonly int DestSlotIndex;

    public MigrationRequest(int sourceClusterChunkId, int sourceSlotIndex, int destCellKey, int destClusterChunkId = AnyCluster,
        int destSlotIndex = AnySlot, MigrationKind kind = MigrationKind.CellCrossing)
    {
        // Both fields are MASKED into place below, so an out-of-range argument does not throw — it silently becomes a different, valid-looking value. A
        // slot of 64 wraps to 0 and would migrate the wrong entity; a kind of 4 or more is shifted off the end and reads back as CellCrossing, which the
        // throttle then refuses to refuse. Every producer today derives the slot from a 64-bit occupancy scan, so these hold; they are asserted because
        // the next producer will not necessarily, and neither failure has a symptom near its cause.
        Debug.Assert((uint)sourceSlotIndex <= SlotMask, $"source slot {sourceSlotIndex} is outside a cluster's 64 slots and would wrap");
        Debug.Assert((uint)kind <= 3u, $"MigrationKind {kind} does not fit the two bits reserved for it");

        SourceClusterChunkId = sourceClusterChunkId;
        _sourceSlotAndKind = (sourceSlotIndex & SlotMask) | ((int)kind << KindShift);
        DestCellKey = destCellKey;
        DestClusterChunkId = destClusterChunkId;
        DestSlotIndex = destSlotIndex;
    }

    /// <summary>Slot index within <see cref="SourceClusterChunkId"/>.</summary>
    public int SourceSlotIndex => _sourceSlotAndKind & SlotMask;

    /// <summary>Why this request exists, and therefore whether the step-11 throttle may refuse it.</summary>
    public MigrationKind Kind => (MigrationKind)((uint)_sourceSlotAndKind >> KindShift);
}
