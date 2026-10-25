using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SwgTatooine.Server.Accounts;
using System;
using System.Globalization;
using System.Text;

namespace SwgTatooine.Server;

/// <summary>
/// <c>GET /swg/missions</c>: the missions the caller's character holds, read off the tick (missions.md § 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Over HTTP, not on the body, and the reason is the cluster.</b> Three journal slots on <c>Player</c> were 48 bytes and took a page from 24 bodies to 21
/// for a crowd that never holds one. The body carries a revision instead (<c>PlayerState.JournalRev</c>, owner <c>journal</c>), and a client re-reads this
/// when it moves.
/// </para>
/// <para>
/// <b>Off the tick, and legal for the <see cref="CharacterStore"/> reason</b>: <see cref="HeldMission"/> is Versioned on an archetype with no placement, which
/// <c>EW-02</c> permits a thread outside the DAG to read. The read is a snapshot, so a client asking in the same frame its revision moved reads the change:
/// the desk's transaction committed before the frame was published.
/// </para>
/// <para>
/// <b>The caller's own, by its bearer token</b> — the one <c>/auth/login</c> returned and the socket presents on <c>HELLO</c>. A mission's lair is where its
/// holder is going; nobody else's business.
/// </para>
/// </remarks>
public static class MissionJournal
{
    /// <summary>Maps the endpoint.</summary>
    public static IEndpointRouteBuilder MapMissionJournal(this IEndpointRouteBuilder app, DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dbe);
        app.MapGet("/swg/missions", (HttpContext context) => Get(dbe, context));
        return app;
    }

    private static IResult Get(DatabaseEngine dbe, HttpContext context)
    {
        // No caching anywhere between here and the client: the answer changes with the revision, and a cached one would show a cancelled mission.
        context.Response.Headers.CacheControl = "no-store";
        const string scheme = "Bearer ";
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith(scheme, StringComparison.Ordinal) ? header[scheme.Length..].Trim() : null;
        if (!AuthToken.TryValidate(token, out var account))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        return CharacterStore.TryFind(dbe, account, out var character, out _)
            ? Results.Text(Json(dbe, character), "application/json")
            : Results.Text("[]", "application/json");
    }

    /// <summary>The character's held missions as a JSON array.</summary>
    /// <remarks>
    /// <c>id</c> is a string: a raw entity id is a 64-bit integer, past what a JavaScript number holds exactly, and it comes back verbatim in
    /// <c>AbandonMission</c>. <c>lair</c> is null while there is none — the holder offline a moment ago, or a new one still to be drawn.
    /// </remarks>
    public static string Json(DatabaseEngine dbe, EntityId character)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var key = unchecked((long)character.RawValue);
        var indexRef = dbe.GetIndexRef<HeldMission, EntityId>(m => m.Holder);
        var json = new StringBuilder("[");
        using var tx = dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<HeldMission, long>(indexRef, key, key);
        var first = true;
        while (found.MoveNext())
        {
            var m = found.CurrentComponent;
            if (m.HolderKind != MissionHolderKind.Character)
            {
                continue;
            }

            json.Append(first ? "{" : ",{");
            first = false;
            json.Append("\"id\":\"").Append(((ulong)found.CurrentEntityPK).ToString(CultureInfo.InvariantCulture)).Append('"')
                .Append(",\"kind\":").Append(m.Kind)
                .Append(",\"name\":\"").Append(CreatureTemplates.Name(m.Kind)).Append('"')
                .Append(",\"difficulty\":").Append(m.Difficulty)
                .Append(",\"reward\":").Append(m.Reward)
                .Append(",\"octant\":").Append(m.Octant)
                .Append(",\"distanceM\":").Append(m.DistanceM)
                .Append(",\"takenTick\":").Append(m.TakenTick)
                .Append(",\"lair\":");
            if (m.Lair.IsNull)
            {
                json.Append("null");
            }
            else
            {
                json.Append("{\"x\":").Append(m.LairX.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append(",\"z\":").Append(m.LairZ.ToString("0.##", CultureInfo.InvariantCulture)).Append('}');
            }

            json.Append('}');
        }

        return json.Append(']').ToString();
    }
}
