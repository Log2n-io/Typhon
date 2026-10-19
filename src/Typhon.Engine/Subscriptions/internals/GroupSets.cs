using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>How a key or extent field is read out of a cluster: the component's place in the cluster, the field's in the component, its type.</summary>
internal readonly struct ClusterFieldReader
{
    public readonly int ComponentOffsetInCluster;
    public readonly int ComponentSize;
    public readonly int FieldOffsetInComponent;
    public readonly FieldType Type;

    public ClusterFieldReader(int componentOffsetInCluster, int componentSize, int fieldOffsetInComponent, FieldType type)
    {
        ComponentOffsetInCluster = componentOffsetInCluster;
        ComponentSize = componentSize;
        FieldOffsetInComponent = fieldOffsetInComponent;
        Type = type;
    }

    /// <summary>The field's address for <paramref name="slot"/> of the cluster at <paramref name="clusterBase"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe byte* At(byte* clusterBase, int slot) => clusterBase + ComponentOffsetInCluster + (slot * ComponentSize) + FieldOffsetInComponent;

    /// <summary>An integer field's value, sign- or zero-extended to 64 bits; a <c>ulong</c> above <see cref="long.MaxValue"/> keeps its bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe long ReadKey(byte* clusterBase, int slot)
    {
        var p = At(clusterBase, slot);
        return Type switch
        {
            FieldType.Byte => *(sbyte*)p,
            FieldType.UByte => *p,
            FieldType.Short => *(short*)p,
            FieldType.UShort or FieldType.Char => *(ushort*)p,
            FieldType.Int => *(int*)p,
            FieldType.UInt => *(uint*)p,
            _ => *(long*)p,
        };
    }

    /// <summary>A <c>u32</c> field's value: the visibility tag mask (14 § 6).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe uint ReadMask(byte* clusterBase, int slot) => *(uint*)At(clusterBase, slot);

    /// <summary>A key logged as raw bytes (<see cref="GroupKeyMove.Raw"/>), read as <paramref name="type"/> reads it: sign- or zero-extended like <see cref="ReadKey"/>.</summary>
    public static long DecodeRaw(ulong raw, FieldType type) => type switch
    {
        FieldType.Byte => (sbyte)raw,
        FieldType.UByte => (byte)raw,
        FieldType.Short => (short)raw,
        FieldType.UShort or FieldType.Char => (ushort)raw,
        FieldType.Int => (int)raw,
        FieldType.UInt => (uint)raw,
        _ => (long)raw,
    };

    /// <summary>Whether <paramref name="key"/> is a value a field of <paramref name="type"/> can hold: what a requested or a shared key must be.</summary>
    public static bool Fits(long key, FieldType type) => type switch
    {
        FieldType.Byte => key is >= sbyte.MinValue and <= sbyte.MaxValue,
        FieldType.UByte => key is >= 0 and <= byte.MaxValue,
        FieldType.Short => key is >= short.MinValue and <= short.MaxValue,
        FieldType.UShort or FieldType.Char => key is >= 0 and <= ushort.MaxValue,
        FieldType.Int => key is >= int.MinValue and <= int.MaxValue,
        FieldType.UInt => key is >= 0 and <= uint.MaxValue,
        _ => true,
    };

    /// <summary>Whether <paramref name="type"/> is an integer key type a group key may be.</summary>
    public static bool IsIntegerKey(FieldType type) =>
        type is FieldType.Byte or FieldType.UByte or FieldType.Short or FieldType.UShort or FieldType.Char or FieldType.Int or FieldType.UInt
            or FieldType.Long or FieldType.ULong;

    /// <summary>Whether an integer key reads sign-extended (<see cref="ReadKey"/>): a region and its members compare keys as 64-bit values.</summary>
    public static bool IsSignedKey(FieldType type) => type is FieldType.Byte or FieldType.Short or FieldType.Int or FieldType.Long;
}

/// <summary>One region a session meets this frame: its key, its distance, and whether the committed keys already hold it (and its cursor then).</summary>
internal struct GroupMet
{
    public long Key;
    public double Distance;
    public bool Kept;
    public uint Cursor;
}

