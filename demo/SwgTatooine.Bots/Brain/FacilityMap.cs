using System.Collections.Generic;

namespace SwgTatooine.Bots.Brain;

/// <summary>The facilities a bot has seen, remembered by the region they belong to.</summary>
/// <remarks>
/// <para>
/// <b>Learned by looking, which is what a new player does.</b> A bot ships the cities' coordinates — a client ships the
/// planet map — but not the facilities inside them: which building of a city is its shuttleport is a draw the world
/// generator made, so the only way to know is to be sent it. A bot walks into a city, sees a structure whose
/// <c>kind</c> says shuttleport and whose <c>region</c> says which city, and remembers it. The second visit is informed.
/// </para>
/// <para>
/// <b>Keyed by the region the wire carries, not by a city index.</b> A region is a city plus one and 0 is "no region",
/// because the server's key is a sparse index's and a sparse index never files key 0. Keeping the wire's own value
/// means there is no sentinel here to disagree about, and the guard below rejects 0 rather than a negative city.
/// </para>
/// <para>
/// <b>The source is pluggable by design, and this is the version that needs nothing from the server.</b> A city's
/// facilities can also be delivered on entry, through the group set the server declares — which removes the first
/// visit's walk rather than the ability to manage without one. Both fill this table; the brain reads it and does not
/// care which filled it.
/// </para>
/// <para>
/// <b>Per bot, not shared.</b> One table per fleet would make every bot omniscient the moment any of them walked into a
/// city, which is a different load and a less honest one: the measurement this fleet exists for includes what a client
/// has to learn.
/// </para>
/// </remarks>
public sealed class FacilityMap
{
    private readonly Dictionary<long, (float X, float Z)> _seen = [];

    /// <summary>How many facilities this bot has learned.</summary>
    public int Count => _seen.Count;

    /// <summary>Remembers a facility the first time it is seen.</summary>
    /// <param name="region">The region it belongs to, as a city plus one; 0 is ignored.</param>
    /// <param name="kind">Its <c>StructureKind</c>.</param>
    /// <param name="x">Where it is, east.</param>
    /// <param name="z">Where it is, north.</param>
    /// <remarks>
    /// <b>The first sighting wins, and it does not matter which.</b> These structures never move, so a second sighting
    /// carries the same coordinates as the first — and a bot standing in a city is sent the same structures every tick,
    /// so overwriting meant a hashed write per structure per bot per tick for data that never changes. A city with two
    /// of a kind — several terminals, which the generator does make — is remembered as whichever was seen first, which
    /// is the correct answer to "where is a terminal in this city" and the wrong answer to "where are all of them". The
    /// brain only ever asks the first.
    /// </remarks>
    public void Observe(int region, int kind, float x, float z)
    {
        if (region <= 0)
        {
            return;
        }

        _seen.TryAdd(Key(region, kind), (x, z));
    }

    /// <summary>Where a region's facility of a kind is, if this bot has seen one.</summary>
    /// <param name="region">The region, as a city plus one.</param>
    /// <param name="kind">Its <c>StructureKind</c>.</param>
    /// <param name="at">Where it is.</param>
    /// <returns>Whether it is known.</returns>
    public bool TryFind(int region, int kind, out (float X, float Z) at)
    {
        if (region <= 0)
        {
            at = default;
            return false;
        }

        return _seen.TryGetValue(Key(region, kind), out at);
    }

    /// <summary>The table's key: a region and a kind in one long.</summary>
    /// <param name="region">The region.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// The kind takes the low 8 bits, which is ample for a <c>StructureKind</c> enumeration of eight and would collide
    /// silently past 256 — stated because the mask is the kind of thing that outlives the assumption behind it.
    /// </remarks>
    private static long Key(int region, int kind) => ((long)region << 8) | (uint)(kind & 0xFF);
}
