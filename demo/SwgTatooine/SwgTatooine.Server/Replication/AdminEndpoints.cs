using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SwgTatooine.Server;

/// <summary>
/// The back office's HTTP surface (accounts.md § 2): every account and its character, one character's progress, wallet and inventory, the item catalogue
/// with its defaults, and the writes an operator may make — credits, level and XP, items given, taken or rewritten. The dashboard's accounts page is its
/// client.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mapped only when <c>SWG_ADMIN_KEY</c> is set</b>, at least <see cref="MinKeyLength"/> characters, and every call must carry it in
/// <see cref="KeyHeader"/>: this server may bind a public address, and these routes give credits and items away. Without the key there is no route at all —
/// not a refusal to probe. The dashboard holds the same key, and asks its operator for it once per browser session (Loïc, 2026-11-02).
/// </para>
/// <para>
/// <b>Reads off the tick, writes in it.</b> The reads are quick transactions on Versioned, non-spatial archetypes (<c>EW-02</c>), as the inventory endpoint's.
/// A write is an <see cref="AdminRequest"/> handed to the simulation (<see cref="Desk"/>) and applied by the <c>Workshop</c> on the next tick; the call
/// answers when it has committed, or with why it was refused.
/// </para>
/// </remarks>
public static partial class AdminEndpoints
{
    /// <summary>The environment variable holding the operators' key.</summary>
    public const string KeyVariable = "SWG_ADMIN_KEY";

    /// <summary>The request header that carries it.</summary>
    public const string KeyHeader = "X-Swg-Admin-Key";

    /// <summary>The shortest key accepted: a shorter one leaves the back office off.</summary>
    public const int MinKeyLength = 16;

    /// <summary>The most accounts one listing returns.</summary>
    public const int MaxListed = 2000;

    /// <summary>How long a write waits for its tick before it is reported as unanswered.</summary>
    private static readonly TimeSpan WriteWait = TimeSpan.FromSeconds(10);

    /// <summary>Where a write goes: the simulation's <see cref="SimBridge.Admin"/>, set by the server before it maps. Null refuses every write.</summary>
    public static Func<AdminRequest, Task<AdminOutcome>> Desk { get; set; }