/// <summary>Kept keys first, then the nearest: the order the per-session bound admits keys in.</summary>
internal readonly struct GroupMetAdmission : IComparer<GroupMet>
{
    public int Compare(GroupMet x, GroupMet y) => x.Kept != y.Kept ? (x.Kept ? -1 : 1) : x.Distance.CompareTo(y.Distance);
}

/// <summary>By key: the order a session's held keys are kept in.</summary>
internal readonly struct GroupMetByKey : IComparer<GroupMet>
{
    public int Compare(GroupMet x, GroupMet y) => x.Key.CompareTo(y.Key);
}

/// <summary>By netId: the fill's order.</summary>
internal readonly struct GroupFillMemberOrder : IComparer<GroupFillMember>
{
    public int Compare(GroupFillMember x, GroupFillMember y) => x.NetId.CompareTo(y.NetId);
}

/// <summary>The wire's group record kinds in a <c>SOURCES</c> block (14 § 3.5, P12).</summary>
internal static class GroupRecordKind
{
    public const byte Complete = Typhon.Protocol.SourceStatus.GroupComplete;
    public const byte Released = Typhon.Protocol.SourceStatus.GroupReleased;

    /// <summary>
    /// A Region set's observer met more keys than the session may hold: the record's key carries how many were left out, 0 once none is (#1407).
    /// </summary>
    public const byte Truncated = Typhon.Protocol.SourceStatus.GroupTruncated;
}

/// <summary>A group lifecycle record a session's next frame carries.</summary>
internal readonly record struct GroupRecord(byte Kind, int Set, long Key);

/// <summary>A member of a key being filled or released: its identity and where its replication entry is.</summary>
internal struct GroupFillMember
{
    public uint NetId;
    public nint Block;
    public byte Slot;
    public ushort Archetype;
}

/// <summary>
/// One identity's member events since a session's last frame, for the keys the session holds (14 § 3.8): the first old position and keys, the last new ones,
/// the union of what changed. The group path's twin of the push log's fold.
/// </summary>
internal struct GroupFold
{
    public const int MaxKeySlots = 4;

    public byte FirstFlags;
    public byte LastFlags;
    public byte SeenSlots;
    public float OldX, OldY, OldZ, NewX, NewY, NewZ;
    public ulong OldCell, NewCell;
    public nint Block;
    public byte Slot;
    public ushort Archetype;
    public byte Groups;
    public bool Segment;
    public uint LastTick;
    public KeySlots Before;
    public KeySlots After;

    // Visibility tags (14 § 6): the mask before the window's first change of it, once one was met.
    public uint WasTags;
    public bool TagsSeen;

    [InlineArray(MaxKeySlots)]
    public struct KeySlots
    {
        private long _element0;
    }
}

/// <summary>A filling key of this frame's pending keys, for the fill's order: its profile group, its distance, its index among the keys.</summary>
internal struct GroupFillOrder
{
    public int Group;
    public double Distance;
    public int Index;
}

/// <summary>Profile group first, then nearest, then key order: the order the fill spends the enter budget in.</summary>
internal readonly struct GroupFillOrderComparer : IComparer<GroupFillOrder>
{
    public int Compare(GroupFillOrder x, GroupFillOrder y)
    {
        var c = x.Group.CompareTo(y.Group);
        if (c != 0)
        {
            return c;
        }

        c = x.Distance.CompareTo(y.Distance);
        return c != 0 ? c : x.Index.CompareTo(y.Index);
    }
}

/// <summary>
/// One side — committed or pending — of a session's keys in one group set: sorted by key, each with its fill cursor. Its capacity is fixed for its life: a
/// side that outgrows it is given a new holder, so a reader that took the holder once never sees a count past its arrays (#1407).
/// </summary>
internal sealed class HeldKeys
{
    /// <summary>The empty side every set starts with; never written — a shared cache line every worker would otherwise dirty.</summary>
    public static readonly HeldKeys Empty = new(0);

    public readonly long[] Keys;
    public readonly uint[] Cursors;
    public int Count;

    public HeldKeys(int capacity)
    {
        Keys = capacity == 0 ? [] : new long[capacity];
        Cursors = capacity == 0 ? [] : new uint[capacity];
    }

    public int Capacity => Keys.Length;

