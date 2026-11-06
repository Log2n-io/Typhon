using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SwgTatooine.Server.Accounts;

namespace SwgTatooine.Server;

/// <summary>
/// A character's mail over HTTP (mail.md § 4): its inbox, one mail with its text, and a mail sent. Read off the tick (<c>EW-02</c>: a non-spatial, Versioned
/// archetype); sent through the <c>Workshop</c>, which answers when the pass that wrote it commits.
/// </summary>
public static class MailEndpoints
{
    private static readonly TimeSpan SendWait = TimeSpan.FromSeconds(10);

    /// <summary>The largest body a send reads, bytes: a subject and a text at their caps, JSON-escaped, with room to spare.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>
    /// A sender's pace (mail.md § 3): one mail a second, three at once, per character — so one token cannot fill an inbox, or the Workshop's queue, in a
    /// burst. Held in memory; a restart forgets it, which costs a burst of three.
    /// </summary>
    public const double SendsPerSecond = 1;

    public const int SendBurst = 3;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (double Tokens, long At)> Buckets = new();

    /// <summary>Whether <paramref name="sender"/> may send now; takes a token when it may.</summary>
    internal static bool TryTakeSend(long sender, long nowTicks)
    {
        while (true)
        {
            var had = Buckets.TryGetValue(sender, out var b);
            var tokens = had ? Math.Min(SendBurst, b.Tokens + (nowTicks - b.At) / (double)TimeSpan.TicksPerSecond * SendsPerSecond) : SendBurst;
            if (tokens < 1)
            {
                return false;
            }

            var next = (tokens - 1, nowTicks);
            if (had ? Buckets.TryUpdate(sender, next, b) : Buckets.TryAdd(sender, next))
            {
                return true;
            }
        }
    }

    /// <summary>Where a mail goes: the simulation's <c>SimBridge.SendPlayerMail</c>, set by the server before it maps. Null refuses every send.</summary>
    public static Func<MailRequest, Task<MailOutcome>> Send { get; set; }

    /// <summary>The world tick mail is aged by, and the tick rate: what hides a mail past its life before the sweep removes it.</summary>
    public static Func<(long WorldTick, int Hz)> Clock { get; set; }

