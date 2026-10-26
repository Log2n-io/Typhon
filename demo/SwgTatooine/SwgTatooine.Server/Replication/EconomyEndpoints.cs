using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SwgTatooine.Server.Accounts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// The verdict of one replay of the whole ledger (economy-foundation.md E0-8): what was read, and every rule found broken.
/// </summary>
public sealed class EconomyAuditResult
{
    /// <summary>The most violations kept by name; the count is exact beyond it.</summary>
    public const int ViolationsKept = 50;

    /// <summary>No rule broken.</summary>
    public bool Ok => ViolationCount == 0;

    /// <summary>Ledger rows replayed.</summary>
    public long Entries { get; internal set; }

    /// <summary>The last sequence number read.</summary>
    public long LastSeq { get; internal set; }

    /// <summary>Wallets read.</summary>
    public int Wallets { get; internal set; }

    /// <summary>World's balance as the head stores it.</summary>
    public long World { get; internal set; }

    /// <summary>Σ of every wallet's balance as stored.</summary>
    public long WalletsTotal { get; internal set; }

    /// <summary>Every rule found broken, counted.</summary>
    public int ViolationCount { get; internal set; }

    /// <summary>The first <see cref="ViolationsKept"/> of them, each saying what and where.</summary>
    public List<string> Violations { get; } = [];

    /// <summary>How long the replay took, milliseconds.</summary>
    public double Milliseconds { get; internal set; }

    internal void Violation(string what)
    {
        ViolationCount++;
        if (Violations.Count < ViolationsKept)
        {
            Violations.Add(what);
        }
    }

    /// <summary>The verdict as the endpoint answers it.</summary>
    public string ToJson()
    {
        var json = new StringBuilder(256);
        json.Append("{\"ok\":").Append(Ok ? "true" : "false")
            .Append(",\"entries\":").Append(Entries)
            .Append(",\"lastSeq\":").Append(LastSeq)
            .Append(",\"wallets\":").Append(Wallets)
            .Append(",\"world\":").Append(World)
            .Append(",\"walletsTotal\":").Append(WalletsTotal)
            .Append(",\"sum\":").Append(WalletsTotal + World)
            .Append(",\"violationCount\":").Append(ViolationCount)
            .Append(",\"violations\":[");
        for (var i = 0; i < Violations.Count; i++)
        {
            json.Append(i == 0 ? "\"" : ",\"").Append(EconomyEndpoints.Escape(Violations[i])).Append('"');
        }

        return json.Append("],\"ms\":").Append(Milliseconds.ToString("0.###", CultureInfo.InvariantCulture)).Append('}').ToString();
    }
}

/// <summary>
/// The economy over HTTP (economy-foundation.md): <c>GET /swg/wallet</c>, the caller's own balance and recent rows; <c>GET /swg/economy/audit</c>, the
/// whole ledger replayed and checked; <c>GET /swg/economy/ledger</c>, the rows themselves, for whoever is checking; <c>GET /swg/economy/stats</c>, the
/// running server's counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off the tick, and legal for the journal's reason</b> (<see cref="MissionJournal"/>): wallets, rows and the head are Versioned on archetypes with no
/// placement, which <c>EW-02</c> lets a thread outside the DAG read. Each answer is one snapshot — one transaction — so the audit sees every row and every
/// balance as of one commit of the <c>Ledger</c> system, which writes them all together: a replay can never catch a transfer half applied.
/// </para>
/// <para>
/// <b>The demo's own, not the engine's tools'</b> (Loïc, 2026-10-26): the Typhon CLI and the Workbench know nothing of this game, so the audit is an endpoint
/// the game serves, and anybody who can reach the server can ask it.
/// </para>
/// </remarks>
public static class EconomyEndpoints
{
    /// <summary>How many of its most recent rows <c>GET /swg/wallet</c> answers with.</summary>
    public const int RecentRows = 20;

    /// <summary>The most rows one <c>GET /swg/economy/ledger</c> answers with.</summary>
    public const int MaxLedgerPage = 1_000;

    private static DatabaseEngine _indexesOf;
    private static IndexRef _bySeq;
    private static IndexRef _byFrom;
    private static IndexRef _byTo;
    private static IndexRef _walletByOwner;
    private static IndexRef _headByKey;
    private static readonly Lock IndexLock = new();

    /// <summary>The running server's economy counters as JSON (<c>GET /swg/economy/stats</c>); set by the server that serves, "{}" until then.</summary>
    public static Func<string> CountersJson { get; set; }

