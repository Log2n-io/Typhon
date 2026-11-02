using System;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SwgTatooine.Server.Accounts;

namespace SwgTatooine.Server;

/// <summary>
/// Crafting over HTTP (crafting-build.md C2): <c>GET /swg/schematics</c>, every schematic with its slots and its lines' formulas — the client owns no item
/// table, and computes a craft's ceilings from these (C2-1) — and <c>GET /swg/craft</c>, the caller's craft under way, re-read on its body's
/// <c>items</c> revision.
/// </summary>
/// <remarks>
/// <b>Off the tick, and legal for the wallet's reason</b> (<see cref="EconomyEndpoints"/>): a craft is Versioned on an archetype with no placement, which
/// <c>EW-02</c> lets a thread outside the DAG read.
/// </remarks>
public static class CraftEndpoints
{
    private static string _schematics;

    /// <summary>Maps the two endpoints.</summary>
    public static IEndpointRouteBuilder MapCrafting(this IEndpointRouteBuilder app, DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dbe);
        app.MapGet("/swg/schematics", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "max-age=60";
            return Results.Text(SchematicsJson(), "application/json");
        });
        app.MapGet("/swg/craft", (HttpContext context) => GetCraft(dbe, context));
        return app;
    }

    /// <summary>
    /// <c>{"schematics":[{"id","name","kind","output","item","points","addon","slots":[{"kind","template","item","count"}],"lines":[{"name","min","max",
    /// "lower","enhanced","terms":[{"slot","source","weight"}]}]}],"attrs":{…}}</c>: what a client needs to show a recipe and compute its ceilings. Fixed:
    /// built once.
    /// </summary>
    public static string SchematicsJson() => _schematics ??= BuildSchematics();

    private static string BuildSchematics()
    {
        var inv = CultureInfo.InvariantCulture;
        var j = new StringBuilder(16_384);
        j.Append("{\"schematics\":[");
        var first = true;
        foreach (var s in Schematics.Every)
        {
            ItemCatalog.TryGet(s.Output, out var output);
            j.Append(first ? "{" : ",{")
                .Append("\"id\":").Append(s.Id)
                .Append(",\"name\":\"").Append(EconomyEndpoints.Escape(s.Name)).Append('"')
                .Append(",\"kind\":\"").Append(s.Kind.ToString().ToLowerInvariant()).Append('"')
                .Append(",\"output\":").Append(s.Output)
                .Append(",\"item\":\"").Append(output.Asset).Append('"')
                .Append(",\"points\":").Append(s.Points)
                .Append(",\"slots\":[");
            first = false;
            for (var k = 0; k < s.Slots.Length; k++)
            {
                var slot = s.Slots[k];
                ItemCatalog.TryGet(slot.Template, out var t);
                j.Append(k == 0 ? "{" : ",{")
                    .Append("\"kind\":\"").Append(slot.Kind.ToString().ToLowerInvariant()).Append('"')
                    .Append(",\"template\":").Append(slot.Template)
                    .Append(",\"item\":\"").Append(slot.Template == 0 ? "segment" : t.Asset).Append('"')
                    .Append(",\"count\":").Append(slot.Count).Append('}');
            }

            j.Append("],\"lines\":[");
            for (var l = 0; l < s.Lines.Length; l++)
            {
                var line = s.Lines[l];
                j.Append(l == 0 ? "{" : ",{")
                    .Append("\"name\":\"").Append(line.Name).Append('"')
                    .Append(",\"min\":").Append(line.Min.ToString("R", inv))
                    .Append(",\"max\":").Append(line.Max.ToString("R", inv))
                    .Append(",\"lower\":").Append(line.Lower ? "true" : "false")
                    .Append(",\"enhanced\":").Append(line.Enhanced ? "true" : "false")
                    .Append(",\"terms\":[");
                for (var t = 0; t < line.Terms.Length; t++)
                {
                    var term = line.Terms[t];
                    j.Append(t == 0 ? "{" : ",{")
                        .Append("\"slot\":").Append(term.Slot)
                        .Append(",\"source\":\"").Append(term.Source.ToString().ToLowerInvariant()).Append('"')
                        .Append(",\"weight\":").Append(term.Weight.ToString("R", inv)).Append('}');
                }

                j.Append("]}");
            }

            j.Append("]}");
        }

        // The materials' attributes (§ 3.2), per template and Mk: TG, EN, DU.
        j.Append("],\"attrs\":{");
        for (ushort m = 1; m <= 11; m++)
        {
            j.Append(m == 1 ? "\"" : ",\"").Append(m).Append("\":[");
            for (byte mk = 1; mk <= 5; mk++)
            {
                j.Append(mk == 1 ? "[" : ",[")
                    .Append(Materials.Of(m, mk, Attr.TG).ToString("R", inv)).Append(',')
                    .Append(Materials.Of(m, mk, Attr.EN).ToString("R", inv)).Append(',')
                    .Append(Materials.Of(m, mk, Attr.DU).ToString("R", inv)).Append(']');
            }

            j.Append(']');
        }

        return j.Append("},\"critGain\":").Append(Schematics.CritGain.ToString("R", inv))
            .Append(",\"enhancePerMk\":").Append(Schematics.EnhancePerMk.ToString("R", inv)).Append('}').ToString();
    }

    private static IResult GetCraft(DatabaseEngine dbe, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        const string scheme = "Bearer ";
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
        if (!AuthToken.TryRead(token, out var account, out var expired) || (expired && !TatooineReplication.IsPlaying(account)))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        return Results.Text(CharacterStore.TryFind(dbe, account, out var character, out _) ? CraftJson(dbe, character) : "{\"craft\":null}", "application/json");
    }

    /// <summary>
    /// A character's craft under way: <c>{"craft":{"id","schematic","output","mk","pointsLeft","addonMk","lines":[{"q","f","value"}]}}</c>, the fractions in
    /// millionths; <c>{"craft":null}</c> when there is none.
    /// </summary>
    public static string CraftJson(DatabaseEngine dbe, EntityId character)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var key = unchecked((long)character.RawValue);
        using var tx = dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<CraftState, long>(dbe.GetIndexRef<CraftState, long>(c => c.Crafter), key, key);
        if (!found.MoveNext() || !Schematics.TryGet(found.CurrentComponent.Schematic, out var s))
        {
            return "{\"craft\":null}";
        }

        var c = found.CurrentComponent;
        var inv = CultureInfo.InvariantCulture;
        ReadOnlySpan<int> q = [c.Q0, c.Q1, c.Q2, c.Q3];
        ReadOnlySpan<int> f = [c.F0, c.F1, c.F2, c.F3];
        var j = new StringBuilder(512);
        j.Append("{\"craft\":{\"id\":\"").Append(((ulong)found.CurrentEntityPK).ToString(inv))
            .Append("\",\"schematic\":").Append(c.Schematic)
            .Append(",\"output\":").Append(c.Output)
            .Append(",\"mk\":").Append(c.Mk)
            .Append(",\"pointsLeft\":").Append(c.PointsLeft)
            .Append(",\"addonMk\":").Append(c.AddonMk)
            .Append(",\"lines\":[");
        for (var i = 0; i < s.Lines.Length; i++)
        {
            j.Append(i == 0 ? "{" : ",{")
                .Append("\"q\":").Append(q[i])
                .Append(",\"f\":").Append(f[i])
                .Append(",\"value\":").Append(Crafting.ValueOf(s, i, q[i], f[i], c.AddonMk).ToString("0.##", inv)).Append('}');
        }

        return j.Append("]}}").ToString();
    }
}
