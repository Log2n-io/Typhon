using System;
using System.Collections.Generic;
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
    public const byte Complete = 2;
    public const byte Released = 3;
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

    [InlineArray(MaxKeySlots)]
    public struct KeySlots
    {
        private long _element0;
    }
}

/// <summary>
/// A session's held keys per group set (14 § 3.8): the committed ones — what the client holds — and the pending ones this frame moves to, each sorted by key
/// with its fill cursor (a netId; <see cref="Complete"/> when every member is held). Committed with the frame, like the geometry.
/// </summary>
internal sealed class GroupSessionState
{
    public const uint Complete = uint.MaxValue;

    public ushort Generation;
    public readonly int Max;
    public readonly long[] CKeys;
    public readonly uint[] CCursor;
    private readonly int[] _cCount;
    private readonly int[] _noKeys;

    /// <summary>This frame is a RESET: the committed keys read as none, and are kept as they are until it is published.</summary>
    public bool Reset;

    /// <summary>The committed key counts per set as this frame reads them: none on a RESET.</summary>
    public int[] CCount => Reset ? _noKeys : _cCount;
    public readonly long[] PKeys;
    public readonly uint[] PCursor;
    public readonly int[] PCount;

    /// <summary>Each pending key's distance from the anchor this frame: the fill spends the enter budget nearest first.</summary>
    public readonly double[] PDistance;

    public GroupSessionState(int sets, int max)
    {
        Max = max;
        CKeys = new long[sets * max];
        CCursor = new uint[sets * max];
        _cCount = new int[sets];
        _noKeys = new int[sets];
        PKeys = new long[sets * max];
        PCursor = new uint[sets * max];
        PCount = new int[sets];
        PDistance = new double[sets * max];
    }

    public void Clear()
    {
        Array.Clear(_cCount);
        Array.Clear(PCount);
        Reset = false;
    }

    /// <summary>Whether a committed key is still filling — the state as the client holds it, whatever this frame's RESET view reads.</summary>
    public bool IsFilling()
    {
        for (var s = 0; s < _cCount.Length; s++)
        {
            for (var i = 0; i < _cCount[s]; i++)
            {
                if (CCursor[(s * Max) + i] != Complete)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Every committed and pending key, to the hub's held set: the client's keys and the ones this frame moves to, whatever a RESET reads.</summary>
    public void CollectKeys(PushHub hub)
    {
        for (var s = 0; s < _cCount.Length; s++)
        {
            for (var i = 0; i < _cCount[s]; i++)
            {
                hub.AddWanted(s, CKeys[(s * Max) + i]);
            }

            for (var i = 0; i < PCount[s]; i++)
            {
                hub.AddWanted(s, PKeys[(s * Max) + i]);
            }
        }
    }

    public void Commit()
    {
        Array.Copy(PKeys, CKeys, PKeys.Length);
        Array.Copy(PCursor, CCursor, PCursor.Length);
        Array.Copy(PCount, _cCount, PCount.Length);
        Reset = false;
    }

    /// <summary>The cursor of <paramref name="key"/> among <paramref name="count"/> sorted keys from <paramref name="at"/>, or 0 when it is not held.</summary>
    public static uint CursorOf(long[] keys, uint[] cursors, int at, int count, long key)
    {
        var lo = at;
        var hi = at + count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var k = keys[mid];
            if (k == key)
            {
                return cursors[mid];
            }

            if (k < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return 0;
    }
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
                Region = CompileRegion(d, engine),
                MaxReachM = MaxReachOf(registry, d.Name),
            };

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

                if (ClusterFieldReader.IsSignedKey(field.Type) != ClusterFieldReader.IsSignedKey(set.Region.Key.Type))
                {
                    // Keys compare as 64-bit values, so an i32 -1 and a u32 0xFFFFFFFF would name two groups for one bit pattern.
                    throw new InvalidOperationException(
                        $"Group set '{d.Name}': '{meta.Name}.{member.KeyField}' is a {field.Type} and the region key a {set.Region.Key.Type}. A member key and its "
                        + "region key are both signed or both unsigned.");
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
