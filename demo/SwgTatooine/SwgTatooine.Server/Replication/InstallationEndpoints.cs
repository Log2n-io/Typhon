using System;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SwgTatooine.Server.Accounts;

namespace SwgTatooine.Server;

/// <summary>
/// <c>GET /swg/installations</c> (crafting-build.md § 10): a character's installations — where, what, its rate and hopper, what it holds now, its state and
/// how long its upkeep lasts — and its lots, used and allowed. Read off the tick (<c>EW-02</c>: a non-spatial, Versioned archetype), what each holds
/// computed at the last Workshop pass's world tick.
/// </summary>
public static class InstallationEndpoints
{
    /// <summary>The world tick now and the ticks in an hour, from the simulation: set when it serves (<c>TatooineSim.Serve</c>).</summary>
    public static Func<(long Now, long TicksPerHour)> Clock;

    public static WebApplication MapInstallations(this WebApplication app, DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/swg/installations", (HttpContext context) => Get(dbe, context));
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

        return CharacterStore.TryFind(dbe, account, out var character, out _)
            ? Results.Text(Json(dbe, unchecked((long)character.RawValue)), "application/json")
            : Results.Text("{\"lots\":0,\"lotsAllowed\":" + Installations.LotsPerCharacter + ",\"installations\":[]}", "application/json");
    }

    /// <summary>
    /// <c>{"lots","lotsAllowed","upkeepPerHour","installations":[{"id","kind","family","familyName","x","z","rate","hopper","held","state","paidSeconds"}]}</c>
    /// for one character.
    /// </summary>
    internal static string Json(DatabaseEngine dbe, long owner)
    {
        var (now, perHour) = Clock?.Invoke() ?? (0, 1);
        var inv = CultureInfo.InvariantCulture;
        var json = new StringBuilder(512);
        var lots = 0;
        json.Append("{\"installations\":[");
        var first = true;
        var index = dbe.GetIndexRef<InstallationCore, long>(c => c.Owner);
        using var tx = dbe.CreateQuickTransaction();
        using (var found = tx.EnumerateIndex<InstallationCore, long>(index, owner, owner))
        {
            while (found.MoveNext())
            {
                var c = found.CurrentComponent;
                lots += c.Kind == InstallationKind.Extractor ? Installations.ExtractorLots : 1;
                ItemCatalog.TryGet(c.Family, out var family);
                var paid = Math.Max(0, c.PaidUntil - now) * 3600 / Math.Max(1, perHour);
                json.Append(first ? "{" : ",{")
                    .Append("\"id\":\"").Append(found.CurrentEntityPK.ToString(inv)).Append('"')
                    .Append(",\"kind\":").Append(c.Kind)
                    .Append(",\"family\":").Append(c.Family)
                    .Append(",\"familyName\":\"").Append(EconomyEndpoints.Escape(family.Name ?? "")).Append('"')
                    .Append(",\"x\":").Append(c.X.ToString("0.#", inv))
                    .Append(",\"z\":").Append(c.Z.ToString("0.#", inv))
                    .Append(",\"rate\":").Append(c.RatePerHour)
                    .Append(",\"hopper\":").Append(c.Hopper)
                    .Append(",\"held\":").Append(Installations.Held(in c, now, perHour))
                    .Append(",\"state\":").Append(InstallationState.Of(in c, now, perHour))
                    .Append(",\"paidSeconds\":").Append(paid)
                    .Append('}');
                first = false;
            }
        }

        return json.Append("],\"lots\":").Append(lots)
            .Append(",\"lotsAllowed\":").Append(Installations.LotsPerCharacter)
            .Append(",\"upkeepPerHour\":").Append(Installations.ExtractorUpkeepPerHour)
            .Append('}').ToString();
    }
}
