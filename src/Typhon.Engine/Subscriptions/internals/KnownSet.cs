using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Typhon.Engine.Internals;

/// <summary>Per-entry flags carried in a <see cref="KnownEntry"/>'s one flag byte.</summary>
[Flags]
internal enum KnownFlags : byte
{
    /// <summary>Nothing set. A fresh entry starts here.</summary>
    None = 0,

    /// <summary>
    /// The next frame must carry this entity's full state rather than only the groups that changed. SUB-11 is what sets it (a projection change, a resumed
    /// session); Phase 1 only has to <i>carry</i> it — which is why growth and shrink copy whole entries rather than rebuilding them from the key.
    /// </summary>
    NeedsFull = 1 << 0,
}

/// <summary>Answer to "do I know this netId, and is its generation current?" — the three outcomes of one probe.</summary>
internal enum KnownProbe : byte
{
    /// <summary>No entry. The hit is an enter.</summary>
    Unknown = 0,

    /// <summary>Known, and the stored generation matches the hit's. The normal path: emit the groups whose tick beats the session's baseline.</summary>
    Current = 1,

    /// <summary>
    /// Known, but under a different generation: the netId was reissued while this session was not being sent. Per
    /// <c>claude/design/Subscriptions/02-execution.md § 5</c> that is a <b>leave now and an enter next frame</b>, never both in one frame.
    /// </summary>
    Stale = 2,
}

/// <summary>Outcome of <see cref="KnownSet.AddSource"/>.</summary>
internal enum KnownAdd : byte
{
    /// <summary>A new entry was created and the source bit set. This is the session's enter record.</summary>
    Entered = 0,

    /// <summary>The entity was already known under this generation; the source bit was added to its mask and its stamp refreshed. No enter record.</summary>
    AlreadyKnown = 1,

    /// <summary>
    /// An entry exists under a <i>different</i> generation and was left untouched. The caller emits the leave, calls <see cref="KnownSet.Remove"/>, and lets
    /// the hit enter in the following frame.
    /// </summary>
    Stale = 2,
}

/// <summary>Outcome of <see cref="KnownSet.RemoveSource"/>.</summary>
internal enum KnownLeave : byte
{
    /// <summary>The session did not know this netId. Nothing to send.</summary>
    NotKnown = 0,

    /// <summary>The bit was cleared but other sources still reach the entity, so it stays known. No leave record.</summary>
    StillKnown = 1,

    /// <summary>The mask emptied: the entry was removed. This is the session's leave record.</summary>
    Left = 2,
}

