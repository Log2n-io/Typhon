using System;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Routes owner-group changes to the sessions that control the changed entity (design/Subscriptions/11 § 2.2): each session's <b>pending owner mask</b>
/// is the union of the owner groups its controlled entity changed since the session's last published frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Push, not pull.</b> The projection already knows which owner groups changed (it compares them); it hands the mask to <see cref="Notice"/>, which finds
/// the controlling sessions through a reverse <c>Control</c> map and ORs the mask into each one's pending word. A frame then reads one word per session
/// instead of probing every controlled entity every tick — the cost follows owner changes, not sessions (SUB-13).
/// </para>
/// <para>
/// <b>The pending word records which groups, never values</b> (SUB-03's clause): the frame reads the values, current, from the owner entry when it writes
/// <c>SELF</c>. The frame stage clears the word only when the frame that carried those groups is published, so a skipped session keeps it and its next frame
/// carries the union.
/// </para>
/// <para>
/// <b>Threads.</b> <see cref="Refresh"/> runs in the Project stage's serial prologue; <see cref="Notice"/> from its parallel chunks, with the map read-only and
/// the pending words written by <see cref="Interlocked.Or(ref int, int)"/>; the Frames stage reads and clears them after the stage barrier.
/// </para>
/// </remarks>
internal sealed class SelfTracker
{
    private readonly int[] _pending;
    private readonly EntityId[] _controlledAtBuild;
    private int _mapVersion = -1;

    // Open-addressed, power-of-two, linear probing: the controlled entity's raw id → the first controlling slot; _next chains the others (-1 ends).
    private ulong[] _keys;
    private int[] _heads;
    private readonly int[] _next;
    private int _mask;

    /// <summary>Creates a tracker for a table of <paramref name="maxSessions"/> slots.</summary>
    /// <param name="maxSessions">The session table's width.</param>
    public SelfTracker(int maxSessions)
    {
        _pending = new int[maxSessions];
        _controlledAtBuild = new EntityId[maxSessions];
        _next = new int[maxSessions];
        Size(16);
    }

    /// <summary>The union of owner groups changed for <paramref name="slot"/>'s controlled entity since its last published frame.</summary>
    /// <param name="slot">The session's slot.</param>
    /// <returns>The mask.</returns>
    public int Pending(int slot) => Volatile.Read(ref _pending[slot]);

    /// <summary>Forgets <paramref name="slot"/>'s pending groups: its frame carried them, and was published.</summary>
    /// <param name="slot">The session's slot.</param>
    public void Clear(int slot) => Volatile.Write(ref _pending[slot], 0);

    /// <summary>
    /// Rebuilds the reverse <c>Control</c> map when <paramref name="sessions"/>' control version moved. Serial, before the projection's chunks.
    /// </summary>
    /// <param name="sessions">The session table.</param>
    public void Refresh(SessionTable sessions)
    {
        var version = sessions.ControlVersion;
        if (version == _mapVersion)
        {
            return;
        }

        _mapVersion = version;
        var count = 0;
        foreach (var session in sessions)
        {
            if (!sessions.ControlledOf(session).IsNull)
            {
                count++;
            }
        }

        if (count * 2 > _keys.Length)
        {
            Size(Math.Max(16, (int)BitOperations.RoundUpToPowerOf2((uint)(count * 2))));
        }
        else
        {
            Array.Clear(_keys);
        }

        foreach (var session in sessions)
        {
            var entity = sessions.ControlledOf(session);

            // A Control change marks every owner group pending (11 § 2.2, SUB-11). Comparing the entity at the frame is not enough: a flip A → X → A
            // between two published frames would find A again and send nothing, while A's changes during the X ticks were routed to nobody.
            if (_controlledAtBuild[session.Slot] != entity)
            {
                _controlledAtBuild[session.Slot] = entity;
                Volatile.Write(ref _pending[session.Slot], -1);
            }

            if (entity.IsNull)
            {
                continue;
            }

            var key = entity.RawValue;
            var i = Hash(key);
            while (_keys[i] != 0 && _keys[i] != key)
            {
                i = (i + 1) & _mask;
            }

            var slot = session.Slot;
            _next[slot] = _keys[i] == key ? _heads[i] : -1;
            _keys[i] = key;
            _heads[i] = slot;
        }
    }

    /// <summary>ORs <paramref name="groups"/> into the pending mask of every session that controls <paramref name="entity"/>. Thread-safe.</summary>
    /// <param name="entity">The entity whose owner groups changed.</param>
    /// <param name="groups">The owner groups that changed, as the projection's mask.</param>
    public void Notice(EntityId entity, int groups)
    {
        var key = entity.RawValue;
        if (key == 0)
        {
            return;
        }

        var i = Hash(key);
        while (true)
        {
            var probe = _keys[i];
            if (probe == 0)
            {
                return;
            }

            if (probe == key)
            {
                break;
            }

            i = (i + 1) & _mask;
        }

        for (var slot = _heads[i]; slot >= 0; slot = _next[slot])
        {
            Interlocked.Or(ref _pending[slot], groups);
        }
    }

    private int Hash(ulong key) => (int)((key * 0x9E3779B97F4A7C15UL) >> 32) & _mask;

    private void Size(int capacity)
    {
        _keys = new ulong[capacity];
        _heads = new int[capacity];
        _mask = capacity - 1;
    }
}
