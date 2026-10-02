using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// The tick's deferred entity commands, one segment per worker slot (#1099). A parallel system queues a spawn or a destroy through
/// <c>ctx.Commands</c> without a transaction; the engine applies the whole buffer in one fence phase.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-worker segments, no atomics on the push path.</b> Exactly <see cref="EventQueue{T}"/>'s arrangement and for the same measured reason: a worker
/// only ever touches its own segment, so queueing a command is a bounds check and a few plain increments. Correctness rests on slot disjointness, which
/// <see cref="DagScheduler.DispatcherWorkerId"/> establishes. The one atomic in the whole write path is the single
/// <see cref="Interlocked.Add(ref long, long)"/> per archetype per tick that reserves a key generation — see <see cref="EntityKeyBlocks"/>.
/// </para>
/// <para>
/// <b>Structure of arrays.</b> 24-byte headers in one buffer, 128-byte <see cref="ComponentValue"/> payloads in another, so the apply's grouping pass
/// walks headers and never pulls a payload into cache merely to sort it.
/// </para>
/// <para>
/// <b>The arena is GC-backed, and that is safe here — said explicitly because the project rule has a crash behind it.</b> Both buffers are managed
/// arrays and are only ever a copy SOURCE: <c>ReadOnlySpan&lt;ComponentValue&gt;.CopyTo</c> writes into them and the apply reads out of them by
/// <c>ref</c>. No <c>byte*</c>, no <c>fixed</c>, no <c>GCHandle</c> and no <c>Unsafe.AsPointer</c> is ever taken over them. That holds by construction
/// because <see cref="ComponentValue"/> carries no reference and no pointer, and it is the same arrangement <see cref="EventQueue{T}"/> already relies
/// on. The pointer lives entirely on the far side of the handoff, where the native staging arena is the DESTINATION of a store.
/// </para>
/// <para>
/// <b>Overflow is tolerated and counted, never thrown.</b> A command that does not fit returns <see cref="EntityId.Null"/> and increments an exact
/// counter. The per-tick budget is a number an application has to guess before it has met its own worst tick, and the worst tick is the mass-death event
/// nobody sizes correctly the first time — so losing the tick would be worse than losing the commands, provided the loss is exact and loud. A reported
/// zero means nothing was lost.
/// </para>
/// </remarks>
[PublicAPI]
public sealed partial class EntityCommandBuffer
{
    /// <summary>Floor on a segment's allocation, matching <c>EventQueue</c>'s: below it, growth churns on trivially small bursts.</summary>
    private const int MinSegmentCapacity = 16;

    /// <summary>Values one command may carry. An archetype's component count is far below this; a longer span is refused rather than truncated.</summary>
    internal const int MaxValuesPerCommand = 255;

    private readonly DatabaseEngine _engine;
    private readonly int _commandsPerTick;

    // Hot per-slot scalars, one cache line each. Written only by the slot's owning worker.
    private EntityCommandSegmentState[] _slots;

    // Per-slot buffers. The outer arrays are fixed at bind and NEVER grown from a worker (rule MD-02); a worker replaces only its own element when its
    // segment doubles, which no other thread reads before the completion barrier.
    private EntityCommandHeader[][] _headers;
    private ComponentValue[][] _payloads;

    private int _initialSegmentCapacity;

    // O(1) "anything queued this tick?" gate, so a tick that queued nothing does not touch every slot's cold padded line, nor clear the key cursors.
    private int _anyProduced;

    private EntityKeyBlocks _keys;

    // One bit per (archetype, reason) already logged this tick. Index 0 is the "archetype unknown" bucket, so an archetype's bucket is id + 1. Touched
    // only on a refusal, with one Interlocked.Or, and cleared by Reset.
    private int[] _refusalLogged;

    private ILogger _logger = NullLogger.Instance;

    internal EntityCommandBuffer(DatabaseEngine engine, int commandsPerTick)
    {
        if (commandsPerTick < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(commandsPerTick), commandsPerTick, "The per-tick entity-command budget must be at least 1.");
        }

