using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Typhon.Engine.Internals;

/// <summary>
/// What one session's interest currently reaches, by cluster: the slots inside its observer, and the identity it was told about in each.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the state that makes replication incremental</b> (15 § 3.2). Phase 1 rediscovered a session's whole view every tick and compared the result
/// against the known-set hit by hit; with this, the interest pass compares CLUSTER MASKS instead — one 64-bit difference per cluster rather than one probe
/// per entity — and hands the frame stage only the slots that entered, left, or changed. At the density 13 § 6 measures that is ~300 slots out of ~2 000.
/// </para>
/// <para>
/// <b>The identities are stored, not re-derived.</b> A leave has to name the entity the SESSION knew at that slot. Reading the block's hot entry a tick
/// later answers "who is standing here now", which is a different entity the moment a slot is reused — and a slot that both loses its occupant and falls
/// out of the observer's radius on the same tick is never visited, so there is no other moment at which the right answer is available. They are indexed by
/// slot rather than packed by mask so that a membership change costs no repacking.
/// </para>
/// <para>
/// <b>It is committed by the frame stage, not by the interest pass.</b> Interest computes the difference against the COMMITTED mask and leaves the new one
/// in the run; only a frame that was actually published moves the view forward. A session whose frame was skipped therefore recomputes its difference from
/// the same baseline next tick and its next frame carries the union of everything it missed, which is SUB-03 in its own words.
/// </para>
/// <para>
/// <b>Native and engine-owned.</b> One per session slot, reset when the slot is rebound.
/// </para>
/// </remarks>
internal sealed unsafe class SessionInterestView : IDisposable
{
    /// <summary>Slots per cluster, and therefore identities per entry.</summary>
    private const int SlotsPerCluster = 64;


    private long* _mapKeys;
    private int* _mapValues;
    private int _mapMask;

    private long* _entryKeys;
    private ulong* _entryMasks;

    /// <summary>
    /// Per cluster: slots this session must look at on its NEXT frame whatever the change masks say.
    /// </summary>
    /// <remarks>
    /// <b>It exists to keep the reduction alive across a slot reuse.</b> When a slot's occupant is replaced, the entity that arrives is not in the tick's
    /// <c>ChangedSlots</c> — it was written before anyone watched it — so a mask-only difference would never visit it and the session would never be told.
    /// The old answer was <c>ForceFullGather</c>: condemn the session's ENTIRE next frame to re-read every slot of its view. Measured on the SWG demo that
    /// fired on about 40 % of frames at d06, and those frames performed roughly 91 % of every slot read in the subsystem (16 § 4), because one reuse
    /// anywhere in a 9 000-slot disc cost all 9 000. The claim being protected covers the DISPLACED SLOTS ONLY, and this is that claim at its true size:
    /// one <c>ulong</c> per cluster, OR-ed into the next visit and cleared by it.
    /// </remarks>
    private ulong* _entryOwed;

    private long* _entryTouched;

    private ulong* _entryIds;
    private int _entryCount;
    private int _entryCapacity;
    private int _deadEntries;
    private int _owedCount;

    private bool _disposed;

    /// <summary>How many clusters the view holds, live and dead.</summary>
    public int EntryCount => _entryCount;

    /// <summary>How many of them are live — the clusters the session's observer currently reaches.</summary>
    public int LiveEntryCount => _entryCount - _deadEntries;

    /// <summary>The key a cluster is held under: the pair that names it within a session's profile.</summary>
    public static long KeyOf(ushort archetype, int chunkId) => ((long)archetype << 32) | (uint)chunkId;

    /// <summary>The archetype half of a key.</summary>
    public static ushort ArchetypeOf(long key) => (ushort)(key >> 32);

    /// <summary>The slots of cluster <paramref name="index"/> that the session's observer reached as of its last published frame.</summary>
    public ulong MaskAt(int index) => _entryMasks[index];

    /// <summary>The key of cluster <paramref name="index"/>.</summary>
    public long KeyAt(int index) => _entryKeys[index];

    /// <summary>Slots of cluster <paramref name="index"/> that the next frame must visit whatever the change masks say.</summary>
    public ulong OwedAt(int index) => _entryOwed[index];

    /// <summary>Brings an entry's debt word toward the core without reading it, for a run the gather has not reached yet.</summary>
    /// <remarks>
    /// A prefetch observes nothing and cannot fault, so it is correct to issue for any index — including one a concurrent growth is about to move. It
    /// folds away entirely where the platform has no such instruction. See <see cref="KnownSet.PrefetchProbe"/> for why the gather issues these at all.
    /// </remarks>
    /// <param name="index">The entry whose debt word a later run will read.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrefetchOwed(int index)
    {
        if (Sse.IsSupported)
        {
            Sse.Prefetch0(_entryOwed + index);
        }
    }

