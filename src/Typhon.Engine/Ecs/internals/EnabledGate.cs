using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The enabled-state filter a field query applies to every slot it would emit (QENABLE-01): the predicate's component must be enabled at the query's
/// snapshot — or disabled and present, when the query names <c>Disabled&lt;T&gt;()</c> on it — and every other component the query names enabled or disabled
/// must be so. One copy for the selective B+Tree scan, the SoA scan and the ordered merge, so the three cannot answer differently (#1259).
/// </summary>
/// <remarks>
/// <para>The cluster's per-component enabled words mirror the committed records (ENABLE-01), so a cluster is filtered with one AND per named component —
/// exact while no enable or disable is still resolved through <see cref="EnabledBitsOverrides"/> and the transaction stages none of its own. Otherwise each
/// candidate's bits are resolved as the broad scan resolves them: the committed bits, then the snapshot's overrides, then the transaction's pending
/// overlay.</para>
/// <para>Presence is asked only for <c>Disabled&lt;T&gt;()</c> on a Versioned component: its bit is clear when disabled and when absent (STAGE-02), and only
/// its chain tells them apart — a revision the snapshot sees. A SingleVersion component the spawn did not supply is present, zero, and disabled.</para>
/// </remarks>
internal unsafe struct EnabledGate
{
    public ushort RequiredEnabled;
    public ushort RequiredDisabled;

    private ArchetypeClusterInfo _layout;
    private bool _exact;
    private EnabledBitsOverrides _overrides;
    private CommitCardTable _cards;
    private long _txTsn;
    private Dictionary<EntityId, ushort> _pending;

    // The predicate component's position among the Versioned slots when presence must be proven, else -1; its slot, and the reader that proves it.
    private int _presenceVersionedIndex;
    private int _presenceSlot;
    private int _predicateTypeId;
    private ArchetypeMetadata _meta;
#pragma warning disable TYPHON005 // The gate borrows the query's transaction, it does not own it
    private Transaction _tx;
#pragma warning restore TYPHON005
    private ArchetypeEngineState _state;

    /// <summary>Whether <see cref="Admits"/> reads records — the presence probe of <c>Disabled&lt;T&gt;()</c> on a Versioned component.</summary>
    public readonly bool NeedsRecords => _presenceVersionedIndex >= 0;

    /// <summary>The archetype's engine state, whose EntityMap the presence probe reads.</summary>
    public readonly ArchetypeEngineState State => _state;

    /// <summary>Whether a slot of a cluster whose mask came back <paramref name="exact"/> still needs <see cref="Admits"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool NeedsPerSlot(bool exact) => !exact || _presenceVersionedIndex >= 0;

    /// <summary>
    /// The gate of a field query on <paramref name="predicateSlot"/>, with the named <c>Enabled&lt;T&gt;</c> / <c>Disabled&lt;T&gt;</c> slots already
    /// resolved for this archetype (<paramref name="namedEnabled"/>, <paramref name="namedDisabled"/>).
    /// </summary>
    public static EnabledGate Create(ArchetypeMetadata meta, ArchetypeEngineState state, ArchetypeClusterInfo layout, int predicateSlot, ushort namedEnabled,
        ushort namedDisabled, Transaction tx)
    {
        var predicateBit = (ushort)(1 << predicateSlot);
        var disabledAsked = (namedDisabled & predicateBit) != 0;
        var pending = tx.PendingEnableDisable;
        var overrides = tx.DBE.EnabledBitsOverrides;
        return new EnabledGate
        {
            RequiredEnabled = disabledAsked ? namedEnabled : (ushort)(namedEnabled | predicateBit),
            RequiredDisabled = namedDisabled,
            _layout = layout,
            _exact = overrides._overrideCount == 0 && (pending == null || pending.Count == 0),
            _overrides = overrides,
            _cards = tx.DBE.CommitCards,
            _txTsn = tx.TSN,
            _pending = pending,
            _presenceVersionedIndex = disabledAsked && layout.SlotToVersionedIndex != null ? layout.SlotToVersionedIndex[predicateSlot] : -1,
            _presenceSlot = predicateSlot,
            _predicateTypeId = meta._componentTypeIds[predicateSlot],
            _meta = meta,
            _tx = tx,
            _state = state,
        };
    }

    /// <summary>
    /// The occupied slots of the cluster at <paramref name="clusterBase"/> its enabled words admit, <paramref name="exact"/> when the words decide; otherwise
    /// every slot, each left to <see cref="Admits"/>.
    /// </summary>
    /// <remarks>
    /// Exactness is re-checked after the words are read: an enable or disable committing during the scan registers its override
    /// (<c>EnabledBitsOverrides.AddPending</c>, an interlocked increment) before its publish writes the words, so a word this reader sees past its snapshot
    /// comes with a live override, read after it. Decided once per query, a commit landing mid-scan showed through.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong ClusterMask(byte* clusterBase, out bool exact)
    {
        exact = false;
        if (!_exact)
        {
            return ulong.MaxValue;
        }

        var mask = ulong.MaxValue;
        for (var bits = (uint)RequiredEnabled; bits != 0; bits &= bits - 1)
        {
            mask &= Volatile.Read(ref *(ulong*)(clusterBase + _layout.EnabledBitsOffset(System.Numerics.BitOperations.TrailingZeroCount(bits))));
        }

        for (var bits = (uint)RequiredDisabled; bits != 0; bits &= bits - 1)
        {
            mask &= ~Volatile.Read(ref *(ulong*)(clusterBase + _layout.EnabledBitsOffset(System.Numerics.BitOperations.TrailingZeroCount(bits))));
        }

        if (_overrides._overrideCount != 0)
        {
            return ulong.MaxValue;
        }

        exact = true;
        return mask;
    }

