using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SwgTatooine.Server.Accounts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>The verdict of one replay of the whole item ledger (inventory-loot.md § 3.5): what was read, and every rule found broken.</summary>
public sealed class ItemAuditResult
{
    /// <summary>The most violations kept by name; the count is exact beyond it.</summary>
    public const int ViolationsKept = 50;

    /// <summary>No rule broken.</summary>
    public bool Ok => ViolationCount == 0;

    /// <summary>Item ledger rows replayed.</summary>
    public long Entries { get; internal set; }

    /// <summary>The last sequence number read.</summary>
    public long LastSeq { get; internal set; }

    /// <summary>Stacks read.</summary>
    public int Stacks { get; internal set; }

    /// <summary>Σ of every stored stack's count.</summary>
    public long Units { get; internal set; }

    /// <summary>Units minted, by the replay.</summary>
    public long Minted { get; internal set; }

    /// <summary>Units destroyed, by the replay.</summary>
    public long Destroyed { get; internal set; }

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
            .Append(",\"stacks\":").Append(Stacks)
            .Append(",\"units\":").Append(Units)
            .Append(",\"minted\":").Append(Minted)
            .Append(",\"destroyed\":").Append(Destroyed)
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
/// Drops lost to a full inventory, per character, for its client to announce once (inventory-loot.md § 3.6, § 5). In memory only — the
/// <see cref="MissionJournal"/> completions' pattern: a reopen forgets them, which costs one unannounced line.
/// </summary>
public static class ItemNotes
{
    /// <summary>How many losses per character the inventory answer carries.</summary>
    public const int Kept = 10;

    /// <summary>One loss, numbered per character from 1 so a client announces each once.</summary>
    internal readonly record struct Lost(int N, long Tick, ushort Template, byte Mk, int Count);

    // Character raw id to its last losses, oldest first: an array replaced whole, never written in place, so an HTTP reader sees one or the other.
    private static readonly ConcurrentDictionary<ulong, Lost[]> Losses = new();

    /// <summary>Records a loss for its character's next read. Called on the tick, once the transaction that lost it committed.</summary>
    internal static void NoteLost(EntityId character, ushort template, byte mk, int count, long tick)
    {
        var before = Losses.TryGetValue(character.RawValue, out var list) ? list : [];
        var n = before.Length == 0 ? 1 : before[^1].N + 1;
        var keep = Math.Min(before.Length, Kept - 1);
        var next = new Lost[keep + 1];
        Array.Copy(before, before.Length - keep, next, 0, keep);
        next[keep] = new Lost(n, tick, template, mk, count);
        Losses[character.RawValue] = next;
    }

    /// <summary>A character's last losses, oldest first.</summary>
    internal static Lost[] Of(EntityId character) => Losses.TryGetValue(character.RawValue, out var list) ? list : [];

    /// <summary>Forgets every loss: a world opened afresh.</summary>
    internal static void Reset() => Losses.Clear();
}

/// <summary>
/// Items over HTTP (inventory-loot.md § 3.5-3.6): <c>GET /swg/inventory</c>, the caller's own stacks, recent rows and losses; <c>GET /swg/items/audit</c>, the
/// whole item ledger replayed and checked; <c>GET /swg/items/ledger</c>, the rows themselves.
/// </summary>
/// <remarks>
/// <b>Off the tick, and legal for the wallet's reason</b> (<see cref="EconomyEndpoints"/>): stacks, rows and the head are Versioned on archetypes with no
/// placement, which <c>EW-02</c> lets a thread outside the DAG read; each answer is one snapshot, so the audit can never catch a drop half placed.
/// </remarks>
public static class ItemEndpoints
{
    /// <summary>How many of its most recent rows <c>GET /swg/inventory</c> answers with.</summary>
    public const int RecentRows = 20;

    /// <summary>The answer for an account with no character yet.</summary>
    internal const string Empty = "{\"open\":false,\"slots\":30,\"items\":[],\"recent\":[],\"lost\":[]}";

