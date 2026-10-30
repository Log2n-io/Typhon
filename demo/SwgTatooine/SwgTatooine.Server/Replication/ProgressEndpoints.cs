using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SwgTatooine.Server.Accounts;
using System;
using System.Globalization;

namespace SwgTatooine.Server;

/// <summary>
/// <c>GET /swg/character</c> (character-level.md § 7): the bearer's character's level and XP, read from <see cref="CharacterProgress"/> — the truth the HUD's
/// bar is a 256th of. For operators, checks and a future character panel.
/// </summary>
public static class ProgressEndpoints
{
    /// <summary>Maps the endpoint.</summary>
    public static IEndpointRouteBuilder MapProgress(this IEndpointRouteBuilder app, DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dbe);
        app.MapGet("/swg/character", (HttpContext context) => GetCharacter(dbe, context));
        return app;
    }

    /// <summary>Bearer-checked exactly as <c>GET /swg/wallet</c>: 401 without a valid token, the progress of the account's character with one.</summary>
    internal static IResult GetCharacter(DatabaseEngine dbe, HttpContext context)
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
            ? Results.Text(ProgressJson(dbe, character), "application/json")
            : Results.StatusCode(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// A character's progress as JSON: <c>{"level","xp","xpLevelStart","xpNextLevel","killXp","missionXp","maxHealth"}</c>; <c>xpNextLevel</c> is the total at
    /// which the next level starts, the cap's own at CL 50.
    /// </summary>
    public static string ProgressJson(DatabaseEngine dbe, EntityId character)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        CharacterProgress p;
        using (var tx = dbe.CreateQuickTransaction())
        {
            p = tx.TryOpen(character, out var entity) ? entity.Read(CharacterArch.Progress) : CharacterProgress.Start;
        }

        var level = Math.Max(1, (int)p.Level);
        var next = Progression.XpAt(Math.Min(Progression.MaxLevel, level + 1));
        return string.Create(CultureInfo.InvariantCulture, $"{{\"level\":{level},\"xp\":{p.Xp},\"xpLevelStart\":{Progression.XpAt(level)},")
            + string.Create(
                CultureInfo.InvariantCulture,
                $"\"xpNextLevel\":{next},\"killXp\":{p.KillXp},\"missionXp\":{p.MissionXp},\"maxHealth\":{Progression.HealthFor(level)}}}");
    }
}
