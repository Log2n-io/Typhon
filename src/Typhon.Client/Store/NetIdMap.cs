using System;

namespace Typhon.Client;

/// <summary>
/// Maps a netId to the packed handle that says where the entity lives, in space proportional to what a session HOLDS
/// rather than to the range of ids the server happens to use.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces a flat <c>int[]</c> indexed by netId, and the reason is a measurement.</b> netIds are dense on the
/// server but they are dense over the WHOLE WORLD, while a session holds only what is inside its own region. The flat
/// array was therefore sized by the world and paid for by every session: against a 16 km Tatooine at five times
/// population, which mints some 85 000 entities while building, every session's map doubled to 524 288 entries — a
/// uniform <b>2 MB of almost entirely zeroes</b>, whether the session was watching four thousand entities or forty.
/// A heap dump of fifty bot sessions found exactly that, 49 arrays of 2 097 176 bytes, <b>67 % of the whole heap</b>,
/// and a thousand-session bot box reached 42 GB and stopped being able to open sessions at all.
/// </para>
/// <para>
/// <b>A hash map and not a paged array, which is the choice worth explaining.</b> Paging a flat array keeps plain
/// indexing on the hot path and would be the natural fit here. It does not work for this access pattern: a session's
/// entities are SPATIALLY local — one region of a few hundred metres — while netIds are handed out in world-build
/// order, so the ids inside one region are scattered across the entire range. Pages would then hold about one live
/// entry each and the paged form could cost more than the flat one. Locality in id space is the assumption paging
/// needs and the one thing this workload does not provide.
/// </para>
/// <para>
/// <b>Open addressing with linear probing, keys and values in separate arrays.</b> A probe touches only keys until it
/// hits, so the values never share a cache line with the search; <c>Dictionary</c> would put both in one entry struct
/// alongside a hash and a next-index, at roughly three times the footprint and an indirection through buckets. Keys
/// are multiplied by the 32-bit golden ratio before being folded down, because netIds arrive in near-sequential runs
/// and the low bits of a counter are the worst possible bucket index for linear probing.
/// </para>
/// <para>
/// <b>netId 0 is the empty sentinel, which costs nothing because it is already invalid.</b> The caller rejects it
/// before this type sees it, so there is no need for a parallel occupancy bitmap or a tombstone value.
/// </para>
/// <para>
/// Deletion shifts the following run backwards rather than leaving a tombstone. Tombstones are simpler but they
/// accumulate, and a session that watches entities enter and leave for an hour is exactly the case where a table full
/// of tombstones degrades to a linear scan while reporting a low load factor.
/// </para>
/// </remarks>
internal sealed class NetIdMap
{
    /// <summary>Entries in the smallest table. A power of two, like every capacity here.</summary>
    /// <remarks>
    /// 64 entries is 512 bytes across both arrays, so an idle or just-opened session costs essentially nothing and the
    /// first few hundred entities are reached in one or two growths.
    /// </remarks>
    private const int MinCapacity = 64;

    /// <summary>The golden ratio over 2^32: Knuth's multiplicative constant, for folding near-sequential ids.</summary>
    private const uint Fibonacci = 2654435769;

    private uint[] _keys;
    private int[] _values;
    private int _mask;
    private int _shift;
    private int _count;

    /// <summary>Creates an empty map.</summary>
    public NetIdMap() => Allocate(MinCapacity);

    /// <summary>How many netIds are mapped.</summary>
    public int Count => _count;

    /// <summary>Entries in the current table, including the empty ones.</summary>
    /// <remarks>Here for the tests that assert this type sizes itself by occupancy rather than by id range.</remarks>
    public int Capacity => _keys.Length;