    /// <summary>Slots this view still owes a later frame, across every cluster it holds.</summary>
    /// <remarks>
    /// <b>A view that owes anything is not complete, and the frame stage has to be able to ask that without walking the entries.</b> The debt is served a
    /// slice at a time, so a frame can finish having visited every slot it CHOSE to and still owe the rest — and latching `ViewComplete` there would tell
    /// the client its world was whole while entities it has never heard of were still queued. Maintained as a running total because the alternative is a
    /// popcount over every entry on every frame, which is the walk the difference exists to avoid.
    /// </remarks>
    public int OwedCount => _owedCount;

    /// <summary>Adds <paramref name="slots"/> to what cluster <paramref name="index"/> owes its next frame.</summary>
    public void Owe(int index, ulong slots)
    {
        var before = _entryOwed[index];
        var after = before | slots;
        _owedCount += BitOperations.PopCount(after) - BitOperations.PopCount(before);
        _entryOwed[index] = after;
    }

    /// <summary>Clears what cluster <paramref name="index"/> owed, because a frame has just visited it.</summary>
    public void ClearOwed(int index)
    {
        _owedCount -= BitOperations.PopCount(_entryOwed[index]);
        _entryOwed[index] = 0;
    }

    /// <summary>Drops <paramref name="served"/> from what cluster <paramref name="index"/> owes, keeping the rest for a later frame.</summary>
    /// <param name="index">The cluster entry.</param>
    /// <param name="served">Slots the frame has just visited, or no longer reaches.</param>
    /// <remarks>
    /// The partial form of <see cref="ClearOwed"/>, and the one the gather uses. A frame serves only a slice of a large debt, and clearing the whole
    /// entry would silently drop the rest — which is an entity the client is never told about, the one failure this whole mechanism exists to prevent.
    /// </remarks>
    public void KeepOwed(int index, ulong served)
    {
        var before = _entryOwed[index];
        var after = before & ~served;
        _owedCount -= BitOperations.PopCount(before) - BitOperations.PopCount(after);
        _entryOwed[index] = after;
    }

    /// <summary>The tick the interest pass last reached cluster <paramref name="index"/>.</summary>
    public long TouchedAt(int index) => _entryTouched[index];

    /// <summary>
    /// The identity the session was told about in <paramref name="slot"/> of cluster <paramref name="index"/>, packed as in <see cref="Pack"/>.
    /// </summary>
    public ulong IdAt(int index, int slot) => _entryIds[((long)index * SlotsPerCluster) + slot];

    /// <summary>
    /// The sixty-four identities of cluster <paramref name="index"/>, indexed by slot.
    /// </summary>
    /// <remarks>
    /// Taken once per RUN by the frame stage rather than once per slot: an entry is 512 bytes of identities, so recomputing the base for every slot walks
    /// the multiply and the bounds of a 512-byte stride where one pointer and a slot index will do. The pointer is valid until the next growth or
    /// compaction, neither of which can happen while a tick is resolving (see <see cref="Compact"/>).
    /// </remarks>
    public ulong* IdsAt(int index) => _entryIds + ((long)index * SlotsPerCluster);

    /// <summary>Packs an identity and its generation into one entry.</summary>
    public static ulong Pack(uint netId, ushort generation) => ((ulong)generation << 32) | netId;

    /// <summary>The identity half of a packed entry.</summary>
    public static uint NetIdOf(ulong id) => (uint)id;

    /// <summary>The generation half of a packed entry.</summary>
    public static ushort GenerationOf(ulong id) => (ushort)(id >> 32);

    /// <summary>
    /// Finds the cluster's entry, creating an empty one if the session has never reached it, and marks it reached on <paramref name="tick"/>.
    /// </summary>
    /// <param name="key">The cluster, from <see cref="KeyOf"/>.</param>
    /// <param name="tick">The tick the interest pass is resolving.</param>
    /// <returns>The entry index.</returns>
    public int Touch(long key, long tick)
    {
        // The map is allocated by the growth path, and the growth path is reached through an entry — so a view that has never held one has no map to probe.
        // Sizing it here rather than in a constructor keeps a runtime's worth of never-used session slots at zero bytes.
        EnsureEntries(_entryCount + 1);

        var slot = FindMapSlot(key);
        if (_mapKeys[slot] != 0)
        {
            var existing = _mapValues[slot];
            _entryTouched[existing] = tick;
            return existing;
        }

        var index = _entryCount++;
        _entryKeys[index] = key;
        _entryMasks[index] = 0;

        // Assigned, NOT reconciled against the running count. A fresh entry's slot holds whatever the last occupant left in native memory, and this is
        // reached only for an entry the count has never included: either the array has just grown, or Compact dropped the previous occupant and already
        // subtracted its debt. Taking the popcount of that stale word and subtracting it corrupted the count, and a count that never reaches zero is a
        // view that never completes — FrameAssemblerTests.ViewCompleteIsSetWhenTheFillUnderTheEnterBudgetCompletes caught it.
        _entryOwed[index] = 0;

        // Counted dead on creation, because an empty mask IS dead and Commit's accounting below is written as transitions of the mask. A new entry that
        // the frame stage then commits non-empty takes the "was empty, now is not" branch and the count comes back to where it should be.
        _deadEntries++;
        _entryTouched[index] = tick;
        new Span<ulong>(_entryIds + ((long)index * SlotsPerCluster), SlotsPerCluster).Clear();

        _mapKeys[slot] = key + 1;
        _mapValues[slot] = index;
        return index;
    }