    /// <summary>Maps the routes under <c>/admin</c> when <paramref name="key"/> is long enough; says on the console which it did.</summary>
    /// <returns>Whether the back office is on.</returns>
    public static bool MapAdmin(this IEndpointRouteBuilder app, DatabaseEngine dbe, string key)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dbe);
        if (string.IsNullOrEmpty(key))
        {
            Console.WriteLine($"  admin: off (set {KeyVariable} on this server and on the dashboard to open its accounts page)");
            return false;
        }

        if (key.Length < MinKeyLength)
        {
            Console.WriteLine($"  !! admin: {KeyVariable} is shorter than {MinKeyLength} characters, so the back office is OFF");
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(key);
        app.MapGet("/admin/accounts", (HttpContext c) => Keyed(c, expected, () => Accounts(dbe, c)));
        app.MapGet("/admin/character/{id}", (HttpContext c) => Keyed(c, expected, () => Character(dbe, c)));
        app.MapGet("/admin/catalog", (HttpContext c) => Keyed(c, expected, () => Results.Text(CatalogJson(), "application/json")));
        app.MapPost("/admin/character/{id}/credits", Writer(expected, AdminAction.Credits));
        app.MapPost("/admin/character/{id}/progress", Writer(expected, AdminAction.Progress));
        app.MapPost("/admin/character/{id}/items", Writer(expected, AdminAction.AddItem));
        app.MapPost("/admin/character/{id}/items/remove", Writer(expected, AdminAction.RemoveItem));
        app.MapPost("/admin/character/{id}/items/stats", Writer(expected, AdminAction.ItemStats));
        MapHorde(app, expected);
        Console.WriteLine($"  admin: ON - /admin/* answers a request carrying {KeyVariable} in {KeyHeader}");
        return true;
    }

    private static bool KeyMatches(HttpContext context, byte[] expected)
    {
        // Digests compared, not the bytes: FixedTimeEquals returns at once on a length mismatch, which would tell the key's length (review).
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers[KeyHeader].ToString()));
        return CryptographicOperations.FixedTimeEquals(given, SHA256.HashData(expected));
    }

    private static IResult Keyed(HttpContext context, byte[] expected, Func<IResult> handler)
    {
        context.Response.Headers.CacheControl = "no-store";
        return KeyMatches(context, expected) ? handler() : Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    // A write's handler, typed as a Delegate: a lambda from HttpContext to Task<IResult> binds as a RequestDelegate, which drops the result it returns.
    private static Delegate Writer(byte[] expected, AdminAction action)
        => (Func<HttpContext, Task<IResult>>)(context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return KeyMatches(context, expected) ? Write(context, action) : Task.FromResult(Results.StatusCode(StatusCodes.Status401Unauthorized));
        });

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET /admin/accounts?q=&amp;who=all|people|bots&amp;limit=</c>: <c>{"total","matched","accounts":[…]}</c>, by account name — each with its
    /// character, level, XP, credits and whether it is in the world now. <c>q</c> is a case-insensitive substring of the account or the character name.
    /// </summary>
    private static IResult Accounts(DatabaseEngine dbe, HttpContext context)
    {
        var query = context.Request.Query;
        var q = query["q"].ToString().Trim();
        var who = query["who"].ToString();
        var limit = int.TryParse(query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, MaxListed) : 200;
        return Results.Text(AccountsJson(dbe, q, who, limit), "application/json");
    }

    /// <summary>The listing <see cref="Accounts"/> answers, for it and for the checks.</summary>
    public static string AccountsJson(DatabaseEngine dbe, string q, string who, int limit)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var inv = CultureInfo.InvariantCulture;
        using var tx = dbe.CreateQuickTransaction();
        var characters = new Dictionary<ulong, (EntityId Id, Character Profile, CharacterProgress Progress)>();
        foreach (var c in tx.Query<CharacterArch>())
        {
            var profile = c.Read(CharacterArch.Profile);
            characters[profile.Account.Id.RawValue] = (c.Id, profile, c.Read(CharacterArch.Progress));
        }

        var balances = Balances(dbe, tx);
        var rows = new List<(string Name, string Json)>();
        var total = 0;
        foreach (var a in tx.Query<AccountArch>())
        {
            total++;
            var account = a.Read(AccountArch.Identity);
            var bot = account.Bot != 0;
            if ((who == "people" && bot) || (who == "bots" && !bot))
            {
                continue;
            }

            var name = account.Name.AsString;
            var has = characters.TryGetValue(a.Id.RawValue, out var ch);
            var characterName = has ? ch.Profile.Name.AsString : "";
            if (q.Length > 0 && !name.Contains(q, StringComparison.OrdinalIgnoreCase) && !characterName.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var json = new StringBuilder(256);
            json.Append("{\"account\":\"").Append(a.Id.RawValue.ToString(inv))
                .Append("\",\"name\":\"").Append(EconomyEndpoints.Escape(name))
                .Append("\",\"bot\":").Append(bot ? "true" : "false")
                .Append(",\"created\":").Append(UnixMs(account.CreatedUtcTicks))
                .Append(",\"lastSeen\":").Append(UnixMs(account.LastSeenUtcTicks))
                .Append(",\"online\":").Append(TatooineReplication.IsPlaying(a.Id) ? "true" : "false");
            if (has)
            {
                var key = unchecked((long)ch.Id.RawValue);
                json.Append(",\"character\":\"").Append(ch.Id.RawValue.ToString(inv))
                    .Append("\",\"characterName\":\"").Append(EconomyEndpoints.Escape(characterName))
                    .Append("\",\"level\":").Append(ch.Progress.Level)
                    .Append(",\"xp\":").Append(ch.Progress.Xp)
                    .Append(",\"credits\":").Append(balances.TryGetValue(key, out var b) ? b.ToString(inv) : "null");
            }

            rows.Add((name, json.Append('}').ToString()));
        }

        rows.Sort(static (x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
        var answer = new StringBuilder(64 + (Math.Min(rows.Count, limit) * 256));
        answer.Append("{\"total\":").Append(total).Append(",\"matched\":").Append(rows.Count).Append(",\"accounts\":[");
        for (var i = 0; i < rows.Count && i < limit; i++)
        {
            answer.Append(i == 0 ? "" : ",").Append(rows[i].Json);
        }

        return answer.Append("]}").ToString();
    }

    /// <summary>
    /// <c>GET /admin/character/{id}</c>: the character, its account, level, XP and the band it sits in, its credits, its realm and last saved position, and
    /// its inventory as the game's own endpoint writes it (<see cref="ItemEndpoints.InventoryJson"/>).
    /// </summary>
    private static IResult Character(DatabaseEngine dbe, HttpContext context)
    {
        if (!TryCharacter(context, out var id))
        {
            return Results.BadRequest();
        }

        var json = CharacterJson(dbe, id);
        return json == null ? Results.NotFound() : Results.Text(json, "application/json");
    }

    /// <summary>The detail <see cref="Character"/> answers; null for no such character.</summary>
    public static string CharacterJson(DatabaseEngine dbe, EntityId id)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var inv = CultureInfo.InvariantCulture;
        Character profile;
        CharacterProgress progress;
        Account account = default;
        long? balance;
        using (var tx = dbe.CreateQuickTransaction())
        {
            if (id.ArchetypeId != dbe.ArchetypeIdOf<CharacterArch>() || !tx.TryOpen(id, out var e))
            {
                return null;
            }

            profile = e.Read(CharacterArch.Profile);
            progress = e.Read(CharacterArch.Progress);
            if (tx.TryOpen(profile.Account.Id, out var a))
            {
                account = a.Read(AccountArch.Identity);
            }

            var key = unchecked((long)id.RawValue);
            using var wallets = tx.EnumerateIndex<Wallet, long>(dbe.GetIndexRef<Wallet, long>(w => w.Owner), key, key);
            balance = wallets.MoveNext() ? wallets.CurrentComponent.Balance : null;
        }

        var level = Math.Max(1, (int)progress.Level);
        var json = new StringBuilder(2048);
        json.Append("{\"character\":\"").Append(id.RawValue.ToString(inv))
            .Append("\",\"name\":\"").Append(EconomyEndpoints.Escape(profile.Name.AsString))
            .Append("\",\"account\":\"").Append(profile.Account.Id.RawValue.ToString(inv))
            .Append("\",\"accountName\":\"").Append(EconomyEndpoints.Escape(account.Name.AsString))
            .Append("\",\"bot\":").Append(account.Bot != 0 ? "true" : "false")
            .Append(",\"online\":").Append(TatooineReplication.IsPlaying(profile.Account.Id) ? "true" : "false")
            .Append(",\"created\":").Append(UnixMs(account.CreatedUtcTicks))
            .Append(",\"lastSeen\":").Append(UnixMs(account.LastSeenUtcTicks))
            .Append(",\"level\":").Append(progress.Level)
            .Append(",\"xp\":").Append(progress.Xp)
            .Append(",\"killXp\":").Append(progress.KillXp)
            .Append(",\"missionXp\":").Append(progress.MissionXp)
            .Append(",\"levelXp\":").Append(Progression.XpAt(level))
            .Append(",\"nextXp\":").Append(level >= Progression.MaxLevel ? Progression.MaxXp : Progression.XpAt(level + 1))
            .Append(",\"maxLevel\":").Append(Progression.MaxLevel)
            .Append(",\"credits\":").Append(balance.HasValue ? balance.Value.ToString(inv) : "null")
            .Append(",\"maxCredits\":").Append(SimBridge.MaxAdminCredits)
            .Append(",\"realm\":").Append(profile.LastRealm)
            .Append(",\"x\":").Append(profile.LastPosition.X.ToString("0.#", inv))
            .Append(",\"z\":").Append(profile.LastPosition.Z.ToString("0.#", inv))
            .Append(",\"inventory\":").Append(ItemEndpoints.InventoryJson(dbe, id))
            .Append('}');
        return json.ToString();
    }

    /// <summary>
    /// <c>GET /admin/catalog</c>: every item template — id, asset, name, category, whether it has a Mk and stats, the equip slot it is worn in, what its stat
    /// lines mean — and for an item with stats the defaults the add form starts from, per Mk (<see cref="SimBridge.AdminDefaults"/>).
    /// </summary>
    public static string CatalogJson()
    {
        var json = new StringBuilder(32 * 1024);
        json.Append("{\"stackMax\":").Append(ItemLedger.StackMax).Append(",\"slots\":").Append(ItemLedger.Slots).Append(",\"templates\":[");
        var first = true;
        foreach (var t in ItemCatalog.Templates)
        {
            var instance = ItemCatalog.HasStats(t.Id);
            json.Append(first ? "{" : ",{")
                .Append("\"id\":").Append(t.Id)
                .Append(",\"asset\":\"").Append(t.Asset)
                .Append("\",\"name\":\"").Append(EconomyEndpoints.Escape(t.Name))
                .Append("\",\"category\":\"").Append(ItemCategory.NameOf(t.Category))
                .Append("\",\"hasMk\":").Append(t.HasMk ? "true" : "false")
                .Append(",\"instance\":").Append(instance ? "true" : "false")
                .Append(",\"wears\":").Append(Equipment.SlotOf(t.Id))
                .Append(",\"lines\":[");
            var lines = LinesOf(t.Category);
            for (var i = 0; i < lines.Length; i++)
            {
                json.Append(i == 0 ? "\"" : ",\"").Append(lines[i]).Append('"');
            }

            json.Append(']');
            if (instance)
            {
                json.Append(",\"defaults\":{");
                var mks = t.HasMk ? 5 : 0;
                for (var mk = t.HasMk ? 1 : 0; mk <= mks; mk++)
                {
                    var d = SimBridge.AdminDefaults(t.Id, (byte)mk);
                    json.Append(mk <= 1 ? "\"" : ",\"").Append(mk).Append("\":");
                    StatsJson(json, in d);
                }

                json.Append('}');
            }

            json.Append('}');
            first = false;
        }

        return json.Append("]}").ToString();
    }

    // What Stat0..Stat2 mean for a category (Equipment, ItemEndpoints.Stats): the names the inventory's stats use.
    private static string[] LinesOf(byte category) => category switch
    {
        ItemCategory.Weapon => ["damage", "delayMs", "rangeDm"],
        ItemCategory.Armor => ["kinetic", "energy"],
        ItemCategory.Part => ["grade"],
        ItemCategory.Potion => ["potency", "durationMs"],
        _ => [],
    };

    private static void StatsJson(StringBuilder json, in ItemInstance s)
        => json.Append("{\"stat0\":").Append(s.Stat0)
            .Append(",\"stat1\":").Append(s.Stat1)
            .Append(",\"stat2\":").Append(s.Stat2)
            .Append(",\"stat3\":").Append(s.Stat3)
            .Append(",\"condition\":").Append(s.Condition)
            .Append(",\"maxCondition\":").Append(s.MaxCondition)
            .Append(",\"lifeMilli\":").Append(s.LifeMilli)
            .Append('}');

    private static Dictionary<long, long> Balances(DatabaseEngine dbe, Transaction tx)
    {
        var balances = new Dictionary<long, long>();
        using var wallets = tx.EnumerateIndex<Wallet, long>(dbe.GetIndexRef<Wallet, long>(w => w.Owner), long.MinValue, long.MaxValue);
        while (wallets.MoveNext())
        {
            balances[wallets.CurrentComponent.Owner] = wallets.CurrentComponent.Balance;
        }

        return balances;
    }

    private static long UnixMs(long utcTicks)
        => utcTicks <= DateTime.UnixEpoch.Ticks ? 0 : (utcTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

    // ── Writes ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A write: the request read from the body, handed to the simulation, answered <c>{"ok","message"}</c> — 200 when it committed, 409 when it was refused,
    /// 400 for a body that names nothing, 503 when no tick answered in time.
    /// </summary>
    private static async Task<IResult> Write(HttpContext context, AdminAction action)
    {
        if (!TryCharacter(context, out var character))
        {
            return Results.BadRequest();
        }

        AdminRequest request;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
            request = Read(body.RootElement, action, character);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or OverflowException or KeyNotFoundException)
        {
            return Answer(StatusCodes.Status400BadRequest, false, "the body is not the JSON this call reads: " + e.Message);
        }

        var outcome = await Submit(request).ConfigureAwait(false);
        Console.WriteLine($"  admin: {action} for character {character.RawValue}: {(outcome.Ok ? "" : "REFUSED - ")}{outcome.Message}");
        var status = outcome.Ok ? StatusCodes.Status200OK
            : outcome.Message == Unanswered ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status409Conflict;
        return Answer(status, outcome.Ok, outcome.Message);
    }

    private const string Unanswered = "no tick answered in time: the simulation may be stopped";

    /// <summary>Hands a request to the simulation and waits for its tick, bounded.</summary>
    public static async Task<AdminOutcome> Submit(AdminRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var desk = Desk;
        if (desk == null)
        {
            return new AdminOutcome(false, "this server runs no simulation to write to");
        }

        var done = desk(request);
        try
        {
            return await done.WaitAsync(WriteWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Given up, so never applied — unless a tick took it first, in which case its answer is moments away and is the truth.
            return request.TryAbandon() ? new AdminOutcome(false, Unanswered) : await done.ConfigureAwait(false);
        }
    }

    /// <summary>A request from its JSON body: the fields its action reads, the others ignored.</summary>
    public static AdminRequest Read(JsonElement body, AdminAction action, EntityId character)
    {
        var r = new AdminRequest { Action = action, Character = unchecked((long)character.RawValue) };
        switch (action)
        {
            case AdminAction.Credits:
                r.Delta = Long(body, "delta");
                break;
            case AdminAction.Progress:
                r.Level = checked((int)Long(body, "level", 0));
                r.Xp = checked((int)Long(body, "xp", -1));
                break;
            case AdminAction.AddItem:
                r.Template = checked((ushort)Long(body, "template"));
                r.Mk = checked((byte)Long(body, "mk", 0));
                r.Count = checked((int)Long(body, "count", 1));
                if (body.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
                {
                    r.HasStats = true;
                    r.Stats = Stats(stats);
                }

                break;
            case AdminAction.RemoveItem:
                r.Item = Long(body, "item");
                r.Count = checked((int)Long(body, "count", 0));
                break;
            case AdminAction.ItemStats:
                r.Item = Long(body, "item");
                r.HasStats = true;
                r.Stats = Stats(body.GetProperty("stats"));
                break;
        }

        return r;
    }

    private static ItemInstance Stats(JsonElement s) => new()
    {
        Stat0 = checked((int)Long(s, "stat0", 0)),
        Stat1 = checked((int)Long(s, "stat1", 0)),
        Stat2 = checked((int)Long(s, "stat2", 0)),
        Stat3 = checked((int)Long(s, "stat3", 0)),
        Condition = checked((int)Long(s, "condition", 0)),
        MaxCondition = checked((int)Long(s, "maxCondition", 0)),
        LifeMilli = checked((short)Long(s, "lifeMilli", 1000)),
    };

    // A number, or a string holding one — ids are strings, a 64-bit value being past what a JavaScript number holds exactly. Required without a fallback.
    private static long Long(JsonElement e, string name, long? fallback = null)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return fallback ?? throw new FormatException($"'{name}' is missing");
        }

        return v.ValueKind == JsonValueKind.String ? long.Parse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture) : v.GetInt64();
    }

    private static bool TryCharacter(HttpContext context, out EntityId character)
    {
        character = default;
        if (!ulong.TryParse(context.Request.RouteValues["id"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var raw) || raw == 0)
        {
            return false;
        }

        character = EntityId.FromRawValue(raw);
        return true;
    }

    private static IResult Answer(int status, bool ok, string message)
        => Results.Text($"{{\"ok\":{(ok ? "true" : "false")},\"message\":\"{EconomyEndpoints.Escape(message)}\"}}", "application/json", Encoding.UTF8, status);
}