/// <summary>
/// One 16-byte known-set entry. The layout is fixed by <c>claude/design/Subscriptions/02-execution.md § 5</c> and the field order below is the probe order,
/// not an arbitrary transcription of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why each field sits where it does.</b> <see cref="NetId"/> is first because it is the only field a probe <i>must</i> read: one 4-byte load at offset 0
/// decides hit, continue or stop, and <c>0</c> (<see cref="NetIdAllocator.NoNetId"/>, reserved) means "empty slot", so emptiness costs no extra byte and no
/// separate metadata array. <see cref="Generation"/> follows immediately because it is read on every hit, one instruction after the key compare, and turns
/// "known" into "known and current" without a second fetch. <see cref="SeenStamp"/> pairs with it so the hit path's read-and-write touches a single 8-byte
/// word. <see cref="Sources"/> and <see cref="Flags"/> are adjacent bytes so the enter path writes them in one store. <see cref="LastSentTick"/> is last
/// because Phase 1 never reads it — it is the coldest field in the entry, and putting it at the end keeps the three hot fields inside the first 8 bytes.
/// <see cref="Archetype"/> is the two bytes that alignment would insert anyway; naming them makes the layout stated rather than inferred.
/// </para>
/// <para>
/// <b>16 is not a rounding, it is the point.</b> The stride divides a cache line exactly, so an entry never straddles one and a probe run of up to four
/// consecutive slots is a single memory touch. That is what makes the "one probe" goal a property of the layout rather than of the load factor: at the
/// design's 0.7 ceiling the mean <i>slot</i> count is Knuth's ~2.2, but the mean <i>line</i> count is ~1.3 (measured — see <see cref="KnownSet"/>).
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal struct KnownEntry
{
    /// <summary>The identity the session knows this entity by. <c>0</c> is <see cref="NetIdAllocator.NoNetId"/> and marks an empty slot.</summary>
    public uint NetId;

    /// <summary>
    /// The generation this session was shown. A hit whose generation differs is a reuse the session missed — a leave, then an enter next frame.
    /// </summary>
    public ushort Generation;

    /// <summary>
    /// Caller-defined "last confirmed" stamp, normally the low 16 bits of the tick that last hit this entity. The engine stores it and never interprets it.
    /// </summary>
    public ushort SeenStamp;

    /// <summary>
    /// Bitmask of the interest sources that reach this entity. The entity is known once; it leaves when the mask empties. Eight sources maximum.
    /// </summary>
    public byte Sources;

    /// <summary>Per-entry flags. Survives growth and shrink because those copy whole entries.</summary>
    public KnownFlags Flags;

    /// <summary>
    /// The archetype the entity belongs to, as an index into the runtime's plan list — the two bytes natural alignment would insert before
    /// <see cref="LastSentTick"/>, spent on the one fact a leave needs and the netId cannot carry.
    /// </summary>
    /// <remarks>
    /// A leave applies "to whatever holds the netId then, provided it belongs to the leave's archetype" (03 § 10), so the leave has to travel in that
    /// archetype's <c>ENTITIES</c> block. The frame stage discovers a leave by sweeping this table for entries the tick's hits did not reach — at which
    /// point the hit that would have named the archetype is precisely what is missing — so the archetype is recorded when the entry is created. Written by
    /// the frame stage through the entry <see cref="KnownSet.AddSource"/> hands back; the table itself never interprets it.
    /// </remarks>
    public ushort Archetype;

    /// <summary>
    /// <b>Phase 2's field: stored, never read here.</b> When byte budgets defer individual entities, the emit rule becomes <c>groupTick &gt; LastSentTick</c>
    /// and this subsumes the per-session baseline (02 § 5). Reserving it now costs nothing — alignment was going to spend the bytes regardless.
    /// </summary>
    public uint LastSentTick;
}

/// <summary>
/// One session's known-set: the open-addressed native table that answers "do I know this netId, is its generation current, and has it changed since my
/// baseline?" in a single probe. It is the <i>only</i> baseline a client has (SUB-03), which is why it is a data structure and not a cache.
/// </summary>
/// <remarks>
/// <para>
/// <b>Open addressing with backward-shift deletion, no tombstones.</b> A known-set churns continuously — a moving player enters and leaves dozens of
/// entities a second — so a tombstoned table would degrade until it was rehashed, and the rehash would be an unbounded pause on the frame path. Backward
/// shifting keeps the invariant that a probe stops at the first empty slot, so the load factor stays an honest measure of probe cost forever.
/// </para>
/// <para>
/// <b>Why the mean probe count is quoted two ways.</b> Knuth's bound for a successful linear-probe search is <c>½(1 + 1/(1−α))</c>: 2.25 at α = 0.70 and 1.84
/// at the α ≈ 0.61 a table holding 10 000 entities actually sits at after doubling. No open-addressed table reaches 1.3 <i>slots</i> at this load — the bound
/// forbids it. What is 1.3 is the count of <b>cache lines touched</b>, because four 16-byte entries share a line and a linear probe run is contiguous:
/// measured 1.19–1.21 at α = 0.61 and 1.29–1.31 at α = 0.70, across sequential, random and strided netId families. That is the number the acceptance
/// criterion is about, and it is the one that costs anything: a probe that stays on the line it started on is one memory access.
/// </para>
/// <para>
/// <b>Growth doubles; shrink halves with a 2× hysteresis gap.</b> Growth happens when an insert would take the load past 0.70, which is the ceiling 02 § 5
/// fixes and what makes the structure cost ≈ 16 / 0.7 ≈ 22.9 B per known entity there. Shrink halves while the halved capacity would still leave the load at
/// or below 0.35 — exactly half the grow threshold — so a set has to double its population before it can grow again. Without that gap a session hovering at
/// a boundary would rehash on alternate ticks. Shrink is driven from <see cref="Remove"/> and <see cref="RemoveSource"/>, and is also reachable explicitly
/// through <see cref="TrimExcess"/>; <see cref="Enumerator.RemoveCurrent"/> deliberately never shrinks, because a rehash in the middle of a walk would
/// invalidate the walk.
/// </para>
/// <para>
/// <b>Native memory, registered.</b> The entry buffer comes from <see cref="IMemoryAllocator.AllocatePinned"/> — the same allocator
/// <see cref="ReplicationBlockPool"/> uses — so its bytes land on the unmanaged gauge and appear in a resource snapshot as a child of this node. It is never
/// a GC array: the project rule is that a raw pointer addresses only engine-owned memory, and a table addressed through <c>KnownEntry*</c> at 64-byte
/// alignment is exactly the case that rule exists for.
/// </para>
/// <para>
/// <b>Sized by interest, not by the database.</b> Nothing here is bounded by a byte budget, because the thing that bounds it is upstream: a session's
/// interest budget (a god session's 10 000-entity cap, 01 § 9) is the cap on how many entities it can know. A capacity ceiling of 2^26 slots (1 GiB) exists
/// only so the byte count stays inside an <c>int</c>; reaching it is a caller bug, and it throws rather than wrapping.
/// </para>
/// <para>
/// <b>Thread safety: none, by contract.</b> A known-set belongs to one session, and session state is written tick-side only (SUB-05). S2a and S2b are
/// parallel over session <i>chunks</i>, so exactly one worker touches a given set in a given tick and the dispatch barrier publishes the writes. There is
/// deliberately no lock and no CAS; the <see cref="ReplicationThreadAffinity"/> guard on the mutators is a DEBUG-only detector for two callers being inside
/// at once, which is what would tear the capacity/pointer pair across a growth.
/// </para>
/// <para>
/// <b>Pointer lifetime.</b> Every <see cref="KnownEntry"/> pointer handed out is valid only until the next mutation of this set. Growth and shrink move the
/// whole buffer, and backward-shift deletion moves entries within it. Read or write through a pointer immediately; never park one across a call.
/// </para>
/// </remarks>
internal sealed unsafe class KnownSet : ResourceNode, IMemoryResource
{
    /// <summary>Bytes per entry, fixed by 02 § 5. Divides a cache line, so a probe run of four consecutive slots is one memory touch.</summary>
    public const int EntryBytes = 16;