    private sealed record IndexSet(DatabaseEngine Dbe, IndexRef BySeq, IndexRef ByFrom, IndexRef ByTo, IndexRef StackByOwner, IndexRef HeadByKey,
        IndexRef WalletByOwner, IndexRef CreditBySeq)
    {
        /// <summary>Each character's newest rows, read since only by what was appended: see <see cref="RecentRowCache{T}"/>.</summary>
        public RecentRowCache<ItemLedgerEntry> Recent { get; } = new(static r => r.Seq, static r => r.From, static r => r.To, RecentRows);
    }

    private static IndexSet _indexes;

    /// <summary>Maps the three endpoints.</summary>
    public static IEndpointRouteBuilder MapItems(this IEndpointRouteBuilder app, DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dbe);
        app.MapGet("/swg/inventory", (HttpContext context) => GetInventory(dbe, context));
        app.MapGet("/swg/items/audit", (HttpContext context) => GetAudit(dbe, context));
        app.MapGet("/swg/items/ledger", (HttpContext context) => GetLedger(dbe, context));
        return app;
    }

    /// <summary>How many inventory reads went to the side indexes rather than the walk of the rows appended since: for the checks.</summary>
    internal static long RecentFullReads(DatabaseEngine dbe) => Indexes(dbe).Recent.FullReads;

    /// <summary>The inventory handler itself, for the checks: a request in, the result out.</summary>
    internal static IResult GetInventoryForChecks(DatabaseEngine dbe, HttpContext context) => GetInventory(dbe, context);

