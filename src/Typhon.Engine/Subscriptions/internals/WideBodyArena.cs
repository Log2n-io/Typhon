using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The out-of-line store of one archetype's wide section bodies (design/Subscriptions/13 § 6): a section holding a <c>str</c> is too wide for the hot entry,
/// so the entry keeps an 8-byte reference — a <c>u32</c> handle and a <c>u32</c> length — and the body lives here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Power-of-two size classes, 32 B to 256 KiB, carved from slabs.</b> A slab is 64 KiB, or one body for the classes above that, taken through
/// <see cref="IMemoryAllocator.AllocatePinned"/> and kept until <see cref="ResourceNode.Dispose()"/>: a body that shrinks or dies returns its slot to its
/// class's free list, never memory to the allocator. A body that changes class moves; one that changes within its class is rewritten in place.
/// </para>
/// <para>
/// <b>Handles, never addresses</b> (13 § 6.4). An entry stores <c>(class + 1) &lt;&lt; 28 | index</c>, 0 meaning "no body", and <see cref="Body"/> turns
/// it into a span — the one place in replication a raw pointer becomes one. That is what lets a migration or a park copy an entry verbatim: nothing to fix
/// up, and nothing a copy can leave dangling.
/// </para>
/// <para>
/// <b>One owner per body, checked.</b> A slot is live from the take that hands it out to the free that gives it back, and a bitmap per class records
/// which: a free of a slot that is not live — a handle copied and then freed by both copies — is refused and counted in <see cref="DoubleFrees"/>
/// rather than threaded onto the free list twice, where it would hand one body to two entries and corrupt both in silence.
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="Store"/> and <see cref="Free(uint)"/> are called from the projection's workers and from the fence's migration slices
/// at once, so the free lists, the bitmaps and the slab tables are mutated under one lock — taken only to allocate or free, which a body that keeps its class never
/// does. <see cref="Body"/> takes no lock: a slab table is replaced, never mutated, and published with a release store, and a slab is never freed before
/// disposal. The bytes of a body are written only by the worker that owns its entry.
/// </para>
/// <para>
/// <b>Bounded, and allocation-free once warm.</b> The arena commits slabs only while its committed bytes stay within its budget. Exhaustion is a
/// <see langword="false"/> from <see cref="Store"/>, never a throw. A class's free list and bitmap are sized when its slab is committed, so freeing never
/// allocates — it runs on the fence and in the cluster drain, where an allocation is not allowed.
/// </para>
/// </remarks>
internal sealed unsafe class WideBodyArena : ResourceNode, IMemoryResource
{
    /// <summary>The smallest body class: 32 B.</summary>
    public const int MinClassBytes = 32;

    /// <summary>The largest body class: 256 KiB. A wide section whose worst case is larger is refused at <c>Start</c>.</summary>
    public const int MaxClassBytes = 256 * 1024;

    /// <summary>A slab's size for every class that fits in it.</summary>
    private const int SlabBytes = 64 * 1024;

    private const int ClassCount = 14;
    private const int ClassShift = 28;
    private const uint IndexMask = (1u << ClassShift) - 1;
    private const int SlabAlignment = 64;

    private readonly IMemoryAllocator _allocator;
    private readonly long _budgetBytes;
    private readonly Lock _lock = new();
    private readonly List<PinnedMemoryBlock> _slabs = [];

    // Per class: the slabs' addresses, by slab index (replaced on growth, never mutated); how many bodies have been carved; the free indices, sized to
    // every slot the class's slabs hold; and which slots are live.
    private readonly nint[][] _slabTable = new nint[ClassCount][];
    private readonly int[] _carved = new int[ClassCount];
    private readonly int[][] _free = new int[ClassCount][];
    private readonly int[] _freeCount = new int[ClassCount];
    private readonly ulong[][] _live = new ulong[ClassCount][];

    private long _committedBytes;
    private long _liveBytes;
    private long _liveBodies;
    private long _doubleFrees;
    private volatile bool _disposed;

