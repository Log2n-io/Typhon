using System.Collections.Generic;

namespace SwgTatooine.Bots.Brain;

/// <summary>One facility a bot has seen: where it stands and which asset it is, which is what says where its user stands.</summary>
/// <param name="X">East.</param>
/// <param name="Z">North.</param>
/// <param name="Asset">The replicated asset index; -1 when it was not sent.</param>
/// <param name="Yaw">Its quarter turns.</param>
public readonly record struct Facility(float X, float Z, int Asset, int Yaw);

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
/// <b>Every one of a kind, not the first.</b> A city has one shuttleport but a bazaar and a mission terminal per few hectares, and "the closest mission
/// terminal" — the browser's pane, and the bot's errand — is a question over all of them. A city holds a handful of each, so a list per key is
/// searched linearly.
/// </para>
/// <para>
/// <b>Per bot, not shared.</b> One table per fleet would make every bot omniscient the moment any of them walked into a
/// city, which is a different load and a less honest one: the measurement this fleet exists for includes what a client
/// has to learn.
/// </para>
/// </remarks>
public sealed class FacilityMap
{
    /// <summary>Two sightings closer than this are the same structure, metres: they never move, and none stand this close.</summary>
    private const float SameM = 0.5f;

    private readonly Dictionary<long, List<Facility>> _seen = [];

    /// <summary>How many facilities this bot has learned.</summary>
    public int Count { get; private set; }

    /// <summary>Remembers a facility the first time it is seen.</summary>
    /// <param name="region">The region it belongs to, as a city plus one; 0 is ignored.</param>
    /// <param name="kind">Its <c>StructureKind</c>.</param>
    /// <param name="x">Where it is, east.</param>
    /// <param name="z">Where it is, north.</param>
    /// <param name="asset">The replicated asset index, or -1.</param>
    /// <param name="yaw">Its quarter turns.</param>
    /// <remarks>
    /// <b>The first sighting wins.</b> These structures never move, so a second sighting carries the same coordinates as the first — and a bot standing
    /// in a city is sent the same structures every tick, so overwriting would be a write per structure per bot per tick for data that never changes.
    /// </remarks>
    public void Observe(int region, int kind, float x, float z, int asset = -1, int yaw = 0)
    {
        if (region <= 0)
        {
            return;
        }

        var key = Key(region, kind);
        if (!_seen.TryGetValue(key, out var list))
        {
            list = [];
            _seen[key] = list;
        }

        foreach (var f in list)
        {
            if (Routing.Distance(f.X, f.Z, x, z) < SameM)
            {
                return;
            }
        }

        list.Add(new Facility(x, z, asset, yaw));
        Count++;
    }

    /// <summary>Where a region's facility of a kind is, if this bot has seen one: the first it saw.</summary>
    /// <param name="region">The region, as a city plus one.</param>
    /// <param name="kind">Its <c>StructureKind</c>.</param>
    /// <param name="at">Where it is.</param>
    /// <returns>Whether it is known.</returns>
    public bool TryFind(int region, int kind, out (float X, float Z) at)
    {
        if (region > 0 && _seen.TryGetValue(Key(region, kind), out var list) && list.Count > 0)
        {
            at = (list[0].X, list[0].Z);
            return true;
        }

        at = default;
        return false;
    }

    /// <summary>The closest known facility of a kind, in any region — the bot's half of the browser's facility pane.</summary>
    /// <param name="kind">The <c>StructureKind</c>.</param>
    /// <param name="x">From, east.</param>
    /// <param name="z">From, north.</param>
    /// <param name="found">The facility.</param>
    /// <param name="region">The region it belongs to.</param>
    /// <returns>Whether any is known.</returns>
    public bool TryNearest(int kind, float x, float z, out Facility found, out int region)
    {
        found = default;
        region = 0;
        var best = double.MaxValue;
        foreach (var (key, list) in _seen)
        {
            if ((int)(key & 0xFF) != (kind & 0xFF))
            {
                continue;
            }

            foreach (var f in list)
            {
                var d = Routing.Distance(x, z, f.X, f.Z);
                if (d < best)
                {
                    best = d;
                    found = f;
                    region = (int)(key >> 8);
                }
            }
        }

        return region != 0;
    }

    /// <summary>The table's key: a region and a kind in one long.</summary>
    /// <remarks>
    /// The kind takes the low 8 bits, which is ample for a <c>StructureKind</c> enumeration of eleven and would collide
    /// silently past 256 — stated because the mask is the kind of thing that outlives the assumption behind it.
    /// </remarks>
    private static long Key(int region, int kind) => ((long)region << 8) | (uint)(kind & 0xFF);
}