    private static IResult GetInventory(DatabaseEngine dbe, HttpContext context)
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
            ? Results.Text(InventoryJson(dbe, character), "application/json")
            : Results.Text(Empty, "application/json");
    }

    private static IResult GetAudit(DatabaseEngine dbe, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = Audit(dbe);
        Console.WriteLine(
            $"  items audit: {(result.Ok ? "OK" : "VIOLATED")} - {result.Entries} rows to seq {result.LastSeq}, {result.Stacks} stacks, {result.Units} units, "
            + $"minted {result.Minted}, destroyed {result.Destroyed}, {result.ViolationCount} violation(s), {result.Milliseconds:0.#} ms");
        return Results.Text(result.ToJson(), "application/json");
    }

    private static IResult GetLedger(DatabaseEngine dbe, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var query = context.Request.Query;
        var after = long.TryParse(query["after"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? Math.Max(0, a) : 0;
        var limit = int.TryParse(query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? Math.Clamp(l, 1, EconomyEndpoints.MaxLedgerPage)
            : 100;
        return Results.Text(LedgerJson(dbe, after, limit), "application/json");
    }

    /// <summary>
    /// A character's inventory as JSON (inventory-loot.md § 3.6): <c>{"open","slots","items":[…by slot…],"recent":[…newest first…],"lost":[…]}</c>.
    /// </summary>
    /// <remarks>
    /// <c>id</c>, <c>seq</c> and <c>ref</c> are strings: a 64-bit value is past what a JavaScript number holds exactly. Every item, row and loss carries its
    /// <c>name</c> and <c>category</c>; a row's <c>dir</c> is <c>"in"</c> when the character received it, <c>"out"</c> when it gave it.
    /// </remarks>
    public static string InventoryJson(DatabaseEngine dbe, EntityId character)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var ix = Indexes(dbe);
        var key = unchecked((long)character.RawValue);
        var inv = CultureInfo.InvariantCulture;
        using var tx = dbe.CreateQuickTransaction();
        bool open;
        using (var wallets = tx.EnumerateIndex<Wallet, long>(ix.WalletByOwner, key, key))
        {
            open = wallets.MoveNext();
        }

        var stacks = new List<(ulong Id, ItemStack Stack)>();
        using (var found = tx.EnumerateIndex<ItemStack, long>(ix.StackByOwner, key, key))
        {
            while (found.MoveNext())
            {
                stacks.Add(((ulong)found.CurrentEntityPK, found.CurrentComponent));
            }
        }

        stacks.Sort(static (x, y) => x.Stack.Slot.CompareTo(y.Stack.Slot));
        var json = new StringBuilder(1024);
        json.Append("{\"open\":").Append(open ? "true" : "false").Append(",\"slots\":").Append(ItemLedger.Slots).Append(",\"items\":[");
        for (var i = 0; i < stacks.Count; i++)
        {
            var s = stacks[i].Stack;
            ItemCatalog.TryGet(s.Template, out var t);
            json.Append(i == 0 ? "{" : ",{")
                .Append("\"id\":\"").Append(stacks[i].Id.ToString(inv)).Append('"')
                .Append(",\"slot\":").Append(s.Slot);
            Item(json, in t, s.Template);
            json.Append(",\"mk\":").Append(s.Mk)
                .Append(",\"count\":").Append(s.Count)
                .Append('}');
        }

        var lastSeq = 0L;
        using (var heads = tx.EnumerateIndex<ItemLedgerHead, int>(ix.HeadByKey, ItemLedger.HeadKey, ItemLedger.HeadKey))
        {
            lastSeq = heads.MoveNext() ? heads.CurrentComponent.LastSeq : 0;
        }

        var rows = ix.Recent.Read(tx, ix.BySeq, ix.ByFrom, ix.ByTo, key, lastSeq);
        json.Append("],\"recent\":[");
        for (var i = 0; i < rows.Count && i < RecentRows; i++)
        {
            var r = rows[i];
            ItemCatalog.TryGet(r.Template, out var t);
            json.Append(i == 0 ? "{" : ",{")
                .Append("\"seq\":\"").Append(r.Seq.ToString(inv)).Append('"')
                .Append(",\"tick\":").Append(r.WorldTick)
                .Append(",\"kind\":\"").Append(ItemMoveKind.NameOf(r.Kind)).Append('"');
            Item(json, in t, r.Template);
            json.Append(",\"mk\":").Append(r.Mk)
                .Append(",\"count\":").Append(r.Count)
                .Append(",\"ref\":\"").Append(r.Ref.ToString(inv)).Append('"')
                .Append(",\"dir\":\"").Append(r.To == key ? "in" : "out").Append("\"}");
        }

        json.Append("],\"lost\":[");
        var lost = ItemNotes.Of(character);
        for (var i = 0; i < lost.Length; i++)
        {
            var l = lost[i];
            ItemCatalog.TryGet(l.Template, out var t);
            json.Append(i == 0 ? "{" : ",{")
                .Append("\"n\":").Append(l.N)
                .Append(",\"tick\":").Append(l.Tick);
            Item(json, in t, l.Template);
            json.Append(",\"mk\":").Append(l.Mk)
                .Append(",\"count\":").Append(l.Count)
                .Append('}');
        }

        return json.Append("]}").ToString();
    }

    // "item" (the asset id), "name" and "category" — the client has no item table — or "unknown" for a template the catalogue does not hold.
    private static void Item(StringBuilder json, in ItemTemplate t, ushort template)
    {
        var known = t.Id == template && template != 0;
        json.Append(",\"item\":\"").Append(known ? t.Asset : "unknown")
            .Append("\",\"name\":\"").Append(known ? EconomyEndpoints.Escape(t.Name) : "Unknown")
            .Append("\",\"category\":\"").Append(known ? ItemCategory.NameOf(t.Category) : "unknown").Append('"');
    }

    /// <summary>Rows after <paramref name="after"/>, in sequence, at most <paramref name="limit"/>: <c>{"rows":[…],"next"}</c>.</summary>
    public static string LedgerJson(DatabaseEngine dbe, long after, int limit)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var ix = Indexes(dbe);
        var inv = CultureInfo.InvariantCulture;
        using var tx = dbe.CreateQuickTransaction();
        var json = new StringBuilder(4096);
        json.Append("{\"rows\":[");
        var n = 0;
        var last = after;
        using (var rows = tx.EnumerateIndex<ItemLedgerEntry, long>(ix.BySeq, after + 1, long.MaxValue))
        {
            while (n < limit && rows.MoveNext())
            {
                var r = rows.CurrentComponent;
                ItemCatalog.TryGet(r.Template, out var t);
                json.Append(n == 0 ? "{" : ",{")
                    .Append("\"seq\":").Append(r.Seq)
                    .Append(",\"tick\":").Append(r.WorldTick)
                    .Append(",\"kind\":\"").Append(ItemMoveKind.NameOf(r.Kind)).Append('"')
                    .Append(",\"from\":\"").Append(r.From.ToString(inv)).Append('"')
                    .Append(",\"to\":\"").Append(r.To.ToString(inv)).Append('"')
                    .Append(",\"count\":").Append(r.Count)
                    .Append(",\"fromAfter\":").Append(r.FromAfter)
                    .Append(",\"toAfter\":").Append(r.ToAfter)
                    .Append(",\"ref\":\"").Append(r.Ref.ToString(inv)).Append('"')
                    .Append(",\"item\":\"").Append(t.Id == r.Template && r.Template != 0 ? t.Asset : "unknown").Append('"')
                    .Append(",\"mk\":").Append(r.Mk)
                    .Append(",\"fromStack\":\"").Append(r.FromStack.ToString(inv)).Append('"')
                    .Append(",\"toStack\":\"").Append(r.ToStack.ToString(inv)).Append("\"}");
                last = r.Seq;
                n++;
            }
        }

        return json.Append("],\"next\":").Append(last).Append('}').ToString();
    }

    // One stack as the replay builds it: the side, template and Mk of its first row, and its count, rows and last sequence since.
    private sealed class Replayed
    {
        public long Owner;
        public ushort Template;
        public byte Mk;
        public long Count;
        public int Rows;
        public long LastSeq;
    }

    /// <summary>
    /// Replays the whole item ledger from its first row in one snapshot and checks every rule of inventory-loot.md § 3.5 against it, every stored stack and
    /// the head.
    /// </summary>
    /// <remarks>
    /// <b>Two independently produced numbers each time</b>: the stored stacks were written by <c>ItemTransfer</c> as it went, and the replay is recomputed
    /// here from nothing but the rows.
    /// </remarks>
    public static ItemAuditResult Audit(DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var ix = Indexes(dbe);
        var started = Stopwatch.GetTimestamp();
        var result = new ItemAuditResult();
        var stacks = new Dictionary<long, Replayed>();
        var minted = new Dictionary<int, long>();
        var destroyed = new Dictionary<int, long>();
        var missionTick = new Dictionary<long, long>();
        var expected = 1L;
        var lastTick = long.MinValue;
        using var tx = dbe.CreateQuickTransaction();
        using (var rows = tx.EnumerateIndex<ItemLedgerEntry, long>(ix.BySeq, long.MinValue, long.MaxValue))
        {
            while (rows.MoveNext())
            {
                var r = rows.CurrentComponent;
                result.Entries++;
                result.LastSeq = r.Seq;
                if (r.Seq != expected)
                {
                    result.Violation($"seq {r.Seq}: expected {expected} - a gap or a repeat in the sequence");
                }

                expected = r.Seq + 1;
                if (r.WorldTick < lastTick)
                {
                    result.Violation($"seq {r.Seq}: world tick {r.WorldTick} is before the previous row's {lastTick}");
                }

                lastTick = r.WorldTick;
                if (!RowRules(result, missionTick, in r))
                {
                    continue;
                }

                var k = Key(r.Template, r.Mk);
                if (r.From == ItemLedger.World)
                {
                    minted[k] = Get(minted, k) + r.Count;
                    result.Minted += r.Count;
                }
                else
                {
                    Replay(result, stacks, r.FromStack, r.From, -r.Count, r.FromAfter, in r);
                }

                if (r.To == ItemLedger.World)
                {
                    destroyed[k] = Get(destroyed, k) + r.Count;
                    result.Destroyed += r.Count;
                }
                else
                {
                    Replay(result, stacks, r.ToStack, r.To, r.Count, r.ToAfter, in r);
                }
            }
        }

        // Every stored stack: its owner a character with a wallet, its count, template, Mk, last sequence and rows the replay's, its slot its own.
        var held = new Dictionary<int, long>();
        var seen = new HashSet<long>();
        var slotsOf = new Dictionary<long, HashSet<byte>>();
        var stacksOf = new Dictionary<long, int>();
        using (var found = tx.EnumerateIndex<ItemStack, long>(ix.StackByOwner, long.MinValue, long.MaxValue))
        {
            while (found.MoveNext())
            {
                var s = found.CurrentComponent;
                var id = found.CurrentEntityPK;
                result.Stacks++;
                result.Units += s.Count;
                seen.Add(id);
                held[Key(s.Template, s.Mk)] = Get(held, Key(s.Template, s.Mk)) + s.Count;

                // Counted apart from the slots: a stack outside the grid, or one sharing a slot, is still one of its owner's.
                var owned = (stacksOf.TryGetValue(s.Owner, out var c) ? c : 0) + 1;
                stacksOf[s.Owner] = owned;
                if (owned == ItemLedger.Slots + 1)
                {
                    result.Violation($"owner {s.Owner}: more than {ItemLedger.Slots} stacks");
                }
                if (!tx.TryOpen(EntityId.FromRawValue((ulong)s.Owner), out _))
                {
                    result.Violation($"stack {id}: its owner {s.Owner} is not a character that exists");
                }

                using (var wallet = tx.EnumerateIndex<Wallet, long>(ix.WalletByOwner, s.Owner, s.Owner))
                {
                    if (!wallet.MoveNext())
                    {
                        result.Violation($"stack {id}: its owner {s.Owner} has no wallet");
                    }
                }

                if (s.Count is < 1 or > ItemLedger.StackMax)
                {
                    result.Violation($"stack {id}: holds {s.Count}, outside 1..{ItemLedger.StackMax}");
                }

                if (s.Slot is < 1 or > ItemLedger.Slots)
                {
                    result.Violation($"stack {id}: slot {s.Slot}, outside 1..{ItemLedger.Slots}");
                }
                else
                {
                    if (!slotsOf.TryGetValue(s.Owner, out var slots))
                    {
                        slots = [];
                        slotsOf[s.Owner] = slots;
                    }

                    if (!slots.Add(s.Slot))
                    {
                        result.Violation($"stack {id}: slot {s.Slot} of {s.Owner} holds another stack too");
                    }
                }

                if (!stacks.TryGetValue(id, out var replayed))
                {
                    result.Violation($"stack {id}: holds {s.Count} and no item ledger row ever moved it");
                    continue;
                }

                if (replayed.Count == 0)
                {
                    result.Violation($"stack {id}: emptied by the replay and still stored");
                }

                if (s.Count != replayed.Count)
                {
                    result.Violation($"stack {id}: holds {s.Count}, the replay says {replayed.Count}");
                }

                if (s.Owner != replayed.Owner || s.Template != replayed.Template || s.Mk != replayed.Mk)
                {
                    result.Violation($"stack {id}: is {s.Owner}'s {s.Template} Mk {s.Mk}, its rows say {replayed.Owner}'s {replayed.Template} Mk {replayed.Mk}");
                }

                if (s.LastSeq != replayed.LastSeq || s.Rows != replayed.Rows)
                {
                    result.Violation($"stack {id}: last moved at seq {s.LastSeq} in {s.Rows} rows, the ledger says {replayed.LastSeq} in {replayed.Rows}");
                }
            }
        }

        foreach (var (id, replayed) in stacks)
        {
            if (replayed.Count > 0 && !seen.Contains(id))
            {
                result.Violation($"stack {id}: the replay leaves it {replayed.Count} and it is not stored");
            }
        }

        // Conservation per (template, Mk), then in total against the head.
        var keys = new HashSet<int>(minted.Keys);
        keys.UnionWith(destroyed.Keys);
        keys.UnionWith(held.Keys);
        foreach (var k in keys)
        {
            var want = Get(minted, k) - Get(destroyed, k);
            if (Get(held, k) != want)
            {
                result.Violation($"conservation: template {k / 8} Mk {k % 8} holds {Get(held, k)}, minted - destroyed is {want}");
            }
        }

        // A mission's cache comes with its reward: the credit ledger, in this same snapshot, pays that mission on the cache's tick (§ 3.4).
        if (missionTick.Count > 0)
        {
            var rewardTick = new Dictionary<long, long>();
            using (var credits = tx.EnumerateIndex<LedgerEntry, long>(ix.CreditBySeq, long.MinValue, long.MaxValue))
            {
                while (credits.MoveNext())
                {
                    var e = credits.CurrentComponent;
                    if (e.Kind == TransferKind.MissionReward)
                    {
                        rewardTick.TryAdd(e.Ref, e.WorldTick);
                    }
                }
            }

            foreach (var (mission, tick) in missionTick)
            {
                if (!rewardTick.TryGetValue(mission, out var paid) || paid != tick)
                {
                    result.Violation(rewardTick.ContainsKey(mission)
                        ? $"mission {mission}: its cache was minted at tick {tick}, its reward paid at tick {paid} - not in one transaction"
                        : $"mission {mission}: its cache was minted at tick {tick} and no reward pays it");
                }
            }
        }

        using (var heads = tx.EnumerateIndex<ItemLedgerHead, int>(ix.HeadByKey, ItemLedger.HeadKey, ItemLedger.HeadKey))
        {
            if (heads.MoveNext())
            {
                var h = heads.CurrentComponent;
                if (h.LastSeq != result.LastSeq)
                {
                    result.Violation($"head: last seq {h.LastSeq}, the ledger ends at {result.LastSeq}");
                }

                if (h.Minted != result.Minted || h.Destroyed != result.Destroyed)
                {
                    result.Violation($"head: minted {h.Minted} and destroyed {h.Destroyed}, the replay says {result.Minted} and {result.Destroyed}");
                }

                if (h.Minted - h.Destroyed != result.Units)
                {
                    result.Violation($"conservation: the head says {h.Minted - h.Destroyed} units are held, the stacks hold {result.Units}");
                }
            }
            else if (result.Entries > 0)
            {
                result.Violation("head: missing, and the item ledger has rows");
            }
        }

        result.Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return result;
    }

    /// <summary>A row's own rules: a count, two sides, a known template and its Mk, a kind with its sides, Mk V only from a mission, a mission's one tick.</summary>
    /// <returns>Whether it can be replayed.</returns>
    private static bool RowRules(ItemAuditResult result, Dictionary<long, long> missionTick, in ItemLedgerEntry r)
    {
        var ok = true;
        if (r.Count <= 0)
        {
            result.Violation($"seq {r.Seq}: count {r.Count} is not above zero");
            ok = false;
        }

        if (r.From == r.To)
        {
            result.Violation($"seq {r.Seq}: both sides are {r.From}");
            ok = false;
        }

        // World's side names no stack and has no count after.
        if ((r.From == ItemLedger.World && (r.FromStack != 0 || r.FromAfter != 0)) || (r.To == ItemLedger.World && (r.ToStack != 0 || r.ToAfter != 0)))
        {
            result.Violation($"seq {r.Seq}: World's side names stack {(r.From == ItemLedger.World ? r.FromStack : r.ToStack)} with "
                + $"{(r.From == ItemLedger.World ? r.FromAfter : r.ToAfter)} after - World holds no stack");
        }

        if (!ItemCatalog.TryGet(r.Template, out var t))
        {
            result.Violation($"seq {r.Seq}: template {r.Template} does not exist");
            ok = false;
        }
        else if (!ItemCatalog.MkFits(in t, r.Mk))
        {
            result.Violation($"seq {r.Seq}: Mk {r.Mk} does not fit {t.Asset}");
        }

        switch (r.Kind)
        {
            case ItemMoveKind.Kill:
            case ItemMoveKind.Lair:
            case ItemMoveKind.Mission:
                if (r.From != ItemLedger.World || r.To == ItemLedger.World)
                {
                    result.Violation($"seq {r.Seq}: a {ItemMoveKind.NameOf(r.Kind)} row must go from World to a character, not {r.From} to {r.To}");
                }

                break;
            default:
                result.Violation($"seq {r.Seq}: unknown kind {r.Kind}");
                break;
        }

        if (r.Mk == 5 && r.Kind != ItemMoveKind.Mission)
        {
            result.Violation($"seq {r.Seq}: Mk V in a {ItemMoveKind.NameOf(r.Kind)} row - only a mission cache holds Mk V");
        }

        if (r.Kind == ItemMoveKind.Mission)
        {
            if (missionTick.TryGetValue(r.Ref, out var tick) && tick != r.WorldTick)
            {
                result.Violation($"seq {r.Seq}: mission {r.Ref}'s cache minted again at tick {r.WorldTick}, first at {tick}");
            }

            missionTick.TryAdd(r.Ref, r.WorldTick);
        }

        return ok;
    }

    /// <summary>One side of a row against the stack it names.</summary>
    private static void Replay(ItemAuditResult result, Dictionary<long, Replayed> stacks, long stack, long owner, int delta, int after, in ItemLedgerEntry r)
    {
        if (stack == 0)
        {
            result.Violation($"seq {r.Seq}: {owner}'s side names no stack");
            return;
        }

        if (!stacks.TryGetValue(stack, out var s))
        {
            s = new Replayed { Owner = owner, Template = r.Template, Mk = r.Mk };
            stacks[stack] = s;
            if (delta < 0)
            {
                result.Violation($"seq {r.Seq}: stack {stack} gives before anything was put in it");
            }
        }
        else if (s.Count == 0 && delta > 0)
        {
            // Emptied and destroyed earlier: an entity id the engine gave again is a new stack.
            s.Owner = owner;
            s.Template = r.Template;
            s.Mk = r.Mk;
            s.Rows = 0;
        }
        else if (s.Owner != owner || s.Template != r.Template || s.Mk != r.Mk)
        {
            result.Violation($"seq {r.Seq}: stack {stack} is {s.Owner}'s {s.Template} Mk {s.Mk}, this row says {owner}'s {r.Template} Mk {r.Mk}");
        }

        s.Count += delta;
        s.Rows++;
        s.LastSeq = r.Seq;
        if (s.Count != after)
        {
            result.Violation($"seq {r.Seq}: stack {stack}'s count after is {after}, the replay says {s.Count}");
        }

        if (s.Count is < 0 or > ItemLedger.StackMax)
        {
            result.Violation($"seq {r.Seq}: stack {stack} holds {s.Count}, outside 0..{ItemLedger.StackMax}");
        }
    }

    private static int Key(ushort template, byte mk) => (template * 8) + mk;

    private static long Get(Dictionary<int, long> map, int key) => map.TryGetValue(key, out var v) ? v : 0;

    /// <summary>The indexes, resolved once per engine.</summary>
    private static IndexSet Indexes(DatabaseEngine dbe)
    {
        var known = Volatile.Read(ref _indexes);
        if (ReferenceEquals(known?.Dbe, dbe))
        {
            return known;
        }

        var set = new IndexSet(
            dbe,
            dbe.GetIndexRef<ItemLedgerEntry, long>(e => e.Seq),
            dbe.GetIndexRef<ItemLedgerEntry, long>(e => e.From),
            dbe.GetIndexRef<ItemLedgerEntry, long>(e => e.To),
            dbe.GetIndexRef<ItemStack, long>(s => s.Owner),
            dbe.GetIndexRef<ItemLedgerHead, int>(h => h.Key),
            dbe.GetIndexRef<Wallet, long>(w => w.Owner),
            dbe.GetIndexRef<LedgerEntry, long>(e => e.Seq));
        Volatile.Write(ref _indexes, set);
        return set;
    }
}