    /// <summary>Creates an arena; nothing is committed until the first body.</summary>
    /// <param name="id">Resource id, unique among <paramref name="parent"/>'s children.</param>
    /// <param name="parent">Resource-graph parent; the slabs register under the arena.</param>
    /// <param name="allocator">Engine allocator.</param>
    /// <param name="budgetBytes">The ceiling on committed slab bytes.</param>
    public WideBodyArena(string id, IResource parent, IMemoryAllocator allocator, long budgetBytes)
        : base(id, ResourceType.Allocator, parent, ExhaustionPolicy.Evict)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _allocator = allocator;
        _budgetBytes = budgetBytes;
        for (var c = 0; c < ClassCount; c++)
        {
            _slabTable[c] = [];
            _free[c] = [];
            _live[c] = [];
        }
    }

    /// <summary>Bytes committed in slabs. Grows only; returns to zero on disposal.</summary>
    public long CommittedBytes => Volatile.Read(ref _committedBytes);

    /// <summary>Bytes of the classes of every live body — what churn must return to its starting value (E-7).</summary>
    public long LiveBytes => Volatile.Read(ref _liveBytes);

    /// <summary>Bodies currently held.</summary>
    public long LiveBodies => Volatile.Read(ref _liveBodies);

    /// <summary>
    /// Frees of a slot that was not live, refused. Zero unless an entry's handle was copied and both copies were ended — a defect this counter makes
    /// visible instead of letting it share one body between two entries.
    /// </summary>
    public long DoubleFrees => Volatile.Read(ref _doubleFrees);

    /// <inheritdoc />
    /// <remarks>Bookkeeping only: the slabs are children of this node and are accounted separately, per the interface contract.</remarks>
    public long EstimatedMemorySize => 256 + (_slabs.Count * IntPtr.Size);

    /// <summary>The size class a body of <paramref name="length"/> bytes takes, or -1 when it exceeds <see cref="MaxClassBytes"/>.</summary>
    public static int ClassFor(int length)
    {
        if (length > MaxClassBytes)
        {
            return -1;
        }

        var size = Math.Max(MinClassBytes, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, length)));
        return BitOperations.Log2((uint)size) - BitOperations.Log2(MinClassBytes);
    }

    /// <summary>The bytes of class <paramref name="sizeClass"/>.</summary>
    public static int ClassBytes(int sizeClass) => MinClassBytes << sizeClass;

    /// <summary>The class of a handle; -1 for no body.</summary>
    public static int ClassOf(uint handle) => handle == 0 ? -1 : (int)(handle >> ClassShift) - 1;

    /// <summary>
    /// A handle's body, its whole class: the caller slices it to the length the entry stores. Empty for handle 0, and once the arena is disposed — a
    /// fence still in flight during teardown reads nothing rather than freed memory.
    /// </summary>
    /// <param name="handle">A live handle.</param>
    /// <returns>The span.</returns>
    public Span<byte> Body(uint handle)
    {
        if (handle == 0 || _disposed)
        {
            return [];
        }

        // Both shifts: a slab holds a power of two of bodies (64 KiB over a power-of-two class, or one), so the slab and the slot are a shift and a mask.
        var sizeClass = ClassOf(handle);
        var index = (int)(handle & IndexMask);
        var slabShift = SlabShift(sizeClass);
        var table = Volatile.Read(ref _slabTable[sizeClass]);
        var slab = index >> slabShift;
        if ((uint)slab >= (uint)table.Length)
        {
            return [];
        }

        var size = ClassBytes(sizeClass);
        return new Span<byte>((byte*)table[slab] + ((long)(index & ((1 << slabShift) - 1)) << (BitOperations.Log2((uint)size))), size);
    }

    /// <summary>A handle's first <paramref name="length"/> bytes, for a reader: empty when the handle names no body that long.</summary>
    public ReadOnlySpan<byte> Read(uint handle, uint length)
    {
        var body = Body(handle);
        return length <= (uint)body.Length ? body[..(int)length] : [];
    }

    /// <summary>
    /// Stores <paramref name="body"/> under <paramref name="handle"/>: in place when the body keeps its class, else in a new slot of the right class, freeing
    /// the old one.
    /// </summary>
    /// <param name="handle">The entry's handle, 0 for none; replaced when the body moves.</param>
    /// <param name="body">The bytes.</param>
    /// <returns><see langword="false"/> when no slot could be had within the budget; <paramref name="handle"/> and its bytes are then unchanged.</returns>
    public bool Store(ref uint handle, ReadOnlySpan<byte> body)
    {
        var wanted = ClassFor(body.Length);
        if (wanted < 0)
        {
            return false;
        }

        if (ClassOf(handle) != wanted)
        {
            uint fresh;
            lock (_lock)
            {
                if (!TryTake(wanted, out fresh))
                {
                    return false;
                }

                if (handle != 0)
                {
                    Give(handle);
                }
            }

            handle = fresh;
        }

        body.CopyTo(Body(handle));
        return true;
    }

    /// <summary>Returns a body's slot to its class. A handle of 0 is ignored.</summary>
    public void Free(uint handle)
    {
        if (handle == 0)
        {
            return;
        }

        lock (_lock)
        {
            Give(handle);
        }
    }

    /// <summary>Returns every non-zero handle of <paramref name="handles"/> under one acquisition of the lock — an entry's bodies, freed together.</summary>
    public void Free(ReadOnlySpan<uint> handles)
    {
        lock (_lock)
        {
            foreach (var handle in handles)
            {
                if (handle != 0)
                {
                    Give(handle);
                }
            }
        }
    }

    /// <summary>Whether this handle is live, for a test that asserts single ownership.</summary>
    internal bool IsLive(uint handle)
    {
        if (handle == 0)
        {
            return false;
        }

        lock (_lock)
        {
            var sizeClass = ClassOf(handle);
            var index = (int)(handle & IndexMask);
            var live = _live[sizeClass];
            return (index >> 6) < live.Length && (live[index >> 6] & (1UL << index)) != 0;
        }
    }

    private bool TryTake(int sizeClass, out uint handle)
    {
        handle = 0;
        if (_disposed)
        {
            return false;
        }

        int index;
        if (_freeCount[sizeClass] > 0)
        {
            index = _free[sizeClass][--_freeCount[sizeClass]];
        }
        else
        {
            index = _carved[sizeClass];
            if (index > (int)IndexMask)
            {
                return false;
            }

            if ((index >> SlabShift(sizeClass)) >= _slabTable[sizeClass].Length && !TryGrow(sizeClass))
            {
                return false;
            }

            _carved[sizeClass] = index + 1;
        }

        _live[sizeClass][index >> 6] |= 1UL << index;
        Volatile.Write(ref _liveBytes, _liveBytes + ClassBytes(sizeClass));
        Volatile.Write(ref _liveBodies, _liveBodies + 1);
        handle = ((uint)(sizeClass + 1) << ClassShift) | (uint)index;
        return true;
    }

    private void Give(uint handle)
    {
        var sizeClass = ClassOf(handle);
        var index = (int)(handle & IndexMask);
        var live = _live[sizeClass];
        if ((index >> 6) >= live.Length || (live[index >> 6] & (1UL << index)) == 0)
        {
            // A slot freed while not live: refused, so the free list never holds it twice. A defect upstream, never a state this arena reaches alone.
            Volatile.Write(ref _doubleFrees, _doubleFrees + 1);
            return;
        }

        live[index >> 6] &= ~(1UL << index);
        _free[sizeClass][_freeCount[sizeClass]++] = index;
        Volatile.Write(ref _liveBytes, _liveBytes - ClassBytes(sizeClass));
        Volatile.Write(ref _liveBodies, _liveBodies - 1);
    }

    private bool TryGrow(int sizeClass)
    {
        var bytes = Math.Max(SlabBytes, ClassBytes(sizeClass));
        if (_committedBytes + bytes > _budgetBytes)
        {
            return false;
        }

        var slab = _allocator.AllocatePinned($"Wide-{_slabs.Count}", this, bytes, false, SlabAlignment);
        _slabs.Add(slab);
        Volatile.Write(ref _committedBytes, _committedBytes + bytes);

        // The free list and the bitmap grow with the slots the class can hand out, here, so a free never has to: it is the one allocation a class
        // makes per 64 KiB of text, on the projection's own path.
        var old = _slabTable[sizeClass];
        var slots = (old.Length + 1) << SlabShift(sizeClass);
        Array.Resize(ref _free[sizeClass], slots);
        Array.Resize(ref _live[sizeClass], (slots + 63) >> 6);

        // Replaced, never grown in place: a worker reading a body concurrently holds the old table, whose slabs are all still there.
        var table = new nint[old.Length + 1];
        old.CopyTo(table, 0);
        table[^1] = (nint)slab.DataAsPointer;
        Volatile.Write(ref _slabTable[sizeClass], table);
        return true;
    }

    // log2 of the bodies one slab of a class holds: 64 KiB over the class, or one body for a class above a slab.
    private static int SlabShift(int sizeClass) => Math.Max(0, BitOperations.Log2(SlabBytes) - BitOperations.Log2((uint)ClassBytes(sizeClass)));

    /// <inheritdoc />
    /// <remarks>
    /// The slab tables are left as they are: a migration slice or a frame still in flight during teardown may read through them, and finds
    /// <see cref="Body"/> empty once <c>_disposed</c> is set rather than an index past an emptied table.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _slabs.Clear();
            _committedBytes = 0;
            _liveBytes = 0;
            _liveBodies = 0;
        }

        // ResourceNode.Dispose frees the slabs, which are children of this node.
        base.Dispose(disposing);
        Parent?.RemoveChild(this);
    }
}
