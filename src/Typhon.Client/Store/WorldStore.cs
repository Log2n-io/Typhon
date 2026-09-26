using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// The client's replica of what the server has shown it: one <see cref="ArchetypeStore"/> per archetype, a netId map, the controlled entity's owner
/// state, aggregate grids, metric values and the frame's acknowledgements.
/// </summary>
/// <remarks>
/// <para>
/// Frames are applied strictly in order, one at a time (05-sdks § 1), by <see cref="FrameApplier"/>. Nothing asynchronous may reorder them: updates do not
/// commute.
/// </para>
/// <para>
/// <b>Protocol inconsistencies are absorbed, and counted.</b> A leave, segment or state record for a netId the store does not hold, or an enter for one it
/// already holds under another archetype, is a server bug or a lost frame — never a reason to throw away the whole replica. <see cref="Anomalies"/> counts
/// them so a test or a monitor can assert there were none.
/// </para>
/// </remarks>
public sealed class WorldStore
{
    private const int SlotBits = 24;
    private const int SlotMask = (1 << SlotBits) - 1;

    private int[] _handles = [];

    /// <summary>Creates a store for a compiled catalog.</summary>
    /// <param name="plan">The session's compiled catalog.</param>
    /// <param name="maxNetId">
    /// Largest netId accepted. netIds are dense on the server, so the map is a flat array of at most this many entries; a corrupt id beyond it counts as an
    /// anomaly instead of allocating.
    /// </param>
    /// <param name="maxRenderDelayMs">
    /// The largest render delay motion will be evaluated at, which sizes every archetype's segment ring against the catalog's tick period (see
    /// <see cref="SegmentRing.DepthFor"/>). The default is the <see cref="Clock"/>'s own default ceiling; pass the clock's <see cref="Clock.MaxDelayMs"/> when
    /// it was built with another one.
    /// </param>
    public WorldStore(CatalogPlan plan, uint maxNetId = 1 << 22, double maxRenderDelayMs = SegmentRing.DefaultMaxRenderDelayMs)
        : this(plan, 0, maxNetId, maxRenderDelayMs)
    {
    }

    /// <summary>Creates a store with an explicit motion history depth; <c>0</c> sizes it from <paramref name="maxRenderDelayMs"/>.</summary>
    /// <param name="plan">The compiled catalog.</param>
    /// <param name="segmentHistory">Segments kept per entity, 1..<see cref="SegmentRing.MaxDepth"/>, or 0 for the render-delay default.</param>
    /// <param name="maxNetId">The largest netId the map accepts.</param>
    /// <param name="maxRenderDelayMs">The largest render delay motion will be evaluated at, when <paramref name="segmentHistory"/> is 0.</param>
    public WorldStore(CatalogPlan plan, int segmentHistory, uint maxNetId = 1 << 22, double maxRenderDelayMs = SegmentRing.DefaultMaxRenderDelayMs)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
        MaxNetId = maxNetId;
        SegmentHistory = segmentHistory > 0 ? segmentHistory : SegmentRing.DepthFor(plan.Catalog.Tick.PeriodUs, maxRenderDelayMs);
        Archetypes = new ArchetypeStore[plan.Archetypes.Length];
        for (var i = 0; i < Archetypes.Length; i++)
        {
            Archetypes[i] = new ArchetypeStore(plan.Archetypes[i], SegmentHistory);
        }

        Self = new SelfState();
        Aggregates = new AggregateGrid[plan.Grids.Length];
        for (var i = 0; i < Aggregates.Length; i++)
        {
            Aggregates[i] = new AggregateGrid(plan.Grids[i]);
        }

        ServerMetricValues = new double[plan.ServerMetrics.Length][];
        for (var i = 0; i < ServerMetricValues.Length; i++)
        {
            ServerMetricValues[i] = new double[plan.ServerMetrics[i].ValueCount];
        }

