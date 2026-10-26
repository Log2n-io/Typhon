using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using Typhon.Engine;

namespace SwgTatooine.Server;

/// <summary>
/// <c>GET /swg/facilities</c>: every shuttleport, bazaar and mission terminal in the world — where it stands, which city it serves, which asset it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>A planet map, not a view.</b> A client already ships the cities' coordinates; this is the rest of the map a player reads before walking anywhere: where
/// a city's facilities stand. A bot sent to "the nearest mission terminal" went to the one it had happened to see — the port's, in the middle of the city it
/// landed in — when one stood at the street's end (Loïc, 2026-10-26: "the nearest that EXISTS"). Nothing here is secret: every structure listed is
/// replicated to whoever stands near it.
/// </para>
/// <para>
/// <b>Built once, before the first tick, from the database</b>: facilities never move, and a reopened world reads the same rows a fresh one wrote. Read
/// then, off any tick, because a spatial archetype is not one a thread outside the DAG may read while the runtime runs (EW-02).
/// </para>
/// </remarks>
public static class FacilityEndpoints
{
    /// <summary>The document served, built by <see cref="Build"/> before the runtime starts; null serves 404: "not published".</summary>
    public static string Json { get; set; }

    /// <summary>Maps the endpoint.</summary>
    public static IEndpointRouteBuilder MapFacilities(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/swg/facilities", Get);
        return app;
    }

    /// <summary>The HTTP handler, for the checks too.</summary>
    internal static IResult Get() => Json is { } json ? Results.Text(json, "application/json") : Results.NotFound();

    /// <summary>
    /// The facilities as JSON: <c>{"facilities":[{"kind","region","realm","x","z","yaw","asset"}…]}</c>, <c>region</c> a city plus one as the wire carries it,
    /// <c>asset</c> absent for a structure no asset stands in for.
    /// </summary>
    /// <param name="dbe">The world, with no runtime ticking it.</param>
    public static string Build(DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var json = new StringBuilder("{\"facilities\":[");
        var first = true;
        using var tx = dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<WorldObject>().GetClusterEnumerator())
        {
            var places = cluster.GetReadOnlySpan(WorldObject.Bounds);
            var structs = cluster.GetReadOnlySpan(WorldObject.Struct);
            var regions = cluster.GetReadOnlySpan(WorldObject.Region);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                ref readonly var st = ref structs[slot];
                if (!IsFacility(st.Kind))
                {
                    continue;
                }

                json.Append(first ? "{" : ",{");
                first = false;
                json.Append("\"kind\":").Append(st.Kind)
                    .Append(",\"region\":").Append(regions[slot].Value)
                    .Append(",\"realm\":").Append(cluster.Realm.Value)
                    .Append(",\"x\":").Append(places[slot].X.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(",\"z\":").Append(places[slot].Z.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(",\"yaw\":").Append(st.Yaw);
                if (st.Asset != StructureAssets.NoAsset)
                {
                    json.Append(",\"asset\":").Append(st.Asset);
                }

                json.Append('}');
            }
        }

        return json.Append("]}").ToString();
    }

    /// <summary>The kinds listed: the ones a player walks to use.</summary>
    public static bool IsFacility(int kind) => kind is StructureKind.Shuttleport or StructureKind.Bazaar or StructureKind.MissionTerminal;
}
