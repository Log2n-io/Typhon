using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SwgTatooine.Server;

/// <summary>
/// The horde's operator routes (horde.md § 7), keyed like the rest of the back office: start one with its knobs, read the running one's live metrics, end
/// it early. The dashboard's Events page is their client; <c>GET /swg/horde</c> is the public half, for the players.
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>Where a horde request goes: the simulation's <see cref="SimBridge.RequestHorde"/>, set by the server before it maps.</summary>
    public static Func<HordeRequest, Task<HordeOutcome>> Horde { get; set; }

    /// <summary>The running (or last) horde as JSON, or null; set by the server before it maps.</summary>
    public static Func<string> HordeReport { get; set; }

    private static void MapHorde(IEndpointRouteBuilder app, byte[] expected)
    {
        app.MapGet("/admin/horde", (HttpContext c) => Keyed(c, expected, () => Results.Text(HordeReport?.Invoke() ?? "null", "application/json")));
        app.MapPost("/admin/horde", HordeWriter(expected, end: false));
        app.MapPost("/admin/horde/end", HordeWriter(expected, end: true));
    }

    // Typed as a Delegate for the reason Writer is: a lambda from HttpContext to Task<IResult> binds as a RequestDelegate and drops its result.
    private static Delegate HordeWriter(byte[] expected, bool end)
        => (Func<HttpContext, Task<IResult>>)(context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return KeyMatches(context, expected) ? WriteHorde(context, end) : Task.FromResult(Results.StatusCode(StatusCodes.Status401Unauthorized));
        });

    /// <summary>
    /// <c>POST /admin/horde</c> <c>{"city","startsInS","durationS","waveSize","wavePeriodS","waves","respawn","level","levelSpread","templates",
    /// "targetDefenders","commitTimeoutS","cooldownS","garrisonPercent","raiderHealthPct","raiderHitPct"}</c> (all but the city optional, the defaults of
    /// horde.md § 9), or <c>POST /admin/horde/end</c>:
    /// <c>{"ok","event","message"}</c>, 409 when refused.
    /// </summary>
    private static async Task<IResult> WriteHorde(HttpContext context, bool end)
    {
        var request = new HordeRequest { End = end };
        if (!end)
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
                ReadHorde(body.RootElement, request);
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or OverflowException)
            {
                return Answer(StatusCodes.Status400BadRequest, false, "the body is not the JSON this call reads: " + e.Message);
            }

            if (string.IsNullOrWhiteSpace(request.City))
            {
                return Answer(StatusCodes.Status400BadRequest, false, "a horde needs a city");
            }

            if (request.WaveSize > SimBridge.HordeMaxWaveSize || request.TargetDefenders > SimBridge.HordeMaxTargetDefenders)
            {
                return Answer(StatusCodes.Status400BadRequest, false,
                    $"a wave is at most {SimBridge.HordeMaxWaveSize}, the attendance at most {SimBridge.HordeMaxTargetDefenders}");
            }
        }

        var desk = Horde;
        if (desk == null)
        {
            return Answer(StatusCodes.Status503ServiceUnavailable, false, "this server runs no simulation");
        }

        var done = desk(request);
        if (await Task.WhenAny(done, Task.Delay(WriteWait)).ConfigureAwait(false) != done && request.TryAbandon())
        {
            return Answer(StatusCodes.Status503ServiceUnavailable, false, Unanswered);
        }

        var outcome = await done.ConfigureAwait(false);
        Console.WriteLine($"  admin: horde {(end ? "end" : request.City)}: {(outcome.Ok ? "" : "REFUSED - ")}{outcome.Message}");
        return Results.Text(
            $"{{\"ok\":{(outcome.Ok ? "true" : "false")},\"event\":\"{outcome.Event}\",\"message\":\"{EconomyEndpoints.Escape(outcome.Message)}\"}}",
            "application/json", System.Text.Encoding.UTF8, outcome.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
    }

    private static void ReadHorde(JsonElement e, HordeRequest r)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("an object");
        }

        static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
        r.City = e.TryGetProperty("city", out var city) ? city.GetString() : null;
        r.StartsInS = Int(e, "startsInS", r.StartsInS);
        r.DurationS = Int(e, "durationS", r.DurationS);
        r.WaveSize = Int(e, "waveSize", r.WaveSize);
        r.WavePeriodS = Int(e, "wavePeriodS", r.WavePeriodS);
        r.Waves = Int(e, "waves", r.Waves);
        r.Respawn = e.TryGetProperty("respawn", out var respawn) ? respawn.GetBoolean() : r.Respawn;
        r.Level = Int(e, "level", r.Level);
        r.LevelSpread = Int(e, "levelSpread", r.LevelSpread);
        r.TemplateMask = Int(e, "templates", r.TemplateMask);
        r.TargetDefenders = Int(e, "targetDefenders", r.TargetDefenders);
        r.CommitTimeoutS = Int(e, "commitTimeoutS", r.CommitTimeoutS);
        r.CooldownS = Int(e, "cooldownS", r.CooldownS);
        r.GarrisonPercent = Int(e, "garrisonPercent", r.GarrisonPercent);
        r.RaiderHealthPct = Int(e, "raiderHealthPct", r.RaiderHealthPct);
        r.RaiderHitPct = Int(e, "raiderHitPct", r.RaiderHitPct);
        r.FortressM = Int(e, "fortressM", r.FortressM);
    }
}

/// <summary>
/// <c>GET /swg/horde</c> (horde.md § 4): the running horde's status, as the broadcast carries it — for a client that connects in the middle of one, since
/// the broadcast is sent on change only. <c>null</c> without one. Public: every session hears the same thing anyway.
/// </summary>
public static class HordeEndpoints
{
    /// <summary>The status JSON, or null; set by the server before it maps.</summary>
    public static Func<string> Status { get; set; }

    public static void MapHorde(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/swg/horde", (HttpContext c) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            return Results.Text(Status?.Invoke() ?? "null", "application/json");
        });
    }
}