    /// <summary>Reads a netId's handle.</summary>
    /// <param name="netId">The entity, never 0.</param>
    /// <param name="value">The handle stored for it.</param>
    /// <returns><see langword="true"/> when the map holds it.</returns>
    public bool TryGet(uint netId, out int value)
    {
        // <b>Rejected here and not left to the caller.</b> 0 is the empty sentinel, so a lookup for it would match the
        // first empty bucket it probed and report a handle of 0 as though it were real. The flat array this replaces
        // happened to be safe -- slot 0 simply held 0 -- and TryLocate is public, so an arbitrary netId reaches this.
        if (netId == 0)
        {
            value = 0;
            return false;
        }

        var i = Bucket(netId);
        while (true)
        {
            var key = _keys[i];
            if (key == netId)
            {
                value = _values[i];
                return true;
            }

            if (key == 0)
            {
                value = 0;
                return false;
            }

            i = (i + 1) & _mask;
        }
    }

    /// <summary>Maps a netId to a handle, replacing any handle it already had.</summary>
    /// <param name="netId">The entity, never 0.</param>
    /// <param name="value">The handle, never 0.</param>
    public void Set(uint netId, int value)
    {
        // Grown BEFORE the insert, not after: a table at full load has no empty bucket to terminate a probe on, so the
        // loop below would never exit. The ratio keeps occupancy at or under three quarters, which is where linear
        // probing's average probe length is still short.
        if ((_count + 1) * 4 >= _keys.Length * 3)
        {
            Grow();
        }

        var i = Bucket(netId);
        while (true)
        {
            var key = _keys[i];
            if (key == netId)
            {
                _values[i] = value;
                return;
            }

            if (key == 0)
            {
                _keys[i] = netId;
                _values[i] = value;
                _count++;
                return;
            }

            i = (i + 1) & _mask;
        }
    }

    /// <summary>Removes a netId, if it is there.</summary>
    /// <param name="netId">The entity.</param>
    public void Remove(uint netId)
    {
        if (netId == 0)
        {
            return;
        }

        var i = Bucket(netId);
        while (true)
        {
            var key = _keys[i];
            if (key == netId)
            {
                break;
            }

            if (key == 0)
            {
                return;
            }

            i = (i + 1) & _mask;
        }

        // Backward-shift deletion. Every entry after the hole is pulled back into it when doing so does not move it
        // before its own ideal bucket, which is what keeps every remaining probe sequence unbroken.
        var hole = i;
        var j = (i + 1) & _mask;
        while (_keys[j] != 0)
        {
            var ideal = Bucket(_keys[j]);
            if (((hole - ideal) & _mask) < ((j - ideal) & _mask))
            {
                _keys[hole] = _keys[j];
                _values[hole] = _values[j];
                hole = j;
            }

            j = (j + 1) & _mask;
        }

        _keys[hole] = 0;
        _values[hole] = 0;
        _count--;
    }

    /// <summary>Forgets every mapping, keeping the table it has.</summary>
    /// <remarks>
    /// The capacity is kept because the caller clears on a reconnect, and a session that comes back watches about what
    /// it watched before: releasing the table would buy a few kilobytes back and then pay for the same growths again.
    /// </remarks>
    public void Clear()
    {
        if (_count == 0)
        {
            return;
        }

        Array.Clear(_keys);
        Array.Clear(_values);
        _count = 0;
    }

    private void Allocate(int capacity)
    {
        _keys = new uint[capacity];
        _values = new int[capacity];
        _mask = capacity - 1;

        // Fold the product's HIGH bits down, which is the half the multiply actually mixes.
        _shift = 32 - System.Numerics.BitOperations.TrailingZeroCount((uint)capacity);
    }

    private int Bucket(uint netId) => (int)((netId * Fibonacci) >> _shift);

    private void Grow()
    {
        var oldKeys = _keys;
        var oldValues = _values;

        Allocate(oldKeys.Length * 2);
        _count = 0;

        for (var i = 0; i < oldKeys.Length; i++)
        {
            if (oldKeys[i] != 0)
            {
                Set(oldKeys[i], oldValues[i]);
            }
        }
    }
}