    public ReadOnlySpan<long> KeySpan => Keys.AsSpan(0, Count);

    /// <summary>The index of <paramref name="key"/>, or a negative number when it is not held.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IndexOf(long key) => Count == 0 ? -1 : Keys.AsSpan(0, Count).BinarySearch(key);

    /// <summary>The cursor of <paramref name="key"/>, or 0 when it is not held.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint CursorOf(long key)
    {
        var i = IndexOf(key);
        return i >= 0 ? Cursors[i] : 0u;
    }
}

/// <summary>
/// A session's held keys per group set (14 § 3.8): the committed ones — what the client holds — and the pending ones this frame moves to, each sorted by key
/// with its fill cursor (a netId; <see cref="Complete"/> when every member is held). Committed with the frame, like the geometry.
/// </summary>
/// <remarks>
/// Costs what it holds (#1407): each side of each set is a <see cref="HeldKeys"/> grown with the keys it holds, never sized by
/// <see cref="SubscriptionsOptions.MaxGroupsPerSession"/>. A frame's pending side is written into the holder the last commit left free, and the commit swaps
/// the two — no copy. A frame that wrote no pending side (an unplaced frame) leaves the committed one as it is.
/// </remarks>
internal sealed class GroupSessionState
{
    public const uint Complete = uint.MaxValue;

    /// <summary>A side's first capacity: the common session holds a handful of keys.</summary>
    internal const int InitialCapacity = 8;

    public ushort Generation;
    private readonly HeldKeys[] _committed;
    private readonly HeldKeys[] _pending;
    private readonly int[] _cOver;
    private readonly int[] _pOver;
    private bool _pendingWritten;

    /// <summary>This frame is a RESET: the committed keys read as none, and are kept as they are until it is published.</summary>
    public bool Reset;

    public GroupSessionState(int sets)
    {
        _committed = new HeldKeys[sets];
        _pending = new HeldKeys[sets];
        _cOver = new int[sets];
        _pOver = new int[sets];
        Array.Fill(_committed, HeldKeys.Empty);
        Array.Fill(_pending, HeldKeys.Empty);
    }

    public int SetCount => _committed.Length;

    /// <summary>The committed keys of <paramref name="set"/> as this frame reads them: none on a RESET.</summary>
    public HeldKeys Committed(int set) => Reset ? HeldKeys.Empty : _committed[set];

    /// <summary>The committed keys as the last published frame left them, a pending RESET or not: what a command is judged against (SUB-26).</summary>
    public HeldKeys Published(int set) => _committed[set];

    /// <summary>The keys this frame moves <paramref name="set"/> to: the committed ones when the frame wrote none.</summary>
    public HeldKeys Pending(int set) => _pendingWritten ? _pending[set] : _committed[set];

    /// <summary>How many keys of a Region set this frame's observer met past the bound and left out (the session is told, GROUP_TRUNCATED).</summary>
    public int PendingOver(int set) => _pendingWritten ? _pOver[set] : _cOver[set];

    /// <summary>The count the client was last told: 0 on a RESET, which clears what it holds.</summary>
    public int CommittedOver(int set) => Reset ? 0 : _cOver[set];

    /// <summary>Whether this frame leaves <paramref name="set"/>'s keys and their cursors as the committed ones read: a steady state, the usual case.</summary>
    public bool PendingIsCommitted(int set)
    {
        if (!_pendingWritten)
        {
            return true;
        }

        var c = Committed(set);
        var p = _pending[set];
        return p.Count == c.Count && p.KeySpan.SequenceEqual(c.KeySpan) && p.Cursors.AsSpan(0, p.Count).SequenceEqual(c.Cursors.AsSpan(0, c.Count));
    }

    /// <summary>Opens this frame's pending side: every set's keys empty and its over-count 0, until the gather writes them.</summary>
    public void BeginFrame()
    {
        for (var s = 0; s < _pending.Length; s++)
        {
            if (_pending[s].Count != 0)
            {
                _pending[s].Count = 0;
            }

            _pOver[s] = 0;
        }

        _pendingWritten = true;
    }

