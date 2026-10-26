using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SwgTatooine.Bots.Brain;

/// <summary>One facility a bot has seen: where it stands and which asset it is, which is what says where its user stands.</summary>
/// <param name="X">East.</param>
/// <param name="Z">North.</param>
/// <param name="AssetSlot">The replicated asset index plus one; 0 when it was not sent — 0 is "no data", never -1.</param>
/// <param name="Yaw">Its quarter turns.</param>
public readonly record struct Facility(float X, float Z, int AssetSlot, int Yaw)
{
    /// <summary>A facility whose asset index is known.</summary>
    public static Facility Of(float x, float z, int asset, int yaw) => new(x, z, asset + 1, yaw);

    /// <summary>The replicated asset index, or -1 — an index the content refuses — when it was not sent. Read it through <see cref="BotAssets.At"/>.</summary>
    public int Asset => AssetSlot - 1;
}

/// <summary>One facility as <c>GET /swg/facilities</c> lists it: the region it serves (a city plus one), its kind, and where it stands.</summary>
public readonly record struct ListedFacility(int Region, int Kind, Facility At);

/// <summary>The facilities a bot knows, remembered by the region they belong to.</summary>
/// <remarks>
/// <para>
/// <b>Every one that exists, from the server's list, when the server publishes one</b> (<c>GET /swg/facilities</c>, <see cref="Seed"/>). The "nearest
/// mission terminal" is the nearest that EXISTS (Loïc, 2026-10-26): learned by looking, a bot that landed at a city's port knew the port's terminal and
/// nothing else, and walked into the city's middle to it past the one at its street's end. A player reads the same list off the planet map.
/// </para>
/// <para>
/// <b>Learned by looking otherwise</b> (<see cref="Observe"/>), which is what this map did before: a bot walks into a city, sees a structure whose
/// <c>kind</c> says shuttleport and whose <c>region</c> says which city, and remembers it. A server without the list is one whose bots still find their way.
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
/// <b>Per bot</b>, each seeded with its own copy of the list: a few hundred entries, and a bot that never sees the list still learns its own.
/// </para>
/// </remarks>
public sealed class FacilityMap
{
    /// <summary>Two sightings closer than this are the same structure, metres: they never move, and none stand this close.</summary>
    private const float SameM = 0.5f;

    private readonly Dictionary<long, List<Facility>> _seen = [];

    /// <summary>How many facilities this bot knows.</summary>
    public int Count { get; private set; }

    /// <summary>How many regions this bot knows a facility of a kind in: a shuttleport per city, so 2 is the fewest that lets it fly anywhere.</summary>
    public int Regions(int kind)
    {
        var n = 0;
        foreach (var (key, list) in _seen)
        {
            n += (int)(key & 0xFF) == (kind & 0xFF) && list.Count > 0 ? 1 : 0;
        }

        return n;
    }

    /// <summary>Whether it was given the server's whole list: then a sighting adds nothing, and is not filed.</summary>
    public bool Seeded { get; private set; }

    /// <summary>Knows every facility listed — the server's list, which is every one that exists — and stops learning by looking.</summary>
    /// <param name="listed">The list; an empty one leaves the map learning by looking.</param>
    public void Seed(IReadOnlyList<ListedFacility> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        foreach (var f in listed)
        {
            Add(f.Region, f.Kind, f.At);
        }

        Seeded = listed.Count > 0;
    }

    /// <summary>
    /// Reads <c>GET /swg/facilities</c>: the facilities of planet 0, where bots live — a city's facilities are kept by the region the wire carries, and a
    /// region does not say which planet. Null when the server publishes no list (404): its bots learn by looking.
    /// </summary>
    public static async Task<List<ListedFacility>> LoadAsync(HttpClient http, Uri server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(server);
        using var response = await http.GetAsync(new Uri(server, "swg/facilities"), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>The list in a <c>/swg/facilities</c> answer, planet 0's only; see <see cref="LoadAsync"/>.</summary>
    public static List<ListedFacility> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var listed = new List<ListedFacility>();
        foreach (var f in doc.RootElement.GetProperty("facilities").EnumerateArray())
        {
            if (f.GetProperty("realm").GetInt32() != 0)
            {
                continue;
            }

            // The asset as the wire's slot, plus one: absent is "not sent", 0, never -1.
            var slot = f.TryGetProperty("asset", out var asset) ? asset.GetInt32() + 1 : 0;
            listed.Add(new ListedFacility(
                f.GetProperty("region").GetInt32(),
                f.GetProperty("kind").GetInt32(),
                new Facility(f.GetProperty("x").GetSingle(), f.GetProperty("z").GetSingle(), slot, f.GetProperty("yaw").GetInt32())));
        }

        return listed;
    }

    /// <summary>Remembers a facility the first time it is seen.</summary>
    /// <param name="region">The region it belongs to, as a city plus one; 0 is ignored.</param>
    /// <param name="kind">Its <c>StructureKind</c>.</param>
    /// <param name="x">Where it is, east.</param>
    /// <param name="z">Where it is, north.</param>
    /// <param name="assetSlot">The replicated asset index plus one, or 0 when the asset was not sent.</param>
    /// <param name="yaw">Its quarter turns.</param>
    /// <remarks>
    /// <b>The first sighting wins.</b> These structures never move, so a second sighting carries the same coordinates as the first — and a bot standing
    /// in a city is sent the same structures every tick, so overwriting would be a write per structure per bot per tick for data that never changes.
    /// </remarks>
    public void Observe(int region, int kind, float x, float z, int assetSlot = 0, int yaw = 0)
    {
        if (!Seeded)
        {
            Add(region, kind, new Facility(x, z, assetSlot, yaw));
        }
    }

    private void Add(int region, int kind, Facility at)
    {
        if (region <= 0)
        {
            return;
        }

        var (x, z) = (at.X, at.Z);
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

        list.Add(at);
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

    /// <summary>The closest known facility of a kind, in any region — the bot's half of the browser's facility pane; with the server's list, the closest
    /// that exists.</summary>
    /// <param name="kind">The <c>StructureKind</c>.</param>
    /// <param name="x">From, east.</param>
    /// <param name="z">From, north.</param>
    /// <param name="found">The facility.</param>
    /// <param name="region">The region it belongs to.</param>
    /// <param name="except">One facility not to answer, by where it stands: the terminal that just had nothing.</param>
    /// <returns>Whether any is known.</returns>
    public bool TryNearest(int kind, float x, float z, out Facility found, out int region, (float X, float Z)? except = null)
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
                if (except is { } skip && Routing.Distance(skip.X, skip.Z, f.X, f.Z) < SameM)
                {
                    continue;
                }

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