    /// <summary>Smallest table, 256 B. Below this the doubling trail costs more than the slots it saves.</summary>
    private const int MinCapacity = 16;

    /// <summary>Largest table: 2^26 slots is 1 GiB of entries, which keeps <c>capacity * EntryBytes</c> inside an <c>int</c>.</summary>
    private const int MaxCapacity = 1 << 26;

    /// <summary>Cache-line aligned, which together with the 16 B stride keeps every entry inside one line.</summary>
    private const int BufferAlignment = 64;

    /// <summary>Grow when an insert would take the load past 7/10 — the ceiling 02 § 5 fixes.</summary>
    private const int GrowNumerator = 7;

    /// <summary>Denominator of <see cref="GrowNumerator"/>.</summary>
    private const int GrowDenominator = 10;

    /// <summary>Ceiling on the load a shrink may leave behind: 35/100, exactly half the grow threshold. That ratio is the hysteresis.</summary>
    private const int ShrinkNumerator = 35;

    /// <summary>Denominator of <see cref="ShrinkNumerator"/>.</summary>
    private const int ShrinkDenominator = 100;

    private readonly IMemoryAllocator _allocator;

    private PinnedMemoryBlock _block;
    private KnownEntry* _entries;
    private int _capacity;
    private int _mask;
    private int _count;
    private int _growThreshold;
    private int _bufferSeq;
    private bool _disposed;

    /// <summary>
    /// Debug-only CONCURRENCY guard on the mutators — it rejects a second caller entering while one is inside, not a caller on a different thread. A session
    /// chunk legitimately lands on a different worker each tick; two workers inside one set at once is the failure worth catching.
    /// </summary>
    private ReplicationThreadAffinity _affinity;

