using System;
using System.Numerics;
using System.Threading;

namespace SwgTatooine;

/// <summary>What one realm held when the census last walked it.</summary>
/// <param name="Realm">The realm id.</param>
/// <param name="Players">Players standing in it.</param>
/// <param name="Npcs">City NPCs standing in it.</param>
/// <param name="Creatures">Creatures in it.</param>
/// <param name="Structures">Buildings, terminals, houses, factories, harvesters and lairs: everything that never moves.</param>
public readonly record struct RealmCensusRow(ushort Realm, int Players, int Npcs, int Creatures, int Structures)
{
    /// <summary>Everything the census counts, in one number.</summary>
    public int Total => Players + Npcs + Creatures + Structures;
}

/// <summary>
/// The per-realm population as of one tick, published as a whole and read without a lock.
/// </summary>
/// <remarks>
/// Immutable and replaced wholesale, because its reader is an HTTP thread and the tick must never wait on it. A reader
/// holding one of these is holding a consistent picture of one instant, not a set of fields drifting apart while it
/// formats them.
/// </remarks>
public sealed class RealmCensusSnapshot
{
    /// <summary>The empty census: what a reader gets before the first walk, and what a run without one keeps.</summary>
    public static readonly RealmCensusSnapshot Empty = new(-1, []);

    private readonly RealmCensusRow[] _rows;

    internal RealmCensusSnapshot(long tick, RealmCensusRow[] rows)
    {
        Tick = tick;
        _rows = rows;
    }

    /// <summary>The tick this was taken at, or -1 for <see cref="Empty"/>.</summary>
    public long Tick { get; }

    /// <summary>How many realms held anything.</summary>
    public int Count => _rows.Length;

    /// <summary>
    /// One row per realm that held anything, ascending by realm id. A realm holding nothing is absent, not zero.
    /// </summary>
    /// <remarks>
    /// A span rather than the array, because this type promises a consistent picture of one instant to readers on other
    /// threads and an array handed out is a picture any of them can edit.
    /// </remarks>
    public ReadOnlySpan<RealmCensusRow> Rows => _rows;

    /// <summary>The row for <paramref name="realm"/>, or an all-zero row when the census found nothing there.</summary>
    public RealmCensusRow Of(ushort realm)
    {
        // Ascending by construction, and the caller is formatting a document realm by realm — but a linear scan over at
        // most a few thousand rows once a second is not worth a binary search's off-by-one surface.
        foreach (var row in _rows)
        {
            if (row.Realm == realm)
            {
                return row;
            }

            if (row.Realm > realm)
            {
                break;
            }
        }

        return new RealmCensusRow(realm, 0, 0, 0, 0);
    }
}

/// <summary>
/// How many of each thing is standing in each realm, counted off the clusters themselves once a second.
/// </summary>
/// <remarks>
/// <para>
/// <b>It walks the archetype's clusters through the transaction, and that is the whole reason it exists in this shape.</b>
/// A realm the policy put to sleep is dispatched to no system — that is what dormancy IS — so a census built out of
/// <c>QuerySystem</c> passes would report every sleeping realm's population as whatever it held when it last ran. The
/// sleeping realms are the ones the panel exists to describe, so that is not a rounding error in the design, it is the
/// design missing its subject. A transaction's cluster enumerator is not realm-filtered and sees them all.
/// </para>
/// <para>
/// <b>Nothing here is a mirror.</b> The alternative — counters incremented on the crossing path, which already knows
/// every committed crossing and would cost nothing — is application-maintained state shadowing the engine's, and that
/// shape is what produced both of the server defects the spectate work spent a session on. A count taken from the
/// clusters cannot drift from them.
/// </para>
/// <para>
/// <b>It runs only in a serving process.</b> The measurement runs take this demo's CPU numbers, and a once-a-second
/// serial walk of every cluster of five archetypes is exactly the kind of term that makes two A/B arms incomparable
/// while looking like nothing. The system is added to the DAG only when replication is, so the measured path is the
/// same code it was.
/// </para>
/// </remarks>
public sealed class RealmCensus
{
    private readonly int _maxRealms;

    /// <summary>Tick-thread only: the sums for one walk, reused so a walk allocates only the rows it publishes.</summary>
    private readonly int[] _players;
    private readonly int[] _npcs;
    private readonly int[] _creatures;
    private readonly int[] _structures;

    private RealmCensusSnapshot _current = RealmCensusSnapshot.Empty;

    /// <summary>Counts over <paramref name="maxRealms"/> realm slots, which is what the engine was configured with.</summary>
    public RealmCensus(int maxRealms)
    {
        _maxRealms = Math.Max(1, maxRealms);
        _players = new int[_maxRealms];
        _npcs = new int[_maxRealms];
        _creatures = new int[_maxRealms];
        _structures = new int[_maxRealms];
    }

    /// <summary>The last completed census. Never null; <see cref="RealmCensusSnapshot.Empty"/> until the first walk.</summary>
    public RealmCensusSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Walks every spatial archetype once and publishes the result.
    /// </summary>
    /// <remarks>
    /// Called from a system body on the tick thread, so it reads through the tick's own transaction rather than opening
    /// one of its own. Counting is <c>PopCount</c> of a cluster's occupancy against the cluster's realm: one pass, no
    /// per-entity work, and no component is opened.
    /// </remarks>
    public void Take(Transaction tx, long tick)
    {
        ArgumentNullException.ThrowIfNull(tx);
        Array.Clear(_players);
        Array.Clear(_npcs);
        Array.Clear(_creatures);
        Array.Clear(_structures);

        Count<Player>(tx, _players);
        Count<CityNpc>(tx, _npcs);
        Count<Creature>(tx, _creatures);

        // The two that never move share one column: a viewer reading this panel is asking who is in the room, and
        // splitting the scenery into buildings and lairs would add a column that answers nothing dormancy turns on.
        Count<WorldObject>(tx, _structures);
        Count<CreatureLair>(tx, _structures);

        var found = 0;
        for (var realm = 0; realm < _maxRealms; realm++)
        {
            if (_players[realm] + _npcs[realm] + _creatures[realm] + _structures[realm] > 0)
            {
                found++;
            }
        }

        var rows = new RealmCensusRow[found];
        var next = 0;
        for (var realm = 0; realm < _maxRealms && next < found; realm++)
        {
            var total = _players[realm] + _npcs[realm] + _creatures[realm] + _structures[realm];
            if (total > 0)
            {
                rows[next++] = new RealmCensusRow((ushort)realm, _players[realm], _npcs[realm], _creatures[realm], _structures[realm]);
            }
        }

        // Published as a whole, after every count is in it. A reader never sees a half-filled walk.
        Volatile.Write(ref _current, new RealmCensusSnapshot(tick, rows));
    }

    /// <summary>Sums one archetype's occupancy into <paramref name="into"/>, indexed by realm.</summary>
    private static void Count<T>(Transaction tx, int[] into)
        where T : class
    {
        foreach (var cluster in tx.For<T>().GetClusterEnumerator())
        {
            var realm = cluster.Realm.Value;

            // A realm id outside the configured slots cannot happen — the engine allocates them from that range — but
            // this walk runs every second for the life of the process and an out-of-range write here would be a
            // corruption rather than a wrong number.
            if (realm < into.Length)
            {
                into[realm] += BitOperations.PopCount(cluster.OccupancyBits);
            }
        }
    }
}