        SessionMetricValues = new double[plan.SessionMetrics.Length][];
        for (var i = 0; i < SessionMetricValues.Length; i++)
        {
            SessionMetricValues[i] = new double[plan.SessionMetrics[i].ValueCount];
        }
    }

    /// <summary>The compiled catalog.</summary>
    public CatalogPlan Plan { get; }

    /// <summary>The largest netId the map accepts.</summary>
    public uint MaxNetId { get; }

    /// <summary>Segments every archetype's ring keeps per slot, from the catalog's tick period and the render delay the store was sized for.</summary>
    public int SegmentHistory { get; }

    /// <summary>The stores, by archetype index.</summary>
    public ArchetypeStore[] Archetypes { get; }

    /// <summary>The controlled entity's owner state, from <c>SELF</c>.</summary>
    public SelfState Self { get; }

    /// <summary>The aggregate grids, by grid index.</summary>
    public AggregateGrid[] Aggregates { get; }

    /// <summary>
    /// The realm the session is in (<c>typhon.3</c>): the frame every position this store holds was decoded over, or <see langword="null"/> before the
    /// first <c>REALM</c> and after a <c>REALM(NONE)</c>.
    /// </summary>
    public RealmFrame Realm { get; private set; }

    /// <summary>The name of <see cref="Realm"/>'s kind, from the catalog's <c>realmKinds</c>; <see langword="null"/> in no realm.</summary>
    public string RealmKind => Realm == null ? null : Plan.RealmKinds[Realm.KindIdx];

    /// <summary>
    /// Raised when a <c>REALM</c> block changes the session's realm: the previous frame (or <see langword="null"/>) and the new one (or
    /// <see langword="null"/>). Raised once, after the <c>RESET</c> that carried it cleared the store and before any of the frame's records apply.
    /// </summary>
    public event Action<RealmFrame, RealmFrame> RealmChanged;

    /// <summary>The latest server-scope metric values, by <see cref="CatalogPlan.ServerMetrics"/> position.</summary>
    public double[][] ServerMetricValues { get; }

    /// <summary>The latest session-scope metric values, by <see cref="CatalogPlan.SessionMetrics"/> position.</summary>
    public double[][] SessionMetricValues { get; }

    /// <summary>This frame's command rejections.</summary>
    public List<(ushort Seq, byte Reason)> Acks { get; } = [];

    /// <summary>This frame's source lifecycle entries.</summary>
    public List<(ushort RequestId, byte Status, ushort Code)> Sources { get; } = [];

    /// <summary>The tick of the frame most recently begun, or −1.</summary>
    public long Tick { get; internal set; } = -1;

    /// <summary>The flags of the frame most recently begun.</summary>
    public TickFlags Flags { get; internal set; }

    /// <summary>The elapsed interval's period reported by the frame most recently begun, or 0.</summary>
    public uint PeriodUs { get; internal set; }

    /// <summary>Frames applied so far.</summary>
    public long Frames { get; internal set; }

    /// <summary>Protocol inconsistencies absorbed instead of thrown.</summary>
    public long Anomalies { get; internal set; }

    /// <summary>
    /// Frames that carried a <c>STATS</c> block, which is how a client knows whether the server is still feeding it statistics.
    /// </summary>
    /// <remarks>
    /// A block is emitted on a fixed cadence and rides on one frame; it is never retried. So "the numbers stopped changing" and "the numbers are changing
    /// slowly" look identical from the values alone, and only a count of the blocks themselves tells them apart. That distinction is the whole reason this
    /// counter exists: it was added after a 110-session run where the metric values sat still and there was no way to say whether the server had stopped
    /// sending or had stopped moving.
    /// </remarks>
    public long StatsBlocks { get; internal set; }

    /// <summary>
    /// Entity records applied — enters, motion segments, state records and leaves — across every frame.
    /// </summary>
    /// <remarks>
    /// Divided by frames and by the archetype's live count, this is the fraction of a watched world that actually CHANGES in a tick. That fraction is what
    /// decides whether a per-session walk over everything watched is wasted work or the only honest way to find the changes, so it is worth a counter rather
    /// than an inference from frame sizes.
    /// </remarks>
    public long Records { get; internal set; }

    /// <summary>Finds where an entity lives.</summary>
    /// <param name="netId">The entity.</param>
    /// <param name="archetype">Its archetype index.</param>
    /// <param name="slot">Its slot.</param>
    /// <returns><see langword="true"/> when the store holds it.</returns>
    public bool TryLocate(uint netId, out int archetype, out int slot)
    {
        if (netId < (uint)_handles.Length && _handles[netId] != 0)
        {
            var handle = _handles[netId] - 1;
            archetype = handle >>> SlotBits;
            slot = handle & SlotMask;
            return true;
        }

        archetype = -1;
        slot = -1;
        return false;
    }

    internal bool TryMap(uint netId, int archetype, int slot)
    {
        if (netId == 0 || netId > MaxNetId)
        {
            return false;
        }

        if (netId >= (uint)_handles.Length)
        {
            var length = Math.Max(1024, _handles.Length);
            while (length <= netId)
            {
                length *= 2;
            }

            var grown = new int[Math.Min(length, (int)Math.Min(MaxNetId + 1L, int.MaxValue))];
            _handles.AsSpan().CopyTo(grown);
            _handles = grown;
        }

        _handles[netId] = ((archetype << SlotBits) | slot) + 1;
        return true;
    }

    internal void Unmap(uint netId)
    {
        if (netId < (uint)_handles.Length)
        {
            _handles[netId] = 0;
        }
    }

    internal void BeginFrame()
    {
        foreach (var a in Archetypes)
        {
            a.BeginFrame();
        }

        foreach (var g in Aggregates)
        {
            g.BeginFrame();
        }

        Self.BeginFrame();
        Acks.Clear();
        Sources.Clear();
    }

    /// <summary>Adopts a <c>REALM</c> block's frame: every aggregate grid is re-laid over it, and <see cref="RealmChanged"/> fires when it changed.</summary>
    internal void SetRealm(RealmFrame frame)
    {
        var previous = Realm;
        Realm = frame;
        foreach (var grid in Aggregates)
        {
            grid.Frame(frame);
        }

        if (!Equals(previous, frame))
        {
            RealmChanged?.Invoke(previous, frame);
        }
    }

    internal void Reset()
    {
        foreach (var a in Archetypes)
        {
            a.Clear();
        }

        Array.Clear(_handles);
        foreach (var g in Aggregates)
        {
            g.Clear();
        }

        Self.Clear();
    }
}

