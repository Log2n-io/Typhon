using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SwgTatooine.Server.Accounts;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading;

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
/// holder is going; nobody else's business. <b>An expired token is still good while its account is playing</b>: the socket checked it once, at
/// <c>HELLO</c>, and a session outlives the token's two hours; refusing it then would freeze the journal of a player still in the world.
/// </para>
/// <para>
/// <b>Completions are stated, not inferred.</b> The answer carries the character's last few completed missions beside the held ones, so a client announces
/// a completion only for a mission the server says was completed — never for one that left the journal some other way (a cancel, a lost character).
/// </para>
/// </remarks>
public static class MissionJournal
{
    /// <summary>How many completions per character the answer carries: more than a client can miss between two reads.</summary>
    private const int CompletionsKept = 4;

    /// <summary>The answer for an account with no character yet.</summary>
    internal const string Empty = "{\"missions\":[],\"completed\":[]}";

    /// <summary>One completed mission, as the journal states it.</summary>
    private readonly record struct Completion(ulong Mission, byte Kind, int Reward);

    // Character raw id to its last completions, oldest first. Written by the mission desk on the tick, read by HTTP threads: an array replaced whole, never
    // written in place, so a reader sees one or the other. In memory only — a restart forgets them, and a client reconnecting has no journal to compare with.
    private static readonly ConcurrentDictionary<ulong, Completion[]> Completed = new();

    private static IndexRef _holderIndex;
    private static DatabaseEngine _holderIndexOf;

    /// <summary>Records a completed mission for its character's next read. Called by the mission desk.</summary>
    internal static void NoteCompleted(EntityId character, EntityId mission, byte kind, int reward)
    {
        var entry = new Completion(mission.RawValue, kind, reward);
        var before = Completed.TryGetValue(character.RawValue, out var list) ? list : [];
        var keep = Math.Min(before.Length, CompletionsKept - 1);
        var next = new Completion[keep + 1];
        Array.Copy(before, before.Length - keep, next, 0, keep);
        next[keep] = entry;
        Completed[character.RawValue] = next;
    }

    /// <summary>Forgets every completion: a world opened afresh. Called by the mission desk when it loads.</summary>
    internal static void ResetCompletions() => Completed.Clear();

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

        // The scheme is case-insensitive (RFC 7235 § 2.1).
        var token = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
        if (!AuthToken.TryRead(token, out var account, out var expired) || (expired && !TatooineReplication.IsPlaying(account)))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        return CharacterStore.TryFind(dbe, account, out var character, out _)
            ? Results.Text(Json(dbe, character), "application/json")
            : Results.Text(Empty, "application/json");
    }

    /// <summary>The HTTP handler itself, for the checks: a request in, the result out.</summary>
    internal static IResult GetForChecks(DatabaseEngine dbe, HttpContext context) => Get(dbe, context);

    /// <summary>
    /// The character's journal as JSON: <c>{"missions":[…held…],"completed":[{"id","kind","name","reward"}…]}</c>.
    /// </summary>
    /// <remarks>
    /// <c>id</c> is a string: a raw entity id is a 64-bit integer, past what a JavaScript number holds exactly, and it comes back verbatim in
    /// <c>AbandonMission</c>. <c>lair</c> is null while there is none — the holder offline a moment ago, or a new one still to be drawn.
    /// </remarks>
    public static string Json(DatabaseEngine dbe, EntityId character)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var key = unchecked((long)character.RawValue);
        var indexRef = HolderIndex(dbe);
        var json = new StringBuilder("{\"missions\":[");
        using var tx = dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<HeldMission, long>(indexRef, key, key);
        var first = true;
        while (found.MoveNext())
        {
            var m = found.CurrentComponent;
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

        json.Append("],\"completed\":[");
        if (Completed.TryGetValue(character.RawValue, out var done))
        {
            for (var i = 0; i < done.Length; i++)
            {
                json.Append(i == 0 ? "{" : ",{")
                    .Append("\"id\":\"").Append(done[i].Mission.ToString(CultureInfo.InvariantCulture)).Append('"')
                    .Append(",\"kind\":").Append(done[i].Kind)
                    .Append(",\"name\":\"").Append(CreatureTemplates.Name(done[i].Kind)).Append('"')
                    .Append(",\"reward\":").Append(done[i].Reward)
                    .Append('}');
            }
        }

        return json.Append("]}").ToString();
    }

    /// <summary>The holder index, resolved once per engine: resolving it parses an expression tree, which a read a second need not repeat.</summary>
    private static IndexRef HolderIndex(DatabaseEngine dbe)
    {
        if (!ReferenceEquals(Volatile.Read(ref _holderIndexOf), dbe))
        {
            _holderIndex = dbe.GetIndexRef<HeldMission, EntityId>(m => m.Holder);
            Volatile.Write(ref _holderIndexOf, dbe);
        }

        return _holderIndex;
    }
}
