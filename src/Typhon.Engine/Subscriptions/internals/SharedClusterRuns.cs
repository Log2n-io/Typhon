using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// One cluster's records for one tick, encoded once for every session that watches it: the wire bytes of a segment run and of a state run, as
/// <c>varu count | records</c> — exactly one <c>ENTITIES</c> sub-list run of 03 § 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this removes is not the encode, it is the per-session multiplier</b> (17 § 16). A state record's bytes depend on the entity and the tick, never
/// on who is watching, so the number of records an encode-once design produces is the number of slots the projection names as changed — a property of the
/// WORLD. Measured at d06, that count is flat in the session count while the records the frame stage emits are linear in it: 41× at 200 sessions and 83× at
/// 1 000. Extrapolated to ten thousand the subsystem would encode the same record about 830 times.
/// </para>
/// <para>
/// <b>Why the bytes can be shared at all, and why the frame stage still walks the slots.</b> A session may reference this run only when its baseline is the
/// previous tick — the mask here names the groups that changed in THIS tick, and a session further behind is owed the union since its own baseline — and
/// when it already knows every entity the run names. The second is NOT answerable per cluster: an entity displaced from one of the session's clusters is
/// dropped from its known-set while the cluster it moved into still names it, so a per-cluster test would hand a client a STATE for an entity it has never
/// heard of. The differential oracle produced exactly that within two hundred ticks. What is shared is therefore the ENCODE — the gaps, the masks and the
/// body copies — and not the classification, which is what 17 § 16.3 said from the start.
/// </para>
/// <para>
/// <b>The bytes live in the producing worker's record arena</b>, which is native, rewound once per tick and grown by doubling — so a run is named by
/// (worker, offset, length) and never by a pointer. <c>RecordArena.Reserve</c> reallocates, which would leave a cached pointer addressing freed memory;
/// resolving through the arena on read is what makes the reference safe across the growth this table cannot see.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 32)]
internal struct SharedClusterRun
{
    /// <summary>The tick these bytes describe. Any other tick means the entry is last tick's and must not be read.</summary>
    public uint Tick;

    /// <summary>The slots the two runs between them describe. A session must already know every one of them.</summary>
    public ulong Slots;

    /// <summary>Which worker's arena the bytes sit in.</summary>
    public int Worker;

    /// <summary>Byte offset of the state run in that arena, or <c>-1</c> when there is none.</summary>
    public int StateOffset;

    /// <summary>Byte offset of the segment run in that arena, or <c>-1</c> when there is none.</summary>
    public int SegmentOffset;

    /// <summary>Length of the state run, prefix included.</summary>
    public ushort StateBytes;

    /// <summary>Length of the segment run, prefix included.</summary>
    public ushort SegmentBytes;

    /// <summary>Records in the state run, so a frame that only references runs can still say how much it carries.</summary>
    public ushort StateCount;

    /// <summary>Records in the segment run.</summary>
    public ushort SegmentCount;
}

/// <summary>
/// One archetype's shared cluster runs for the current tick, indexed by chunk id.
/// </summary>
/// <remarks>
/// <para>
/// <b>A dense array rather than a dictionary, and stamped rather than cleared.</b> The frame stage asks this question once per session per cluster it
/// watches — hundreds of thousands of times a tick at the session counts this exists for — so the lookup has to be an index and a tick comparison. Clearing
/// the table between ticks would be a pass over every chunk the archetype has ever had; stamping each entry with the tick it was written for makes a stale
/// entry indistinguishable from an absent one at no per-tick cost, which is the same device <c>ReplicationBlockHeader.ChangedTick</c> uses beside it.
/// </para>
/// <para>
/// <b>Native, grown by doubling, never shrunk.</b> The chunk-id space of an archetype is bounded by its cluster count and grows only when the world does,
/// so the steady state reaches the allocator on no tick at all.
/// </para>
/// </remarks>
internal sealed unsafe class SharedRunTable : IDisposable
{
    private SharedClusterRun* _runs;
    private int _capacity;
    private bool _disposed;