    /// <summary>
    /// <paramref name="set"/>'s pending side, emptied, with room for <paramref name="needed"/> keys: the free holder when it fits and is not four times too
    /// large, else a new one.
    /// </summary>
    public HeldKeys BeginPending(int set, int needed)
    {
        Debug.Assert(_pendingWritten, "BeginFrame opens the pending side");
        var h = _pending[set];
        if (h.Capacity < needed || (h.Capacity > 64 && needed < h.Capacity / 4))
        {
            h = needed == 0 ? HeldKeys.Empty : new HeldKeys(CapacityFor(needed));
            _pending[set] = h;
        }
        else if (h.Count != 0)
        {
            h.Count = 0;
        }

        return h;
    }

    /// <summary><paramref name="set"/>'s pending side with room for <paramref name="needed"/> keys, its keys kept.</summary>
    public HeldKeys GrowPending(int set, int needed)
    {
        var h = _pending[set];
        if (h.Capacity >= needed)
        {
            return h;
        }

        var grown = new HeldKeys(CapacityFor(needed)) { Count = h.Count };
        h.Keys.AsSpan(0, h.Count).CopyTo(grown.Keys);
        h.Cursors.AsSpan(0, h.Count).CopyTo(grown.Cursors);
        _pending[set] = grown;
        return grown;
    }

    public void SetPendingOver(int set, int over) => _pOver[set] = over;