    /// <summary>Records the identity the session is being told about in a slot.</summary>
    public void SetId(int index, int slot, ulong id) => _entryIds[((long)index * SlotsPerCluster) + slot] = id;

    /// <summary>Moves a cluster's membership to what this tick's published frame described.</summary>
    /// <param name="index">The entry.</param>
    /// <param name="mask">The slots the observer now reaches.</param>
    public void Commit(int index, ulong mask)
    {
        var before = _entryMasks[index];
        _entryMasks[index] = mask;
        if (mask == 0 && before != 0)
        {
            _deadEntries++;
        }
        else if (mask != 0 && before == 0)
        {
            _deadEntries--;
        }
    }

    /// <summary>Empties the view: the session in the slot has changed, or its store is being reset.</summary>
    public void Clear()
    {
        if (_mapMask >= 0 && _mapKeys != null)
        {
            new Span<long>(_mapKeys, _mapMask + 1).Clear();
        }

        _entryCount = 0;
        _deadEntries = 0;
    }

    /// <summary>
    /// Discards the clusters the session no longer reaches, so a roaming observer does not accumulate an entry for every cluster it has ever seen.
    /// </summary>
    /// <remarks>
    /// <b>Called BEFORE a tick touches the view, never after.</b> A run carries the entry index the frame stage commits through, so moving entries while
    /// those indices are outstanding would commit one cluster's membership onto another's. At the top of the interest pass every entry is committed and no
    /// index is live, which is the only point in the tick where that is true.
    /// <b>An entry the live residency pass is keeping survives an empty mask.</b> A cluster inside the margin but outside the radius has described
    /// nothing, so its committed mask is zero, and dropping it would throw away exactly the band the incremental pass exists to walk.
    /// It rebuilds only when the dead outnumber the live. An entry is 544 bytes — a mask, a key, a stamp and sixty-four
    /// identities — so a session crossing a large world would otherwise grow without bound, at two hundred sessions, for no benefit at all.
    /// </remarks>
    public void Compact()
    {
        if (_deadEntries <= _entryCount - _deadEntries || _deadEntries == 0)
        {
            return;
        }

        var write = 0;
        for (var read = 0; read < _entryCount; read++)
        {
            if (_entryMasks[read] == 0)
            {
                // A cluster the session no longer reaches. Its debt goes with it: every slot it held is described by a leave, so there is nothing left to
                // owe, and leaving the count behind would keep the view permanently incomplete.
                _owedCount -= BitOperations.PopCount(_entryOwed[read]);
                continue;
            }

            if (write != read)
            {
                _entryKeys[write] = _entryKeys[read];
                _entryMasks[write] = _entryMasks[read];
                _entryOwed[write] = _entryOwed[read];
                _entryTouched[write] = _entryTouched[read];
                new Span<ulong>(_entryIds + ((long)read * SlotsPerCluster), SlotsPerCluster)
                    .CopyTo(new Span<ulong>(_entryIds + ((long)write * SlotsPerCluster), SlotsPerCluster));
            }

            write++;
        }

        _entryCount = write;
        _deadEntries = 0;

        new Span<long>(_mapKeys, _mapMask + 1).Clear();
        for (var i = 0; i < _entryCount; i++)
        {
            var slot = FindMapSlot(_entryKeys[i]);
            _mapKeys[slot] = _entryKeys[i] + 1;
            _mapValues[slot] = i;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMemory.Free(_mapKeys);
        NativeMemory.Free(_mapValues);
        NativeMemory.Free(_entryKeys);
        NativeMemory.Free(_entryMasks);
        NativeMemory.Free(_entryOwed);
        NativeMemory.Free(_entryTouched);
        NativeMemory.Free(_entryIds);
        _mapKeys = null;
        _mapValues = null;
        _entryKeys = null;
        _entryMasks = null;
        _entryOwed = null;
        _entryTouched = null;
        _entryIds = null;
        _mapMask = 0;
        _entryCount = 0;
        _entryCapacity = 0;
        _deadEntries = 0;
    }

    /// <summary>Native bytes held, for the owner's resource accounting.</summary>
    public long EstimatedBytes =>
        (((long)_mapMask + 1) * (sizeof(long) + sizeof(int)))
        + ((long)_entryCapacity * ((2 * sizeof(long)) + (2 * sizeof(ulong)) + sizeof(byte) + (SlotsPerCluster * sizeof(ulong))));

    private int FindMapSlot(long key)
    {
        var stored = key + 1;
        var slot = (int)(((ulong)key * 11400714819323198485ul) >> 40) & _mapMask;
        while (true)
        {
            var k = _mapKeys[slot];
            if (k == 0 || k == stored)
            {
                return slot;
            }

            slot = (slot + 1) & _mapMask;
        }
    }

    /// <summary>
    /// Grows the entry arrays and, with them, the map — which is kept at least four times the entry capacity so the linear probe above always terminates.
    /// </summary>
    private void EnsureEntries(int count)
    {
        if (count <= _entryCapacity)
        {
            return;
        }

        var capacity = _entryCapacity == 0 ? 64 : _entryCapacity;
        while (capacity < count)
        {
            capacity *= 2;
        }

        _entryKeys = (long*)NativeMemory.Realloc(_entryKeys, (nuint)capacity * sizeof(long));
        _entryMasks = (ulong*)NativeMemory.Realloc(_entryMasks, (nuint)capacity * sizeof(ulong));
        _entryOwed = (ulong*)NativeMemory.Realloc(_entryOwed, (nuint)capacity * sizeof(ulong));
        _entryTouched = (long*)NativeMemory.Realloc(_entryTouched, (nuint)capacity * sizeof(long));
        _entryIds = (ulong*)NativeMemory.Realloc(_entryIds, (nuint)capacity * SlotsPerCluster * sizeof(ulong));
        _entryCapacity = capacity;

        var mapCapacity = capacity * 4;
        NativeMemory.Free(_mapKeys);
        NativeMemory.Free(_mapValues);
        _mapKeys = (long*)NativeMemory.AllocZeroed((nuint)mapCapacity, sizeof(long));
        _mapValues = (int*)NativeMemory.Alloc((nuint)mapCapacity, sizeof(int));
        _mapMask = mapCapacity - 1;

        for (var i = 0; i < _entryCount; i++)
        {
            var slot = FindMapSlot(_entryKeys[i]);
            _mapKeys[slot] = _entryKeys[i] + 1;
            _mapValues[slot] = i;
        }
    }
}

/// <summary>One <see cref="SessionInterestView"/> per session slot, created on demand and reset when a slot is handed to a new session.</summary>
/// <remarks>
/// Shared by the interest pass and the frame assembler, which is why it is not a field of either: interest computes the difference against it and the frame
/// stage commits it, and a store owned by one of the two would make the other reach through it.
/// </remarks>
internal sealed class SessionViewStore : IDisposable
{
    private readonly SessionInterestView[] _views;
    private readonly ushort[] _generations;
    private bool _disposed;

