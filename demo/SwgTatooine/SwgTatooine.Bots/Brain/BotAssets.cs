using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Navigation;

namespace SwgTatooine.Bots.Brain;

/// <summary>A building's door: the published path that walks a body through it, in the asset's frame (mirrored, unturned).</summary>
/// <param name="Approach">The points to walk in order; the first is on open ground, the last is past the door's plane, so walking it crosses.</param>
/// <param name="Target">The asset entered: the room's.</param>
public sealed record DoorPlan((float X, float Z)[] Approach, string Target);

/// <summary>
/// What one structure asset means to a bot: where its walls are, how to go through its door, where a user of it stands, and — for a room — how to cross
/// its floor.
/// </summary>
/// <remarks>
/// Every point is in the asset's frame <b>mirrored in X</b>, which is the world's convention (<c>ASSET_FORMAT.md</c> § 2: a descriptor is glTF,
/// right-handed, and the world is the renderer's). The mirror is applied once, at the read, as the server's reader and the browser's do — a door read
/// unmirrored stands at the mirror image of the drawn doorway.
/// </remarks>
public sealed class AssetPlan
{
    /// <summary>The asset id.</summary>
    public required string Id { get; init; }

    /// <summary>The blocking geometry the server collides with.</summary>
    public PlanBox[] Walls { get; init; } = [];

    /// <summary>Its door, or null for a structure nobody enters.</summary>
    public DoorPlan Door { get; init; }

    /// <summary>
    /// How a user reaches the place it uses this structure from, ending ON that place: a terminal's single stand point, or a shuttleport's walk from
    /// outside its gate to its ticket terminal (the child's published <c>approach</c>). Empty when it is not used.
    /// </summary>
    public (float X, float Z)[] UsePath { get; init; } = [];

    /// <summary>A room's floor graph (§ 5.9), or null.</summary>
    public RoomNav Nav { get; init; }
}

/// <summary>
/// The public content a bot reads exactly as a browser does: the server's <c>/typhon/demo.json</c> and the published asset descriptors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content, not knowledge.</b> A browser downloads these files to draw the world, and a bot downloads the same files to walk through it; nothing here
/// is something a client is not sent. Which building of a city is its shuttleport is still learned per bot (<see cref="FacilityMap"/>) — this only says
/// what a <c>tat_house_small</c> is.
/// </para>
/// <para>
/// <b>One copy per fleet process</b>, as an HTTP cache would hold one: the files are immutable (<c>catalogue-N.json</c>, <c>&lt;id&gt;-&lt;rev&gt;.json</c>)
/// and every bot would fetch the same bytes.
/// </para>
/// </remarks>
public sealed class BotAssets : IWallPlans
{
    private readonly AssetPlan[] _byIndex;
    private readonly Dictionary<string, AssetPlan> _byId;

    /// <summary>Content from parts, for tests and for <see cref="LoadAsync"/>.</summary>
    /// <param name="structures">The plans in the order the replicated <c>asset</c> field indexes them.</param>
    public BotAssets(AssetPlan[] structures)
    {
        ArgumentNullException.ThrowIfNull(structures);
        _byIndex = structures;
        _byId = [];
        foreach (var plan in structures)
        {
            _byId[plan.Id] = plan;
        }
    }

    /// <summary>How many structure assets there are.</summary>
    public int Count => _byIndex.Length;

    /// <summary>The plan of a replicated <c>asset</c> index, or null for one this content does not know.</summary>
    public AssetPlan At(int index) => (uint)index < (uint)_byIndex.Length ? _byIndex[index] : null;

    /// <inheritdoc/>
    public PlanBox[] WallsOf(int asset) => At(asset)?.Walls;

    /// <summary>The plan of an asset id, or null.</summary>
    public AssetPlan Of(string id) => id != null && _byId.TryGetValue(id, out var plan) ? plan : null;