    /// <summary>Creates an empty known-set and commits its first buffer.</summary>
    /// <param name="id">Stable resource id, unique among <paramref name="parent"/>'s children.</param>
    /// <param name="parent">Resource-graph parent; this node registers under it and its entry buffer registers under this node.</param>
    /// <param name="allocator">Engine allocator, supplied by DI exactly as <see cref="ReplicationBlockPool"/> takes it.</param>
    /// <param name="initialCapacity">
    /// Starting SLOT count, rounded up to a power of two and floored at <see cref="MinCapacity"/> — not an entity count. A set expected to hold <c>n</c>
    /// entities without a rehash wants roughly <c>n / 0.7</c> here.
    /// </param>
    public KnownSet(string id, IResource parent, IMemoryAllocator allocator, int initialCapacity = MinCapacity)
        : base(id, ResourceType.Cache, parent, ExhaustionPolicy.None)
    {
        ArgumentNullException.ThrowIfNull(allocator);

        if (initialCapacity <= 0 || initialCapacity > MaxCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity,
                $"A known-set capacity is a slot count in (0, {MaxCapacity}]; it is rounded up to a power of two.");
        }

        _allocator = allocator;
        Allocate(Math.Max(MinCapacity, (int)BitOperations.RoundUpToPowerOf2((uint)initialCapacity)));

