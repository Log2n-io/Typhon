using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// One worker's identity tables for a session's frame: which identities the gather read, and which it has already emitted a <c>LEAVE</c> for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per worker, so nothing here is shared and nothing here is synchronized</b> — the same property <see cref="FrameWorkerScratch"/> and
/// <see cref="HitArena"/> rest on. A session belongs to one chunk for the whole of its assembly.
/// </para>
/// <para>
/// <b>What the two tables decide.</b> The incremental gather reads only the slots that entered a session's view or that S1 marked changed, so the leaves
/// the interest pass hands it have to be filtered: an entity that merely moved to another cluster appears as a departure from the old one and as an arrival
/// in the new, and a frame carrying both a leave and an enter for one identity is the divergence SUB-06 forbids. "Did this tick read it" answers that, and
/// "has a leave already gone out for it" keeps the answer single even where one identity names two block entries.
/// </para>
/// <para>
/// <b>Both tables are reset by clearing the slots that were written, not by clearing the table.</b> A session touches a few hundred entries of a table
/// sized for thousands, so a wholesale clear would cost more than the gather it serves; and re-probing to find a key's slot cannot be used to clear it,
/// because zeroing a slot in the middle of a probe chain makes every later key on that chain unreachable. The written slots are recorded as they are
/// written, which is the only reset that is both cheap and correct.
/// </para>
/// <para>
/// <b>Both grow before the insert that would take them past half full.</b> They are open-addressed with linear probing, and a probe over a full table does
/// not slow down — it never returns. Sized from a fixed hint, the first measured run of the incremental path at four times the density the hint was chosen
/// for froze the server: frame counts stopped advancing, every session was reported unserved, and no exception was raised anywhere.
/// </para>
/// </remarks>
internal sealed unsafe class FrameIdentityScratch : IDisposable
{
    private uint* _seenKeys;
    private int* _seenTouched;
    private int _seenCount;
    private int _seenMask;

    private uint* _emittedKeys;
    private int* _emittedTouched;
    private int _emittedCount;
    private int _emittedMask;

    private ulong* _displaced;
    private int _displacedCount;
    private int _displacedCapacity;

    private bool _disposed;

    /// <summary>Empties both tables for the next session this worker assembles.</summary>
    public void BeginSession()
    {
        if (_seenKeys == null)
        {
            Allocate(ref _seenKeys, ref _seenTouched, ref _seenMask, ref _seenCount, 256);
            Allocate(ref _emittedKeys, ref _emittedTouched, ref _emittedMask, ref _emittedCount, 128);
            _displacedCount = 0;
            return;
        }

        for (var i = 0; i < _seenCount; i++)
        {
            _seenKeys[_seenTouched[i]] = 0;
        }

        for (var i = 0; i < _emittedCount; i++)
        {
            _emittedKeys[_emittedTouched[i]] = 0;
        }

        _seenCount = 0;
        _emittedCount = 0;
        _displacedCount = 0;
    }

    /// <summary>Records that this tick's gather read the block entry of <paramref name="netId"/>.</summary>
    public void NoteSeen(uint netId)
    {
        if ((_seenCount + 1) * 2 > _seenMask + 1)
        {
            Grow(ref _seenKeys, ref _seenTouched, ref _seenMask, ref _seenCount);
        }

        var slot = FindSlot(_seenKeys, _seenMask, netId);
        if (_seenKeys[slot] == 0)
        {
            _seenKeys[slot] = netId;
            _seenTouched[_seenCount++] = slot;
        }
    }

    /// <summary>Whether this tick's gather read the block entry of <paramref name="netId"/> anywhere in this session's view.</summary>
    public bool WasSeen(uint netId) => _seenKeys[FindSlot(_seenKeys, _seenMask, netId)] != 0;

    /// <summary>Records that a leave has already been emitted for <paramref name="netId"/> in this session's frame.</summary>
    public void NoteEmitted(uint netId)
    {
        if ((_emittedCount + 1) * 2 > _emittedMask + 1)
        {
            Grow(ref _emittedKeys, ref _emittedTouched, ref _emittedMask, ref _emittedCount);
        }

        var slot = FindSlot(_emittedKeys, _emittedMask, netId);
        if (_emittedKeys[slot] == 0)
        {
            _emittedKeys[slot] = netId;
            _emittedTouched[_emittedCount++] = slot;
        }
    }