    /// <summary>Reads the content from a server: <c>/typhon/demo.json</c>, its catalogue, and every structure descriptor it names.</summary>
    /// <param name="http">The client to fetch with.</param>
    /// <param name="server">The server's HTTP base.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The content, or null when the server publishes no asset set (then bots walk as they always did).</returns>
    public static async Task<BotAssets> LoadAsync(HttpClient http, Uri server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(server);

        using var demo = JsonDocument.Parse(await http.GetStringAsync(new Uri(server, "typhon/demo.json"), ct).ConfigureAwait(false));
        if (!demo.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var assetBase = new Uri(server, assets.GetProperty("base").GetString() ?? "/assets/");
        using var catalogue = JsonDocument.Parse(
            await http.GetStringAsync(new Uri(assetBase, assets.GetProperty("catalogue").GetString()), ct).ConfigureAwait(false));
        var entries = catalogue.RootElement.GetProperty("assets");

        var ids = new List<string>();
        foreach (var id in assets.GetProperty("structures").EnumerateArray())
        {
            ids.Add(id.GetString() ?? string.Empty);
        }

        var collision = assets.TryGetProperty("collision", out var c) ? c : default;
        var plans = new AssetPlan[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var walls = collision.ValueKind == JsonValueKind.Array && i < collision.GetArrayLength() ? ReadWalls(collision[i]) : [];
            var descriptor = await DescriptorAsync(http, assetBase, entries, ids[i], ct).ConfigureAwait(false);
            JsonDocument child = null;
            try
            {
                // A structure whose use is its child's (the shuttleport and its ticket terminal): the child's stand point ends the walk.
                var childId = FirstChildWithApproach(descriptor);
                if (childId != null)
                {
                    child = await DescriptorAsync(http, assetBase, entries, childId, ct).ConfigureAwait(false);
                }

                plans[i] = Plan(ids[i], walls, descriptor?.RootElement, child?.RootElement);
            }
            finally
            {
                descriptor?.Dispose();
                child?.Dispose();
            }
        }

        return new BotAssets(plans);
    }

    private static async Task<JsonDocument> DescriptorAsync(HttpClient http, Uri assetBase, JsonElement entries, string id, CancellationToken ct)
    {
        if (!entries.TryGetProperty(id, out var entry) || !entry.TryGetProperty("descriptor", out var file))
        {
            return null;
        }

        return JsonDocument.Parse(await http.GetStringAsync(new Uri(assetBase, file.GetString()), ct).ConfigureAwait(false));
    }

    private static string FirstChildWithApproach(JsonDocument descriptor)
    {
        if (descriptor == null || !descriptor.RootElement.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var child in children.EnumerateArray())
        {
            if (child.TryGetProperty("approach", out var approach) && approach.GetArrayLength() > 0)
            {
                return child.GetProperty("asset").GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// A plan from a descriptor (and, for a structure used through a child, that child's): the first portal, the first interaction or the first child
    /// with an approach, and the room graph.
    /// </summary>
    /// <param name="id">The asset id.</param>
    /// <param name="walls">Its published walls.</param>
    /// <param name="descriptor">Its descriptor, or null when the catalogue has none.</param>
    /// <param name="child">The descriptor of the child its use goes through, or null.</param>
    /// <returns>The plan.</returns>
    public static AssetPlan Plan(string id, PlanBox[] walls, JsonElement? descriptor, JsonElement? child)
    {
        if (descriptor is not { } d)
        {
            return new AssetPlan { Id = id, Walls = walls };
        }

        DoorPlan door = null;
        if (d.TryGetProperty("portals", out var portals) && portals.ValueKind == JsonValueKind.Array && portals.GetArrayLength() > 0)
        {
            // The first portal only, as the server reads it (§ 5.3: one portal per asset today).
            var portal = portals[0];
            var approach = Points(portal, "approach");
            if (approach.Length > 0)
            {
                door = new DoorPlan(approach, portal.GetProperty("target").GetString());
            }
        }

        var use = Array.Empty<(float, float)>();
        if (d.TryGetProperty("interactions", out var interactions) && interactions.ValueKind == JsonValueKind.Array && interactions.GetArrayLength() > 0)
        {
            use = [Point(interactions[0].GetProperty("pos"))];
        }
        else if (child is { } ch && d.TryGetProperty("children", out var children))
        {
            foreach (var c in children.EnumerateArray())
            {
                var path = Points(c, "approach");
                if (path.Length == 0)
                {
                    continue;
                }

                // The approach ends on the child's stand point (§ 5.7 rule 3), in this asset's frame already; the stand point itself is appended only
                // when the child publishes one the path does not already reach, which keeps the use place exact whichever way the exporter wrote it.
                var stand = StandOf(ch, c);
                use = stand is { } s && Distance(path[^1], s) > 0.05f ? [.. path, s] : path;
                break;
            }
        }

        RoomNav nav = null;
        if (d.TryGetProperty("nav", out var navElement) && navElement.ValueKind == JsonValueKind.Object)
        {
            nav = RoomNav.Read(navElement);
        }

        return new AssetPlan { Id = id, Walls = walls, Door = door, UsePath = use, Nav = nav };
    }

    /// <summary>
    /// A child's interaction stand point in the parent's frame: the child's pose (an unturned child, which every published one is) plus its point.
    /// </summary>
    private static (float X, float Z)? StandOf(JsonElement child, JsonElement placement)
    {
        if (!child.TryGetProperty("interactions", out var interactions) || interactions.GetArrayLength() == 0)
        {
            return null;
        }

        var at = Point(placement.GetProperty("pos"));
        var local = Point(interactions[0].GetProperty("pos"));
        return (at.X + local.X, at.Z + local.Z);
    }

    /// <summary>
    /// The walls of one asset from <c>demo.json</c>'s ten-number boxes: [minX, minZ, maxX, maxZ, minY, maxY, planeY, slopeX, slopeZ, radius].
    /// </summary>
    /// <remarks>
    /// Already in the world's convention — the server publishes them mirrored. A short box is a wall of unknown height, as the browser reads it.
    /// </remarks>
    public static PlanBox[] ReadWalls(JsonElement boxes)
    {
        if (boxes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<PlanBox>();
        foreach (var box in boxes.EnumerateArray())
        {
            var n = box.GetArrayLength();
            if (n < 4)
            {
                continue;
            }

            var sloped = n > 8 && (box[7].GetSingle() != 0f || box[8].GetSingle() != 0f);
            list.Add(new PlanBox(
                box[0].GetSingle(),
                box[1].GetSingle(),
                box[2].GetSingle(),
                box[3].GetSingle(),
                n > 5 ? box[4].GetSingle() : 0f,
                n > 5 ? box[5].GetSingle() : float.PositiveInfinity,
                sloped,
                n > 9 ? box[9].GetSingle() : 0f));
        }

        return [.. list];
    }

    /// <summary>A descriptor point's plan position, mirrored: (x, z) in glTF is (-x, z) in the world's convention.</summary>
    internal static (float X, float Z) Point(JsonElement xyz) => (-xyz[0].GetSingle(), xyz[2].GetSingle());

    private static (float X, float Z)[] Points(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var points = new (float X, float Z)[list.GetArrayLength()];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = Point(list[i]);
        }

        return points;
    }

    private static float Distance((float X, float Z) a, (float X, float Z) b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    /// <summary>The inverse of <see cref="Place"/>: a world point in the frame of a structure standing at (x, z) turned by its quarter turns.</summary>
    public static (float X, float Z) Unplace(float x, float z, int quarters, (float X, float Z) world)
    {
        var (sin, cos) = (quarters & 3) switch
        {
            1 => (-1f, 0f),
            2 => (0f, -1f),
            3 => (1f, 0f),
            _ => (0f, 1f),
        };

        var dx = world.X - x;
        var dz = world.Z - z;
        return ((dx * cos) + (dz * sin), (dz * cos) - (dx * sin));
    }

    /// <summary>A point of an asset's plan, turned by the structure's quarter turns and moved to where it stands — the server's own placement.</summary>
    /// <param name="x">The structure, east.</param>
    /// <param name="z">The structure, north.</param>
    /// <param name="quarters">Its replicated <c>yaw</c>.</param>
    /// <param name="local">The point, in the asset's (mirrored) frame.</param>
    /// <returns>World metres.</returns>
    public static (float X, float Z) Place(float x, float z, int quarters, (float X, float Z) local)
    {
        var (sin, cos) = (quarters & 3) switch
        {
            1 => (-1f, 0f),
            2 => (0f, -1f),
            3 => (1f, 0f),
            _ => (0f, 1f),
        };

        return (x + ((local.X * cos) - (local.Z * sin)), z + ((local.X * sin) + (local.Z * cos)));
    }
}