    /// <summary>Creates a store for a runtime admitting at most <paramref name="maxSessions"/> sessions.</summary>
    public SessionViewStore(int maxSessions)
    {
        _views = new SessionInterestView[Math.Max(1, maxSessions)];
        _generations = new ushort[_views.Length];
    }

    /// <summary>How many slots the store covers.</summary>
    public int Capacity => _views.Length;

    /// <summary>
    /// The view of the session in <paramref name="slot"/>, emptied first if the slot has been handed to a different session since it was last asked for.
    /// </summary>
    /// <param name="slot">The session's slot.</param>
    /// <param name="generation">Its generation; a change is a new session and resets the view.</param>
    /// <returns>The view, or <see langword="null"/> when the slot is out of range.</returns>
    public SessionInterestView ViewOf(int slot, ushort generation)
    {
        if ((uint)slot >= (uint)_views.Length)
        {
            return null;
        }

        var view = _views[slot];
        if (view == null)
        {
            view = new SessionInterestView();
            _views[slot] = view;
            _generations[slot] = generation;
            return view;
        }

        if (_generations[slot] != generation)
        {
            view.Clear();
            _generations[slot] = generation;
        }

        return view;
    }

    /// <summary>Native bytes held across every slot, for the owner's resource accounting.</summary>
    public long EstimatedBytes
    {
        get
        {
            var total = 0L;
            for (var i = 0; i < _views.Length; i++)
            {
                total += _views[i]?.EstimatedBytes ?? 0;
            }

            return total;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < _views.Length; i++)
        {
            _views[i]?.Dispose();
            _views[i] = null;
        }
    }
}