        Debug.Assert(sizeof(KnownEntry) == EntryBytes, "KnownEntry must stay 16 B: the stride is what keeps an entry inside one cache line.");
    }

    /// <summary>Entities this session currently knows.</summary>
    public int KnownCount => _count;

    /// <summary>Slots in the table. Always a power of two; grows by doubling and shrinks by halving.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Bytes of native memory held by the entry buffer. Divided by <see cref="KnownCount"/>, this is the design's ≈ 23 B per known entity at load 0.7.
    /// </summary>
    public long CapacityBytes => (long)_capacity * EntryBytes;

    /// <summary>Count of entities the session knows, surfaced to the Workbench resource tree.</summary>
    public override int? Count => _count;

    /// <summary>
    /// Address of the entry buffer, for diagnostics and tests. It changes on every growth and shrink; nothing may hold it.
    /// </summary>
    public nint EntriesAddress => (nint)_entries;

    /// <inheritdoc />
    /// <remarks>Bookkeeping only. The entry buffer is a child of this node and is accounted separately, per the interface contract.</remarks>
    public int EstimatedMemorySize => 64;

    /// <summary>
    /// Brings the line a <see cref="Probe"/> of <paramref name="netId"/> would read first toward the core, without reading it.
    /// </summary>
    /// <remarks>
    /// <b>A hint and nothing else.</b> It observes no state, returns nothing, and cannot fault: a prefetch of an address the caller may not read is
    /// architecturally a no-op, which is what makes it safe to issue for a slot the walk has not reached yet. On a platform with no prefetch instruction
    /// the whole body folds away, so the feature costs a JIT-time constant when it is not available.
    /// <para>
    /// It exists because the gather's probe is the one miss whose ADDRESS depends on a previous miss — the slot's netId, read from its replication block —
    /// so the only way to overlap it with anything is to compute it one slot early. Measured at d06 with 200 sessions the gather spent 409 ns per interest
    /// run for 1.7 visited slots, which is five dependent misses end to end and no memory-level parallelism at all.
    /// </para>
    /// </remarks>
    /// <param name="netId">The identity a later <see cref="Probe"/> will look up.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrefetchProbe(uint netId)
    {
        if (Sse.IsSupported)
        {
            Sse.Prefetch0(_entries + (int)(HashUtils.FastHash32(netId) & (uint)_mask));
        }
    }

    /// <summary>
    /// Answers the probe question for one hit: unknown, known-and-current, or known-under-another-generation.
    /// </summary>
    /// <param name="netId">The identity to look up. Never <see cref="NetIdAllocator.NoNetId"/>.</param>
    /// <param name="generation">The generation the hit carries.</param>
    /// <param name="entry">
    /// The entry when the result is not <see cref="KnownProbe.Unknown"/>; otherwise <see langword="null"/>. Valid until the next mutation.
    /// </param>
    /// <remarks>
    /// "Has it changed since my baseline?" is the caller's comparison, not this method's: in Phase 1 the baseline is one tick per session, so the test is
    /// <c>groupTick &gt; baseline</c>. Phase 2 moves it into the entry as <see cref="KnownEntry.LastSentTick"/>, which is why that field is already here.
    /// </remarks>
    public KnownProbe Probe(uint netId, ushort generation, out KnownEntry* entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfReservedNetId(netId);

        var slot = FindSlot(netId);
        if (slot < 0)
        {
            entry = null;
            return KnownProbe.Unknown;
        }

        entry = _entries + slot;
        return entry->Generation == generation ? KnownProbe.Current : KnownProbe.Stale;
    }

    /// <summary>
    /// Records that <paramref name="sourceBit"/> reaches <paramref name="netId"/>, creating the entry when this is the first source to do so.
    /// </summary>
    /// <param name="netId">The identity. Never <see cref="NetIdAllocator.NoNetId"/>.</param>
    /// <param name="generation">The generation the hit carries.</param>
    /// <param name="sourceBit">A single-bit mask naming the interest source. Zero is rejected: a source that sets no bit could never be removed.</param>
    /// <param name="seenStamp">Stored into <see cref="KnownEntry.SeenStamp"/>; normally the low 16 bits of the current tick.</param>
    /// <param name="entry">The entry, new or existing. Valid until the next mutation.</param>
    /// <returns>
    /// <see cref="KnownAdd.Entered"/> when a new entry was created — the caller's enter record; <see cref="KnownAdd.AlreadyKnown"/> when the bit was merged
    /// into an existing entry; <see cref="KnownAdd.Stale"/> when an entry exists under another generation, in which case <b>nothing was modified</b> and the
    /// caller owes a leave (02 § 5).
    /// </returns>
    public KnownAdd AddSource(uint netId, ushort generation, byte sourceBit, ushort seenStamp, out KnownEntry* entry)
    {
        _affinity.Enter(nameof(KnownSet), nameof(AddSource));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfReservedNetId(netId);

            if (sourceBit == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceBit), sourceBit,
                    "A source must set at least one bit; a zero mask would create an entry that no RemoveSource could ever empty.");
            }

            var slot = FindSlot(netId);
            if (slot >= 0)
            {
                entry = _entries + slot;

                // A reuse this session did not see. Leaving the entry untouched is deliberate: the caller needs the OLD generation to emit the leave, and
                // overwriting it here would turn a leave-then-enter into a silent identity swap — the divergence SUB-03 exists to forbid.
                if (entry->Generation != generation)
                {
                    return KnownAdd.Stale;
                }

                entry->Sources |= sourceBit;
                entry->SeenStamp = seenStamp;
                return KnownAdd.AlreadyKnown;
            }

            // Grow only when the probe proved an insert is coming. Checking the threshold before the lookup would rehash on every AlreadyKnown call once the
            // set sat at its threshold — a per-hit rehash for a set that was not growing at all.
            if (_count + 1 > _growThreshold)
            {
                Resize(_capacity * 2);
                slot = FindSlot(netId);
            }

            entry = _entries + ~slot;
            entry->NetId = netId;
            entry->Generation = generation;
            entry->SeenStamp = seenStamp;
            entry->Sources = sourceBit;
            entry->Flags = KnownFlags.None;
            entry->Archetype = 0;
            entry->LastSentTick = 0;
            _count++;
            return KnownAdd.Entered;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>
    /// Clears <paramref name="sourceBit"/> from <paramref name="netId"/>'s mask, removing the entry when the mask empties — which is the session's leave.
    /// </summary>
    /// <returns><see cref="KnownLeave.NotKnown"/>, <see cref="KnownLeave.StillKnown"/> or <see cref="KnownLeave.Left"/>.</returns>
    public KnownLeave RemoveSource(uint netId, byte sourceBit)
    {
        _affinity.Enter(nameof(KnownSet), nameof(RemoveSource));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfReservedNetId(netId);

            var slot = FindSlot(netId);
            if (slot < 0)
            {
                return KnownLeave.NotKnown;
            }

            var entry = _entries + slot;
            entry->Sources &= (byte)~sourceBit;

            if (entry->Sources != 0)
            {
                return KnownLeave.StillKnown;
            }

            RemoveAt(slot);
            ShrinkIfSparse();
            return KnownLeave.Left;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>
    /// Drops <paramref name="netId"/> whatever its source mask holds — the stale-generation leave, and a session reset's per-entity path.
    /// </summary>
    /// <returns><see langword="false"/> when the session did not know it.</returns>
    public bool Remove(uint netId)
    {
        _affinity.Enter(nameof(KnownSet), nameof(Remove));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfReservedNetId(netId);

            var slot = FindSlot(netId);
            if (slot < 0)
            {
                return false;
            }

            RemoveAt(slot);
            ShrinkIfSparse();
            return true;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>
    /// Forgets every entity, keeping the capacity. A <c>RESET</c> frame's session-side half: the client is told to clear its store and the set refills from
    /// the same interest, so giving the buffer back only to take it again next tick would be churn for nothing. Call <see cref="TrimExcess"/> to release it.
    /// </summary>
    public void Clear()
    {
        _affinity.Enter(nameof(KnownSet), nameof(Clear));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            NativeMemory.Clear(_entries, (nuint)((long)_capacity * EntryBytes));
            _count = 0;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>
    /// Applies the shrink policy now: halve while the halved capacity would still leave the load at or below 0.35, never below <see cref="MinCapacity"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Remove"/> and <see cref="RemoveSource"/> already call this, so the common leave path self-shrinks. It is public for the one case they
    /// cannot cover: a sweep that removes through <see cref="Enumerator.RemoveCurrent"/>, where shrinking mid-walk would invalidate the walk. Call it after
    /// the sweep.
    /// </remarks>
    public void TrimExcess()
    {
        _affinity.Enter(nameof(KnownSet), nameof(TrimExcess));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ShrinkIfSparse();
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>Walks the live entries in slot order. Order is an implementation detail and changes with every rehash.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>
    /// Diagnostic: how many slots a probe for <paramref name="netId"/> examines, hit or miss.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="FindSlot"/> rather than re-implemented, so it cannot drift from the real probe: the sequence starts at the home slot and steps
    /// by one, so the distance from home to the slot the walk stopped on <i>is</i> the count. Costs the hot path nothing, because nothing on the hot path
    /// counts anything.
    /// </remarks>
    public int ProbeLength(uint netId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfReservedNetId(netId);

        var slot = FindSlot(netId);
        var stopped = slot >= 0 ? slot : ~slot;
        var home = (int)(HashUtils.FastHash32(netId) & (uint)_mask);
        return ((stopped - home + _capacity) & _mask) + 1;
    }

    /// <summary>
    /// Diagnostic: how many 64-byte cache lines a probe for <paramref name="netId"/> touches. This is the number the "one probe" goal is about — four entries
    /// share a line and a probe run is contiguous, so a run that stays on its line is a single memory access.
    /// </summary>
    public int ProbeLineCount(uint netId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfReservedNetId(netId);

        var entriesPerLine = 64 / EntryBytes;
        var home = (int)(HashUtils.FastHash32(netId) & (uint)_mask);
        var length = ProbeLength(netId);

        // The run may wrap the table, and a wrapped run's two halves are never on the same line, so counting from the unwrapped end is exact either way: the
        // table's first slot is line-aligned because the buffer is 64 B aligned and the capacity is a power of two at least 16.
        return ((home + length - 1) / entriesPerLine) - (home / entriesPerLine) + 1;
    }

    /// <summary>
    /// Locates <paramref name="netId"/>'s slot, or the slot an insert would take.
    /// </summary>
    /// <returns>
    /// The slot index when the key is present; otherwise the bitwise complement of the first empty slot — negative, and the insertion point for free, so one
    /// walk serves both the probe and the insert.
    /// </returns>
    /// <remarks>
    /// The key comparison is the whole entry test: a 4-byte key is no more expensive to compare than a stored hash would be, so unlike <c>HashMapKV</c> this
    /// table stores no hash and spends all 16 bytes on state. The loop terminates because the load factor never reaches 1.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindSlot(uint netId)
    {
        var mask = _mask;
        var entries = _entries;
        var slot = (int)(HashUtils.FastHash32(netId) & (uint)mask);

        while (true)
        {
            var id = entries[slot].NetId;

            if (id == netId)
            {
                return slot;
            }

            if (id == NetIdAllocator.NoNetId)
            {
                return ~slot;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>
    /// Removes the entry at <paramref name="slot"/> by backward shifting, so no tombstone is left and a probe still stops at the first empty slot.
    /// </summary>
    private void RemoveAt(int slot)
    {
        var mask = _mask;
        var capacity = _capacity;
        var entries = _entries;
        var j = (slot + 1) & mask;

        while (true)
        {
            var netId = entries[j].NetId;
            if (netId == NetIdAllocator.NoNetId)
            {
                break;
            }

            // Move j back into the hole only when the hole is at least as close to j's home as j is: anything nearer would break the "no empty slot between
            // home and entry" invariant for some OTHER key.
            var home = (int)(HashUtils.FastHash32(netId) & (uint)mask);
            var distanceToHole = (slot - home + capacity) & mask;
            var distanceToJ = (j - home + capacity) & mask;

            if (distanceToHole < distanceToJ)
            {
                entries[slot] = entries[j];
                slot = j;
            }

            j = (j + 1) & mask;
        }

        // Only the key is cleared. Emptiness is defined by the key alone, and an insert rewrites every field, so zeroing the rest would be 12 bytes of work
        // per leave for no observable difference.
        entries[slot].NetId = NetIdAllocator.NoNetId;
        _count--;
    }

    /// <summary>
    /// The slot just past the first empty one — a <b>run boundary</b>, and the only safe place for a removing walk to start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A probe run stops at the first empty slot, so no run spans this position. Starting a walk here is what makes probe order and walk order the same
    /// thing, and that equivalence is the whole correctness argument for <see cref="Enumerator.RemoveCurrent"/>: a backward shift only ever moves an entry
    /// EARLIER in its run, so with the two orders aligned it can only move an entry to a slot the walk has yet to reach.
    /// </para>
    /// <para>
    /// Starting at slot 0 instead is what the obvious implementation does, and it is wrong in a way that only appears under load: a run that wraps the end of
    /// the table has its tail at low indices, so a shift can carry an entry from a low, already-visited slot up to a high, not-yet-visited one — and the walk
    /// hands the caller the same entity twice. At a 0.68 load over six seeds, one seed reproduced it.
    /// </para>
    /// <para>
    /// The scan is bounded by the length of the run covering slot 0, which at the 0.7 ceiling averages a couple of slots. A table with no empty slot cannot
    /// exist here, but the fallback keeps the loop total rather than trusting that.
    /// </para>
    /// </remarks>
    private int FirstSlotAfterAnEmptyOne()
    {
        var entries = _entries;

        for (var i = 0; i < _capacity; i++)
        {
            if (entries[i].NetId == NetIdAllocator.NoNetId)
            {
                return (i + 1) & _mask;
            }
        }

        return 0;
    }

    /// <summary>Halves the table while the halved capacity would still leave the load at or below 0.35.</summary>
    private void ShrinkIfSparse()
    {
        var target = _capacity;

        while (target > MinCapacity && (long)_count * ShrinkDenominator <= (long)(target / 2) * ShrinkNumerator)
        {
            target /= 2;
        }

        if (target != _capacity)
        {
            Resize(target);
        }
    }

    /// <summary>
    /// Moves every live entry into a new buffer of <paramref name="newCapacity"/> slots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Entries are copied whole.</b> That is what makes <see cref="KnownFlags.NeedsFull"/>, the source mask, the stamp and Phase 2's
    /// <see cref="KnownEntry.LastSentTick"/> survive a growth without this method knowing what any of them mean. Rebuilding an entry from its key would
    /// silently drop every one of them, and the flag that went missing would only show up as a client rendering stale state.
    /// </para>
    /// <para>
    /// The new buffer is committed before the old one is released, so a resize peaks at 1.5× the new size. Releasing first is not an option — the entries
    /// have to be read from somewhere — and a session's table is small enough that the peak is not worth an in-place algorithm.
    /// </para>
    /// </remarks>
    private void Resize(int newCapacity)
    {
        if (newCapacity > MaxCapacity)
        {
            throw new InvalidOperationException(
                $"A known-set cannot exceed {MaxCapacity} slots ({(long)MaxCapacity * EntryBytes / (1024 * 1024)} MiB). A session knowing that many entities " +
                "means its interest budget is not bounding anything, which is the bug to fix.");
        }

        var oldEntries = _entries;
        var oldCapacity = _capacity;
        var oldBlock = _block;

        Allocate(newCapacity);

        var mask = _mask;
        var entries = _entries;

        for (var i = 0; i < oldCapacity; i++)
        {
            var source = oldEntries + i;
            if (source->NetId == NetIdAllocator.NoNetId)
            {
                continue;
            }

            var slot = (int)(HashUtils.FastHash32(source->NetId) & (uint)mask);
            while (entries[slot].NetId != NetIdAllocator.NoNetId)
            {
                slot = (slot + 1) & mask;
            }

            entries[slot] = *source;
        }

        oldBlock.Dispose();
    }

    /// <summary>
    /// Commits a zeroed buffer and installs it. Zeroed is what makes every slot empty, since emptiness is <see cref="NetIdAllocator.NoNetId"/>.
    /// </summary>
    private void Allocate(int capacity)
    {
        // The id carries a sequence number because a resize registers the new buffer while the old one is still a child of this node.
        _block = _allocator.AllocatePinned($"Entries-{_bufferSeq++}", this, capacity * EntryBytes, true, BufferAlignment);
        _entries = (KnownEntry*)_block.DataAsPointer;
        _capacity = capacity;
        _mask = capacity - 1;
        _growThreshold = (int)((long)capacity * GrowNumerator / GrowDenominator);
    }

    /// <summary>Rejects the reserved identity, which doubles as this table's empty-slot marker and can therefore never be a key.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowIfReservedNetId(uint netId)
    {
        if (netId == NetIdAllocator.NoNetId)
        {
            throw new ArgumentOutOfRangeException(nameof(netId), netId,
                "0 is NetIdAllocator.NoNetId and marks an empty slot; it is never handed out and can never be a known-set key.");
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Drop the pointer before the buffer goes, so nothing survives that names freed memory. The counters reset with it: a capacity left behind would let
        // a post-dispose insert compute a slot against a null base.
        _entries = null;
        _block = null;
        _capacity = 0;
        _mask = 0;
        _count = 0;
        _growThreshold = 0;

        // ResourceNode.Dispose frees the entry buffer, which is a child of this node.
        base.Dispose(disposing);

        // Last, so the tree stays walkable while the child disposes.
        Parent?.RemoveChild(this);
    }

    /// <summary>
    /// Walks the live entries, starting at a run boundary so that removing as it goes is safe. A <c>ref struct</c> because it hands out raw pointers into
    /// the table: it must not outlive the stack frame that walks.
    /// </summary>
    /// <remarks>
    /// Mutating the set through anything but <see cref="RemoveCurrent"/> during a walk is undefined — growth moves the buffer and the walk would finish
    /// reading freed memory. Slot order is not netId order and is not stable across a rehash.
    /// </remarks>
    public ref struct Enumerator
    {
        private readonly KnownSet _set;

        /// <summary>The run boundary the walk starts from. See <see cref="FirstSlotAfterAnEmptyOne"/> for why the walk cannot simply start at slot 0.</summary>
        private readonly int _start;

        /// <summary>Slots advanced from <see cref="_start"/>, so the walk covers the whole table exactly once however it wraps.</summary>
        private int _steps;

        internal Enumerator(KnownSet set)
        {
            _set = set;
            _start = set.FirstSlotAfterAnEmptyOne();
            _steps = -1;
        }

        /// <summary>The entry at the current slot. Valid until the next mutation.</summary>
        public KnownEntry* Current => _set._entries + ((_start + _steps) & _set._mask);

        /// <summary>Advances to the next live entry.</summary>
        public bool MoveNext()
        {
            var set = _set;
            var entries = set._entries;
            var mask = set._mask;
            var capacity = set._capacity;

            while (++_steps < capacity)
            {
                if (entries[(_start + _steps) & mask].NetId != NetIdAllocator.NoNetId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Removes the entry the walk is standing on — the sweep that turns "not seen this tick" into a leave.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The cursor steps <b>back</b> one slot, because backward-shift deletion pulls an entry from later in the run into the hole just vacated; without
        /// the rewind that entry is walked straight past and never swept, which leaks exactly the entries the sweep exists to find. When nothing moved the
        /// rewind costs one re-examination of an empty slot, which is cheaper than deciding whether it was needed.
        /// </para>
        /// <para>
        /// That "later in the run is later in the walk" step is only true because the walk starts at a run boundary — see
        /// <see cref="FirstSlotAfterAnEmptyOne"/>. The two properties are one mechanism and must not be separated.
        /// </para>
        /// <para>
        /// This never shrinks the table: a rehash mid-walk would invalidate the walk. Call <see cref="TrimExcess"/> afterwards.
        /// </para>
        /// </remarks>
        public void RemoveCurrent()
        {
            _set.RemoveAt((_start + _steps) & _set._mask);
            _steps--;
        }
    }
}