    /// <summary>Whether a leave has already been emitted for <paramref name="netId"/> in this session's frame.</summary>
    public bool WasEmitted(uint netId) => _emittedKeys[FindSlot(_emittedKeys, _emittedMask, netId)] != 0;

    /// <summary>Identities a reused slot pushed out of the session's view this tick, pending the test that they did not simply move.</summary>
    public ReadOnlySpan<ulong> Displaced => new(_displaced, _displacedCount);

    /// <summary>
    /// Records that the slot a session held <paramref name="id"/> in is now occupied by somebody else.
    /// </summary>
    /// <remarks>
    /// <b>This is the half a mask cannot see.</b> Interest compares per-cluster slot masks, so a slot whose OCCUPANT changed while its bit stayed set looks
    /// unchanged — and that is exactly what a destroy followed by a spawn into the same slot produces, which is common while a session is being skipped.
    /// The gather reads such a slot in any case (S1 re-initializes a reused slot and an initialized slot is always in the change mask), so the comparison
    /// costs one load against an identity already in hand. Whether it is a LEAVE is not decided here: the entity may simply have moved somewhere else in the
    /// same view, and that is not known until every run has been walked.
    /// </remarks>
    public void NoteDisplaced(ulong id)
    {
        if (_displacedCount == _displacedCapacity)
        {
            var capacity = _displacedCapacity == 0 ? 64 : _displacedCapacity * 2;
            _displaced = (ulong*)NativeMemory.Realloc(_displaced, (nuint)capacity * sizeof(ulong));
            _displacedCapacity = capacity;
        }

        _displaced[_displacedCount++] = id;
    }

    /// <summary>Native bytes held, for the owner's resource accounting.</summary>
    public long EstimatedBytes => ((long)_seenMask + 1 + _emittedMask + 1) * (sizeof(uint) + sizeof(int));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMemory.Free(_seenKeys);
        NativeMemory.Free(_seenTouched);
        NativeMemory.Free(_emittedKeys);
        NativeMemory.Free(_emittedTouched);
        NativeMemory.Free(_displaced);
        _displaced = null;
        _displacedCapacity = 0;
        _displacedCount = 0;
        _seenKeys = null;
        _seenTouched = null;
        _emittedKeys = null;
        _emittedTouched = null;
        _seenMask = 0;
        _emittedMask = 0;
        _seenCount = 0;
        _emittedCount = 0;
    }

    private static int FindSlot(uint* keys, int mask, uint netId)
    {
        var slot = (int)((netId * 2654435769u) >> 8) & mask;
        while (true)
        {
            var k = keys[slot];
            if (k == 0 || k == netId)
            {
                return slot;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>Doubles a table and reinserts what it holds, which the recorded slots make a walk of the ENTRIES rather than of the table.</summary>
    private static void Grow(ref uint* keys, ref int* touched, ref int mask, ref int count)
    {
        var oldKeys = keys;
        var oldTouched = touched;
        var oldCount = count;
        var capacity = (mask + 1) * 2;

        keys = (uint*)NativeMemory.AllocZeroed((nuint)capacity, sizeof(uint));
        touched = (int*)NativeMemory.Alloc((nuint)capacity, sizeof(int));
        mask = capacity - 1;
        count = 0;

        for (var i = 0; i < oldCount; i++)
        {
            var key = oldKeys[oldTouched[i]];
            var slot = FindSlot(keys, mask, key);
            keys[slot] = key;
            touched[count++] = slot;
        }

        NativeMemory.Free(oldKeys);
        NativeMemory.Free(oldTouched);
    }

    private static void Allocate(ref uint* keys, ref int* touched, ref int mask, ref int count, int capacity)
    {
        keys = (uint*)NativeMemory.AllocZeroed((nuint)capacity, sizeof(uint));
        touched = (int*)NativeMemory.Alloc((nuint)capacity, sizeof(int));
        mask = capacity - 1;
        count = 0;
    }
}
