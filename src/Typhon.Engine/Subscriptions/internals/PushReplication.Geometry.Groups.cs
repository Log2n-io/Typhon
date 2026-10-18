using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>One member event filed under a (set, key) in a tick's member log (14 § 3.8).</summary>
internal struct MemberEntry
{
    public int Set;
    public long Key;

    /// <summary>The event's index in its tick's <c>Events</c>.</summary>
    public int Event;

    /// <summary>The member's key in this set before the event, and after it: 0 is no group.</summary>
    public long Old;

    public long New;
}

/// <summary>Orders member entries by (set, key), then by event — the order a session's binary search over a held key relies on.</summary>
internal readonly struct MemberEntryOrder : IComparer<MemberEntry>
{
    public int Compare(MemberEntry x, MemberEntry y)
    {
        var c = x.Set.CompareTo(y.Set);
        if (c != 0)
        {
            return c;
        }

        c = x.Key.CompareTo(y.Key);
        return c != 0 ? c : x.Event.CompareTo(y.Event);
    }
}

// Group sets (14 § 3, § 3.8), the generic half: the member log, the region table and its index, and a Sphere session's group gather.
internal sealed unsafe partial class PushReplication<TEvent>
{
    // ══ The member log ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Files this tick's member events under (set, key): every event of a member archetype under its new key, and under its old one when it changed. Serial,
    /// at the index's finish, over the primaries only — a mover's secondary is the same event.
    /// </summary>
    private void BuildMemberLog(TickLog slot)
    {
        slot.MemberCount = 0;
        RefreshRegions();
        for (var k = 0; k < slot.CellCount; k++)
        {
            for (var i = slot.Starts[k]; i < slot.PrimaryEnds[k]; i++)
            {
                ref var e = ref slot.Events[i];
                var keys = _plans[e.Archetype].GroupKeys;
                for (var g = 0; g < keys.Length; g++)
                {
                    ref readonly var gk = ref keys[g];
                    long ko, kn;
                    if ((e.Flags & PushEvent.KeyChanged) != 0 && _keyChangeMap.TryGetValue(KeyChangeId(e.NetId, gk.Set), out var change))
                    {
                        ko = change.Old;
                        kn = change.New;
                    }
                    else
                    {
                        ko = kn = LiveKey(e.Archetype, e.Block, e.Slot, e.NetId, e.Flags, gk.Slot);
                    }

                    if (kn != 0)
                    {
                        AddMember(slot, gk.Set, kn, i, ko, kn);
                    }

                    if (ko != 0 && ko != kn)
                    {
                        AddMember(slot, gk.Set, ko, i, ko, kn);
                    }
                }
            }
        }

        if (slot.MemberCount > 1)
        {
            slot.Members.AsSpan(0, slot.MemberCount).Sort(default(MemberEntryOrder));
        }
    }

    private static void AddMember(TickLog slot, int set, long key, int eventIndex, long ko, long kn)
    {
        if (slot.MemberCount == slot.Members.Length)
        {
            Array.Resize(ref slot.Members, Math.Max(64, slot.MemberCount * 2));
        }

        slot.Members[slot.MemberCount++] = new MemberEntry { Set = set, Key = key, Event = eventIndex, Old = ko, New = kn };
    }

    /// <summary>A described member's key in one set: its cold entry's, when the entry still names <paramref name="netId"/>; 0 for a leave or a reused slot.</summary>
    private long LiveKey(int archetype, nint block, int slot, uint netId, byte flags, int keySlot)
    {
        if ((flags & PushEvent.HasNew) == 0 || block == 0)
        {
            return 0;
        }

        var layout = _states[archetype].Layout;
        var bytes = (byte*)block;
        var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (slot * layout.HotStride));
        if (hot->NetId != netId)
        {
            return 0;
        }