        _engine = engine;
        _commandsPerTick = commandsPerTick;
        _refusalLogged = new int[16];
        BindWorkerSlots(1, 1);
    }

    /// <summary>Commands this buffer is sized for across all slots per tick, as declared. A skewed tick may exceed it; see <see cref="OverflowCount"/>.</summary>
    public int Capacity => _commandsPerTick;

    /// <summary>Commands accepted this tick, summed across every slot.</summary>
    public int Count
    {
        get
        {
            AcquireSegments();
            var total = 0;
            for (var i = 0; i < _slots.Length; i++)
            {
                total += _slots[i].Count;
            }

            return total;
        }
    }

    /// <summary>Entities this tick's accepted spawns will create — the sum over accepted commands of their entity counts, so a <c>SpawnMany</c> counts once per entity.</summary>
    public int PendingEntities
    {
        get
        {
            AcquireSegments();
            var total = 0;
            for (var i = 0; i < _slots.Length; i++)
            {
                total += _slots[i].SpawnedEntities;
            }

            return total;
        }
    }

    /// <summary>
    /// Commands dropped this tick because a segment was at its ceiling or an archetype had used its key-block generations. Exact, not sampled: the
    /// tolerate-and-count verdict rests on a reported zero meaning nothing was lost.
    /// </summary>
    public uint OverflowCount
    {
        get
        {
            AcquireSegments();
            uint total = 0;
            for (var i = 0; i < _slots.Length; i++)
            {
                total += _slots[i].Overflow;
            }

            return total;
        }
    }

    /// <summary>
    /// Commands refused at the call because they could not be valid — an unregistered archetype, a realm that cannot hold the entity, too many values, or
    /// a producer outside the key-block stride. Distinct from <see cref="OverflowCount"/>: that is the engine out of room, this is an impossible request.
    /// </summary>
    public uint RejectedCount
    {
        get
        {
            AcquireSegments();
            uint total = 0;
            for (var i = 0; i < _slots.Length; i++)
            {
                total += _slots[i].Rejected;
            }

            return total;
        }
    }

    /// <summary>
    /// The deepest any one slot's segment got this tick, summed across slots — what to size <see cref="Capacity"/> against.
    /// </summary>
    /// <remarks>
    /// A sum of per-slot high waters, which is the right figure HERE and would be wrong for an event queue: nothing drains this buffer mid-tick, so every
    /// slot's peak is its end-of-tick depth and the sum is a real simultaneous total. <see cref="EventQueue{T}"/> cannot say that, which is why it stamps
    /// a queue-level peak before draining instead.
    /// </remarks>
    public int PeakDepth
    {
        get
        {
            AcquireSegments();
            var total = 0;
            for (var i = 0; i < _slots.Length; i++)
            {
                total += _slots[i].PeakDepth;
            }

            return total;
        }
    }

    /// <summary>True when nothing has been queued this tick. O(1).</summary>
    public bool IsEmpty => Volatile.Read(ref _anyProduced) == 0;

    /// <summary>Key generations taken for one archetype this tick. One is healthy; above one means the derived block is small for this workload.</summary>
    internal int GenerationsTaken(int internalArchetypeId) => _keys.GenerationsTaken(internalArchetypeId);

    internal EntityKeyBlocks Keys => _keys;

    internal DatabaseEngine Engine => _engine;

    /// <summary>The logger the refusal path writes through. Null resets to a no-op, so a buffer built outside a runtime never NREs.</summary>
    internal ILogger Logger { set => _logger = value ?? NullLogger.Instance; }

    /// <summary>
    /// Acquire fence paired with the producers' plain segment stores — JIT-folded on x64, a load barrier on arm64. Necessary rather than
    /// belt-and-braces: the scheduler's completion barrier is decremented with <see cref="Interlocked"/> but spun on with a plain load, so without this
    /// an arm64 reader may sink its segment loads above it and fold stale counts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireSegments()
    {
        if (!X86Base.IsSupported)
        {
            Interlocked.MemoryBarrier();
        }
    }

    /// <summary>
    /// Sizes the per-slot segments and the key blocks. Called by the runtime when the resolved worker count is first known, mirroring
    /// <c>EventQueueBase.BindWorkerSlots</c>.
    /// </summary>
    /// <param name="slotCount"><see cref="DagScheduler.WorkerSlotCount"/> — worker threads plus the dispatcher slot.</param>
    /// <param name="workerCount">Real worker threads, which is what the key-block stride is derived from.</param>
    internal void BindWorkerSlots(int slotCount, int workerCount)
    {
        if (slotCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount), slotCount, "Worker slot count must be at least 1.");
        }

        // Split the declared per-tick budget evenly across slots, floored so a many-worker runtime does not degenerate into segments of one or two
        // commands. Skew is absorbed by growth rather than by over-allocating every slot up front.
        var perSlot = Math.Max(MinSegmentCapacity, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, _commandsPerTick / slotCount)));
        _initialSegmentCapacity = Math.Min(perSlot, _commandsPerTick);

        if (_slots == null || _slots.Length != slotCount)
        {
            _slots = new EntityCommandSegmentState[slotCount];
            _headers = new EntityCommandHeader[slotCount][];
            _payloads = new ComponentValue[slotCount][];
            _anyProduced = 0;
        }

        // The stride bounds ChunkIndex, which the runtime caps at twice the worker width (CostChunkCount, and FenceWorkPlan.ComputeMaxChunks for the
        // fence). A producer whose chunk index lands outside it is REFUSED and counted rather than wrapped — wrapping would hand two producers the same
        // key block, and duplicate entity ids would be both catastrophic and silent.
        var stride = Math.Max(2, 2 * Math.Max(1, workerCount));
        _keys = new EntityKeyBlocks(_engine, stride, EntityKeyBlocks.DeriveBlockSize(_commandsPerTick, stride));
        _keys.EnsureArchetypes(_engine?._archetypeStates?.Length ?? 16);
        EnsureRefusalTable();
    }

    /// <summary>
    /// Returns a handle bound to one worker slot. Resolve it ONCE per system body — the per-command cost is then a bounds check and a few increments.
    /// </summary>
    /// <param name="workerSlot"><see cref="TickContext.WorkerId"/> of the calling worker.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The slot is outside <c>[0, WorkerSlotCount)</c> — most likely <see cref="TickContext.NonWorkerId"/> from a lifecycle hook, which owns no segment.
    /// </exception>
    /// <param name="chunkIndex">
    /// <see cref="TickContext.ChunkIndex"/> — what the key blocks stride by, so an id does not depend on which worker ran the chunk.
    /// </param>
    internal EntityCommands GetWriter(int workerSlot, int chunkIndex = 0)
    {
        if ((uint)workerSlot >= (uint)_slots.Length)
        {
            var reason = workerSlot == TickContext.NonWorkerId
                ? "A lifecycle-hook context (OnFirstTick / OnShutdown) owns no worker slot — queue entity commands from a system body instead."
                : $"Slot {workerSlot} is outside [0, {_slots.Length}).";
            throw new ArgumentOutOfRangeException(nameof(workerSlot), workerSlot, $"Entity command buffer: {reason}");
        }

        return new EntityCommands(this, workerSlot, chunkIndex);
    }

    /// <summary>Clears the per-tick state. Serial, at tick start. Grown buffers are deliberately kept — the high-water allocation is the point of growth.</summary>
    internal void Reset()
    {
        if (Volatile.Read(ref _anyProduced) != 0)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                _slots[i] = default;
            }

            _keys.Reset();
            Array.Clear(_refusalLogged);
            Volatile.Write(ref _anyProduced, 0);
        }

        // Cheap even on an idle tick, and the alternative is a producer meeting an archetype registered since bind and being refused for it.
        _keys.EnsureArchetypes(_engine?._archetypeStates?.Length ?? 16);
        EnsureRefusalTable();
    }

    /// <summary>Sizes the refusal-dedup table to the archetypes plus the unknown bucket. Serial, from bind and tick start — never from a producer.</summary>
    private void EnsureRefusalTable()
    {
        var needed = (_engine?._archetypeStates?.Length ?? 16) + 1;
        if (_refusalLogged == null || _refusalLogged.Length < needed)
        {
            _refusalLogged = new int[needed];
        }
    }

    /// <summary>Raises the O(1) emptiness gate. Every writer stores the same value, so concurrent stores are benign.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkProduced()
    {
        if (Volatile.Read(ref _anyProduced) == 0)
        {
            Volatile.Write(ref _anyProduced, 1);
        }
    }

    /// <summary>
    /// Records a refused command: counts it on the slot, and logs it ONCE per (archetype, reason) per tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the counters are not enough on their own.</b> A command that returns <see cref="EntityId.Null"/> is a lost game action, and the shapes that
    /// cause one are mostly mistakes a developer can fix — an archetype never registered, a realm that cannot hold the entity. A number in a property is
    /// no use to someone who does not already suspect the problem; a line naming the archetype and the reason is.
    /// </para>
    /// <para>
    /// <b>Why once per tick.</b> The failure is per-tick systematic, not per-call interesting: a system spawning into an unregistered archetype does it for
    /// every entity it processes, which at a few thousand entities a tick would be a log line per entity per tick forever. The dedup is one
    /// <see cref="Interlocked.Or(ref int, int)"/> on a per-archetype mask, paid only on the refusal path, and the exact count stays available in
    /// <see cref="RejectedCount"/> and <see cref="OverflowCount"/>.
    /// </para>
    /// </remarks>
    internal void NoteRefusal(int workerSlot, int internalArchetypeId, EntityCommandRefusal reason)
    {
        ref var slot = ref _slots[workerSlot];
        if (reason is EntityCommandRefusal.SegmentFull or EntityCommandRefusal.KeyBlocksExhausted)
        {
            slot.Overflow++;
        }
        else
        {
            slot.Rejected++;
        }

        // Slot 0 of the mask table is the "archetype unknown" bucket — a refusal for an unregistered archetype has no id to key on.
        var bucket = (uint)internalArchetypeId < (uint)(_refusalLogged.Length - 1) ? internalArchetypeId + 1 : 0;
        var bit = 1 << (int)reason;
        if ((Interlocked.Or(ref _refusalLogged[bucket], bit) & bit) == 0)
        {
            LogCommandRefused(DescribeArchetype(internalArchetypeId), reason.ToString());
        }
    }

    /// <summary>The archetype's name for a log line, or a stand-in when there is no usable id — never throws, and never allocates on the accepted path.</summary>
    private string DescribeArchetype(int internalArchetypeId)
    {
        if ((uint)internalArchetypeId >= (uint)(_engine?._archetypeStates?.Length ?? 0))
        {
            return "<unregistered>";
        }

        var meta = ArchetypeRegistry.GetMetadata((ushort)internalArchetypeId);
        return meta?.ArchetypeType?.Name ?? internalArchetypeId.ToString(CultureInfo.InvariantCulture);
    }

    [LoggerMessage(LogLevel.Warning,
        "An entity command for archetype '{Archetype}' was refused ({Reason}); the command was NOT queued and the caller was given EntityId.Null. "
        + "Exact counts for the tick are on EntityCommandBuffer.RejectedCount / OverflowCount. Logged once per archetype and reason per tick.")]
    private partial void LogCommandRefused(string archetype, string reason);

    internal ref EntityCommandSegmentState SlotState(int workerSlot) => ref _slots[workerSlot];

    internal EntityCommandHeader[][] SlotHeaders() => _headers;

    internal ComponentValue[][] SlotPayloads() => _payloads;

    internal int SlotCount => _slots.Length;

    /// <summary>
    /// Makes room in this slot for one header and <paramref name="valueCount"/> values, growing or reporting the ceiling. Slot-owner-only. Split out of
    /// the fast path so that stays inlineable.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal bool TryReserveRoom(int workerSlot, int valueCount)
    {
        ref var slot = ref _slots[workerSlot];

        var headers = _headers[workerSlot];
        if (headers == null)
        {
            // First command in this slot — allocate lazily, so a buffer only pays for the workers that actually queue into it.
            _headers[workerSlot] = new EntityCommandHeader[_initialSegmentCapacity];
            _payloads[workerSlot] = new ComponentValue[_initialSegmentCapacity];
            headers = _headers[workerSlot];
        }

        if (slot.Count >= headers.Length)
        {
            if (headers.Length >= _commandsPerTick)
            {
                // The count is NOT bumped here: the caller reports it through NoteRefusal, which also logs. Counting in both places doubled it.
                return false;
            }

            var grown = Math.Min(headers.Length * 2, _commandsPerTick);
            Array.Resize(ref headers, grown);
            _headers[workerSlot] = headers;
        }

        if (valueCount > 0)
        {
            var payloads = _payloads[workerSlot];
            var needed = slot.PayloadCount + valueCount;
            if (needed > payloads.Length)
            {
                // The payload pool's ceiling is the command ceiling times the values one command may carry — a budget expressed in commands cannot also
                // bound values, and refusing a legal command because an unrelated one was value-heavy would make the budget mean two things.
                var ceiling = _commandsPerTick * MaxValuesPerCommand;
                if (needed > ceiling)
                {
                    return false;
                }

                var grown = Math.Min(Math.Max(payloads.Length * 2, needed), ceiling);
                Array.Resize(ref payloads, grown);
                _payloads[workerSlot] = payloads;
            }
        }

        return true;
    }
}