/// <summary>The controlled entity's owner-only state (W17), accumulated across <c>SELF</c> blocks.</summary>
public sealed class SelfState
{
    /// <summary>The controlled entity's archetype, or <see langword="null"/> before the first <c>SELF</c> and while the session controls none.</summary>
    public ArchetypePlan Archetype { get; private set; }

    /// <summary>The controlled entity; 0 for none (W17′).</summary>
    public uint NetId { get; private set; }

    /// <summary>The highest command sequence the server drained into a tick at or before the latest frame.</summary>
    public ushort LastSeq { get; private set; }

    /// <summary>Whether this frame carried a <c>SELF</c> block.</summary>
    public bool Received { get; private set; }

    /// <summary>The owner groups carried by this frame's <c>SELF</c>.</summary>
    public byte OwnerMask { get; private set; }

    /// <summary>Owner field numbers by <see cref="FieldPlan.Ordinal"/>; <see langword="null"/> until received or when not numeric.</summary>
    public double[][] Numbers { get; private set; } = [];

    /// <summary>Owner field texts by ordinal.</summary>
    public string[] Texts { get; private set; } = [];

    /// <summary>Owner field bytes by ordinal.</summary>
    public byte[][] Bytes { get; private set; } = [];

    internal void Receive(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
    {
        // Owner values belong to one entity (W17): a control change starts from nothing, and SUB-11 resends every owner group in its frame. netId 0 is
        // no entity at all (W17′): the owner state is dropped and only lastSeq is kept.
        if (archetype == null)
        {
            Archetype = null;
            Numbers = [];
            Texts = [];
            Bytes = [];
        }
        else if (Archetype != archetype || NetId != netId)
        {
            Archetype = archetype;
            Numbers = new double[archetype.OwnerFields.Length][];
            Texts = new string[archetype.OwnerFields.Length];
            Bytes = new byte[archetype.OwnerFields.Length][];
        }

        NetId = netId;
        LastSeq = lastSeq;
        OwnerMask = ownerMask;
        Received = true;
    }

    internal void BeginFrame()
    {
        Received = false;
        OwnerMask = 0;
    }

    internal void Clear()
    {
        Archetype = null;
        NetId = 0;
        LastSeq = 0;
        Numbers = [];
        Texts = [];
        Bytes = [];
        Received = false;
        OwnerMask = 0;
    }
}

/// <summary>Per-cell counts per archetype for one grid (<c>AGG</c>), with the cells changed this frame.</summary>
public sealed class AggregateGrid
{
    private const long MaxCells = 1 << 24;