        return *(long*)(bytes + layout.ColdOffset + (slot * layout.ColdStride) + layout.GroupKeyOffsetInColdEntry + (8 * keySlot));
    }

    /// <summary>A member's current key in one set, read from its cold entry, or 0 when the entry no longer describes it (released, or the slot reused).</summary>
    private long CurrentKey(ushort archetype, nint block, byte slot, uint netId, int keySlot)
    {
        if (block == 0)
        {
            return 0;
        }

        var layout = _states[archetype].Layout;
        var bytes = (byte*)block;
        var b = (ReplicationBlockHeader*)block;
        var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (slot * layout.HotStride));
        if (hot->NetId != netId || (b->ProjectedOccupancy & (1UL << slot)) == 0)
        {
            return 0;
        }

        return *(long*)(bytes + layout.ColdOffset + (slot * layout.ColdStride) + layout.GroupKeyOffsetInColdEntry + (8 * keySlot));
    }

    // ══ Regions and their index (P6, P7) ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    private readonly struct RegionByKey : IComparer<RegionEntry>
    {
        public int Compare(RegionEntry x, RegionEntry y) => x.Key.CompareTo(y.Key);
    }

    private struct RegionEntry
    {
        public long Key;
        public SpatialFieldType Type;
        public float A0, A1, A2, A3, A4, A5;   // box min x, y, z, max x, y, z — or a sphere's centre x, y, z and its radius in A3

        public readonly bool SameAs(in RegionEntry other) =>
            Key == other.Key && Type == other.Type && A0 == other.A0 && A1 == other.A1 && A2 == other.A2 && A3 == other.A3 && A4 == other.A4
            && A5 == other.A5;
    }

    // Per set: the regions of this realm, and the index — every (cell, region) pair within the set's widest reach plus its margin, by cell.
    private RegionEntry[][] _regionTable = [];
    private int[] _regionCount = [];
    private ulong[][] _regionCells = [];
    private int[][] _regionIds = [];
    private int[] _regionCellCount = [];
    private RegionEntry[] _regionScratch = new RegionEntry[16];

    // Per set: the regions too wide to file cell by cell (P7's oversized list), which every session of the set tests. And the index build's pairs, reused.
    private int[][] _regionOversized = [];
    private int[] _regionOversizedCount = [];
    private (ulong Cell, int Region)[] _regionPairs = new (ulong, int)[64];

    /// <summary>A region filed under more cells than this goes on its set's oversized list instead (P7).</summary>
    internal const int MaxGroupRegionCells = 4096;

    /// <summary>Regions read, region tables rebuilt, summed. Diagnostic.</summary>
    internal long RegionRebuilds;

    /// <summary>
    /// Reads every set's region archetype and rebuilds the set's index when its table changed (P7): built at the first tick and edited only when an extent,
    /// a key or the set of regions changes. Serial, at the index's finish.
    /// </summary>
    private void RefreshRegions()
    {
        if (_regionTable.Length != GroupSets.Length)
        {
            _regionTable = new RegionEntry[GroupSets.Length][];
            _regionCount = new int[GroupSets.Length];
            _regionCells = new ulong[GroupSets.Length][];
            _regionIds = new int[GroupSets.Length][];
            _regionCellCount = new int[GroupSets.Length];
            _regionOversized = new int[GroupSets.Length][];
            _regionOversizedCount = new int[GroupSets.Length];
            for (var s = 0; s < GroupSets.Length; s++)
            {
                _regionTable[s] = [];
                _regionCells[s] = [];
                _regionIds[s] = [];
                _regionOversized[s] = [];
                _regionCount[s] = -1;   // never built
            }
        }

        for (var s = 0; s < GroupSets.Length; s++)
        {
            if (GroupSets[s].Region == null)
            {
                _regionCount[s] = 0;
                continue;
            }

            var count = ReadRegions(GroupSets[s].Region, ref _regionScratch);
            var table = _regionTable[s];
            var same = count == _regionCount[s];
            for (var i = 0; same && i < count; i++)
            {
                same = _regionScratch[i].SameAs(in table[i]);
            }

            if (same)
            {
                continue;
            }

            if (table.Length < count)
            {
                _regionTable[s] = table = new RegionEntry[Math.Max(count, 16)];
            }

            Array.Copy(_regionScratch, table, count);
            _regionCount[s] = count;
            BuildRegionIndex(s, GroupSets[s].MaxReachM + GroupSets[s].LeaveMarginM);
            RegionRebuilds++;
        }
    }

    /// <summary>The regions of this realm in the region archetype's clusters, ordered by key; a key held by two regions keeps the first one read.</summary>
    private int ReadRegions(CompiledRegion region, ref RegionEntry[] into)
    {
        var cs = region.ClusterState;
        if (cs?.ClusterSegment == null)
        {
            return 0;
        }

        var count = 0;
        using var epoch = EpochGuard.Enter(cs.ClusterSegment.Store.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var c = 0; c < cs.ActiveClusterCount; c++)
            {
                var chunk = cs.ActiveClusterIds[c];
                var realm = cs.ClusterRealmMap != null && (uint)chunk < (uint)cs.ClusterRealmMap.Length ? cs.ClusterRealmMap[chunk] : (ushort)0;
                if (realm != ServedRealm)
                {
                    continue;
                }

                var clusterBase = accessor.GetChunkAddress(chunk);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    var key = region.Key.ReadKey(clusterBase, slot);
                    if (key == 0)
                    {
                        continue;
                    }

                    if (count == into.Length)
                    {
                        Array.Resize(ref into, count * 2);
                    }

                    var p = (float*)region.Extent.At(clusterBase, slot);
                    ref var r = ref into[count++];
                    r.Key = key;
                    r.Type = region.ExtentType;
                    switch (region.ExtentType)
                    {
                        case SpatialFieldType.AABB2F:
                            (r.A0, r.A1, r.A2, r.A3, r.A4, r.A5) = (p[0], p[1], 0f, p[2], p[3], 0f);
                            break;
                        case SpatialFieldType.AABB3F:
                            (r.A0, r.A1, r.A2, r.A3, r.A4, r.A5) = (p[0], p[1], p[2], p[3], p[4], p[5]);
                            break;
                        case SpatialFieldType.BSphere2F:
                            (r.A0, r.A1, r.A2, r.A3, r.A4, r.A5) = (p[0], p[1], 0f, p[2], 0f, 0f);
                            break;
                        default:
                            (r.A0, r.A1, r.A2, r.A3, r.A4, r.A5) = (p[0], p[1], p[2], p[3], 0f, 0f);
                            break;
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        if (count > 1)
        {
            // Stable on a key held twice, so "the first read" is the cluster walk's first: a sort that reordered equal keys would flip between ticks and
            // rebuild the index for nothing.
            var span = into.AsSpan(0, count);
            span.Sort(default(RegionByKey));
            var kept = 1;
            for (var i = 1; i < count; i++)
            {
                if (span[i].Key != span[kept - 1].Key)
                {
                    span[kept++] = span[i];
                }
            }

            count = kept;
        }

        return count;
    }

    /// <summary>
    /// Files every region under each grid cell within <paramref name="pad"/> of its extent's bounding box, sorted by cell; a region wider than
    /// <see cref="MaxGroupRegionCells"/> cells goes on the set's oversized list. A 2D extent is filed on the plane z = 0, where its sessions look it up.
    /// </summary>
    private void BuildRegionIndex(int set, double pad)
    {
        var table = _regionTable[set];
        var pairs = 0;
        var oversized = 0;
        for (var i = 0; i < _regionCount[set]; i++)
        {
            Bounds(in table[i], out var x0, out var y0, out var z0, out var x1, out var y1, out var z1);
            var cx0 = CellX(x0 - pad);
            var cx1 = CellX(x1 + pad);
            var cy0 = CellY(y0 - pad);
            var cy1 = CellY(y1 + pad);
            var flat = !TEvent.Deep || Is2D(table[i].Type);
            var cz0 = flat ? CellZ(0d) : CellZ(z0 - pad);
            var cz1 = flat ? CellZ(0d) : CellZ(z1 + pad);
            var cells = (long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1) * (cz1 - cz0 + 1);
            if (cells > MaxGroupRegionCells)
            {
                if (oversized == _regionOversized[set].Length)
                {
                    Array.Resize(ref _regionOversized[set], Math.Max(4, oversized * 2));
                }

                _regionOversized[set][oversized++] = i;
                continue;
            }

            for (var cz = cz0; cz <= cz1; cz++)
            {
                for (var cy = cy0; cy <= cy1; cy++)
                {
                    for (var cx = cx0; cx <= cx1; cx++)
                    {
                        if (pairs == _regionPairs.Length)
                        {
                            Array.Resize(ref _regionPairs, pairs * 2);
                        }

                        _regionPairs[pairs++] = (TEvent.Key(cx, cy, cz), i);
                    }
                }
            }
        }

        _regionPairs.AsSpan(0, pairs).Sort();
        if (_regionCells[set].Length < pairs)
        {
            _regionCells[set] = new ulong[pairs];
            _regionIds[set] = new int[pairs];
        }

        for (var i = 0; i < pairs; i++)
        {
            _regionCells[set][i] = _regionPairs[i].Cell;
            _regionIds[set][i] = _regionPairs[i].Region;
        }

        _regionCellCount[set] = pairs;
        _regionOversizedCount[set] = oversized;
    }

    private static bool Is2D(SpatialFieldType type) => type is SpatialFieldType.AABB2F or SpatialFieldType.BSphere2F;

    private static void Bounds(in RegionEntry r, out double x0, out double y0, out double z0, out double x1, out double y1, out double z1)
    {
        if (r.Type is SpatialFieldType.AABB2F or SpatialFieldType.AABB3F)
        {
            (x0, y0, z0, x1, y1, z1) = (r.A0, r.A1, r.A2, r.A3, r.A4, r.A5);
        }
        else
        {
            (x0, y0, z0, x1, y1, z1) = (r.A0 - r.A3, r.A1 - r.A3, r.A2 - r.A3, r.A0 + r.A3, r.A1 + r.A3, r.A2 + r.A3);
        }
    }

    /// <summary>
    /// The anchor's distance to the extent's nearest point, zero inside it (P6). A 2D extent ignores z, and so does a 3D one in a flat grid, whose anchors lie
    /// on z = 0 whatever the extent's z range.
    /// </summary>
    private static double ExtentDistance(in RegionEntry r, double ax, double ay, double az)
    {
        switch (r.Type)
        {
            case SpatialFieldType.AABB2F:
            case SpatialFieldType.AABB3F:
            {
                var dx = Math.Max(0d, Math.Max(r.A0 - ax, ax - r.A3));
                var dy = Math.Max(0d, Math.Max(r.A1 - ay, ay - r.A4));
                var dz = r.Type == SpatialFieldType.AABB3F && TEvent.Deep ? Math.Max(0d, Math.Max(r.A2 - az, az - r.A5)) : 0d;
                return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            }
            default:
            {
                var dx = ax - r.A0;
                var dy = ay - r.A1;
                var dz = r.Type == SpatialFieldType.BSphere3F && TEvent.Deep ? az - r.A2 : 0d;
                return Math.Max(0d, Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) - r.A3);
            }
        }
    }

    // ══ Per-session state ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    // Sized with the session slots, serially (GrowSessions); a slot's state is made and touched by the one worker gathering that session.
    private GroupSessionState[] _groupSessions = new GroupSessionState[InitialSessionSlots];

    private protected override GroupSessionState GroupStateAtOrNull(int local) => (uint)local < (uint)_groupSessions.Length ? _groupSessions[local] : null;

    internal override GroupKeyState GroupKeyStateOf(SessionId session, int set, long key)
    {
        var gs = GroupStateAtOrNull(L(session));
        if (gs == null || gs.Generation != session.Generation || (uint)set >= (uint)gs.PCount.Length)
        {
            return GroupKeyState.NotHeld;
        }

        var max = gs.Max;
        if (!IsHeldKey(gs.PKeys, set * max, gs.PCount[set], key))
        {
            return GroupKeyState.NotHeld;
        }

        return GroupSessionState.CursorOf(gs.PKeys, gs.PCursor, set * max, gs.PCount[set], key) == GroupSessionState.Complete
            ? GroupKeyState.Complete
            : GroupKeyState.Filling;
    }

    private GroupSessionState GroupStateOf(int local, ushort generation)
    {
        Debug.Assert((uint)local < (uint)_groupSessions.Length, "the group sessions are sized with the session slots");
        var gs = _groupSessions[local] ??= new GroupSessionState(GroupSets.Length, MaxGroupsPerSession);
        if (gs.Generation != generation)
        {
            gs.Clear();
            gs.Generation = generation;
        }

        return gs;
    }

    /// <summary>
    /// An unplaced frame holds what was held: the pending keys are the committed ones — none when the frame is a RESET, which clears the client's store.
    /// </summary>
    private void HoldGroups(int local, ushort generation, bool reset)
    {
        if ((uint)local < (uint)_groupSessions.Length && _groupSessions[local] is { } gs && gs.Generation == generation)
        {
            gs.Reset = false;
            if (reset)
            {
                Array.Clear(gs.PCount);
                return;
            }

            Array.Copy(gs.CKeys, gs.PKeys, gs.CKeys.Length);
            Array.Copy(gs.CCursor, gs.PCursor, gs.CCursor.Length);
            Array.Copy(gs.CCount, gs.PCount, gs.PCount.Length);
        }
    }

    internal override bool AnyGroupFillingForTest()
    {
        foreach (var gs in _groupSessions)
        {
            if (gs != null && gs.IsFilling())
            {
                return true;
            }
        }

        return false;
    }

    private void CommitGroups(int local, ushort generation)
    {
        if ((uint)local < (uint)_groupSessions.Length && _groupSessions[local] is { } gs && gs.Generation == generation)
        {
            gs.Commit();
        }
    }

    // ══ The group gather (P8–P11) ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A Sphere session's group frame (14 § 3.8): which keys it holds now, their fill under the remaining enter budget, the members of the keys it released,
    /// the member events of every key it held before or holds now, and the post-pass that keeps the geometric records true to the union.
    /// </summary>
    /// <returns>How many enters the fill emitted.</returns>
    private int GatherGroups(int local, SessionId session, ProfileGroup[] groups, bool reset, int gap, uint tick, in Ball a, int oOriginX, int oOriginY,
        int oOriginZ, ReadOnlySpan<ushort> o, in Ball n, int nOriginX, int nOriginY, int nOriginZ, ReadOnlySpan<ushort> d, in ArchetypeSet archetypes,
        FrameWorkerScratch scratch, int budget, ref long leaves, ref long updates)
    {
        var gs = GroupStateOf(local, session.Generation);
        var max = gs.Max;

        // A RESET clears what the client holds: every key is filled and completed again (P11). Read through an empty view rather than cleared, so a frame
        // that is then not published leaves the committed keys as the client still has them.
        if (reset && gs.IsFilling())
        {
            Interlocked.Increment(ref GroupResetsMidFill);
        }

        gs.Reset = reset;

        // ── 1. The keys this frame moves to: the regions the anchor meets, the held ones under hysteresis; an explicit set's requested keys, a shared
        //       set's one ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        Array.Clear(gs.PCount);
        Span<long> sharedKey = stackalloc long[1];
        foreach (var group in groups)
        {
            var set = group.Set;
            var compiled = GroupSets[set];
            var met = 0;
            if (compiled.Selector != GroupSelector.Region)
            {
                // Not met by geometry (14 § 4): every key is at distance 0, so the fill takes them in key order; the bound was the request's.
                sharedKey[0] = compiled.SharedKey;
                var keys = compiled.Selector == GroupSelector.Shared ? sharedKey : Requests != null ? Requests.KeysOf(session, set) : default;
                for (var k = 0; k < keys.Length && met < max; k++)
                {
                    var cursor = GroupSessionState.CursorOf(gs.CKeys, gs.CCursor, set * max, gs.CCount[set], keys[k]);
                    var kept = cursor != 0 || IsHeldKey(gs.CKeys, set * max, gs.CCount[set], keys[k]);
                    gs.PKeys[(set * max) + met] = keys[k];
                    gs.PCursor[(set * max) + met] = kept ? cursor : 0u;
                    gs.PDistance[(set * max) + met] = 0d;
                    met++;
                }

                gs.PCount[set] = met;
                continue;
            }

            // A 2D extent lies on the plane z = 0, where it was filed: its sessions look it up there, whatever their altitude.
            var cz = Is2D(compiled.Region.ExtentType) ? CellZ(0d) : CellZ(n.Z);
            var cellKey = TEvent.Key(CellX(n.X), CellY(n.Y), cz);
            var cells = _regionCells.Length > set ? _regionCells[set] : [];
            var count = _regionCellCount.Length > set ? _regionCellCount[set] : 0;
            var lo = LowerBound(cells, count, cellKey);
            for (var i = lo; i < count && cells[i] == cellKey; i++)
            {
                Meet(gs, set, max, group, compiled, _regionIds[set][i], in n, scratch, ref met);
            }

            for (var i = 0; _regionOversizedCount.Length > set && i < _regionOversizedCount[set]; i++)
            {
                Meet(gs, set, max, group, compiled, _regionOversized[set][i], in n, scratch, ref met);
            }

            // Kept keys first, then the nearest new ones; past the per-session bound a key is not held, and counted.
            var candidates = scratch.GroupMet.AsSpan(0, met);
            candidates.Sort(default(GroupMetAdmission));
            if (met > max)
            {
                Interlocked.Add(ref GroupsOverLimit, met - max);
                met = max;
                candidates = candidates[..met];
            }

            candidates.Sort(default(GroupMetByKey));
            for (var i = 0; i < met; i++)
            {
                gs.PKeys[(set * max) + i] = candidates[i].Key;
                gs.PCursor[(set * max) + i] = candidates[i].Kept ? candidates[i].Cursor : 0u;
                gs.PDistance[(set * max) + i] = candidates[i].Distance;
            }

            gs.PCount[set] = met;
        }

        // ── 2. Fill cursors, fixed before any member is decided: the budget alone moves them, nearest filling key first ──────────────────────────
        var spent = 0;
        Span<int> orders = stackalloc int[max];   // MaxGroupsPerSession is at most 1 024: 4 KB
        foreach (var group in groups)
        {
            var set = group.Set;
            var count = gs.PCount[set];
            var order = orders[..count];
            for (var i = 0; i < count; i++)
            {
                order[i] = i;
            }

            // Insertion sort by distance: at most MaxGroupsPerSession keys, and usually one or two.
            for (var i = 1; i < count; i++)
            {
                var v = order[i];
                var j = i - 1;
                while (j >= 0 && gs.PDistance[(set * max) + order[j]] > gs.PDistance[(set * max) + v])
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = v;
            }

            for (var o2 = 0; o2 < count; o2++)
            {
                var at = (set * max) + order[o2];
                if (gs.PCursor[at] == GroupSessionState.Complete)
                {
                    continue;
                }

                // A spent budget moves no cursor: no read. A key whose members are all gone completes on the next frame with budget left.
                if (spent >= budget)
                {
                    continue;
                }

                var from = gs.PCursor[at];
                var members = ReadMembers(set, gs.PKeys[at], from, uint.MaxValue, scratch, sort: true, out var pending);
                var take = Math.Min(members, budget - spent);
                spent += take;

                // A member with no replication state yet — a positionless one whose block a held key is about to attach, or one the pool could not give a
                // block — cannot be sent, so the key is not complete while one is pending (14 § 5): the cursor passes what was taken and waits.
                gs.PCursor[at] = take < members ? scratch.FillMembers[take].NetId
                    : pending > 0 ? (members > 0 ? scratch.FillMembers[members - 1].NetId + 1 : from)
                    : GroupSessionState.Complete;
            }
        }

        // ── 3. The member events of every key held before or now, folded per identity over the frame's window ────────────────────────────────────
        var first = gap > 0 ? tick - (uint)gap : tick;
        for (var t = first; t != tick + 1; t++)
        {
            var slot = _log[t % LogDepth];
            if (!slot.Valid || slot.Tick != t)
            {
                continue;
            }

            foreach (var group in groups)
            {
                var set = group.Set;
                FoldKeys(slot, t, set, gs.CKeys, set * max, gs.CCount[set], scratch);
                FoldKeys(slot, t, set, gs.PKeys, set * max, gs.PCount[set], scratch);
            }
        }

        // The positionless members' events, in the hub's log: realm-free, filed under their keys alone (14 § 5).
        for (var t = first; Hub != null && Hub.IsAnyLazy && t != tick + 1; t++)
        {
            var unplaced = Hub.UnplacedAt(t);
            if (unplaced == null)
            {
                continue;
            }

            foreach (var group in groups)
            {
                var set = group.Set;
                FoldUnplacedKeys(unplaced, t, set, gs.CKeys, set * max, gs.CCount[set], scratch);
                FoldUnplacedKeys(unplaced, t, set, gs.PKeys, set * max, gs.PCount[set], scratch);
            }
        }

        var entered = 0;
        var log = gap > 0 ? Log : null;
        foreach (var (netId, fold) in scratch.GroupFolds)
        {
            var f = fold;
            var inSet = archetypes.Contains(f.Archetype);
            bool geoWas, geoIs;
            if (log != null)
            {
                // A catch-up: the fold above holds only the events filed under held keys, so its first and last may not be the window's. The geometric
                // decision is the geometric log's own — the same entry the catch-up emitted from — and an identity it never met was in neither sphere.
                var index = inSet ? log.IndexOf(netId) : -1;
                if (index >= 0)
                {
                    ref var entry = ref log.Entries[index];
                    geoWas = (entry.FirstFlags & PushEvent.HasOld) != 0 && Within(a, entry.OldX, entry.OldY, entry.OldZ)
                        && Held(o, oOriginX, oOriginY, oOriginZ, entry.OldKey);
                    geoIs = (entry.Last.Flags & PushEvent.HasNew) != 0 && Within(n, entry.Last.NewX, entry.Last.NewY, entry.Last.NewZ)
                        && Held(d, nOriginX, nOriginY, nOriginZ, entry.Last.NewKey);
                }
                else
                {
                    geoWas = geoIs = false;
                }
            }
            else
            {
                geoWas = inSet && (f.FirstFlags & PushEvent.HasOld) != 0 && Within(a, f.OldX, f.OldY, f.OldZ)
                    && Held(o, oOriginX, oOriginY, oOriginZ, f.OldCell);
                geoIs = inSet && (f.LastFlags & PushEvent.HasNew) != 0 && Within(n, f.NewX, f.NewY, f.NewZ) && Held(d, nOriginX, nOriginY, nOriginZ, f.NewCell);
            }

            if (geoWas && geoIs)
            {
                continue;   // held by geometry on both sides: its update, with its distance LOD, is the geometry's
            }

            var was = geoWas || KeysHeld(gs, true, f.Archetype, netId, ref f.Before);
            var isIn = geoIs || ((f.LastFlags & PushEvent.HasNew) != 0 && KeysHeld(gs, false, f.Archetype, netId, ref f.After));
            if (isIn && !was)
            {
                scratch.Add(f.Archetype, FrameListKind.Enter, new FrameRecord { NetId = netId, Block = f.Block, Slot = f.Slot, Archetype = f.Archetype });
                entered++;
            }
            else if (isIn)
            {
                EmitRecord(netId, f.Block, f.Slot, f.Archetype, f.Groups, f.Segment, scratch, ref updates);
            }
            else if (was)
            {
                scratch.Add(f.Archetype, FrameListKind.Leave, new FrameRecord { NetId = netId, Block = f.Block, Slot = f.Slot, Archetype = f.Archetype });
                leaves++;
            }
        }

        // ── 4. Fill: the members the cursors now take that have no event in the window (those were the events') ─────────────────────────────────
        foreach (var group in groups)
        {
            var set = group.Set;
            for (var i = 0; i < gs.PCount[set]; i++)
            {
                var at = (set * max) + i;
                var key = gs.PKeys[at];
                var before = GroupSessionState.CursorOf(gs.CKeys, gs.CCursor, set * max, gs.CCount[set], key);
                var after = gs.PCursor[at];
                if (before == GroupSessionState.Complete || after == before)
                {
                    continue;
                }

                var members = ReadMembers(set, key, before, after, scratch, sort: false, out _);
                for (var m = 0; m < members; m++)
                {
                    ref var member = ref scratch.FillMembers[m];
                    if (OwnedByLog(member, tick, gap))
                    {
                        continue;
                    }

                    if (!Untouched(member, gs, true, in a, oOriginX, oOriginY, oOriginZ, o, in archetypes))
                    {
                        continue;   // held before — by geometry or another key
                    }

                    scratch.Add(member.Archetype, FrameListKind.Enter,
                        new FrameRecord { NetId = member.NetId, Block = member.Block, Slot = member.Slot, Archetype = member.Archetype });
                    entered++;
                }

                if (after == GroupSessionState.Complete)
                {
                    scratch.GroupRecords.Add(new GroupRecord(GroupRecordKind.Complete, set, key));
                }
            }

            // ── 5. Release: a key held before and not now — its members not held otherwise leave, and the key is released ─────────────────────────
            for (var i = 0; i < gs.CCount[set]; i++)
            {
                var at = (set * max) + i;
                var key = gs.CKeys[at];
                if (IsHeldKey(gs.PKeys, set * max, gs.PCount[set], key))
                {
                    continue;
                }

                var cursor = gs.CCursor[at];
                var members = ReadMembers(set, key, 0u, cursor, scratch, sort: false, out _);
                for (var m = 0; m < members; m++)
                {
                    ref var member = ref scratch.FillMembers[m];
                    if (OwnedByLog(member, tick, gap))
                    {
                        continue;
                    }

                    if (!Untouched(member, gs, false, in n, nOriginX, nOriginY, nOriginZ, d, in archetypes))
                    {
                        continue;   // still held — by geometry or another key
                    }

                    scratch.Add(member.Archetype, FrameListKind.Leave,
                        new FrameRecord { NetId = member.NetId, Block = member.Block, Slot = member.Slot, Archetype = member.Archetype });
                    leaves++;
                }

                scratch.GroupRecords.Add(new GroupRecord(GroupRecordKind.Released, set, key));
            }
        }

        // ── 6. The geometric records, kept true to the union ────────────────────────────────────────────────────────────────────────────────────
        PostPass(gs, in archetypes, scratch);
        return entered;
    }

    /// <summary>Step 1's test for one region of a set: met within the reach, kept within reach plus margin when the committed keys hold it.</summary>
    private void Meet(GroupSessionState gs, int set, int max, ProfileGroup group, CompiledGroupSet compiled, int region, in Ball n,
        FrameWorkerScratch scratch, ref int met)
    {
        ref var r = ref _regionTable[set][region];
        var distance = ExtentDistance(in r, n.X, n.Y, n.Z);
        var cursor = GroupSessionState.CursorOf(gs.CKeys, gs.CCursor, set * max, gs.CCount[set], r.Key);
        var kept = cursor != 0 || IsHeldKey(gs.CKeys, set * max, gs.CCount[set], r.Key);
        if (distance > group.ReachM && !(kept && distance <= group.ReachM + compiled.LeaveMarginM))
        {
            return;
        }

        if (met == scratch.GroupMet.Length)
        {
            Array.Resize(ref scratch.GroupMet, met * 2);
        }

        scratch.GroupMet[met++] = new GroupMet { Key = r.Key, Distance = distance, Kept = kept, Cursor = cursor };
    }

    private static int LowerBound(ulong[] keys, int count, ulong key)
    {
        var lo = 0;
        var hi = count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (keys[mid] < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static bool IsHeldKey(long[] keys, int at, int count, long key)
    {
        var lo = at;
        var hi = at + count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (keys[mid] == key)
            {
                return true;
            }

            if (keys[mid] < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether some set's key held the member, before the frame (<paramref name="committed"/>) or after it: the key in the session's keys of that set, and the
    /// member below the key's fill cursor.
    /// </summary>
    private bool KeysHeld(GroupSessionState gs, bool committed, ushort archetype, uint netId, ref GroupFold.KeySlots keys)
    {
        var groupKeys = _plans[archetype].GroupKeys;
        var max = gs.Max;
        for (var g = 0; g < groupKeys.Length; g++)
        {
            var key = keys[groupKeys[g].Slot];
            if (key == 0)
            {
                continue;
            }

            var set = groupKeys[g].Set;
            var cursor = committed
                ? GroupSessionState.CursorOf(gs.CKeys, gs.CCursor, set * max, gs.CCount[set], key)
                : GroupSessionState.CursorOf(gs.PKeys, gs.PCursor, set * max, gs.PCount[set], key);
            if (cursor > netId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// For a member with no event in the window — its key unchanged — whether nothing else holds it on the given side of the frame: not geometry, and no key of
    /// another set. The key being filled or released is excluded by its cursor, which the caller tested.
    /// </summary>
    private bool Untouched(in GroupFillMember member, GroupSessionState gs, bool committed, in Ball ball, int originX, int originY, int originZ,
        ReadOnlySpan<ushort> window, in ArchetypeSet archetypes)
    {
        if (archetypes.Contains(member.Archetype))
        {
            DecodeVisibility(member.Archetype, member.Block, member.Slot, out var x, out var y, out var z);
            if (Within(ball, x, y, z) && Held(window, originX, originY, originZ, CellKey(x, y, z)))
            {
                return false;
            }
        }

        var groupKeys = _plans[member.Archetype].GroupKeys;
        if (groupKeys.Length > 1)
        {
            GroupFold.KeySlots keys = default;
            for (var g = 0; g < groupKeys.Length; g++)
            {
                keys[groupKeys[g].Slot] = CurrentKey(member.Archetype, member.Block, member.Slot, member.NetId, groupKeys[g].Slot);
            }

            // Every set but the one under fill or release would answer the same; asking all of them is correct because that one's cursor excluded the member.
            return !KeysHeldOther(gs, committed, member.Archetype, member.NetId, ref keys);
        }

        return true;
    }

    private bool KeysHeldOther(GroupSessionState gs, bool committed, ushort archetype, uint netId, ref GroupFold.KeySlots keys) =>
        KeysHeld(gs, committed, archetype, netId, ref keys);

    /// <summary>Whether the member had an event since the session's last frame: the log's replay, or this tick's push step, owns it then (SUB-19).</summary>
    private bool OwnedByLog(in GroupFillMember member, uint tick, int gap)
    {
        var layout = _states[member.Archetype].Layout;
        var stamp = *(uint*)((byte*)member.Block + layout.ColdOffset + (member.Slot * layout.ColdStride) + layout.LastEventTickOffsetInColdEntry);
        return tick - stamp <= (uint)gap;
    }

    /// <summary>
    /// Reads a key's members from every member archetype's index into <see cref="FrameWorkerScratch.FillMembers"/>, those this realm's sessions can hold -
    /// a positioned member of this realm, a positionless one of any (14 § 5) — with a netId in [<paramref name="from"/>, <paramref name="until"/>), sorted
    /// by netId when <paramref name="sort"/> asks. A member whose projected key is not <paramref name="key"/> yet is skipped: its projection is its event,
    /// and the fill agrees with the events. One with no replication state yet — no block, no identity, not projected — is <paramref name="pending"/>.
    /// </summary>
    /// <remarks>
    /// <para>The tree is keyed by the field's own type: the 64-bit key is passed by address, read little-endian at the field's width (x64, arm64).</para>
    /// <para>
    /// <b>Off-DAG moves.</b> A positionless member's Versioned key can be moved by a commit off the DAG (EW-02) after the projection read it: the tree no
    /// longer lists it under the key its events still file it under. Such a commit logs the move before it touches the tree, so the log is read after the
    /// tree and its moves out of <paramref name="key"/> are members too; the projected-key test keeps only those the projection still filed there.
    /// </para>
    /// </remarks>
    private int ReadMembers(int set, long key, uint from, uint until, FrameWorkerScratch scratch, bool sort, out int pending)
    {
        var count = 0;
        pending = 0;
        var compiled = GroupSets[set];
        foreach (var member in compiled.Members)
        {
            var tree = member.Tree ??= GroupSetCompiler.TreeOf(member, _states[member.Plan]);
            if (tree == null)
            {
                continue;
            }

            var k = key;
            var accessor = tree.Segment.CreateChunkAccessor();
            try
            {
                using var buffer = tree.TryGetMultiple(&k, ref accessor);
                if (buffer.IsValid)
                {
                    do
                    {
                        foreach (var location in buffer.ReadOnlyElements)
                        {
                            AddMember(member, location, key, from, until, scratch, ref count, ref pending);
                        }
                    }
                    while (buffer.NextChunk());
                }
            }
            finally
            {
                accessor.Dispose();
            }

            if (member.Moves != null)
            {
                count = AddMoves(member, key, from, until, scratch, count);
            }
        }

        if (sort && count > 1)
        {
            scratch.FillMembers.AsSpan(0, count).Sort(default(GroupFillMemberOrder));
        }

        return count;
    }

    // One index location: a member to read, one still pending, or none — another realm's, or one whose projected key is another.
    private void AddMember(CompiledGroupMember member, int location, long key, uint from, uint until, FrameWorkerScratch scratch, ref int count,
        ref int pending)
    {
        var state = _states[member.Plan];
        var chunk = location >> 6;
        var block = BlockOf(member.Plan, chunk);
        if (block == null)
        {
            // No block yet: a positionless member a held key is about to attach, or a cluster the pool deferred (SUB-32). Another realm's is not ours.
            if (member.Positionless || state.RealmOfCluster(chunk) == ServedRealm)
            {
                pending++;
            }

            return;
        }

        if (!member.Positionless && block->Realm != ServedRealm)
        {
            return;
        }

        var layout = state.Layout;
        var slot = location & 63;
        var hot = (ReplicationHotEntry*)((byte*)block + layout.HotOffset + (slot * layout.HotStride));
        var netId = hot->NetId;
        if (netId == NetIdAllocator.NoNetId || (block->ProjectedOccupancy & (1UL << slot)) == 0)
        {
            pending++;
            return;
        }

        if (netId < from || netId >= until || CurrentKey((ushort)member.Plan, (nint)block, (byte)slot, netId, member.KeySlot) != key)
        {
            return;
        }

        if (count == scratch.FillMembers.Length)
        {
            Array.Resize(ref scratch.FillMembers, count * 2);
        }

        scratch.FillMembers[count++] = new GroupFillMember { NetId = netId, Block = (nint)block, Slot = (byte)slot, Archetype = (ushort)member.Plan };
    }

    // The off-DAG moves out of a key since the blocks step (see ReadMembers): each a member when the projection still filed it there, once. A logged entity
    // with no state is not pending here: it left the key, and the tree read already counted what the key still holds.
    private int AddMoves(CompiledGroupMember member, long key, uint from, uint until, FrameWorkerScratch scratch, int count)
    {
        // The tree was read first; a move the read missed was logged before the tree changed, and is visible past this fence (arm64 as x64).
        Interlocked.MemoryBarrier();
        var moves = member.Moves.Read();
        var ignored = 0;
        foreach (ref readonly var move in moves)
        {
            if (move.Watch != member.MoveWatch || DecodeKey(move.Raw, member.Field.Type) != key)
            {
                continue;
            }

            var before = count;
            AddMember(member, move.Location, key, from, until, scratch, ref count, ref ignored);
            if (count == before)
            {
                continue;
            }

            // Moved out and back, or moved twice: in the tree or in the log already — once.
            for (var i = 0; i < count - 1; i++)
            {
                if (scratch.FillMembers[i].NetId == scratch.FillMembers[count - 1].NetId)
                {
                    count--;
                    break;
                }
            }
        }

        return count;
    }

    // A logged key's bytes as the field's integer type reads them, sign- or zero-extended to 64 bits like ClusterFieldReader.ReadKey.
    private static long DecodeKey(ulong raw, FieldType type) => type switch
    {
        FieldType.Byte => (sbyte)raw,
        FieldType.UByte => (byte)raw,
        FieldType.Short => (short)raw,
        FieldType.UShort or FieldType.Char => (ushort)raw,
        FieldType.Int => (int)raw,
        FieldType.UInt => (uint)raw,
        _ => (long)raw,
    };

    /// <summary>Folds one tick's member entries of the held keys into the session's per-identity fold.</summary>
    private void FoldKeys(TickLog slot, uint t, int set, long[] keys, int at, int count, FrameWorkerScratch scratch)
    {
        for (var i = 0; i < count; i++)
        {
            var key = keys[at + i];
            var lo = MemberLowerBound(slot, set, key);
            for (var m = lo; m < slot.MemberCount && slot.Members[m].Set == set && slot.Members[m].Key == key; m++)
            {
                ref var entry = ref slot.Members[m];
                ref var e = ref slot.Events[entry.Event];
                var keySlot = KeySlotOf(e.Archetype, set);
                ref var fold = ref CollectionsMarshalHelper.GetOrAdd(scratch.GroupFolds, e.NetId, out var existed);
                if (!existed)
                {
                    fold = default;
                    fold.FirstFlags = e.Flags;
                    fold.OldX = e.OldX;
                    fold.OldY = e.OldY;
                    fold.OldZ = e.OldZ;
                    fold.OldCell = e.OldKey;
                    InitKeys(ref fold, e.Archetype, e.Block, e.Slot, e.NetId);
                }

                // A set's key before the window is its first entry's old key — whichever set's bucket met the identity first, and in whichever tick — and
                // its key after is its last entry's new one.
                if ((fold.SeenSlots & (1 << keySlot)) == 0)
                {
                    fold.Before[keySlot] = entry.Old;
                    fold.SeenSlots |= (byte)(1 << keySlot);
                }

                fold.After[keySlot] = entry.New;
                if (existed && fold.LastTick == t)
                {
                    continue;   // the same event, met again under its other key or in the other list of keys
                }

                fold.LastFlags = e.Flags;
                fold.NewX = e.NewX;
                fold.NewY = e.NewY;
                fold.NewZ = e.NewZ;
                fold.NewCell = e.NewKey;
                fold.Block = e.Block;
                fold.Slot = e.Slot;
                fold.Archetype = e.Archetype;
                fold.Groups |= e.Groups;
                fold.Segment |= (e.Flags & PushEvent.Segment) != 0;
                fold.LastTick = t;
            }
        }
    }

    /// <summary>Folds one tick's positionless member entries of the held keys into the session's per-identity fold: no position, no cell.</summary>
    private void FoldUnplacedKeys(UnplacedTick slot, uint t, int set, long[] keys, int at, int count, FrameWorkerScratch scratch)
    {
        for (var i = 0; i < count; i++)
        {
            var key = keys[at + i];
            var lo = UnplacedLowerBound(slot, set, key);
            for (var m = lo; m < slot.MemberCount && slot.Members[m].Set == set && slot.Members[m].Key == key; m++)
            {
                ref var entry = ref slot.Members[m];
                ref var e = ref slot.Events[entry.Event];
                var keySlot = KeySlotOf(e.Archetype, set);
                ref var fold = ref CollectionsMarshalHelper.GetOrAdd(scratch.GroupFolds, e.NetId, out var existed);
                if (!existed)
                {
                    fold = default;
                    fold.FirstFlags = e.Flags;
                    InitKeys(ref fold, e.Archetype, e.Block, e.Slot, e.NetId);
                }

                if ((fold.SeenSlots & (1 << keySlot)) == 0)
                {
                    fold.Before[keySlot] = entry.Old;
                    fold.SeenSlots |= (byte)(1 << keySlot);
                }

                fold.After[keySlot] = entry.New;
                if (existed && fold.LastTick == t)
                {
                    continue;
                }

                fold.LastFlags = e.Flags;
                fold.Block = e.Block;
                fold.Slot = e.Slot;
                fold.Archetype = e.Archetype;
                fold.Groups |= e.Groups;
                fold.LastTick = t;
            }
        }
    }

    private static int UnplacedLowerBound(UnplacedTick slot, int set, long key)
    {
        var lo = 0;
        var hi = slot.MemberCount;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            ref var m = ref slot.Members[mid];
            if (m.Set < set || (m.Set == set && m.Key < key))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>A fold's other sets' keys, which no held key of theirs filed: unchanged since, so the entry's current keys, on both sides.</summary>
    private void InitKeys(ref GroupFold fold, ushort archetype, nint block, byte slot, uint netId)
    {
        var groupKeys = _plans[archetype].GroupKeys;
        fold.Block = block;
        fold.Slot = slot;
        fold.Archetype = archetype;
        for (var g = 0; g < groupKeys.Length; g++)
        {
            var current = CurrentKey(archetype, block, slot, netId, groupKeys[g].Slot);
            fold.Before[groupKeys[g].Slot] = current;
            fold.After[groupKeys[g].Slot] = current;
        }
    }

    private int KeySlotOf(ushort archetype, int set)
    {
        var groupKeys = _plans[archetype].GroupKeys;
        for (var g = 0; g < groupKeys.Length; g++)
        {
            if (groupKeys[g].Set == set)
            {
                return groupKeys[g].Slot;
            }
        }

        return 0;
    }

    private static int MemberLowerBound(TickLog slot, int set, long key)
    {
        var lo = 0;
        var hi = slot.MemberCount;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            ref var m = ref slot.Members[mid];
            if (m.Set < set || (m.Set == set && m.Key < key))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// The union with geometry (P9), on the geometric steps' own records: an enter of a member its keys already held before the frame is dropped, and so is a
    /// leave of a member its keys still hold after it. The group steps' records pass untouched — they were decided on the union.
    /// </summary>
    private void PostPass(GroupSessionState gs, in ArchetypeSet archetypes, FrameWorkerScratch scratch)
    {
        foreach (var a in _pushIndices)
        {
            var groupKeys = _plans[a].GroupKeys;
            if (groupKeys.Length == 0 || !archetypes.Contains(a))
            {
                continue;
            }

            var enters = scratch.MutableList(a, FrameListKind.Enter);
            var keep = 0;
            for (var i = 0; i < enters.Length; i++)
            {
                ref var r = ref enters[i];
                if (!MemberKeysHeld(gs, true, r, scratch))
                {
                    enters[keep++] = r;
                }
            }

            scratch.Truncate(a, FrameListKind.Enter, keep);

            var leavesList = scratch.MutableList(a, FrameListKind.Leave);
            keep = 0;
            for (var i = 0; i < leavesList.Length; i++)
            {
                ref var r = ref leavesList[i];
                if (!MemberKeysHeld(gs, false, r, scratch))
                {
                    leavesList[keep++] = r;
                }
            }

            scratch.Truncate(a, FrameListKind.Leave, keep);
        }
    }

    /// <summary>A record's member, held by one of its keys before or after the frame: its fold's keys when it had member events, else its entry's.</summary>
    private bool MemberKeysHeld(GroupSessionState gs, bool committed, in FrameRecord r, FrameWorkerScratch scratch)
    {
        if (scratch.GroupFolds.TryGetValue(r.NetId, out var fold))
        {
            if (!committed && (fold.LastFlags & PushEvent.HasNew) == 0)
            {
                return false;
            }

            return committed ? KeysHeld(gs, true, r.Archetype, r.NetId, ref fold.Before) : KeysHeld(gs, false, r.Archetype, r.NetId, ref fold.After);
        }

        var groupKeys = _plans[r.Archetype].GroupKeys;
        GroupFold.KeySlots keys = default;
        var any = false;
        for (var g = 0; g < groupKeys.Length; g++)
        {
            var key = CurrentKey(r.Archetype, r.Block, r.Slot, r.NetId, groupKeys[g].Slot);
            keys[groupKeys[g].Slot] = key;
            any |= key != 0;
        }

        return any && KeysHeld(gs, committed, r.Archetype, r.NetId, ref keys);
    }
}

/// <summary>Get-or-add by reference into a <see cref="Dictionary{TKey,TValue}"/> of structs, with no second lookup.</summary>
internal static class CollectionsMarshalHelper
{
    public static ref TValue GetOrAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, out bool existed) where TKey : notnull =>
        ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out existed);
}
