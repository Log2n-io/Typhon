using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

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
        var regionsFrom = Stopwatch.GetTimestamp();
        RefreshRegions();
        RegionRefreshTicks += Stopwatch.GetTimestamp() - regionsFrom;
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

    private struct RegionEntry
    {
        public long Key;
        public SpatialFieldType Type;
        public float A0, A1, A2, A3, A4, A5;   // box min x, y, z, max x, y, z — or a sphere's centre x, y, z and its radius in A3

        public readonly bool SameAs(in RegionEntry other) =>
            Key == other.Key && Type == other.Type && A0 == other.A0 && A1 == other.A1 && A2 == other.A2 && A3 == other.A3 && A4 == other.A4
            && A5 == other.A5;
    }

    /// <summary>
    /// The cells a region is filed under — its extent's bounding box padded by the set's reach and margin — or <see cref="Oversized"/>, filed under none.
    /// <see cref="None"/> is a region not filed at all: one not yet seen, or gone.
    /// </summary>
    private struct RegionBox : IEquatable<RegionBox>
    {
        public int X0, X1, Y0, Y1, Z0, Z1;
        public bool Oversized;

        public static RegionBox None => new() { X0 = 1, X1 = 0, Y0 = 1, Y1 = 0, Z0 = 1, Z1 = 0 };

        /// <summary>Whether the region is filed under cell (x, y, z): inside the box, and the box is filed cell by cell.</summary>
        public readonly bool Files(int x, int y, int z) => !Oversized && x >= X0 && x <= X1 && y >= Y0 && y <= Y1 && z >= Z0 && z <= Z1;

        public readonly bool Equals(RegionBox other) =>
            X0 == other.X0 && X1 == other.X1 && Y0 == other.Y0 && Y1 == other.Y1 && Z0 == other.Z0 && Z1 == other.Z1 && Oversized == other.Oversized;
    }

    /// <summary>
    /// One region set's regions and their index (#1409). A region keeps a <b>slot</b> for as long as its key is seen, so a region that spawns or dies
    /// touches its own cells and nobody else's. A cell's <b>bucket</b> lists the slots filed under it in key order — the order a session's walk of a cell
    /// relies on — and a region whose padded box moves is removed from the cells it left and inserted in the cells it reached, nothing else. Built and edited
    /// serially at the index's finish; read by the gather's workers afterwards, with no write in between.
    /// </summary>
    private sealed class RegionIndex
    {
        public RegionEntry[] Entries = new RegionEntry[16];
        public RegionBox[] Boxes = new RegionBox[16];
        public uint[] Seen = new uint[16];
        public bool[] Live = new bool[16];
        public int Slots;
        public int LiveCount;
        public int[] FreeSlots = new int[16];
        public int FreeSlotCount;
        public readonly Dictionary<long, int> SlotOfKey = [];

        public readonly Dictionary<ulong, int> BucketOfCell = [];
        public int[][] Buckets = new int[16][];
        public int[] BucketCounts = new int[16];
        public int BucketTotal;
        public int[] FreeBuckets = new int[16];
        public int FreeBucketCount;

        /// <summary>The regions too wide to file cell by cell (P7), in key order: every session of the set tests them.</summary>
        public int[] Oversized = new int[4];
        public int OversizedCount;

        // The last cluster walk's keys in walk order and the slot each was given (-1 for a key read twice). A walk that meets the same keys in the same
        // order — every tick a region neither spawns, dies nor migrates — reaches each slot without a lookup (P9).
        public long[] WalkKeys = [];
        public int[] WalkSlots = [];
        public int WalkCount;
        public uint Walks;
    }

    // Per set: its index, or null when the set has no region selector.
    private RegionIndex[] _regionIndexes = [];

    // The refresh's walk, serial and reused.
    private RegionEntry[] _regionScratch = new RegionEntry[16];

    /// <summary>A region filed under more cells than this goes on its set's oversized list instead (P7).</summary>
    internal const int MaxGroupRegionCells = 4096;

    /// <summary>
    /// Reads every set's region archetype and keeps the set's index current (P7, #1409): a region seen for the first time is filed, one no longer seen is
    /// removed, and one whose padded box moved is re-filed in the cells it left and reached. Nothing else is touched. Serial, at the index's finish.
    /// </summary>
    private void RefreshRegions()
    {
        if (_regionIndexes.Length != GroupSets.Length)
        {
            _regionIndexes = new RegionIndex[GroupSets.Length];
            for (var s = 0; s < GroupSets.Length; s++)
            {
                _regionIndexes[s] = GroupSets[s].Region == null ? null : new RegionIndex();
            }
        }

        for (var s = 0; s < GroupSets.Length; s++)
        {
            var index = _regionIndexes[s];
            if (index == null)
            {
                continue;
            }

            var pad = GroupSets[s].MaxReachM + GroupSets[s].LeaveMarginM;
            var walked = WalkRegions(GroupSets[s].Region);
            index.Walks++;
            var sameWalk = walked == index.WalkCount;
            for (var i = 0; sameWalk && i < walked; i++)
            {
                sameWalk = _regionScratch[i].Key == index.WalkKeys[i];
            }

            if (sameWalk)
            {
                // The same keys in the same order: the same slots, no region born or gone. Only an extent can have moved.
                for (var i = 0; i < walked; i++)
                {
                    var slot = index.WalkSlots[i];
                    if (slot >= 0)
                    {
                        Update(index, slot, in _regionScratch[i], pad);
                    }
                }
            }
            else
            {
                Rewalk(index, walked, pad);
            }

            VerifyRegionIndex(index, pad);
        }
    }

    /// <summary>
    /// A walk that differs from the last one: each key finds or is given its slot, a key read twice keeps its first reading, and a slot not seen is a
    /// region gone.
    /// </summary>
    private void Rewalk(RegionIndex index, int walked, double pad)
    {
        if (index.WalkKeys.Length < walked)
        {
            index.WalkKeys = new long[_regionScratch.Length];
            index.WalkSlots = new int[_regionScratch.Length];
        }

        var stamp = index.Walks;
        var claimed = 0;
        for (var i = 0; i < walked; i++)
        {
            ref readonly var entry = ref _regionScratch[i];
            index.WalkKeys[i] = entry.Key;
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(index.SlotOfKey, entry.Key, out var exists);
            if (!exists)
            {
                slot = TakeSlot(index);
                index.Entries[slot] = entry;
                index.Boxes[slot] = RegionBox.None;
                index.Seen[slot] = stamp;
                index.WalkSlots[i] = slot;
                claimed++;
                Refile(index, slot, RegionBox.None, BoxOf(in entry, pad));
                continue;
            }

            if (index.Seen[slot] == stamp)
            {
                index.WalkSlots[i] = -1;
                continue;
            }

            index.Seen[slot] = stamp;
            index.WalkSlots[i] = slot;
            claimed++;
            Update(index, slot, in entry, pad);
        }

        index.WalkCount = walked;
        if (claimed == index.LiveCount)
        {
            // Every live region was seen: none is gone.
            return;
        }

        for (var slot = 0; slot < index.Slots; slot++)
        {
            if (!index.Live[slot] || index.Seen[slot] == stamp)
            {
                continue;
            }

            Refile(index, slot, index.Boxes[slot], RegionBox.None);
            index.SlotOfKey.Remove(index.Entries[slot].Key);
            index.Live[slot] = false;
            index.LiveCount--;
            if (index.FreeSlotCount == index.FreeSlots.Length)
            {
                Array.Resize(ref index.FreeSlots, index.FreeSlotCount * 2);
            }

            index.FreeSlots[index.FreeSlotCount++] = slot;
        }
    }

    private static int TakeSlot(RegionIndex index)
    {
        int slot;
        if (index.FreeSlotCount > 0)
        {
            slot = index.FreeSlots[--index.FreeSlotCount];
        }
        else
        {
            slot = index.Slots++;
            if (slot == index.Entries.Length)
            {
                var size = slot * 2;
                Array.Resize(ref index.Entries, size);
                Array.Resize(ref index.Boxes, size);
                Array.Resize(ref index.Seen, size);
                Array.Resize(ref index.Live, size);
            }
        }

        index.Live[slot] = true;
        index.LiveCount++;
        return slot;
    }

    /// <summary>A region seen again: a new extent is kept, and re-filed only when the cells it is filed under changed (P7).</summary>
    private void Update(RegionIndex index, int slot, in RegionEntry now, double pad)
    {
        ref var entry = ref index.Entries[slot];
        if (entry.SameAs(in now))
        {
            return;
        }

        entry = now;
        if (RefileSkippedForTest)
        {
            return;
        }

        var box = BoxOf(in now, pad);
        if (!box.Equals(index.Boxes[slot]))
        {
            Refile(index, slot, index.Boxes[slot], box);
        }
    }

    /// <summary>
    /// Moves <paramref name="slot"/> from the cells of <paramref name="was"/> to those of <paramref name="now"/>: removed from each cell it left, inserted in
    /// each it reached, and on or off the oversized list. Either box may be <see cref="RegionBox.None"/> — a region born or gone.
    /// </summary>
    private void Refile(RegionIndex index, int slot, RegionBox was, RegionBox now)
    {
        var key = index.Entries[slot].Key;
        if (was.Oversized && !now.Oversized)
        {
            RemoveSorted(index, ref index.Oversized, ref index.OversizedCount, slot, key);
        }
        else if (!was.Oversized)
        {
            for (var cz = was.Z0; cz <= was.Z1; cz++)
            {
                for (var cy = was.Y0; cy <= was.Y1; cy++)
                {
                    for (var cx = was.X0; cx <= was.X1; cx++)
                    {
                        if (!now.Files(cx, cy, cz))
                        {
                            BucketRemove(index, TEvent.Key(cx, cy, cz), slot, key);
                        }
                    }
                }
            }
        }

        if (now.Oversized && !was.Oversized)
        {
            if (index.OversizedCount == index.Oversized.Length)
            {
                Array.Resize(ref index.Oversized, index.OversizedCount * 2);
            }

            InsertSorted(index, index.Oversized, ref index.OversizedCount, slot, key);
        }
        else if (!now.Oversized)
        {
            for (var cz = now.Z0; cz <= now.Z1; cz++)
            {
                for (var cy = now.Y0; cy <= now.Y1; cy++)
                {
                    for (var cx = now.X0; cx <= now.X1; cx++)
                    {
                        if (!was.Files(cx, cy, cz))
                        {
                            BucketInsert(index, TEvent.Key(cx, cy, cz), slot, key);
                        }
                    }
                }
            }
        }

        index.Boxes[slot] = now;
        RegionRefiles++;
    }

    private void BucketInsert(RegionIndex index, ulong cell, int slot, long key)
    {
        ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(index.BucketOfCell, cell, out var exists);
        if (!exists)
        {
            bucket = TakeBucket(index);
        }

        ref var slots = ref index.Buckets[bucket];
        ref var count = ref index.BucketCounts[bucket];
        if (count == slots.Length)
        {
            Array.Resize(ref slots, Math.Max(4, count * 2));
        }

        InsertSorted(index, slots, ref count, slot, key);
    }

    private void BucketRemove(RegionIndex index, ulong cell, int slot, long key)
    {
        if (!index.BucketOfCell.TryGetValue(cell, out var bucket))
        {
            RegionIndexMismatches++;
            return;
        }

        ref var count = ref index.BucketCounts[bucket];
        RemoveSorted(index, ref index.Buckets[bucket], ref count, slot, key);
        if (count == 0)
        {
            index.BucketOfCell.Remove(cell);
            if (index.FreeBucketCount == index.FreeBuckets.Length)
            {
                Array.Resize(ref index.FreeBuckets, index.FreeBucketCount * 2);
            }

            index.FreeBuckets[index.FreeBucketCount++] = bucket;
        }
    }

    private static int TakeBucket(RegionIndex index)
    {
        if (index.FreeBucketCount > 0)
        {
            return index.FreeBuckets[--index.FreeBucketCount];
        }

        var bucket = index.BucketTotal++;
        if (bucket == index.Buckets.Length)
        {
            Array.Resize(ref index.Buckets, bucket * 2);
            Array.Resize(ref index.BucketCounts, bucket * 2);
        }

        index.Buckets[bucket] ??= new int[4];
        return bucket;
    }

    /// <summary>Where <paramref name="key"/> goes in <paramref name="slots"/>, kept in key order: the first position whose region's key is not below
    /// it.</summary>
    private static int KeyLowerBound(RegionIndex index, int[] slots, int count, long key)
    {
        var lo = 0;
        var hi = count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (index.Entries[slots[mid]].Key < key)
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

    private static void InsertSorted(RegionIndex index, int[] slots, ref int count, int slot, long key)
    {
        var at = KeyLowerBound(index, slots, count, key);
        Array.Copy(slots, at, slots, at + 1, count - at);
        slots[at] = slot;
        count++;
    }

    private void RemoveSorted(RegionIndex index, ref int[] slots, ref int count, int slot, long key)
    {
        // Keys are unique among live regions, so the lower bound is the region itself.
        var at = KeyLowerBound(index, slots, count, key);
        if (at == count || slots[at] != slot)
        {
            // Never on a correct index: counted rather than thrown, since this runs on the tick.
            RegionIndexMismatches++;
            return;
        }

        Array.Copy(slots, at + 1, slots, at, count - at - 1);
        count--;
    }

    /// <summary>
    /// Reads the regions of this realm from the region archetype's clusters into <see cref="_regionScratch"/>, in walk order; a key of 0 is no region.
    /// Returns how many.
    /// </summary>
    private int WalkRegions(CompiledRegion region)
    {
        var cs = region.ClusterState;
        if (cs?.ClusterSegment == null)
        {
            return 0;
        }

        var walked = 0;
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

                    if (walked == _regionScratch.Length)
                    {
                        Array.Resize(ref _regionScratch, walked * 2);
                    }

                    var p = (float*)region.Extent.At(clusterBase, slot);
                    ref var r = ref _regionScratch[walked++];
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

        return walked;
    }

    /// <summary>The cells <paramref name="r"/> is filed under: its bounding box padded by <paramref name="pad"/>, on the plane z = 0 when it is flat.</summary>
    private RegionBox BoxOf(in RegionEntry r, double pad)
    {
        Bounds(in r, out var x0, out var y0, out var z0, out var x1, out var y1, out var z1);
        var flat = !TEvent.Deep || Is2D(r.Type);
        var box = new RegionBox
        {
            X0 = CellX(x0 - pad),
            X1 = CellX(x1 + pad),
            Y0 = CellY(y0 - pad),
            Y1 = CellY(y1 + pad),
            Z0 = flat ? CellZ(0d) : CellZ(z0 - pad),
            Z1 = flat ? CellZ(0d) : CellZ(z1 + pad),
        };

        box.Oversized = (long)(box.X1 - box.X0 + 1) * (box.Y1 - box.Y0 + 1) * (box.Z1 - box.Z0 + 1) > MaxGroupRegionCells;
        return box;
    }

    /// <summary>
    /// #1409's differential check, when a test asked for it: the index equals one built from scratch over the same live regions — every cell's slots in key
    /// order, the oversized list, each region's box.
    /// </summary>
    private void VerifyRegionIndex(RegionIndex index, double pad)
    {
        // The check lives in its own method because it sorts with a lambda that captures index: C# allocates a captured parameter's closure at the ENTRY of
        // the method that owns it, before any early return — so with the check inline, every tick paid 24 B for a test that was off (AC-6, 2026-10-20).
        if (VerifyRegionIndexForTest)
        {
            VerifyRegionIndexCore(index, pad);
        }
    }

    private void VerifyRegionIndexCore(RegionIndex index, double pad)
    {
        var live = new List<int>();
        for (var slot = 0; slot < index.Slots; slot++)
        {
            if (index.Live[slot])
            {
                live.Add(slot);
            }
        }

        live.Sort((a, b) => index.Entries[a].Key.CompareTo(index.Entries[b].Key));
        var expected = new Dictionary<ulong, List<int>>();
        var oversized = new List<int>();
        var same = index.LiveCount == live.Count && index.SlotOfKey.Count == live.Count;
        foreach (var slot in live)
        {
            same &= index.SlotOfKey.TryGetValue(index.Entries[slot].Key, out var mapped) && mapped == slot;
            var box = BoxOf(in index.Entries[slot], pad);
            same &= box.Equals(index.Boxes[slot]);
            if (box.Oversized)
            {
                oversized.Add(slot);
                continue;
            }

            for (var cz = box.Z0; cz <= box.Z1; cz++)
            {
                for (var cy = box.Y0; cy <= box.Y1; cy++)
                {
                    for (var cx = box.X0; cx <= box.X1; cx++)
                    {
                        var cell = TEvent.Key(cx, cy, cz);
                        if (!expected.TryGetValue(cell, out var list))
                        {
                            expected[cell] = list = [];
                        }

                        list.Add(slot);
                    }
                }
            }
        }

        same &= expected.Count == index.BucketOfCell.Count;
        foreach (var (cell, slots) in expected)
        {
            same &= index.BucketOfCell.TryGetValue(cell, out var bucket)
                && index.Buckets[bucket].AsSpan(0, index.BucketCounts[bucket]).SequenceEqual(CollectionsMarshal.AsSpan(slots));
        }

        same &= index.Oversized.AsSpan(0, index.OversizedCount).SequenceEqual(CollectionsMarshal.AsSpan(oversized));
        if (!same)
        {
            RegionIndexMismatches++;
        }
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
        if (gs == null || gs.Generation != session.Generation || (uint)set >= (uint)gs.SetCount)
        {
            return GroupKeyState.NotHeld;
        }

        var pending = gs.Pending(set);
        var i = pending.IndexOf(key);
        if (i < 0)
        {
            return GroupKeyState.NotHeld;
        }

        return pending.Cursors[i] == GroupSessionState.Complete ? GroupKeyState.Complete : GroupKeyState.Filling;
    }

    internal override void WithholdCompletions(SessionId session, List<GroupRecord> records)
    {
        var gs = GroupStateAtOrNull(L(session));
        var write = 0;
        for (var read = 0; read < records.Count; read++)
        {
            var record = records[read];
            if (record.Kind != GroupRecordKind.Complete)
            {
                records[write++] = record;
                continue;
            }

            // HeldAll holds every member (CursorOf > netId), and the next fill completes the key; the fit withholds it again while an enter is owed. A
            // completion was decided by this frame's fill, so its pending side is the frame's own, never the committed one.
            if (gs != null && gs.Generation == session.Generation && (uint)record.Set < (uint)gs.SetCount)
            {
                var pending = gs.Pending(record.Set);
                var i = pending.IndexOf(record.Key);
                if (i >= 0 && pending.Cursors[i] == GroupSessionState.Complete)
                {
                    pending.Cursors[i] = GroupSessionState.HeldAll;
                }
            }
        }

        records.RemoveRange(write, records.Count - write);
    }

    internal override void ReadMemberKeys(int archetype, nint block, int slot, uint netId, ref GroupFold.KeySlots keys)
    {
        var groupKeys = _plans[archetype].GroupKeys;
        for (var g = 0; g < groupKeys.Length; g++)
        {
            keys[groupKeys[g].Slot] = CurrentKey((ushort)archetype, block, (byte)slot, netId, groupKeys[g].Slot);
        }
    }

    internal override bool GroupHoldsCommitted(SessionId session, int archetype, nint block, int slot, uint netId)
    {
        var gs = GroupStateAtOrNull(L(session));
        var groupKeys = _plans[archetype].GroupKeys;
        if (gs == null || gs.Generation != session.Generation || groupKeys.Length == 0)
        {
            return false;
        }

        // The keys the last published frame held, a pending RESET or not: the client still holds them until that RESET is published.
        for (var g = 0; g < groupKeys.Length; g++)
        {
            var key = CurrentKey((ushort)archetype, block, (byte)slot, netId, groupKeys[g].Slot);
            if (key != 0 && gs.Published(groupKeys[g].Set).CursorOf(key) > netId)
            {
                return true;
            }
        }

        return false;
    }

    // A slot handed to a new session holds no key: a generation number alone cannot tell two sessions apart, since every session slot counts its own.
    private protected override void ResetSlot(int local)
    {
        base.ResetSlot(local);
        if ((uint)local < (uint)_groupSessions.Length && _groupSessions[local] is { } gs)
        {
            gs.Clear();
            gs.Generation = 0;
        }
    }

    private GroupSessionState GroupStateOf(int local, ushort generation)
    {
        Debug.Assert((uint)local < (uint)_groupSessions.Length, "the group sessions are sized with the session slots");
        var gs = _groupSessions[local] ??= new GroupSessionState(GroupSets.Length);
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
            gs.Hold(reset);
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

    /// <summary>Whether the slot's session holds a key of any set — committed, or on its way to: what a profile naming no set must still release.</summary>
    private bool HoldsAnyGroup(int local, ushort generation)
    {
        if ((uint)local >= (uint)_groupSessions.Length || _groupSessions[local] is not { } gs || gs.Generation != generation)
        {
            return false;
        }

        for (var set = 0; set < gs.SetCount; set++)
        {
            if (gs.Published(set).Count != 0 || gs.Pending(set).Count != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A session whose observer is not a Sphere holds no key (groups are a Sphere's): the keys it held under one are dropped from the pending side. True
    /// when it held any — the caller's frame then resets, so the client drops them too and nothing a key held is left unaccounted for.
    /// </summary>
    private bool DropGroups(int local, ushort generation)
    {
        if (!HoldsAnyGroup(local, generation))
        {
            return false;
        }

        _groupSessions[local].Hold(reset: true);
        return true;
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
    /// <remarks>
    /// Its work grows with the keys the session holds and the frame's member events, never with <see cref="SubscriptionsOptions.MaxGroupsPerSession"/>
    /// (#1407): the held keys are walked in key order against what they are compared with, and only the filling keys are ordered for the fill.
    /// </remarks>
    /// <returns>How many enters the fill emitted.</returns>
    private int GatherGroups(int local, SessionId session, ProfileGroup[] groups, bool reset, int gap, uint tick, in Ball a, int oOriginX, int oOriginY,
        int oOriginZ, ReadOnlySpan<ushort> o, in Ball n, int nOriginX, int nOriginY, int nOriginZ, ReadOnlySpan<ushort> d, in ArchetypeSet archetypes,
        FrameWorkerScratch scratch, int budget, int releaseBudget, ref long leaves, ref long updates)
    {
        var gs = GroupStateOf(local, session.Generation);
        var max = MaxGroupsPerSession;

        // A RESET clears what the client holds: every key is filled and completed again (P11). Read through an empty view rather than cleared, so a frame
        // that is then not published leaves the committed keys as the client still has them.
        if (reset && gs.IsFilling())
        {
            Interlocked.Increment(ref GroupResetsMidFill);
        }

        gs.Reset = reset;

        // ── 1. The keys this frame moves to: the regions the anchor meets, the held ones under hysteresis; an explicit set's requested keys, a shared
        //       set's one. A set the profile no longer names moves to none ──────────────────────────────────────────────────────────────────────────
        gs.BeginFrame();
        scratch.FillOrderCount = 0;
        Span<long> sharedKey = stackalloc long[1];
        for (var g = 0; g < groups.Length; g++)
        {
            var group = groups[g];
            var set = group.Set;
            var compiled = GroupSets[set];
            var committed = gs.Committed(set);
            if (compiled.Selector != GroupSelector.Region)
            {
                // Not met by geometry (14 § 4): every key is at distance 0, so the fill takes them in key order; the bound was the request's.
                sharedKey[0] = compiled.SharedKey;
                var keys = compiled.Selector == GroupSelector.Shared ? sharedKey : Requests != null ? Requests.KeysOf(session, set) : default;
                var count = Math.Min(keys.Length, max);
                var pending = gs.BeginPending(set, count);
                var walk = 0;
                for (var k = 0; k < count; k++)
                {
                    var cursor = CommittedCursor(committed, keys[k], ref walk, out _);
                    pending.Keys[k] = keys[k];
                    pending.Cursors[k] = cursor;
                    if (cursor != GroupSessionState.Complete)
                    {
                        AddFillOrder(scratch, g, 0d, k);
                    }
                }

                if (count != 0)
                {
                    pending.Count = count;
                }

                continue;
            }

            // A 2D extent lies on the plane z = 0, where it was filed: its sessions look it up there, whatever their altitude. A cell's regions and the
            // oversized list are each in key order, so the committed keys are walked alongside them.
            var cz = Is2D(compiled.Region.ExtentType) ? CellZ(0d) : CellZ(n.Z);
            var cellKey = TEvent.Key(CellX(n.X), CellY(n.Y), cz);
            var index = _regionIndexes.Length > set ? _regionIndexes[set] : null;
            var met = 0;
            var inKeyOrder = true;
            if (index != null)
            {
                var cellWalk = 0;
                if (index.BucketOfCell.TryGetValue(cellKey, out var bucket))
                {
                    var slots = index.Buckets[bucket];
                    for (var i = 0; i < index.BucketCounts[bucket]; i++)
                    {
                        Meet(committed, ref cellWalk, index, group, compiled, slots[i], in n, scratch, ref met, ref inKeyOrder);
                    }
                }

                var oversizedWalk = 0;
                for (var i = 0; i < index.OversizedCount; i++)
                {
                    Meet(committed, ref oversizedWalk, index, group, compiled, index.Oversized[i], in n, scratch, ref met, ref inKeyOrder);
                }
            }

            // Kept keys first, then the nearest new ones; past the per-session bound a key is not held, counted, and the session told how many.
            var candidates = scratch.GroupMet.AsSpan(0, met);
            var over = 0;
            if (met > max)
            {
                SpanSort.Sort(candidates, default(GroupMetAdmission));
                over = met - max;
                Interlocked.Add(ref GroupsOverLimit, over);
                candidates = candidates[..max];
                SpanSort.Sort(candidates, default(GroupMetByKey));
            }
            else if (!inKeyOrder)
            {
                SpanSort.Sort(candidates, default(GroupMetByKey));
            }

            var held = gs.BeginPending(set, candidates.Length);
            for (var i = 0; i < candidates.Length; i++)
            {
                ref readonly var c = ref candidates[i];
                held.Keys[i] = c.Key;
                held.Cursors[i] = c.Cursor;
                if (c.Cursor != GroupSessionState.Complete)
                {
                    AddFillOrder(scratch, g, c.Distance, i);
                }
            }

            if (candidates.Length != 0)
            {
                held.Count = candidates.Length;
            }

            gs.SetPendingOver(set, over);
        }

        // A set the profile no longer names moves to none: its free side shrinks back, so what it held stops costing (#1407).
        for (var set = 0; set < gs.SetCount; set++)
        {
            if (!NamedBy(groups, set))
            {
                gs.BeginPending(set, 0);
            }
        }

        // The truncation, as a level: said whenever it differs from what the client was last told — after a RESET, which cleared that, again (#1407).
        for (var set = 0; set < gs.SetCount; set++)
        {
            var over = gs.PendingOver(set);
            if (over != gs.CommittedOver(set))
            {
                scratch.GroupRecords.Add(new GroupRecord(GroupRecordKind.Truncated, set, over));
            }
        }

        // ── 2. Fill cursors, fixed before any member is decided: the budget alone moves them, nearest filling key first. A completion spends at least one
        //       unit, so a frame carries no more GROUP_COMPLETE than the budget, however many keys are empty ────────────────────────────────────────────
        var spent = 0;
        var order = scratch.FillOrder.AsSpan(0, scratch.FillOrderCount);
        if (order.Length > 1)
        {
            order.Sort(default(GroupFillOrderComparer));
        }

        // Completions withheld last frame first (15 § 4.6): every member is held already, so each spends its unit and none waits for budget — there are
        // never more of them than one frame's completions, and a key a nearer fill starved would break "the first frame that owes no enter" (SUB-33).
        foreach (ref readonly var f in order)
        {
            var withheld = gs.Pending(groups[f.Group].Set);
            if (withheld.Cursors[f.Index] == GroupSessionState.HeldAll)
            {
                withheld.Cursors[f.Index] = GroupSessionState.Complete;
                spent++;
            }
        }

        foreach (ref readonly var f in order)
        {
            // A spent budget moves no cursor: no read. A key whose members are all gone completes on the next frame with budget left.
            if (spent >= budget || scratch.FillSpent >= scratch.FillAllowance)
            {
                break;
            }

            var set = groups[f.Group].Set;
            var pending = gs.Pending(set);
            var from = pending.Cursors[f.Index];
            if (from == GroupSessionState.Complete)
            {
                continue;
            }

            var members = ReadMembers(set, pending.Keys[f.Index], from, uint.MaxValue, scratch, sort: true, out var unresolved);
            var take = Math.Min(members, budget - spent);

            // The byte allowance (15 § 4.2): the longest prefix whose enters it admits, the last one allowed past it as a cell's last entity is.
            for (var m = 0; m < take; m++)
            {
                if (scratch.FillSpent >= scratch.FillAllowance)
                {
                    take = m;
                    break;
                }

                ref var member = ref scratch.FillMembers[m];
                scratch.FillSpent += MeterEnter(member.Archetype, member.Block, member.Slot);
            }

            // A member with no replication state yet — a positionless one whose block a held key is about to attach, or one the pool could not give a
            // block — cannot be sent, so the key is not complete while one is pending (14 § 5): the cursor passes what was taken and waits.
            var cursor = take < members ? scratch.FillMembers[take].NetId
                : unresolved > 0 ? (members > 0 ? scratch.FillMembers[members - 1].NetId + 1 : from)
                : GroupSessionState.Complete;
            pending.Cursors[f.Index] = cursor;
            spent += cursor == GroupSessionState.Complete ? Math.Max(take, 1) : take;
        }

        // ── 2b. Releases, bounded: past the release budget a key the session no longer meets stays held, its cursor as committed, and is released by a
        //        later frame — so a frame releasing thousands of keys at once cannot outgrow the frame (#1407) ────────────────────────────────────────
        DeferReleases(gs, scratch, releaseBudget);

        // ── 3. The member events of every key held before or now, folded per identity over the frame's window ────────────────────────────────────
        var first = gap > 0 ? tick - (uint)gap : tick;
        for (var t = first; t != tick + 1; t++)
        {
            var slot = _log[t % LogDepth];
            if (!slot.Valid || slot.Tick != t)
            {
                continue;
            }

            for (var set = 0; set < gs.SetCount; set++)
            {
                var matches = MatchHeld(slot.Members, slot.MemberCount, set, gs.Committed(set), gs.Pending(set), scratch);
                for (var m = 0; m < matches; m++)
                {
                    FoldEntry(slot, scratch.HeldMatches[m], t, set, scratch);
                }
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

            for (var set = 0; set < gs.SetCount; set++)
            {
                var matches = MatchHeld(unplaced.Members, unplaced.MemberCount, set, gs.Committed(set), gs.Pending(set), scratch);
                for (var m = 0; m < matches; m++)
                {
                    FoldUnplacedEntry(unplaced, scratch.HeldMatches[m], t, set, scratch);
                }
            }
        }

        // A fold holds only the events filed under held keys: a mask change made while the member sat under another key is not among them, and the first
        // of its own may be a later tick's. Its mask before the window is the window's own answer, read here for every folded member so the emission below
        // and the post-pass judge by the same mask the geometric catch-up does (P20).
        if (HasTags && scratch.GroupFolds.Count > 0)
        {
            foreach (var netId in scratch.GroupFolds.Keys)
            {
                ref var fold = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(scratch.GroupFolds, netId);
                if (_tagOffset[fold.Archetype] != 0)
                {
                    fold.TagsSeen = TryWindowOldMask(netId, _unplaced[fold.Archetype], first, out fold.WasTags);
                }
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
                continue;   // held by geometry on both sides: its update, with its distance LOD, is the geometry's — its visibility too
            }

            // Visibility (P19): the union is held only where the session sees the entity — before the frame by the mask it had then, after by its current one.
            var tagged = _tagOffset[f.Archetype] != 0;
            uint tagsWas = 0, tagsIs = 0;
            if (tagged)
            {
                FoldTagsOf(ref f, log, inSet, netId, out tagsWas, out tagsIs);
            }

            var was = (geoWas || KeysHeld(gs, true, f.Archetype, netId, ref f.Before)) && (!tagged || Sees(local, tagsWas));
            var isIn = (geoIs || ((f.LastFlags & PushEvent.HasNew) != 0 && KeysHeld(gs, false, f.Archetype, netId, ref f.After)))
                && (!tagged || Sees(local, tagsIs));
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

        // ── 4–5. Per set — the profile's in its order, then any other the session still holds — the fill and the releases ─────────────────────────
        for (var k = 0; k < groups.Length + gs.SetCount; k++)
        {
            var set = k < groups.Length ? groups[k].Set : k - groups.Length;
            if (k >= groups.Length && NamedBy(groups, set))
            {
                continue;
            }

            var committed = gs.Committed(set);
            var pending = gs.Pending(set);

            // ── 4. Fill: the members the cursors now take that have no event in the window (those were the events') ─────────────────────────────
            var walk = 0;
            for (var i = 0; i < pending.Count; i++)
            {
                var key = pending.Keys[i];
                var before = CommittedCursor(committed, key, ref walk, out _);
                var after = pending.Cursors[i];
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

                    // No event in the window, so its mask is the one it had: a member the session does not see is passed, never sent (P19).
                    if (!SeesNow(local, member.Archetype, member.Block, member.Slot))
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
            walk = 0;
            for (var i = 0; i < committed.Count; i++)
            {
                var key = committed.Keys[i];
                while (walk < pending.Count && pending.Keys[walk] < key)
                {
                    walk++;
                }

                if (walk < pending.Count && pending.Keys[walk] == key)
                {
                    continue;
                }

                var members = ReadMembers(set, key, 0u, committed.Cursors[i], scratch, sort: false, out _);
                for (var m = 0; m < members; m++)
                {
                    ref var member = ref scratch.FillMembers[m];
                    if (OwnedByLog(member, tick, gap))
                    {
                        continue;
                    }

                    // Never sent while the session did not see it, so it has nothing to leave.
                    if (!SeesNow(local, member.Archetype, member.Block, member.Slot))
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
        PostPass(local, gs, in archetypes, scratch);
        return entered;
    }

    private static bool NamedBy(ProfileGroup[] groups, int set)
    {
        foreach (var group in groups)
        {
            if (group.Set == set)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The committed cursor of <paramref name="key"/>, walking <paramref name="committed"/> from <paramref name="walk"/>: keys asked in ascending order cost
    /// one pass over the committed ones. 0 when it is not held, and <paramref name="held"/> then false.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CommittedCursor(HeldKeys committed, long key, ref int walk, out bool held)
    {
        while (walk < committed.Count && committed.Keys[walk] < key)
        {
            walk++;
        }

        held = walk < committed.Count && committed.Keys[walk] == key;
        return held ? committed.Cursors[walk] : 0u;
    }

    private static void AddFillOrder(FrameWorkerScratch scratch, int group, double distance, int index)
    {
        if (scratch.FillOrderCount == scratch.FillOrder.Length)
        {
            Array.Resize(ref scratch.FillOrder, scratch.FillOrderCount * 2);
        }

        scratch.FillOrder[scratch.FillOrderCount++] = new GroupFillOrder { Group = group, Distance = distance, Index = index };
    }

    /// <summary>
    /// Keeps held, past <paramref name="releaseBudget"/> releases this frame, the keys the session held and no longer holds: each goes back into the pending
    /// keys with its committed cursor — still held, still filling where it was — to be released by a later frame (#1407).
    /// </summary>
    private void DeferReleases(GroupSessionState gs, FrameWorkerScratch scratch, int releaseBudget)
    {
        var released = 0;
        for (var set = 0; set < gs.SetCount; set++)
        {
            var committed = gs.Committed(set);
            if (committed.Count == 0)
            {
                continue;
            }

            var pending = gs.Pending(set);
            var deferred = 0;
            var walk = 0;
            for (var i = 0; i < committed.Count; i++)
            {
                var key = committed.Keys[i];
                while (walk < pending.Count && pending.Keys[walk] < key)
                {
                    walk++;
                }

                if (walk < pending.Count && pending.Keys[walk] == key)
                {
                    continue;
                }

                if (released < releaseBudget)
                {
                    released++;
                    continue;
                }

                if (deferred == scratch.GroupMet.Length)
                {
                    Array.Resize(ref scratch.GroupMet, deferred * 2);
                }

                scratch.GroupMet[deferred++] = new GroupMet { Key = key, Cursor = committed.Cursors[i] };
            }

            if (deferred == 0)
            {
                continue;
            }

            Interlocked.Add(ref GroupReleasesDeferred, deferred);

            // Merged in from the back: both runs are in key order and share no key.
            var count = pending.Count;
            pending = gs.GrowPending(set, count + deferred);
            var p = count - 1;
            var q = deferred - 1;
            for (var w = count + deferred - 1; q >= 0; w--)
            {
                if (p >= 0 && pending.Keys[p] > scratch.GroupMet[q].Key)
                {
                    pending.Keys[w] = pending.Keys[p];
                    pending.Cursors[w] = pending.Cursors[p];
                    p--;
                }
                else
                {
                    pending.Keys[w] = scratch.GroupMet[q].Key;
                    pending.Cursors[w] = scratch.GroupMet[q].Cursor;
                    q--;
                }
            }

            pending.Count = count + deferred;
        }
    }

    /// <summary>
    /// Step 1's test for one region of a set: met within the reach, kept within reach plus margin when the committed keys hold it. Regions are asked in key
    /// order within a run, so <paramref name="walk"/> walks the committed keys once.
    /// </summary>
    private void Meet(HeldKeys committed, ref int walk, RegionIndex index, ProfileGroup group, CompiledGroupSet compiled, int region, in Ball n,
        FrameWorkerScratch scratch, ref int met, ref bool inKeyOrder)
    {
        ref var r = ref index.Entries[region];
        var distance = ExtentDistance(in r, n.X, n.Y, n.Z);
        var cursor = CommittedCursor(committed, r.Key, ref walk, out var kept);
        if (distance > group.ReachM && !(kept && distance <= group.ReachM + compiled.LeaveMarginM))
        {
            return;
        }

        if (met == scratch.GroupMet.Length)
        {
            Array.Resize(ref scratch.GroupMet, met * 2);
        }

        inKeyOrder &= met == 0 || scratch.GroupMet[met - 1].Key < r.Key;
        scratch.GroupMet[met++] = new GroupMet { Key = r.Key, Distance = distance, Kept = kept, Cursor = cursor };
    }

    /// <summary>
    /// Whether some set's key held the member, before the frame (<paramref name="committed"/>) or after it: the key in the session's keys of that set, and the
    /// member below the key's fill cursor.
    /// </summary>
    private bool KeysHeld(GroupSessionState gs, bool committed, ushort archetype, uint netId, ref GroupFold.KeySlots keys)
    {
        var groupKeys = _plans[archetype].GroupKeys;
        for (var g = 0; g < groupKeys.Length; g++)
        {
            var key = keys[groupKeys[g].Slot];
            if (key == 0)
            {
                continue;
            }

            var set = groupKeys[g].Set;
            var cursor = committed ? gs.Committed(set).CursorOf(key) : gs.Pending(set).CursorOf(key);
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
            SpanSort.Sort(scratch.FillMembers.AsSpan(0, count), default(GroupFillMemberOrder));
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
            if (move.Watch != member.MoveWatch || ClusterFieldReader.DecodeRaw(move.Raw, member.Field.Type) != key)
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


    /// <summary>
    /// A folded member's masks before and after the frame (P20): a catch-up's geometric entry when the window met it there — the same entry geometry decided
    /// on — else the fold's own; the current mask after, 0 for a leave.
    /// </summary>
    private void FoldTagsOf(ref GroupFold f, LogTable log, bool inSet, uint netId, out uint was, out uint isNow)
    {
        var index = log != null && inSet ? log.IndexOf(netId) : -1;
        if (index >= 0)
        {
            EntryTags(log, ref log.Entries[index], out was, out isNow);
            return;
        }

        isNow = (f.LastFlags & PushEvent.HasNew) != 0 ? MaskAt(f.Archetype, f.Block, f.Slot) : 0u;
        was = f.TagsSeen ? f.WasTags : isNow;
    }

    /// <summary>
    /// The entries of one tick's member log in <paramref name="set"/> filed under a key held before (<paramref name="committed"/>) or after
    /// (<paramref name="pending"/>) the frame, into <see cref="FrameWorkerScratch.HeldMatches"/> in log order: each filing tested against the held keys when
    /// the filings are fewer, else each held key looked up among the filings from where the last ended — O(min · log max), never per held key and filing.
    /// </summary>
    /// <returns>How many entries matched.</returns>
    internal static int MatchHeld(MemberEntry[] log, int logCount, int set, HeldKeys committed, HeldKeys pending, FrameWorkerScratch scratch)
    {
        if (committed.Count == 0 && pending.Count == 0)
        {
            return 0;
        }

        var lo = EntryLowerBound(log, 0, logCount, set, long.MinValue);
        var hi = lo == logCount || log[lo].Set != set ? lo : EntryLowerBound(log, lo, logCount, set + 1, long.MinValue);
        if (lo == hi)
        {
            return 0;
        }

        var matches = 0;
        if (hi - lo <= committed.Count + pending.Count)
        {
            var last = 0L;
            var held = false;
            for (var m = lo; m < hi; m++)
            {
                var key = log[m].Key;
                if (m == lo || key != last)
                {
                    last = key;
                    held = committed.IndexOf(key) >= 0 || pending.IndexOf(key) >= 0;
                }

                if (held)
                {
                    AddMatch(scratch, ref matches, m);
                }
            }

            return matches;
        }

        // Each distinct key of the two sides, in key order.
        var i = 0;
        var j = 0;
        var from = lo;
        while ((i < committed.Count || j < pending.Count) && from < hi)
        {
            long key;
            if (j >= pending.Count || (i < committed.Count && committed.Keys[i] < pending.Keys[j]))
            {
                key = committed.Keys[i++];
            }
            else if (i >= committed.Count || pending.Keys[j] < committed.Keys[i])
            {
                key = pending.Keys[j++];
            }
            else
            {
                key = committed.Keys[i++];
                j++;
            }

            var m = EntryLowerBound(log, from, hi, set, key);
            for (; m < hi && log[m].Key == key; m++)
            {
                AddMatch(scratch, ref matches, m);
            }

            from = m;
        }

        return matches;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddMatch(FrameWorkerScratch scratch, ref int matches, int entry)
    {
        if (matches == scratch.HeldMatches.Length)
        {
            Array.Resize(ref scratch.HeldMatches, matches * 2);
        }

        scratch.HeldMatches[matches++] = entry;
    }

    /// <summary>The first entry in [<paramref name="lo"/>, <paramref name="hi"/>) at or past (<paramref name="set"/>, <paramref name="key"/>).</summary>
    private static int EntryLowerBound(MemberEntry[] log, int lo, int hi, int set, long key)
    {
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            ref var m = ref log[mid];
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

    /// <summary>Folds one member entry of a held key into the session's per-identity fold.</summary>
    private void FoldEntry(TickLog slot, int index, uint t, int set, FrameWorkerScratch scratch)
    {
        ref var entry = ref slot.Members[index];
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

        // A set's key before the window is its first entry's old key — whichever set's bucket met the identity first, and in whichever tick — and its key
        // after is its last entry's new one.
        if ((fold.SeenSlots & (1 << keySlot)) == 0)
        {
            fold.Before[keySlot] = entry.Old;
            fold.SeenSlots |= (byte)(1 << keySlot);
        }

        fold.After[keySlot] = entry.New;
        if (existed && fold.LastTick == t)
        {
            return;   // the same event, met again under its other key
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

    /// <summary>Folds one positionless member entry of a held key into the session's per-identity fold: no position, no cell.</summary>
    private void FoldUnplacedEntry(UnplacedTick slot, int index, uint t, int set, FrameWorkerScratch scratch)
    {
        ref var entry = ref slot.Members[index];
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
            return;
        }

        fold.LastFlags = e.Flags;
        fold.Block = e.Block;
        fold.Slot = e.Slot;
        fold.Archetype = e.Archetype;
        fold.Groups |= e.Groups;
        fold.LastTick = t;
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

    /// <summary>
    /// The union with geometry (P9), on the geometric steps' own records: an enter of a member its keys already held before the frame is dropped, and so is a
    /// leave of a member its keys still hold after it. The group steps' records pass untouched — they were decided on the union.
    /// </summary>
    private void PostPass(int local, GroupSessionState gs, in ArchetypeSet archetypes, FrameWorkerScratch scratch)
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
                if (!MemberKeysHeld(local, gs, true, r, scratch))
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
                if (!MemberKeysHeld(local, gs, false, r, scratch))
                {
                    leavesList[keep++] = r;
                }
            }

            scratch.Truncate(a, FrameListKind.Leave, keep);
        }
    }

    /// <summary>
    /// A record's member, held by one of its keys before or after the frame — and seen by the session then (P19): its fold's keys and masks when it had member
    /// events, else its entry's.
    /// </summary>
    private bool MemberKeysHeld(int local, GroupSessionState gs, bool committed, in FrameRecord r, FrameWorkerScratch scratch)
    {
        if (scratch.GroupFolds.TryGetValue(r.NetId, out var fold))
        {
            if (!committed && (fold.LastFlags & PushEvent.HasNew) == 0)
            {
                return false;
            }

            if (_tagOffset[r.Archetype] != 0)
            {
                var tagsIs = (fold.LastFlags & PushEvent.HasNew) != 0 ? MaskAt(fold.Archetype, fold.Block, fold.Slot) : 0u;
                if (!Sees(local, committed && fold.TagsSeen ? fold.WasTags : tagsIs))
                {
                    return false;
                }
            }

            return committed ? KeysHeld(gs, true, r.Archetype, r.NetId, ref fold.Before) : KeysHeld(gs, false, r.Archetype, r.NetId, ref fold.After);
        }

        if (!SeesNow(local, r.Archetype, r.Block, r.Slot))
        {
            return false;
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