    public static WebApplication MapMail(this WebApplication app, DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/swg/mail", (HttpContext context) => WithCharacter(dbe, context, owner => Results.Text(InboxJson(dbe, owner), "application/json")));
        app.MapGet("/swg/mail/{id}", (HttpContext context, string id) => WithCharacter(dbe, context, owner =>
            long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mail) && MailJson(dbe, owner, mail) is { } json
                ? Results.Text(json, "application/json")
                : Results.NotFound()));
        // Typed as a Func returning the IResult: a bare lambda binds to RequestDelegate, which awaits the task and drops its result — every answer an
        // empty 200, a refusal and a 401 included (seen live, 2026-11-04).
        app.MapPost("/swg/mail", (Func<HttpContext, Task<IResult>>)(context => Post(dbe, context)));
        return app;
    }

    private static bool TryOwner(DatabaseEngine dbe, HttpContext context, out long owner)
    {
        owner = 0;
        context.Response.Headers.CacheControl = "no-store";
        const string scheme = "Bearer ";
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
        if (!AuthToken.TryRead(token, out var account, out var expired) || (expired && !TatooineReplication.IsPlaying(account)))
        {
            return false;
        }

        owner = CharacterStore.TryFind(dbe, account, out var character, out _) ? unchecked((long)character.RawValue) : 0;
        return true;
    }

    private static IResult WithCharacter(DatabaseEngine dbe, HttpContext context, Func<long, IResult> answer)
        => TryOwner(dbe, context, out var owner) ? answer(owner) : Results.StatusCode(StatusCodes.Status401Unauthorized);

    /// <summary><c>{"unread","max","mails":[{"id","kind","from","sentTick","read","subject"}]}</c>, newest first; mail past its life left out.</summary>
    internal static string InboxJson(DatabaseEngine dbe, long owner)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<(long Id, MailCore H)>();
        var names = new Dictionary<long, string>();
        if (owner != 0)
        {
            var oldest = Oldest();
            var index = dbe.GetIndexRef<MailCore, long>(m => m.Owner);
            using var tx = dbe.CreateQuickTransaction();
            using (var found = tx.EnumerateIndex<MailCore, long>(index, owner, owner))
            {
                while (found.MoveNext())
                {
                    if (found.CurrentComponent.SentTick >= oldest)
                    {
                        rows.Add((found.CurrentEntityPK, found.CurrentComponent));
                    }
                }
            }

            foreach (var (_, h) in rows)
            {
                if (h.From != 0 && !names.ContainsKey(h.From))
                {
                    names[h.From] = tx.TryOpen(EntityId.FromRawValue((ulong)h.From), out var e) ? e.Read(CharacterArch.Profile).Name.AsString : "someone";
                }
            }
        }

        rows.Sort((a, b) => b.H.SentTick.CompareTo(a.H.SentTick) != 0 ? b.H.SentTick.CompareTo(a.H.SentTick) : b.Id.CompareTo(a.Id));
        var unread = 0;
        foreach (var (_, h) in rows)
        {
            unread += (h.Flags & MailFlags.Read) == 0 ? 1 : 0;
        }

        var json = new StringBuilder(128 + rows.Count * 128);
        json.Append("{\"unread\":").Append(unread).Append(",\"max\":").Append(Mail.MaxPerCharacter).Append(",\"mails\":[");
        for (var i = 0; i < rows.Count; i++)
        {
            var (id, h) = rows[i];
            json.Append(i == 0 ? "{" : ",{")
                .Append("\"id\":\"").Append(id.ToString(inv)).Append('"')
                .Append(",\"kind\":\"").Append(MailKind.NameOf(h.Kind)).Append('"')
                .Append(",\"from\":\"").Append(h.From == 0 ? "" : EconomyEndpoints.Escape(names[h.From])).Append('"')
                .Append(",\"sentTick\":").Append(h.SentTick.ToString(inv))
                .Append(",\"ageS\":").Append(AgeSeconds(h.SentTick).ToString(inv))
                .Append(",\"ref\":\"").Append(h.Ref.ToString(inv)).Append('"')
                .Append(",\"read\":").Append((h.Flags & MailFlags.Read) != 0 ? "true" : "false")
                .Append(",\"subject\":\"").Append(EconomyEndpoints.Escape(h.Subject.AsString)).Append('"')
                .Append('}');
        }

        return json.Append("]}").ToString();
    }

    /// <summary>One of <paramref name="owner"/>'s mails with its text; null for none of the owner's.</summary>
    internal static string MailJson(DatabaseEngine dbe, long owner, long mail)
    {
        if (owner == 0 || mail == 0)
        {
            return null;
        }

        using var tx = dbe.CreateQuickTransaction();
        var id = EntityId.FromRawValue((ulong)mail);
        if (id.ArchetypeId != dbe.ArchetypeIdOf<MailArch>() || !tx.TryOpen(id, out var e))
        {
            return null;
        }

        var h = e.Read(MailArch.Core);
        if (h.Owner != owner || h.SentTick < Oldest())
        {
            return null;
        }

        var from = h.From != 0 && tx.TryOpen(EntityId.FromRawValue((ulong)h.From), out var f) ? f.Read(CharacterArch.Profile).Name.AsString : "";
        return new StringBuilder(256)
            .Append("{\"id\":\"").Append(mail.ToString(CultureInfo.InvariantCulture)).Append('"')
            .Append(",\"kind\":\"").Append(MailKind.NameOf(h.Kind)).Append('"')
            .Append(",\"from\":\"").Append(EconomyEndpoints.Escape(from)).Append('"')
            .Append(",\"sentTick\":").Append(h.SentTick.ToString(CultureInfo.InvariantCulture))
            .Append(",\"ageS\":").Append(AgeSeconds(h.SentTick).ToString(CultureInfo.InvariantCulture))
            .Append(",\"ref\":\"").Append(h.Ref.ToString(CultureInfo.InvariantCulture)).Append('"')
            .Append(",\"read\":").Append((h.Flags & MailFlags.Read) != 0 ? "true" : "false")
            .Append(",\"subject\":\"").Append(EconomyEndpoints.Escape(h.Subject.AsString)).Append('"')
            .Append(",\"text\":\"").Append(EconomyEndpoints.Escape(TextOf(tx, h))).Append("\"}")
            .ToString();
    }

    /// <summary>
    /// A mail's text: the UTF-8 bytes of its collection, in order; empty for none. Read through the read-only enumerator: the accessor is the write path — it
    /// opens a chunk accessor on the transaction's change set and commits it when disposed — and a read has nothing to commit.
    /// </summary>
    private static string TextOf(Transaction tx, MailCore mail)
    {
        // Pooled rather than on the stack: AC-20's check keeps stack buffers out of the blueprint, where they would mean bytes being packed.
        var utf8 = ArrayPool<byte>.Shared.Rent(Mail.TextMaxBytes);
        try
        {
            var n = 0;
            foreach (ref readonly var b in tx.GetReadOnlyCollectionEnumerator(ref mail.Text))
            {
                // Never past what a text may hold (Mail.CleanText): a longer one is cut rather than overrun.
                if (n == Mail.TextMaxBytes)
                {
                    break;
                }

                utf8[n++] = b;
            }

            return n == 0 ? string.Empty : Encoding.UTF8.GetString(utf8, 0, n);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(utf8);
        }
    }

    /// <summary>How long ago, seconds of world time, a mail sent at <paramref name="sentTick"/> was: what the client says it by.</summary>
    private static long AgeSeconds(long sentTick)
    {
        var clock = Clock?.Invoke() ?? (0, 1);
        return Math.Max(0, (clock.WorldTick - sentTick) / Math.Max(1, clock.Hz));
    }

    private static long Oldest()
    {
        var clock = Clock?.Invoke() ?? (0, 1);
        return clock.WorldTick - Mail.LifeTicks(clock.Hz);
    }

    /// <summary><c>POST /swg/mail</c> <c>{"to","subject","text"}</c>: <c>{"ok","mail"}</c>, or <c>{"ok":false,"reason","message"}</c>.</summary>
    private static async Task<IResult> Post(DatabaseEngine dbe, HttpContext context)
    {
        if (!TryOwner(dbe, context, out var owner))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (owner == 0)
        {
            return Answer(StatusCodes.Status409Conflict, new MailOutcome(false, 0, TatooineReplication.NotInWorld, "you have no character yet"));
        }

        if (context.Request.ContentLength is > MaxBodyBytes)
        {
            return Answer(StatusCodes.Status413PayloadTooLarge, new MailOutcome(false, 0, TatooineReplication.MailBad, "the mail is too long"));
        }

        string to, subject, text;
        try
        {
            // Bounded whatever the header said: a chunked body is read no further than the cap.
            var buffer = new byte[MaxBodyBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await context.Request.Body.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false)) > 0)
            {
                read += n;
            }

            if (read > MaxBodyBytes)
            {
                return Answer(StatusCodes.Status413PayloadTooLarge, new MailOutcome(false, 0, TatooineReplication.MailBad, "the mail is too long"));
            }

            using var body = JsonDocument.Parse(buffer.AsMemory(0, read));
            var root = body.RootElement;
            to = root.GetProperty("to").GetString();
            subject = Mail.CleanSubject(root.GetProperty("subject").GetString());
            text = Mail.CleanText(root.TryGetProperty("text", out var t) ? t.GetString() : "");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Answer(
                StatusCodes.Status400BadRequest,
                new MailOutcome(false, 0, TatooineReplication.MailBad, "the body is not {\"to\",\"subject\",\"text\"}"));
        }

        if (string.IsNullOrWhiteSpace(to) || subject == null || text == null)
        {
            return Answer(StatusCodes.Status400BadRequest, new MailOutcome(false, 0, TatooineReplication.MailBad,
                $"a mail needs a name, a subject of 1 to {Mail.SubjectMaxBytes} bytes and a text of {Mail.TextMaxBytes} at most, without control characters"));
        }

        if (!TryTakeSend(owner, DateTime.UtcNow.Ticks))
        {
            return Answer(StatusCodes.Status429TooManyRequests, new MailOutcome(false, 0, TatooineReplication.MailBad, "too fast: one mail a second"));
        }

        var send = Send;
        if (send == null)
        {
            return Answer(
                StatusCodes.Status503ServiceUnavailable,
                new MailOutcome(false, 0, TatooineReplication.CraftFailed, "this server runs no simulation"));
        }

        var request = new MailRequest { From = owner, To = to.Trim(), Subject = subject, Text = text };
        var done = send(request);
        MailOutcome outcome;
        try
        {
            outcome = await done.WaitAsync(SendWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            outcome = request.TryAbandon()
                ? new MailOutcome(false, 0, TatooineReplication.CraftFailed, "no tick answered in time: the simulation may be stopped")
                : await done.ConfigureAwait(false);
        }

        return Answer(outcome.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict, outcome);
    }

    private static IResult Answer(int status, MailOutcome outcome)
    {
        var json = outcome.Ok
            ? $"{{\"ok\":true,\"mail\":\"{outcome.Mail.ToString(CultureInfo.InvariantCulture)}\"}}"
            : $"{{\"ok\":false,\"reason\":{outcome.Reason.ToString(CultureInfo.InvariantCulture)},\"message\":\"{EconomyEndpoints.Escape(outcome.Message)}\"}}";
        return Results.Text(json, "application/json", Encoding.UTF8, status);
    }
}