    private static int CapacityFor(int needed) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(InitialCapacity, needed));

    /// <summary>A new session in the slot: nothing held.</summary>
    public void Clear()
    {
        Array.Fill(_committed, HeldKeys.Empty);
        Array.Fill(_pending, HeldKeys.Empty);
        Array.Clear(_cOver);
        Array.Clear(_pOver);
        _pendingWritten = false;
        Reset = false;
    }

    /// <summary>An unplaced frame holds what was held: the committed keys — none when the frame is a RESET, which clears the client's store.</summary>
    public void Hold(bool reset)
    {
        Reset = false;
        if (reset)
        {
            BeginFrame();
            for (var s = 0; s < _pending.Length; s++)
            {
                BeginPending(s, 0);
            }

            return;
        }

        _pendingWritten = false;
    }

    /// <summary>Whether a committed key is still filling — the state as the client holds it, whatever this frame's RESET view reads.</summary>
    public bool IsFilling()
    {
        foreach (var c in _committed)
        {
            if (c.Cursors.AsSpan(0, c.Count).IndexOfAnyExcept(Complete) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every committed and pending key, to the hub's held set: the client's keys and the ones this frame moves to, whatever a RESET reads.</summary>
    public void CollectKeys(PushHub hub, int set)
    {
        var c = _committed[set];
        for (var i = 0; i < c.Count; i++)
        {
            hub.AddWanted(set, c.Keys[i]);
        }

        var p = Pending(set);
        if (p != c && !p.KeySpan.SequenceEqual(c.KeySpan))
        {
            for (var i = 0; i < p.Count; i++)
            {
                hub.AddWanted(set, p.Keys[i]);
            }
        }
    }

    /// <summary>The frame is published: its pending sides become the committed ones, by swapping the holders.</summary>
    public void Commit()
    {
        if (_pendingWritten)
        {
            for (var s = 0; s < _committed.Length; s++)
            {
                (_committed[s], _pending[s]) = (_pending[s], _committed[s]);
                _cOver[s] = _pOver[s];
            }

            _pendingWritten = false;
        }

        Reset = false;
    }

    /// <summary>A side's capacity, for the tests that a bound costs only what is held.</summary>
    internal int CapacityForTest(int set, bool committed) => committed ? _committed[set].Capacity : _pending[set].Capacity;
}

/// <summary>A profile observer's interest in one group set, resolved: the set's index and the reach its regions are met at.</summary>
internal readonly record struct ProfileGroup(int Set, double ReachM);

/// <summary>One group set a member archetype's push reads the key of (14 § 3.8).</summary>
internal readonly struct CompiledGroupKey
{
    /// <summary>The group set's index: its number on the wire.</summary>
    public readonly int Set;

    /// <summary>The key's 8-byte slot in the cold entry's group-key region.</summary>
    public readonly int Slot;

    /// <summary>Where the key field is in the cluster.</summary>
    public readonly ClusterFieldReader Field;

    public CompiledGroupKey(int set, int slot, ClusterFieldReader field)
    {
        Set = set;
        Slot = slot;
        Field = field;
    }
}

/// <summary>A member archetype of a compiled group set: its plan index, its key slot, and the field whose index lists the members of a key.</summary>
internal sealed class CompiledGroupMember
{
    public int Plan;
    public int KeySlot;
    public ArchetypeMetadata Archetype;

    /// <summary>The component's slot in the archetype, and the field's stable id: what finds the field's B+Tree among the archetype's index slots.</summary>
    public byte ComponentSlot;

    public int FieldOffsetInComponent;
    public ClusterFieldReader Field;

    /// <summary>The key field's B+Tree, resolved from the archetype's index slots on first use.</summary>
    public BTreeBase<PersistentStore> Tree;

    /// <summary>The member has no position (14 § 5): served through its keys alone, its replication state only while a held key needs it.</summary>
    public bool Positionless;

    /// <summary>The key's bytes in the component.</summary>
    public int FieldSize;

    /// <summary>The key is on a Versioned component, which an off-DAG commit may write (EW-02).</summary>
    public bool Versioned;

    /// <summary>
    /// The archetype's log of key moves, and this field's watch in it: a positionless member with a Versioned key only, whose index an off-DAG commit can
    /// move between the projection and the gather. Null otherwise.
    /// </summary>
    public GroupKeyMoveLog Moves;

    public int MoveWatch = -1;
}

/// <summary>A compiled region selector: the region archetype and where its key and extent are.</summary>
internal sealed class CompiledRegion
{
    public ArchetypeMetadata Archetype;

    /// <summary>The region archetype's cluster state, bound by the runtime once the engine's archetypes are initialized.</summary>
    public ArchetypeClusterState ClusterState;
    public ClusterFieldReader Key;
    public ClusterFieldReader Extent;
    public SpatialFieldType ExtentType;
}

/// <summary>A group set resolved against the engine's schema at <c>Start</c> (14 § 3, § 3.8).</summary>
internal sealed class CompiledGroupSet
{
    public int Index;
    public string Name;

    /// <summary>What decides which keys a session holds: a region (<see cref="Region"/>), the session's requests, or <see cref="SharedKey"/>.</summary>
    public GroupSelector Selector;

    /// <summary>A shared set's one key; 0 otherwise.</summary>
    public long SharedKey;

    /// <summary>An explicit set's verdict on a client's request; null when clients may not ask.</summary>
    public GroupRequestHandler Admit;

    /// <summary>The key's integer type: the region key's, or an explicit or shared set's first member's.</summary>
    public FieldType KeyType;

    /// <summary>The region selector; null for an explicit or a shared set.</summary>
    public CompiledRegion Region;
    public CompiledGroupMember[] Members;
    public double LeaveMarginM;

    /// <summary>The widest reach any profile declares for this set: the region index files a region under every cell within it plus the margin.</summary>
    public double MaxReachM;

    /// <summary>The member of this set for plan <paramref name="plan"/>, or null.</summary>
    public CompiledGroupMember MemberOf(int plan)
    {
        foreach (var m in Members)
        {
            if (m.Plan == plan)
            {
                return m;
            }
        }

        return null;
    }
}

/// <summary>
/// Resolves the registry's group sets against the engine's schema and the compiled plans: each member's key field, refused unless it is an integer field
/// with a sparse multi-value index; each region's key and extent; and the cold-entry key region of every member plan.
/// </summary>
internal static class GroupSetCompiler
{
    public static CompiledGroupSet[] Compile(SubscriptionsRegistry registry, DatabaseEngine engine, CompiledProjectionPlan[] plans)
    {
        var declared = registry.GroupSets;
        if (declared.Count == 0)
        {
            return [];
        }

        var sets = new CompiledGroupSet[declared.Count];
        var keysByPlan = new List<CompiledGroupKey>[plans.Length];
        for (var s = 0; s < declared.Count; s++)
        {
            var d = declared[s];
            var set = new CompiledGroupSet
            {
                Index = s,
                Name = d.Name,
                LeaveMarginM = d.LeaveMarginM,
                Selector = d.Selector,
                SharedKey = d.SharedKey,
                Admit = d.Admit,
                Region = d.Selector == GroupSelector.Region ? CompileRegion(d, engine) : null,
                MaxReachM = d.Selector == GroupSelector.Region ? MaxReachOf(registry, d.Name) : 0d,
            };
            set.KeyType = set.Region?.Key.Type ?? FieldType.None;

            var members = new CompiledGroupMember[d.Members.Count];
            for (var m = 0; m < members.Length; m++)
            {
                var member = d.Members[m];
                var plan = PlanOf(plans, member.Archetype);
                if (plan < 0)
                {
                    throw new InvalidOperationException(
                        $"Group set '{d.Name}' names member '{member.Archetype.Name}', which declares no projection. A member is entered like any replicated "
                        + "entity, so declare it with subs.Archetype<" + member.Archetype.Name + ">(...).");
                }

                var meta = Resolve(member.Archetype);
                var (slot, field, reader) = ResolveField(meta, engine, member.KeyComponent, member.KeyField, $"group set '{d.Name}' member key");
                if (!ClusterFieldReader.IsIntegerKey(field.Type))
                {
                    throw new InvalidOperationException(
                        $"Group set '{d.Name}': '{meta.Name}.{member.KeyField}' is a {field.Type}. A group key is an integer field (I8 … U64), 0 for no group.");
                }

                // The set's key type: its region's, or for an explicit or shared set its widest member's — the range a requested or a shared key must fit.
                if (set.KeyType == FieldType.None || (set.Region == null && KeySize(field.Type) > KeySize(set.KeyType)
                    && ClusterFieldReader.IsSignedKey(field.Type) == ClusterFieldReader.IsSignedKey(set.KeyType)))
                {
                    set.KeyType = field.Type;
                }

                if (ClusterFieldReader.IsSignedKey(field.Type) != ClusterFieldReader.IsSignedKey(set.KeyType))
                {
                    // Keys compare as 64-bit values, so an i32 -1 and a u32 0xFFFFFFFF would name two groups for one bit pattern.
                    throw new InvalidOperationException(
                        $"Group set '{d.Name}': '{meta.Name}.{member.KeyField}' is a {field.Type} and the set's key a {set.KeyType}. A member key and its "
                        + "set's key are both signed or both unsigned.");
                }

                var table = engine.GetComponentTable(meta._slotToComponentType[slot]);
                if (table?.StorageMode == StorageMode.Transient)
                {
                    // #1162: a Transient index is not re-valued on migration, so a member's location in it can go stale; refused until it is.
                    throw new NotSupportedException(
                        $"Group set '{d.Name}': '{meta.Name}.{member.KeyField}' is on a Transient component. A group key field is SingleVersion until "
                        + "Transient indexes follow migration (#1162).");
                }

                // A Versioned key is admitted: its commit copies the HEAD into the cluster slot, marks it (the tap) and moves the index entry, all before the
                // fence. An off-DAG commit (EW-02) could move the index between the projection and the gather. A positioned member's archetype has a
                // spatial field, which EW-02 forbids an off-DAG thread to write; a positionless member has no such guard, and the runtime gives its
                // archetype a log of key moves that the fill reads beside the index (14 § 5).

                if (!field.HasIndex || !field.IndexAllowMultiple || !field.IndexSparse)
                {
                    throw new InvalidOperationException(
                        $"Group set '{d.Name}': '{meta.Name}.{member.KeyField}' needs [Index(AllowMultiple = true, Sparse = true)]. The set reads that index to "
                        + "fill a key a session comes to hold, and keeps no copy of the relation; 0, \"no group\", is never indexed.");
                }

                keysByPlan[plan] ??= [];
                var keySlot = keysByPlan[plan].Count;
                keysByPlan[plan].Add(new CompiledGroupKey(s, keySlot, reader));
                members[m] = new CompiledGroupMember
                {
                    Plan = plan,
                    KeySlot = keySlot,
                    Archetype = meta,
                    ComponentSlot = slot,
                    FieldOffsetInComponent = field.OffsetInComponentStorage,
                    Field = reader,
                    Positionless = plans[plan].Position == null,
                    FieldSize = KeySize(field.Type),
                    Versioned = table?.StorageMode == StorageMode.Versioned,
                };
            }

            if (set.Selector == GroupSelector.Shared && !ClusterFieldReader.Fits(set.SharedKey, set.KeyType))
            {
                throw new InvalidOperationException(
                    $"Group set '{d.Name}': the shared key {set.SharedKey} is out of range for its members' {set.KeyType} key: no member could hold it.");
            }

            set.Members = members;
            sets[s] = set;
        }

        for (var p = 0; p < plans.Length; p++)
        {
            if (keysByPlan[p] == null)
            {
                continue;
            }

            if (keysByPlan[p].Count > GroupFold.MaxKeySlots)
            {
                throw new NotSupportedException(
                    $"Archetype '{plans[p].Name}' is a member of {keysByPlan[p].Count} group sets; an archetype is a member of at most {GroupFold.MaxKeySlots}.");
            }

            plans[p].GroupKeys = [.. keysByPlan[p]];
            plans[p].BlockLayout = plans[p].BlockLayout.WithGroupKeys(keysByPlan[p].Count);
        }

        return sets;
    }

    private static CompiledRegion CompileRegion(GroupSetDeclaration d, DatabaseEngine engine)
    {
        var region = d.Region;
        var meta = Resolve(region.Archetype);
        var (_, keyField, key) = ResolveField(meta, engine, region.KeyComponent, region.KeyField, $"group set '{d.Name}' region key");
        if (!ClusterFieldReader.IsIntegerKey(keyField.Type))
        {
            throw new InvalidOperationException($"Group set '{d.Name}': the region key '{meta.Name}.{region.KeyField}' is a {keyField.Type}, not an integer.");
        }

        var (_, extentField, extent) = ResolveField(meta, engine, region.ExtentComponent, region.ExtentField, $"group set '{d.Name}' region extent");
        var extentType = extentField.DotNetType == typeof(AABB2F) ? SpatialFieldType.AABB2F
            : extentField.DotNetType == typeof(AABB3F) ? SpatialFieldType.AABB3F
            : extentField.DotNetType == typeof(BSphere2F) ? SpatialFieldType.BSphere2F
            : extentField.DotNetType == typeof(BSphere3F) ? SpatialFieldType.BSphere3F
            : (SpatialFieldType)255;
        if (extentType == (SpatialFieldType)255)
        {
            throw new InvalidOperationException(
                $"Group set '{d.Name}': the region extent '{meta.Name}.{region.ExtentField}' is a {extentField.DotNetType?.Name}. An extent is an AABB2F, "
                + "AABB3F, BSphere2F or BSphere3F.");
        }

        return new CompiledRegion { Archetype = meta, Key = key, Extent = extent, ExtentType = extentType };
    }

    private static int KeySize(FieldType type) => type switch
    {
        FieldType.Byte or FieldType.UByte => 1,
        FieldType.Short or FieldType.UShort or FieldType.Char => 2,
        FieldType.Int or FieldType.UInt => 4,
        _ => 8,
    };

    /// <summary>The B+Tree of a member's key field, from its archetype's index slots; null when the archetype has none yet.</summary>
    internal static BTreeBase<PersistentStore> TreeOf(CompiledGroupMember member, ArchetypeReplicationState state)
    {
        var slots = state.ClusterState?.IndexSlots;
        if (slots == null)
        {
            return null;
        }

        foreach (var s in slots)
        {
            if (s.Slot != member.ComponentSlot || s.Fields == null)
            {
                continue;
            }

            foreach (var f in s.Fields)
            {
                if (f.FieldOffset == member.FieldOffsetInComponent)
                {
                    return f.Index;
                }
            }
        }

        return null;
    }

    private static double MaxReachOf(SubscriptionsRegistry registry, string set)
    {
        var reach = 0d;
        foreach (var profile in registry.Profiles)
        {
            reach = Math.Max(reach, ReachIn(profile.Observers, set));
            foreach (var variant in profile.Variants.Values)
            {
                reach = Math.Max(reach, ReachIn(variant.Observers, set));
            }
        }

        return reach;
    }

    private static double ReachIn(IReadOnlyList<ObserverDeclaration> observers, string set)
    {
        var reach = 0d;
        foreach (var o in observers)
        {
            foreach (var interest in o.GroupInterests)
            {
                if (string.Equals(interest.Set, set, StringComparison.Ordinal))
                {
                    reach = Math.Max(reach, interest.ReachM);
                }
            }
        }

        return reach;
    }

    /// <summary>
    /// The visibility tag masks (14 § 6, P18): each declaring archetype's <c>u32</c> field resolved through its cluster layout, and its cold entry widened by
    /// the four bytes its last push read. Before the replication state is carved to the layout.
    /// </summary>
    internal static void CompileTagMasks(SubscriptionsRegistry registry, DatabaseEngine engine, CompiledProjectionPlan[] plans)
    {
        foreach (var projection in registry.Archetypes)
        {
            if (projection.VisibilityFieldName == null)
            {
                continue;
            }

            var plan = PlanOf(plans, projection.ArchetypeType);
            if (plan < 0)
            {
                continue;
            }

            ArchetypeRegistry.EnsureFinalized(projection.ArchetypeType);
            ArchetypeMetadata meta = null;
            foreach (var candidate in ArchetypeRegistry.GetAllArchetypes())
            {
                meta = candidate.ArchetypeType == projection.ArchetypeType ? candidate : meta;
            }

            if (meta is not { IsClusterEligible: true, ClusterLayout: not null })
            {
                throw new InvalidOperationException(
                    $"Archetype '{projection.Name}' declares a visibility mask but is not cluster-backed: the mask is read through the cluster layout.");
            }

            var (slot, field, reader) = ResolveField(meta, engine, projection.VisibilityComponentTypeId, projection.VisibilityFieldName,
                $"visibility mask of archetype '{meta.Name}'");
            if (field.Type != FieldType.UInt)
            {
                throw new InvalidOperationException(
                    $"Archetype '{meta.Name}': the visibility mask '{projection.VisibilityComponentName}.{projection.VisibilityFieldName}' is a {field.Type}. "
                    + "A mask is a uint, 0 for public.");
            }

            plans[plan].HasTagMask = true;
            plans[plan].TagMask = reader;
            plans[plan].TagMaskSlot = slot;
            plans[plan].BlockLayout = plans[plan].BlockLayout.WithTagMask();
        }
    }

    private static int PlanOf(CompiledProjectionPlan[] plans, Type archetype)
    {
        for (var p = 0; p < plans.Length; p++)
        {
            if (plans[p].ArchetypeType == archetype)
            {
                return p;
            }
        }

        return -1;
    }

    private static ArchetypeMetadata Resolve(Type archetype)
    {
        ArchetypeRegistry.EnsureFinalized(archetype);
        foreach (var candidate in ArchetypeRegistry.GetAllArchetypes())
        {
            if (candidate.ArchetypeType == archetype)
            {
                if (!candidate.IsClusterEligible || candidate.ClusterLayout == null)
                {
                    throw new InvalidOperationException(
                        $"Archetype '{candidate.Name}' is named by a group set but is not cluster-backed: groups read their keys through the cluster layout.");
                }

                return candidate;
            }
        }

        throw new InvalidOperationException($"Archetype '{archetype.Name}' is named by a group set but is not registered with this engine.");
    }

    private static (byte Slot, DBComponentDefinition.Field Field, ClusterFieldReader Reader) ResolveField(ArchetypeMetadata meta, DatabaseEngine engine,
        int componentTypeId, string fieldName, string what)
    {
        if (componentTypeId < 0 || !meta.TryGetSlot(componentTypeId, out var slot))
        {
            throw new InvalidOperationException($"The {what} names a component that archetype '{meta.Name}' does not have.");
        }

        var type = meta._slotToComponentType[slot];
        var definition = type == null ? null : engine.GetComponentTable(type)?.Definition;
        if (definition == null || !definition.FieldsByName.TryGetValue(fieldName, out var field) || field.IsStatic)
        {
            throw new InvalidOperationException($"The {what} names '{type?.Name}.{fieldName}', which the component does not store.");
        }

        var layout = meta.ClusterLayout;
        return (slot, field, new ClusterFieldReader(layout.ComponentOffset(slot), layout.ComponentSize(slot), field.OffsetInComponentStorage, field.Type));
    }
}
