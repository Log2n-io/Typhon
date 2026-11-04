using System;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SwgTatooine.Server.Accounts;

namespace SwgTatooine.Server;

/// <summary>
/// <c>GET /swg/waypoints</c> (client.md C38): a character's own waypoints — name, kind, colour, where, the installation each pins, which is active — and
/// how many it may keep. Read off the tick (<c>EW-02</c>: a non-spatial, Versioned archetype); the client reads it at login and after each of its own
/// waypoint commands, which are all that change it.
/// </summary>
public static class WaypointEndpoints
{
    public static WebApplication MapWaypoints(this WebApplication app, DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/swg/waypoints", (HttpContext context) => Get(dbe, context));
        return app;
    }

    private static IResult Get(DatabaseEngine dbe, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        const string scheme = "Bearer ";
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
        if (!AuthToken.TryRead(token, out var account, out var expired) || (expired && !TatooineReplication.IsPlaying(account)))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        return Results.Text(
            CharacterStore.TryFind(dbe, account, out var character, out _) ? Json(dbe, unchecked((long)character.RawValue)) : Json(dbe, 0),
            "application/json");
    }

    /// <summary><c>{"max","waypoints":[{"id","name","kind","color","x","z","realm","installation","active"}]}</c> for one character; none for 0.</summary>
    internal static string Json(DatabaseEngine dbe, long owner)
    {
        var inv = CultureInfo.InvariantCulture;
        var json = new StringBuilder(256);
        json.Append("{\"max\":").Append(Waypoints.MaxPerCharacter).Append(",\"waypoints\":[");
        if (owner != 0)
        {
            var first = true;
            var index = dbe.GetIndexRef<WaypointCore, long>(c => c.Owner);
            using var tx = dbe.CreateQuickTransaction();
            using var found = tx.EnumerateIndex<WaypointCore, long>(index, owner, owner);
            while (found.MoveNext())
            {
                var c = found.CurrentComponent;
                json.Append(first ? "{" : ",{")
                    .Append("\"id\":\"").Append(found.CurrentEntityPK.ToString(inv)).Append('"')
                    .Append(",\"name\":\"").Append(EconomyEndpoints.Escape(c.Name.AsString)).Append('"')
                    .Append(",\"kind\":").Append(c.Kind)
                    .Append(",\"color\":").Append(c.Color)
                    .Append(",\"x\":").Append(c.X.ToString("0.#", inv))
                    .Append(",\"z\":").Append(c.Z.ToString("0.#", inv))
                    .Append(",\"realm\":").Append(c.Realm)
                    .Append(",\"installation\":\"").Append(c.Installation.ToString(inv)).Append('"')
                    .Append(",\"active\":").Append(c.Active != 0 ? "true" : "false")
                    .Append('}');
                first = false;
            }
        }

        return json.Append("]}").ToString();
    }
}
