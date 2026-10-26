using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Loot (inventory-loot.md § 3.4, § 4): every kill of a creature or a camp foe, and every wild lair broken, drops into the inventory of the character whose
/// body landed the last blow — when that body is within reach, in the victim's realm, and its character has a wallet.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>Loot</c> system, the only consumer of the kill queue the combat drain fills</b>, after the <c>Ledger</c> (a wallet it opened this tick is then
/// visible, and both write <see cref="PlayerState"/>) and after the mission desk (both write items). A mission lair's death is not looted here: its cache is
/// minted by the desk with the mission's reward, in the transaction that ends the mission.
/// </para>
/// <para>
/// The stacks, rows and head go through one side transaction per tick with work, committed once; the items revision of each body whose inventory moved, or
/// whose drop was lost, through the tick's own after that commit.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    // Per tick with kills: every character with a body online, by its body. A body missing from it is the crowd's.
    private readonly Dictionary<EntityId, EntityId> _lootCharacterOf = [];
    private readonly HashSet<EntityId> _lootItemsMoved = [];
    private readonly List<KillEvent> _stagedScratch = [];
    private readonly List<(EntityId Character, EntityId Killer, KillEvent Kill)> _lootAccepted = [];

    private long _lootRefusedCrowd;
    private long _lootRefusedWallet;
    private long _lootRefusedRealm;
    private long _lootRefusedReach;
    private long _lootCommits;

    /// <summary>Kills looted by nobody because their killer was no character's body: the crowd's, or no body at all.</summary>
    public long LootRefusedCrowd => Interlocked.Read(ref _lootRefusedCrowd);

    /// <summary>Kills looted by nobody because the killer's character had no wallet yet.</summary>
    public long LootRefusedWallet => Interlocked.Read(ref _lootRefusedWallet);

    /// <summary>Kills looted by nobody because the killer stood in another realm.</summary>
    public long LootRefusedRealm => Interlocked.Read(ref _lootRefusedRealm);

    /// <summary>Kills looted by nobody because the killer stood farther than <see cref="LootTables.LootReachM"/>.</summary>
    public long LootRefusedReach => Interlocked.Read(ref _lootRefusedReach);

    /// <summary>Every kill looted by nobody, whatever the reason.</summary>
    public long LootRefusedTotal => LootRefusedCrowd + LootRefusedWallet + LootRefusedRealm + LootRefusedReach;

    // Kills staged by the checks (StageKill), looted by the next tick's Loot as if the combat drain had pushed them.
    private readonly ConcurrentQueue<KillEvent> _stagedKills = new();

    // Transfers staged by the checks (StageTransfer), applied through ItemTransfer in the next tick's Loot transaction, before its kills.
    private readonly ConcurrentQueue<(long From, long To, ushort Template, byte Mk, int Count, byte Kind, long Ref)> _stagedTransfers = new();

    /// <summary>Kills the queue could not hold, and so looted by nobody.</summary>
    public long LootQueueOverflow => KillLootQueue?.OverflowCount ?? 0;

    /// <summary>Ticks on which the <c>Loot</c> system committed item writes: each one a durable write (<c>CausalityChecks.AC2</c>).</summary>
    public long LootCommits => Interlocked.Read(ref _lootCommits);

    /// <summary>A kill for the next tick's <c>Loot</c> to loot, as if the combat drain had pushed it: the checks' seam onto the real consumer path.</summary>
    internal void StageKill(in KillEvent kill) => _stagedKills.Enqueue(kill);

    /// <summary>
    /// A transfer for the next tick's <c>Loot</c> transaction to apply through <see cref="ItemTransfer"/>: the checks' way to fill an inventory, and to
    /// exercise a destroy and a move, which no system makes yet.
    /// </summary>
    internal void StageTransfer(long from, long to, ushort template, byte mk, int count, byte kind, long reference)
        => _stagedTransfers.Enqueue((from, to, template, mk, count, kind, reference));

    /// <summary>
    /// The <c>Loot</c> system: this tick's kills, drained from <see cref="KillLootQueue"/>, each looted in one side transaction committed once.
    /// </summary>
    /// <remarks>
    /// <b>A mission lair's own death, broken by its holder, is skipped</b>: its cache is minted by the desk with the mission's reward (inventory-loot.md
    /// § 3.4). Broken by a stranger it is an ordinary lair; its defenders are ordinary kills (character-level.md § 2).
    /// </remarks>
    public void LootTick(TickContext ctx)
    {
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var pending = KillLootQueue?.Count ?? 0;
        if (pending == 0 && _stagedKills.IsEmpty && _stagedTransfers.IsEmpty)
        {
            return;
        }

        Span<KillEvent> kills = pending <= 64 ? stackalloc KillEvent[64] : new KillEvent[pending];
        var n = pending > 0 ? KillLootQueue.Drain(kills) : 0;
        _stagedScratch.Clear();
        while (_stagedKills.TryDequeue(out var s))
        {
            _stagedScratch.Add(s);
        }

        // Who gets what, first: a transaction is opened only when a kill passes, so a tick of the crowd's kills alone writes and commits nothing.
        var tick0 = ctx.Transaction;
        var worldTick = ctx.WorldTick;
        TatooineReplication.FillBodyCharacters(_lootCharacterOf);
        _lootAccepted.Clear();
        for (var i = 0; i < n; i++)
        {
            Gate(tick0, in kills[i]);
        }

        foreach (var kill in _stagedScratch)
        {
            Gate(tick0, in kill);
        }

        if (_lootAccepted.Count == 0 && _stagedTransfers.IsEmpty)
        {
            return;
        }

        using var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
        while (_stagedTransfers.TryDequeue(out var t))
        {
            ItemTransfer(tx, t.From, t.To, t.Template, t.Mk, t.Count, t.Kind, t.Ref, worldTick);
        }

        foreach (var (character, killer, kill) in _lootAccepted)
        {
            var lair = kill.VictimKind == KillVictimKind.Lair;
            var source = lair ? DropSource.WildCache : DropSource.Kill;
            var drop = LootTables.Roll(kill.Species, kill.VictimLevel, source, kill.Victim.RawValue, worldTick);
            PlaceDrop(tx, character, in drop, lair ? ItemMoveKind.Lair : ItemMoveKind.Kill, source, (long)kill.Victim.RawValue, worldTick);
            _lootItemsMoved.Add(killer);
        }

        FlushItemHead(tx);
        EndLoot(tick0, tx.Commit());
    }

    /// <summary>
    /// Whether a kill drops, and to whom: only to a character's body, with a wallet, in the victim's realm, within <see cref="LootTables.LootReachM"/>; each
    /// refusal counted by its reason. A mission lair's own death drops nothing here when its holder broke it — the desk mints that cache with the reward —
    /// nor when its holder cannot be told; broken by a stranger, it drops the ordinary wild cache to the stranger, as it pays the stranger lair XP
    /// (character-level.md, <see cref="IsStranger"/>).
    /// </summary>
    private void Gate(Transaction tick0, in KillEvent kill)
    {
        var killer = kill.Killer;
        if (killer.IsNull || !_lootCharacterOf.TryGetValue(killer, out var character))
        {
            Interlocked.Increment(ref _lootRefusedCrowd);
            return;
        }

        if (kill.VictimKind == KillVictimKind.Lair && !kill.Mission.IsNull && !IsStranger(tick0, in kill, character))
        {
            return;
        }

        if (!HasWallet(tick0, (long)character.RawValue))
        {
            Interlocked.Increment(ref _lootRefusedWallet);
            return;
        }

        if (!tick0.TryOpen(killer, out var body))
        {
            Interlocked.Increment(ref _lootRefusedCrowd);
            return;
        }

        if (body.Read(Player.Realm).Value != kill.Realm)
        {
            Interlocked.Increment(ref _lootRefusedRealm);
            return;
        }

        var at = body.Read(Player.Bounds);
        if (Distance2(at.X, at.Z, kill.X, kill.Z) > LootTables.LootReachM * LootTables.LootReachM)
        {
            Interlocked.Increment(ref _lootRefusedReach);
            return;
        }

        _lootAccepted.Add((character, killer, kill));
    }

    /// <summary>The <c>Loot</c> transaction ended: counted and its bodies told, or — not committed — the items read again and the drops lost with it.</summary>
    private void EndLoot(Transaction tick0, bool committed)
    {
        var wrote = _itemsDirty;
        if (EndItems(committed))
        {
            if (wrote)
            {
                Interlocked.Increment(ref _lootCommits);
            }

            foreach (var body in _lootItemsMoved)
            {
                BumpItems(tick0, body);
            }
        }

        _lootItemsMoved.Clear();
    }
}