    /// <summary>Native bytes this table holds, for the owner's resource accounting.</summary>
    public long EstimatedBytes => (long)_capacity * sizeof(SharedClusterRun);

    /// <summary>How many chunk ids the table can currently name.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Publishes one cluster's runs. Called by the projection pass, from the one worker that owns the block.
    /// </summary>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="run">The run descriptor.</param>
    /// <returns><see langword="false"/> when the table cannot name this chunk, which is not an error — the cluster is encoded per session instead.</returns>
    /// <remarks>
    /// <b>It never grows, and that is load-bearing.</b> The projection runs one worker per chunk of the watched-block list and every one of them publishes
    /// into this table, so a growth here would be several threads reallocating one buffer with no synchronization at all — which is not a torn value but a
    /// freed pointer the others keep writing through. It cost a silently dead server process to find. <see cref="EnsureCapacity"/> is called from the
    /// projection's serial prologue, where the watched set for the tick is already known and nothing is running beside it.
    /// </remarks>
    public bool Publish(int chunkId, in SharedClusterRun run)
    {
        if ((uint)chunkId >= (uint)_capacity)
        {
            return false;
        }

        _runs[chunkId] = run;
        return true;
    }

    /// <summary>
    /// Makes room for chunk ids below <paramref name="needed"/>. Called from the projection's serial prologue, never from a worker.
    /// </summary>
    /// <param name="needed">One past the largest chunk id this tick can publish.</param>
    public void EnsureCapacity(int needed)
    {
        if (needed > _capacity)
        {
            Grow(needed);
        }
    }

    /// <summary>
    /// Retires any run this table holds for <paramref name="chunkId"/>, so a block that was projected and produced nothing shared cannot be read against
    /// an earlier tick's bytes.
    /// </summary>
    /// <param name="chunkId">The cluster.</param>
    /// <remarks>
    /// Needed because the table is stamped and not cleared: a cluster shared at tick T and not shareable at T+1 would still carry T's stamp, and a reader
    /// comparing against T+1 would correctly reject it — but a reader is only ever given T+1, so the danger is the OTHER direction, a chunk id recycled
    /// onto a different cluster. Retiring on every projection that does not publish keeps "the entry names this tick" and "the entry is this cluster's"
    /// the same statement.
    /// </remarks>
    public void Retire(int chunkId)
    {
        if ((uint)chunkId < (uint)_capacity)
        {
            _runs[chunkId].Tick = 0;
        }
    }

    /// <summary>
    /// The run published for <paramref name="chunkId"/> at <paramref name="tick"/>, if there is one.
    /// </summary>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="tick">The tick being assembled.</param>
    /// <returns>A pointer to the entry, or <see langword="null"/> when this cluster published nothing for this tick.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SharedClusterRun* At(int chunkId, uint tick)
    {
        if ((uint)chunkId >= (uint)_capacity)
        {
            return null;
        }

        var entry = _runs + chunkId;
        return entry->Tick == tick ? entry : null;
    }

    private void Grow(int needed)
    {
        var capacity = _capacity == 0 ? 256 : _capacity;
        while (capacity < needed)
        {
            capacity *= 2;
        }

        var bytes = (nuint)capacity * (nuint)sizeof(SharedClusterRun);
        var grown = (SharedClusterRun*)NativeMemory.Alloc(bytes);
        NativeMemory.Clear(grown, bytes);
        if (_runs != null)
        {
            Buffer.MemoryCopy(_runs, grown, bytes, (nuint)_capacity * (nuint)sizeof(SharedClusterRun));
            NativeMemory.Free(_runs);
        }

        _runs = grown;
        _capacity = capacity;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMemory.Free(_runs);
        _runs = null;
        _capacity = 0;
    }
}
