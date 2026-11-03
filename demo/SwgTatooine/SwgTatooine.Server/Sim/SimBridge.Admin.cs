using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SwgTatooine.Server;

/// <summary>What an operator asks of one character from the back office (the dashboard's accounts page, <c>AdminEndpoints</c>).</summary>
public enum AdminAction : byte
{
    /// <summary>
    /// Give (<see cref="AdminRequest.Delta"/> &gt; 0) or take credits, as <see cref="TransferKind.AdminGrant"/> or <see cref="TransferKind.AdminTake"/>
    /// rows.
    /// </summary>
    Credits,

    /// <summary>Set the combat level, its XP, or both (<see cref="AdminRequest.Level"/>, <see cref="AdminRequest.Xp"/>).</summary>
    Progress,

    /// <summary>
    /// Mint items into the character's grid as <see cref="ItemMoveKind.AdminGrant"/> rows; an item with stats takes <see cref="AdminRequest.Stats"/>.
    /// </summary>
    AddItem,

    /// <summary>Destroy a stack's units, or an item, worn or not, as <see cref="ItemMoveKind.AdminTake"/> rows.</summary>
    RemoveItem,

    /// <summary>Rewrite an item's stats in place: no ledger row, its owner and slot untouched.</summary>
    ItemStats,
}

/// <summary>
/// One operator request: built by the endpoint off the tick, applied by the <c>Workshop</c> inside it (<see cref="SimBridge.Admin"/>), answered when its
/// transaction has committed — or refused, having written nothing.
/// </summary>
public sealed class AdminRequest
{
    public AdminAction Action;

    /// <summary>The character's raw id.</summary>
    public long Character;

    /// <summary><see cref="AdminAction.Credits"/>: credits to give, or to take when negative.</summary>
    public long Delta;

    /// <summary><see cref="AdminAction.Progress"/>: the level to set, 1-50; 0 keeps it and takes it from <see cref="Xp"/>.</summary>
    public int Level;

    /// <summary><see cref="AdminAction.Progress"/>: the XP to set, clamped into the level's band; -1 for the band's start.</summary>
    public int Xp = -1;

    /// <summary><see cref="AdminAction.AddItem"/>: the template and Mk to mint, and how many.</summary>
    public ushort Template;

    public byte Mk;

    /// <summary><see cref="AdminAction.AddItem"/>: units to mint; <see cref="AdminAction.RemoveItem"/>: units to take from a stack, 0 for all of it.</summary>
    public int Count;

    /// <summary><see cref="AdminAction.RemoveItem"/> and <see cref="AdminAction.ItemStats"/>: the stack or item's raw id.</summary>
    public long Item;

    /// <summary>Whether <see cref="Stats"/> was given: an <see cref="AdminAction.AddItem"/> without it gets <see cref="SimBridge.AdminDefaults"/>.</summary>
    public bool HasStats;

    /// <summary>The stat lines, condition and life factor to mint or write; crafter, serial and repairs are not the operator's to set.</summary>
    public ItemInstance Stats;

    internal readonly TaskCompletionSource<AdminOutcome> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 0 queued, 1 taken by a Workshop pass, 2 given up by its caller: whichever comes first wins, so a request answered "no tick answered" is never applied
    // after all — retried, it would have been applied twice (review, 2026-11-03).
    private int _state;

    /// <summary>The Workshop takes it to apply; false when its caller gave up on it first.</summary>
    internal bool TryTake() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    /// <summary>Its caller gives up waiting; false when a Workshop pass has it already, and its answer is coming.</summary>
    internal bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

    // What it did, said once its transaction commits.
    internal string Applied;
}

/// <summary>An operator request's answer: done and what was done, or refused and why.</summary>
public readonly record struct AdminOutcome(bool Ok, string Message);

