using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>One index entry a commit took off a group key (14 § 5): where the entity is, which watched field, and the key it left.</summary>
internal struct GroupKeyMove
{
    /// <summary>The entity's <c>ClusterLocation</c>, <c>chunk × 64 + slot</c>.</summary>
    public int Location;

    /// <summary>The watched field's index, as <see cref="GroupKeyMoveLog.Watch"/> returned it.</summary>
    public int Watch;

    /// <summary>The key's bytes, at the field's width, zero above it: read back by the field's type.</summary>
    public ulong Raw;
}

/// <summary>
/// The group key moves the replication track cannot see in the index (14 § 5): a Versioned commit off the DAG (EW-02) may move a positionless member's
/// index entry between the projection and the gather, and the gather would then fill a key without a member the projection still filed under it. Each
/// commit that removes an entry from a watched field's index appends it here first; the gather reads the moves out of a key alongside the index, and the
/// blocks step clears the log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Appended before the index changes.</b> A gather that reads the index and then this log, and finds an entry gone from the first, finds its move in the
/// second: the append's lock release orders it before the index write, and the reader fences between its two reads.
/// </para>
/// <para>
/// <b>Read without the lock.</b> A writer publishes the array before the count that covers it, so a reader that loads the count and then the array sees at
/// least that many complete entries. The only reset is the blocks step's, which runs while no gather does.
/// </para>
/// <para>
/// Zero cost where nothing watches: an archetype whose fields no group set reads has no log, and the commit path tests one null.
/// </para>
/// </remarks>
internal sealed unsafe class GroupKeyMoveLog
{
    private readonly Lock _lock = new();
    private (int Slot, int Offset, int Size)[] _watched = [];
    private GroupKeyMove[] _moves = new GroupKeyMove[16];
    private int _count;

    /// <summary>Moves appended since the last clear, cumulative over the log's life. Diagnostic.</summary>
    public long Appended;

    /// <summary>Watches a field: its moves are logged from now on. Serial, at <c>Start</c>.</summary>
    /// <param name="componentSlot">The component's slot in the archetype.</param>
    /// <param name="fieldOffset">The field's offset in the component.</param>
    /// <param name="fieldSize">The field's size, at most 8 bytes.</param>
    /// <returns>The field's watch index, which each of its moves carries.</returns>
    public int Watch(int componentSlot, int fieldOffset, int fieldSize)
    {
        if (fieldSize is <= 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldSize), fieldSize, "A group key is an integer field of at most 8 bytes.");
        }

        for (var i = 0; i < _watched.Length; i++)
        {
            if (_watched[i].Slot == componentSlot && _watched[i].Offset == fieldOffset)
            {
                return i;
            }
        }

        Array.Resize(ref _watched, _watched.Length + 1);
        _watched[^1] = (componentSlot, fieldOffset, fieldSize);
        return _watched.Length - 1;
    }

    /// <summary>
    /// Logs that the entity at <paramref name="location"/> is leaving the key at <paramref name="key"/>, when the field is watched. Called by a commit, on
    /// any thread, BEFORE it moves or removes the index entry.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Note(int componentSlot, int fieldOffset, int location, byte* key)
    {
        var watched = _watched;
        for (var i = 0; i < watched.Length; i++)
        {
            if (watched[i].Slot == componentSlot && watched[i].Offset == fieldOffset)
            {
                Append(location, i, key, watched[i].Size);
                return;
            }
        }
    }

    private void Append(int location, int watch, byte* key, int size)
    {
        ulong raw = 0;
        Buffer.MemoryCopy(key, &raw, sizeof(ulong), size);
        lock (_lock)
        {
            var count = _count;
            var moves = _moves;
            if (count == moves.Length)
            {
                var grown = new GroupKeyMove[moves.Length * 2];
                Array.Copy(moves, grown, count);
                Volatile.Write(ref _moves, grown);
                moves = grown;
            }

            moves[count] = new GroupKeyMove { Location = location, Watch = watch, Raw = raw };
            Volatile.Write(ref _count, count + 1);
            Appended++;
        }
    }

    /// <summary>The moves logged since the last clear. Safe while commits append; the span may miss the newest, never show a torn one.</summary>
    public ReadOnlySpan<GroupKeyMove> Read()
    {
        var count = Volatile.Read(ref _count);
        var moves = Volatile.Read(ref _moves);
        return new ReadOnlySpan<GroupKeyMove>(moves, 0, Math.Min(count, moves.Length));
    }

    /// <summary>Forgets every move. Serial, at the blocks step: a move logged before the coming projection is one the projection reads.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Volatile.Write(ref _count, 0);
        }
    }
}