    internal AggregateGrid(CatalogGrid grid)
    {
        Grid = grid;
        ArchetypeCount = grid.Archetypes?.Length ?? 0;
    }

    /// <summary>Lays the grid over a realm's frame (<c>typhon.3</c>): origin and dimensions are the frame's, the tile <c>tileCells × cellM</c>.</summary>
    internal void Frame(RealmFrame frame)
    {
        Origin = frame == null ? [0d, 0d, 0d] : [frame.Min[0], frame.Min[1], frame.Min[2]];
        TileM = frame == null ? 0d : Grid.TileCells * frame.CellM;
        Dims = frame == null ? [0, 0, 0] : [frame.AggregateDim(0, Grid.TileCells), frame.AggregateDim(1, Grid.TileCells), frame.AggregateDim(2, Grid.TileCells)];
        var cells = frame == null ? 0 : Math.Min(frame.AggregateCellCount(Grid.TileCells), MaxCells);
        if (cells != CellCount)
        {
            CellCount = (int)cells;
            Counts = [];
        }
        else
        {
            Array.Clear(Counts);
        }

        Changed.Clear();
    }

    /// <summary>The grid.</summary>
    public CatalogGrid Grid { get; }

    /// <summary>Counts per cell.</summary>
    public int ArchetypeCount { get; }

    /// <summary>Cells in the grid over the session's realm, capped at 2²⁴; 0 in no realm.</summary>
    public int CellCount { get; private set; }

    /// <summary>The grid's origin over the session's realm: the frame's lower bounds, three axes.</summary>
    public double[] Origin { get; private set; } = [0d, 0d, 0d];

    /// <summary>The tile's side over the session's realm, in metres.</summary>
    public double TileM { get; private set; }

    /// <summary>Cells per axis over the session's realm, three axes (1 on z in a flat realm).</summary>
    public int[] Dims { get; private set; } = [0, 0, 0];

    /// <summary><c>CellCount × ArchetypeCount</c> counts, allocated on the first <c>AGG</c> for this grid.</summary>
    public uint[] Counts { get; private set; } = [];

    /// <summary>Cells changed this frame.</summary>
    public List<uint> Changed { get; } = [];

    internal void BeginFrame() => Changed.Clear();

    internal void Clear()
    {
        Array.Clear(Counts);
        Changed.Clear();
    }

    internal void Set(uint cell, ReadOnlySpan<uint> counts)
    {
        if (Counts.Length == 0)
        {
            Counts = new uint[(long)CellCount * ArchetypeCount];
        }

        counts.CopyTo(Counts.AsSpan((int)cell * ArchetypeCount, ArchetypeCount));
        Changed.Add(cell);
    }
}