/// <summary>
/// The back office's writes (accounts.md § 2): credits, level and XP, items given, taken or rewritten — for any character, online or not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Applied by the <c>Workshop</c>, in its side transaction</b>: it is the last serial writer of credits and items in the chain
/// <c>Missions → Ledger → Loot → Market → Workshop</c>, and it already computes a body's gear from what it wears. So an operator's write goes through the one
/// code path every other write takes — <see cref="Transfer"/>, <see cref="ItemTransfer"/>, <see cref="MintInstance"/> — and both audits replay it like any
/// other
/// row: credits and items given or taken are <c>admingrant</c> and <c>admintake</c> rows, never a balance overwritten.
/// </para>
/// <para>
/// <b>Two writes are not ledger moves</b>, and write no row: an item's stats rewritten in place, and a character's level and XP. Level and XP go through the
/// same side transaction, declared a side write of <see cref="CharacterProgress"/>; the body, when online, is brought to them exactly — down as well as up —
/// by <c>Progression</c>, the only writer of a body's level in the tick's last phases.
/// </para>
/// <para>A request is validated whole before its first write, and refused with its reason when it fails. One that passes is answered when the transaction
/// commits, or refused with every other of that tick when it does not.</para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>The most credits one request may give or take: a mistyped amount is refused rather than minted.</summary>
    public const long MaxAdminCredits = 1_000_000_000;

    private readonly ConcurrentQueue<AdminRequest> _admin = new();
    private readonly List<AdminRequest> _adminApplied = [];
    private readonly Dictionary<EntityId, EntityId> _adminBodies = [];
    private readonly HashSet<EntityId> _adminWallets = [];
    private readonly HashSet<EntityId> _adminItems = [];
    private readonly List<(EntityId Character, CharacterProgress Progress)> _adminProgress = [];
    private bool _adminEconomy;
    private ushort _adminCharacterRouting;

    /// <summary>
    /// Queues an operator request for the next <c>Workshop</c> pass. The task completes when it has committed, or with its refusal; a server that has stopped
    /// ticking never completes it, so the caller bounds its wait.
    /// </summary>
    public Task<AdminOutcome> Admin(AdminRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _admin.Enqueue(request);
        return request.Done.Task;
    }

    /// <summary>
    /// The stats an item with stats is minted with when the operator gives none: the vendor's standard issue at Mk I; else its schematic's lines as a
    /// <i>Good</i> assembly (60 %) of perfect inputs would set them, no experimentation and no add-on, with a life factor of 1 000.
    /// </summary>
    public static ItemInstance AdminDefaults(ushort template, byte mk)
    {
        if (mk <= 1 && Equipment.TryStandard(template, out var issue))
        {
            return StandardStats(in issue);
        }

        var schematic = SchematicFor(template);
        if (schematic == null || !schematic.MakesInstance)
        {
            return new ItemInstance { Condition = 1, MaxCondition = 1, LifeMilli = 1000 };
        }

        const int good = Crafting.Scale * 6 / 10;
        var state = new CraftState
        {
            Output = template,
            Mk = mk,
            Q0 = Crafting.Scale,
            Q1 = Crafting.Scale,
            Q2 = Crafting.Scale,
            Q3 = Crafting.Scale,
            F0 = good,
            F1 = good,
            F2 = good,
            F3 = good,
        };
        var r = ResultOf(schematic, in state);
        if (r.LifeMilli > 0 && r.LifeMilli != 1000)
        {
            r.MaxCondition = (int)((long)r.MaxCondition * 1000 / r.LifeMilli);
        }

        r.LifeMilli = 1000;
        r.Condition = r.MaxCondition;
        r.Crafter = 0;
        r.Serial = 0;
        return r;
    }

    // A template's schematic: its own, or for an armour piece without one (the composite set), the one making the piece of its slot.
    private static Schematic SchematicFor(ushort template)
    {
        if (Schematics.TryGet(template, out var s) && s.Output == template)
        {
            return s;
        }

        var slot = Equipment.SlotOf(template);
        if (slot == 0)
        {
            return null;
        }

        foreach (var candidate in Schematics.Every)
        {
            if (candidate.Kind == SchematicKind.Armor && Equipment.SlotOf(candidate.Output) == slot)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Whether a request waits: the <c>Workshop</c> runs for it on a tick with nothing else to do.</summary>
    private bool AdminPending => !_admin.IsEmpty;

    /// <summary>
    /// Every queued request, inside the <c>Workshop</c>'s side transaction, after its commands and before the gear: a worn item rewritten or taken is in the
    /// gear computed this pass.
    /// </summary>
    private void AdminTick(Transaction tx, long worldTick)
    {
        _adminApplied.Clear();
        _adminWallets.Clear();
        _adminItems.Clear();
        _adminProgress.Clear();
        _adminEconomy = false;
        if (_admin.IsEmpty)
        {
            return;
        }

        TatooineReplication.FillCharacterBodies(_adminBodies);
        while (_admin.TryDequeue(out var request))
        {
            if (!request.TryTake())
            {
                continue;   // given up by its caller: never applied
            }

            var refused = Apply(tx, request, worldTick);
            if (refused != null)
            {
                request.Done.TrySetResult(new AdminOutcome(false, refused));
            }
            else
            {
                _adminApplied.Add(request);
            }
        }
    }

    /// <summary>
    /// After the <c>Workshop</c>'s commit: each applied request answered, its bodies told — the wallet and items revisions, the level through
    /// <c>Progression</c>. Not committed, every one of them is refused: nothing it wrote is left.
    /// </summary>
    private void EndAdmin(TickContext ctx, bool committed)
    {
        foreach (var request in _adminApplied)
        {
            request.Done.TrySetResult(committed
                ? new AdminOutcome(true, request.Applied)
                : new AdminOutcome(false, "the tick's transaction did not commit: nothing changed, try again"));
        }

        if (committed)
        {
            var tick0 = ctx.Transaction;
            foreach (var body in _adminWallets)
            {
                BumpWallet(tick0, body);
            }

            foreach (var body in _adminItems)
            {
                BumpItems(tick0, body);
            }

            if (_adminProgress.Count > 0)
            {
                var writer = ctx.Writer(ProgressQueue);
                foreach (var (character, progress) in _adminProgress)
                {
                    if (_adminBodies.TryGetValue(character, out var body))
                    {
                        writer.Push(new ProgressRefresh { Body = body, Progress = progress, Exact = 1 });
                    }
                }
            }
        }

        _adminApplied.Clear();
        _adminWallets.Clear();
        _adminItems.Clear();
        _adminProgress.Clear();
    }

    /// <returns>Null when applied; else why it was refused, having written nothing.</returns>
    private string Apply(Transaction tx, AdminRequest r, long worldTick)
    {
        var key = r.Character;
        var character = EntityId.FromRawValue((ulong)key);
        _adminCharacterRouting = _adminCharacterRouting != 0 ? _adminCharacterRouting : Dbe.ArchetypeIdOf<CharacterArch>();
        if (key == 0 || character.ArchetypeId != _adminCharacterRouting || !tx.TryOpen(character, out _))
        {
            return "no such character";
        }

        _adminBodies.TryGetValue(character, out var body);
        return r.Action switch
        {
            AdminAction.Credits => Credits(tx, r, key, body, worldTick),
            AdminAction.Progress => Progress(tx, r, character),
            AdminAction.AddItem => AddItem(tx, r, key, body, worldTick),
            AdminAction.RemoveItem => RemoveItem(tx, r, character, body, worldTick),
            AdminAction.ItemStats => ItemStats(tx, r, character, body),
            _ => "unknown action",
        };
    }

    private string Credits(Transaction tx, AdminRequest r, long key, EntityId body, long worldTick)
    {
        if (r.Delta == 0 || Math.Abs(r.Delta) > MaxAdminCredits)
        {
            return FormattableString.Invariant($"an amount from 1 to {MaxAdminCredits:N0} credits, either way");
        }

        EnsureEconomy(tx);
        _adminEconomy = true;
        if (!_walletOf.ContainsKey(key))
        {
            return "this character has no wallet yet: it opens on its first login";
        }

        var moved = r.Delta > 0
            ? Transfer(tx, EconomyLedger.World, key, r.Delta, TransferKind.AdminGrant, 0, worldTick)
            : Transfer(tx, key, EconomyLedger.World, -r.Delta, TransferKind.AdminTake, 0, worldTick);
        if (!moved)
        {
            return FormattableString.Invariant($"it holds {BalanceOf(tx, key):N0} credits");
        }

        r.Applied = FormattableString.Invariant($"{(r.Delta > 0 ? "gave" : "took")} {Math.Abs(r.Delta):N0} credits");
        Told(_adminWallets, body);
        return null;
    }

    private string Progress(Transaction tx, AdminRequest r, EntityId character)
    {
        int level;
        int xp;
        if (r.Level != 0)
        {
            if (r.Level is < 1 or > Progression.MaxLevel)
            {
                return FormattableString.Invariant($"a level from 1 to {Progression.MaxLevel}");
            }

            level = r.Level;
            var low = Progression.XpAt(level);
            var high = level == Progression.MaxLevel ? Progression.MaxXp : Progression.XpAt(level + 1) - 1;
            xp = r.Xp < 0 ? low : Math.Clamp(r.Xp, low, high);
        }
        else if (r.Xp >= 0)
        {
            xp = Math.Min(r.Xp, Progression.MaxXp);
            level = LevelAt(xp);
        }
        else
        {
            return "a level, an XP, or both";
        }

        if (!tx.TryOpenMut(character, out var entity))
        {
            return "no such character";
        }

        var p = entity.Read(CharacterArch.Progress);
        p.Level = (byte)level;
        p.Xp = xp;
        entity.Set(CharacterArch.Progress, p);
        _adminProgress.Add((character, p));
        r.Applied = FormattableString.Invariant($"set CL {level} at {xp:N0} XP");
        return null;
    }

    /// <summary>The level a total XP reaches: the highest whose band starts at or below it.</summary>
    internal static int LevelAt(int xp)
    {
        for (var level = Progression.MaxLevel; level > 1; level--)
        {
            if (xp >= Progression.XpAt(level))
            {
                return level;
            }
        }

        return 1;
    }

    private string AddItem(Transaction tx, AdminRequest r, long key, EntityId body, long worldTick)
    {
        if (!ItemCatalog.TryGet(r.Template, out var t))
        {
            return FormattableString.Invariant($"no template {r.Template}");
        }

        if (!ItemCatalog.MkFits(in t, r.Mk))
        {
            return t.HasMk ? $"{t.Name} takes a Mk from 1 to 5" : $"{t.Name} has no Mk: give 0";
        }

        if (r.Count < 1 || r.Count > ItemLedger.Slots * ItemLedger.StackMax)
        {
            return FormattableString.Invariant($"a count from 1 to {ItemLedger.Slots * ItemLedger.StackMax}");
        }

        if (!HasWallet(tx, key))
        {
            return "this character has no inventory yet: it opens with its wallet, on its first login";
        }

        EnsureItems(tx);
        int placed;
        if (ItemCatalog.HasStats(r.Template))
        {
            var stats = r.HasStats ? r.Stats : AdminDefaults(r.Template, r.Mk);
            var invalid = CheckStats(ref stats);
            if (invalid != null)
            {
                return invalid;
            }

            placed = 0;
            while (placed < r.Count && !MintInstance(tx, key, r.Template, r.Mk, in stats, ItemMoveKind.AdminGrant, 0, worldTick).IsNull)
            {
                placed++;
            }
        }
        else
        {
            placed = ItemTransfer(tx, ItemLedger.World, key, r.Template, r.Mk, r.Count, ItemMoveKind.AdminGrant, 0, worldTick);
        }

        if (placed == 0)
        {
            return "the inventory has no room for it";
        }

        r.Applied = $"gave {placed} {t.Name}{MkText(in t, r.Mk)}{(placed < r.Count ? $" ({r.Count - placed} did not fit)" : "")}";
        Told(_adminItems, body);
        return null;
    }

    private string RemoveItem(Transaction tx, AdminRequest r, EntityId character, EntityId body, long worldTick)
    {
        var key = (long)character.RawValue;
        if (!HasWallet(tx, key))
        {
            return "this character has no inventory";
        }

        EnsureItems(tx);
        var items = ItemsOf(key);
        var item = EntityId.FromRawValue((ulong)r.Item);
        var s = SlotOf(items, item);
        if (s != 0)
        {
            var slot = items.Slots[s];
            ItemCatalog.TryGet(slot.Template, out var t);
            // An item with stats goes whole: one, or a crate's count.
            var n = slot.Instance ? slot.Count : r.Count <= 0 ? slot.Count : Math.Min(r.Count, slot.Count);
            if (ItemTransfer(tx, key, ItemLedger.World, slot.Template, slot.Mk, n, ItemMoveKind.AdminTake, 0, worldTick, item) == 0)
            {
                return "the item ledger refused it";
            }

            r.Applied = $"took {n} {t.Name}{MkText(in t, slot.Mk)}";
            Told(_adminItems, body);
            return null;
        }

        var k = WornSlot(items, item);
        if (k < 0)
        {
            return "not in this character's inventory";
        }

        var worn = items.Equipped[k];
        ItemCatalog.TryGet(worn.Template, out var w);
        if (!DestroyWorn(tx, key, k, ItemMoveKind.AdminTake, 0, worldTick))
        {
            return "the item ledger refused it";
        }

        r.Applied = $"took {w.Name}{MkText(in w, worn.Mk)}, worn";
        Told(_adminItems, body);
        GearDue(character, body);
        return null;
    }

    private string ItemStats(Transaction tx, AdminRequest r, EntityId character, EntityId body)
    {
        var key = (long)character.RawValue;
        EnsureItems(tx);
        var items = ItemsOf(key);
        var item = EntityId.FromRawValue((ulong)r.Item);
        var s = SlotOf(items, item);
        var k = s == 0 ? WornSlot(items, item) : -1;
        if (s == 0 && k < 0)
        {
            return "not in this character's inventory";
        }

        var template = s != 0 ? items.Slots[s].Template : items.Equipped[k].Template;
        if (!IsInstance(item) || !tx.TryOpenMut(item, out var entity))
        {
            return "a stack has no stats: only an item with stats does";
        }

        // A crate's items share one record and must stay pristine (crafting-catalog.md § 11.2): unpacked first, one is rewritten.
        if (s != 0 && items.Slots[s].Count != 1)
        {
            return "a crate: unpack the item to rewrite first";
        }

        var stats = r.Stats;
        var invalid = CheckStats(ref stats);
        if (invalid != null)
        {
            return invalid;
        }

        // Who made it, its serial and its repairs are its history, not the operator's to rewrite. Broken is what its condition says: at 0 an item that
        // wears is broken, above it not; and one below its max is not pristine any more.
        var current = entity.Read(ItemInstanceArch.Stats);
        stats.Crafter = current.Crafter;
        stats.Serial = current.Serial;
        stats.Repairs = current.Repairs;
        var flags = stats.Condition > 0 || stats.MaxCondition == 0 ? current.Flags & ~ItemFlags.Broken : current.Flags | ItemFlags.Broken;
        stats.Flags = (byte)(stats.Condition < stats.MaxCondition ? flags & ~ItemFlags.Pristine : flags);
        entity.Set(ItemInstanceArch.Stats, stats);
        ItemCatalog.TryGet(template, out var t);
        r.Applied = $"rewrote {t.Name}'s stats{(k >= 0 ? ", worn" : "")}";
        Told(_adminItems, body);
        if (k >= 0)
        {
            GearDue(character, body);
        }

        return null;
    }

    // Stats an operator gave, made whole: nothing negative, a condition within its maximum, a life factor. A maximum of 0 is legal: a part has no condition.
    private static string CheckStats(ref ItemInstance s)
    {
        if (s.Stat0 < 0 || s.Stat1 < 0 || s.Stat2 < 0 || s.Stat3 < 0 || s.Condition < 0 || s.MaxCondition < 0 || s.LifeMilli < 0)
        {
            return "no stat may be negative";
        }

        s.Condition = Math.Min(s.Condition, s.MaxCondition);
        s.LifeMilli = s.LifeMilli == 0 ? (short)1000 : s.LifeMilli;
        return null;
    }

    /// <summary>The equip slot index (<c>slot - EquipSlot.First</c>) wearing <paramref name="item"/>; -1 when it is not worn.</summary>
    private static int WornSlot(OwnerItems items, EntityId item)
    {
        for (var k = 0; k < EquipSlot.Count; k++)
        {
            if (!item.IsNull && items.Equipped[k].Stack == item)
            {
                return k;
            }
        }

        return -1;
    }

    // A worn item changed: the body's gear, computed this very pass from what the transaction leaves worn.
    private void GearDue(EntityId character, EntityId body)
    {
        if (!body.IsNull)
        {
            _gearScratch.Add((character, body));
        }
    }

    private static void Told(HashSet<EntityId> bodies, EntityId body)
    {
        if (!body.IsNull)
        {
            bodies.Add(body);
        }
    }

    private static string MkText(in ItemTemplate t, byte mk) => t.HasMk ? " Mk " + mk.ToString(CultureInfo.InvariantCulture) : "";
}