    /// <summary>
    /// Whether the occupied slot <paramref name="slotIndex"/> of the cluster at <paramref name="clusterBase"/>, holding <paramref name="rawId"/>, passes the
    /// gate — the per-slot half <see cref="ClusterMask"/> leaves when it does not decide alone (<paramref name="exact"/>, as it returned).
    /// <paramref name="recordBuf"/> and <paramref name="mapAccessor"/> serve the presence probe.
    /// </summary>
    public readonly bool Admits(long rawId, byte* clusterBase, int slotIndex, bool exact, byte* recordBuf, ref ChunkAccessor<PersistentStore> mapAccessor)
    {
        if (!exact)
        {
            // The committed bits of the slots this gate reads, from the cluster's words; the overrides answer for the snapshot, the overlay for this
            // transaction's own changes — the broad scan's resolution, BroadScanAction.
            ushort bits = 0;
            for (var named = (uint)(RequiredEnabled | RequiredDisabled); named != 0; named &= named - 1)
            {
                var s = System.Numerics.BitOperations.TrailingZeroCount(named);
                if ((Volatile.Read(ref *(ulong*)(clusterBase + _layout.EnabledBitsOffset(s))) & (1UL << slotIndex)) != 0)
                {
                    bits |= (ushort)(1 << s);
                }
            }

            if (!PassesResolved(rawId, bits))
            {
                return false;
            }
        }

        return _presenceVersionedIndex < 0 || IsPresent(rawId, recordBuf, ref mapAccessor);
    }

    /// <summary>
    /// Whether a reserved slot passes — a spawn placed and not yet published (VIS-08), or a destroyed entity hidden until its cleanup (VIS-09) — from its
    /// record, which <paramref name="record"/> holds: its bits resolved for the snapshot as any slot's are. Read as they stand, a destroy hidden from an older
    /// snapshot showed that snapshot a disable committed after it.
    /// </summary>
    public readonly bool AdmitsRecord(long rawId, byte* record)
    {
        if (!PassesResolved(rawId, ClusterEntityRecordAccessor.GetHeader(record).EnabledBits))
        {
            return false;
        }

        return _presenceVersionedIndex < 0 || IsPresentAtSnapshot(ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, _presenceVersionedIndex));
    }

    /// <summary>The committed <paramref name="bits"/> resolved as the broad scan resolves them — the snapshot's overrides, then the transaction's own pending
    /// changes — and tested against the gate's masks.</summary>
    private readonly bool PassesResolved(long rawId, ushort bits)
    {
        var id = EntityId.FromRaw(rawId);
        bits = _overrides.ResolveEnabledBits(id.EntityKey, bits, _txTsn, _cards);
        if (_pending != null && _pending.TryGetValue(id, out var pendingBits))
        {
            // A component this transaction supplied to the entity is absent to its own field queries until it commits: the scans read the committed slot,
            // which holds no value of it (QENABLE-01). Its enabled bit alone would test bytes the entity never had. Only an own flip to enabled can be one;
            // a supply disabled again fails the gate on its bit, or the presence probe on its unpublished root.
            if ((pendingBits & ~bits & (1 << _presenceSlot)) != 0 && _tx.FirstSuppliedHere(rawId, _predicateTypeId))
            {
                return false;
            }

            bits = pendingBits;
        }

        return (bits & RequiredEnabled) == RequiredEnabled && (bits & RequiredDisabled) == 0;
    }

    /// <remarks>
    /// At the snapshot, as a point read decides it: a chain with a revision this reader sees. The record's root alone is current state — a first supply
    /// committed after the snapshot made the component present to this reader, matching <c>Disabled&lt;T&gt;()</c> on bytes it never had.
    /// </remarks>
    private readonly bool IsPresent(long rawId, byte* recordBuf, ref ChunkAccessor<PersistentStore> mapAccessor)
    {
        if (!_state.EntityMap.TryGet(EntityId.FromRaw(rawId).EntityKey, recordBuf, ref mapAccessor))
        {
            return false;
        }

        return IsPresentAtSnapshot(ClusterEntityRecordAccessor.GetCompRevFirstChunkId(recordBuf, _presenceVersionedIndex));
    }

    /// <summary>Whether the chain at <paramref name="root"/> holds a revision this reader sees — the component exists at its snapshot.</summary>
    private readonly bool IsPresentAtSnapshot(int root) => root != 0 && _tx.ResolveVersionedContentChunk(_meta, _presenceSlot, root) != 0;
}