    /// <summary>Maps the three endpoints.</summary>
    public static IEndpointRouteBuilder MapEconomy(this IEndpointRouteBuilder app, DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dbe);
        app.MapGet("/swg/wallet", (HttpContext context) => GetWallet(dbe, context));
        app.MapGet("/swg/economy/audit", (HttpContext context) => GetAudit(dbe, context));
        app.MapGet("/swg/economy/ledger", (HttpContext context) => GetLedger(dbe, context));
        app.MapGet("/swg/economy/stats", () => Results.Text(CountersJson?.Invoke() ?? "{}", "application/json"));
        return app;
    }

    private static IResult GetWallet(DatabaseEngine dbe, HttpContext context)
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
            ? Results.Text(WalletJson(dbe, character), "application/json")
            : Results.Text("{\"open\":false,\"credits\":0,\"transfers\":[]}", "application/json");
    }

    private static IResult GetAudit(DatabaseEngine dbe, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = Audit(dbe);
        Console.WriteLine(
            $"  economy audit: {(result.Ok ? "OK" : "VIOLATED")} — {result.Entries} rows to seq {result.LastSeq}, {result.Wallets} wallets, "
            + $"world {result.World}, sum {result.WalletsTotal + result.World}, {result.ViolationCount} violation(s), {result.Milliseconds:0.#} ms");
        return Results.Text(result.ToJson(), "application/json");
    }

    private static IResult GetLedger(DatabaseEngine dbe, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var query = context.Request.Query;
        var after = long.TryParse(query["after"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? Math.Max(0, a) : 0;
        var limit = int.TryParse(query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, MaxLedgerPage) : 100;
        return Results.Text(LedgerJson(dbe, after, limit), "application/json");
    }

    /// <summary>
    /// A character's wallet as JSON: <c>{"open","credits","transfers":[{"seq","tick","kind","amount","other","ref"}…]}</c>, the most recent row first.
    /// </summary>
    /// <remarks>
    /// <c>amount</c> is signed from this wallet's side: what it received is positive, what it paid negative. <c>other</c> is the other side's character
    /// name, <c>"World"</c> for a grant or a reward, or null for a character that no longer exists. <c>seq</c> and <c>ref</c> are strings: a 64-bit value
    /// is past what a JavaScript number holds exactly.
    /// </remarks>
    public static string WalletJson(DatabaseEngine dbe, EntityId character)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        Indexes(dbe);
        var key = unchecked((long)character.RawValue);
        using var tx = dbe.CreateQuickTransaction();
        var open = false;
        var credits = 0L;
        using (var wallets = tx.EnumerateIndex<Wallet, long>(_walletByOwner, key, key))
        {
            if (wallets.MoveNext())
            {
                open = true;
                credits = wallets.CurrentComponent.Balance;
            }
        }

        // The last RecentRows of each side, then the last RecentRows of both: a wallet's rows are two range scans of their side's index.
        var rows = new List<LedgerEntry>();
        Collect(tx, _byFrom, key, rows);
        Collect(tx, _byTo, key, rows);
        rows.Sort(static (x, y) => y.Seq.CompareTo(x.Seq));
        var json = new StringBuilder(512);
        json.Append("{\"open\":").Append(open ? "true" : "false").Append(",\"credits\":").Append(credits).Append(",\"transfers\":[");
        for (var i = 0; i < rows.Count && i < RecentRows; i++)
        {
            var r = rows[i];
            var received = r.To == key;
            var other = received ? r.From : r.To;
            json.Append(i == 0 ? "{" : ",{")
                .Append("\"seq\":\"").Append(r.Seq.ToString(CultureInfo.InvariantCulture)).Append('"')
                .Append(",\"tick\":").Append(r.WorldTick)
                .Append(",\"kind\":\"").Append(TransferKind.NameOf(r.Kind)).Append('"')
                .Append(",\"amount\":").Append(received ? r.Amount : -r.Amount)
                .Append(",\"other\":");
            var name = NameOf(tx, other);
            if (name == null)
            {
                json.Append("null");
            }
            else
            {
                json.Append('"').Append(Escape(name)).Append('"');
            }

            json.Append(",\"ref\":\"").Append(r.Ref.ToString(CultureInfo.InvariantCulture)).Append("\"}");
        }

        return json.Append("]}").ToString();
    }

    /// <summary>Rows after <paramref name="after"/>, in sequence, at most <paramref name="limit"/>: <c>{"rows":[…],"next"}</c>.</summary>
    public static string LedgerJson(DatabaseEngine dbe, long after, int limit)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        Indexes(dbe);
        using var tx = dbe.CreateQuickTransaction();
        var json = new StringBuilder(4096);
        json.Append("{\"rows\":[");
        var n = 0;
        var last = after;
        using (var rows = tx.EnumerateIndex<LedgerEntry, long>(_bySeq, after + 1, long.MaxValue))
        {
            while (n < limit && rows.MoveNext())
            {
                var r = rows.CurrentComponent;
                json.Append(n == 0 ? "{" : ",{")
                    .Append("\"seq\":").Append(r.Seq)
                    .Append(",\"tick\":").Append(r.WorldTick)
                    .Append(",\"kind\":\"").Append(TransferKind.NameOf(r.Kind)).Append('"')
                    .Append(",\"from\":\"").Append(r.From.ToString(CultureInfo.InvariantCulture)).Append('"')
                    .Append(",\"to\":\"").Append(r.To.ToString(CultureInfo.InvariantCulture)).Append('"')
                    .Append(",\"amount\":").Append(r.Amount)
                    .Append(",\"fromAfter\":").Append(r.FromAfter)
                    .Append(",\"toAfter\":").Append(r.ToAfter)
                    .Append(",\"ref\":\"").Append(r.Ref.ToString(CultureInfo.InvariantCulture)).Append("\"}");
                last = r.Seq;
                n++;
            }
        }

        return json.Append("],\"next\":").Append(last).Append('}').ToString();
    }

    /// <summary>
    /// Replays the whole ledger from its first row in one snapshot and checks every rule of economy-foundation.md E0-8 against it and against every
    /// wallet and the head.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules: the sequence runs from 1 without a gap; every amount is above zero and has two distinct sides; a wallet's first row is its grant from
    /// World; each row's two balances-after equal the replay's; no wallet goes below zero; every wallet equals its replay, its last sequence and its count of
    /// rows; every side of a row has a wallet; the head's sequence and World's balance equal the replay's; and every balance, World included, sums to zero.
    /// </para>
    /// <para>
    /// <b>Two independently produced numbers each time</b>, which is what makes it an audit rather than a re-read: the stored balances were written by the
    /// <c>Ledger</c> system as it went, and the replay is recomputed here from nothing but the rows.
    /// </para>
    /// </remarks>
    public static EconomyAuditResult Audit(DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        Indexes(dbe);
        var started = Stopwatch.GetTimestamp();
        var result = new EconomyAuditResult();
        var balance = new Dictionary<long, long> { [EconomyLedger.World] = 0 };
        var rowsOf = new Dictionary<long, int>();
        var lastSeqOf = new Dictionary<long, long>();
        var expected = 1L;
        using var tx = dbe.CreateQuickTransaction();
        using (var rows = tx.EnumerateIndex<LedgerEntry, long>(_bySeq, long.MinValue, long.MaxValue))
        {
            while (rows.MoveNext())
            {
                var r = rows.CurrentComponent;
                result.Entries++;
                result.LastSeq = r.Seq;
                if (r.Seq != expected)
                {
                    result.Violation($"seq {r.Seq}: expected {expected} — a gap or a repeat in the sequence");
                }

                expected = r.Seq + 1;
                if (r.Amount <= 0)
                {
                    result.Violation($"seq {r.Seq}: amount {r.Amount} is not above zero");
                }

                if (r.From == r.To)
                {
                    result.Violation($"seq {r.Seq}: both sides are {r.From}");
                }

                if (r.Kind is < TransferKind.Grant or > TransferKind.Tip)
                {
                    result.Violation($"seq {r.Seq}: unknown kind {r.Kind}");
                }

                FirstRow(result, rowsOf, r.From, in r);
                FirstRow(result, rowsOf, r.To, in r);
                var from = Get(balance, r.From) - r.Amount;
                var to = Get(balance, r.To) + r.Amount;
                balance[r.From] = from;
                balance[r.To] = to;
                if (from != r.FromAfter)
                {
                    result.Violation($"seq {r.Seq}: {r.From}'s balance after is {r.FromAfter}, the replay says {from}");
                }

                if (to != r.ToAfter)
                {
                    result.Violation($"seq {r.Seq}: {r.To}'s balance after is {r.ToAfter}, the replay says {to}");
                }

                if (r.From != EconomyLedger.World && from < 0)
                {
                    result.Violation($"seq {r.Seq}: {r.From} goes below zero ({from})");
                }

                lastSeqOf[r.From] = r.Seq;
                lastSeqOf[r.To] = r.Seq;
            }
        }

        var walletOwners = new HashSet<long>();
        using (var wallets = tx.EnumerateIndex<Wallet, long>(_walletByOwner, long.MinValue, long.MaxValue))
        {
            while (wallets.MoveNext())
            {
                var w = wallets.CurrentComponent;
                result.Wallets++;
                result.WalletsTotal += w.Balance;
                walletOwners.Add(w.Owner);
                if (!balance.TryGetValue(w.Owner, out var replayed))
                {
                    result.Violation($"wallet {w.Owner}: holds {w.Balance} and no ledger row ever moved it");
                    continue;
                }

                if (w.Balance != replayed)
                {
                    result.Violation($"wallet {w.Owner}: holds {w.Balance}, the replay says {replayed}");
                }

                if (w.LastSeq != Get(lastSeqOf, w.Owner))
                {
                    result.Violation($"wallet {w.Owner}: last moved at seq {w.LastSeq}, the ledger says {Get(lastSeqOf, w.Owner)}");
                }

                if (w.Transfers != (rowsOf.TryGetValue(w.Owner, out var n) ? n : 0))
                {
                    result.Violation($"wallet {w.Owner}: counts {w.Transfers} rows, the ledger has {n}");
                }
            }
        }

        foreach (var (owner, _) in balance)
        {
            if (owner != EconomyLedger.World && !walletOwners.Contains(owner))
            {
                result.Violation($"account {owner}: rows name it and it has no wallet");
            }
        }

        using (var heads = tx.EnumerateIndex<LedgerHead, int>(_headByKey, EconomyLedger.HeadKey, EconomyLedger.HeadKey))
        {
            if (heads.MoveNext())
            {
                var h = heads.CurrentComponent;
                result.World = h.World;
                if (h.LastSeq != result.LastSeq)
                {
                    result.Violation($"head: last seq {h.LastSeq}, the ledger ends at {result.LastSeq}");
                }

                if (h.World != balance[EconomyLedger.World])
                {
                    result.Violation($"head: World holds {h.World}, the replay says {balance[EconomyLedger.World]}");
                }
            }
            else if (result.Entries > 0)
            {
                result.Violation("head: missing, and the ledger has rows");
            }
        }

        if (result.WalletsTotal + result.World != 0)
        {
            result.Violation($"conservation: the wallets hold {result.WalletsTotal} and World {result.World}, which sum to {result.WalletsTotal + result.World}, not 0");
        }

        result.Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return result;
    }

    /// <summary>Counts a row against one of its sides, and checks that a wallet's first row is its grant.</summary>
    private static void FirstRow(EconomyAuditResult result, Dictionary<long, int> rowsOf, long side, in LedgerEntry r)
    {
        if (side == EconomyLedger.World)
        {
            return;
        }

        var n = rowsOf.TryGetValue(side, out var seen) ? seen : 0;
        if (n == 0 && (r.Kind != TransferKind.Grant || r.From != EconomyLedger.World || r.To != side))
        {
            result.Violation($"seq {r.Seq}: {side}'s first row is a {TransferKind.NameOf(r.Kind)}, not its grant");
        }

        rowsOf[side] = n + 1;
    }

    private static long Get(Dictionary<long, long> map, long key) => map.TryGetValue(key, out var v) ? v : 0;

    private static void Collect(Transaction tx, IndexRef index, long key, List<LedgerEntry> into)
    {
        var start = into.Count;
        using var rows = tx.EnumerateIndex<LedgerEntry, long>(index, key, key);
        while (rows.MoveNext())
        {
            into.Add(rows.CurrentComponent);
        }

        // Only the most recent RecentRows of this side can be among the most recent of both.
        var mine = into.Count - start;
        if (mine > RecentRows)
        {
            into.Sort(start, mine, Comparer<LedgerEntry>.Create(static (x, y) => y.Seq.CompareTo(x.Seq)));
            into.RemoveRange(start + RecentRows, mine - RecentRows);
        }
    }

    /// <summary>The other side's name: <c>"World"</c>, a character's, or null for one that is gone.</summary>
    private static string NameOf(Transaction tx, long account)
    {
        if (account == EconomyLedger.World)
        {
            return "World";
        }

        return tx.TryOpen(EntityId.FromRawValue((ulong)account), out var e) ? e.Read(CharacterArch.Profile).Name.AsString : null;
    }

    /// <summary>A string as a JSON string's contents.</summary>
    internal static string Escape(string s)
    {
        var b = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    b.Append("\\\"");
                    break;
                case '\\':
                    b.Append("\\\\");
                    break;
                case < ' ':
                    b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    b.Append(c);
                    break;
            }
        }

        return b.ToString();
    }

    /// <summary>The indexes, resolved once per engine: resolving one parses an expression tree, which a read a second need not repeat.</summary>
    private static void Indexes(DatabaseEngine dbe)
    {
        if (ReferenceEquals(Volatile.Read(ref _indexesOf), dbe))
        {
            return;
        }

        lock (IndexLock)
        {
            if (ReferenceEquals(_indexesOf, dbe))
            {
                return;
            }

            _bySeq = dbe.GetIndexRef<LedgerEntry, long>(e => e.Seq);
            _byFrom = dbe.GetIndexRef<LedgerEntry, long>(e => e.From);
            _byTo = dbe.GetIndexRef<LedgerEntry, long>(e => e.To);
            _walletByOwner = dbe.GetIndexRef<Wallet, long>(w => w.Owner);
            _headByKey = dbe.GetIndexRef<LedgerHead, int>(h => h.Key);
            Volatile.Write(ref _indexesOf, dbe);
        }
    }
}
