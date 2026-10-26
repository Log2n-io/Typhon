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

    /// <summary>Kills the queue could not hold, and so looted by nobody.</summary>
    public long LootQueueOverflow => KillLootQueue?.OverflowCount ?? 0;

    /// <summary>Ticks on which the <c>Loot</c> system committed item writes: each one a durable write (<c>CausalityChecks.AC2</c>).</summary>
    public long LootCommits => Interlocked.Read(ref _lootCommits);

    /// <summary>A kill for the next tick's <c>Loot</c> to loot, as if the combat drain had pushed it: the checks' seam onto the real consumer path.</summary>
    internal void StageKill(in KillEvent kill) => _stagedKills.Enqueue(kill);

    /// <summary>
    /// The <c>Loot</c> system: this tick's kills, drained from <see cref="KillLootQueue"/>, each looted in one side transaction committed once.
    /// </summary>
    /// <remarks>
    /// <b>A mission lair's own death is skipped</b>: its cache is minted by the desk with the mission's reward (inventory-loot.md § 3.4). Its defenders are
    /// ordinary kills (character-level.md § 2).
    /// </remarks>
    public void LootTick(TickContext ctx)
    {
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var pending = KillLootQueue?.Count ?? 0;
        if (pending == 0 && _stagedKills.IsEmpty)
        {
            return;
        }

        Span<KillEvent> kills = pending <= 64 ? stackalloc KillEvent[64] : new KillEvent[pending];
        var n = pending > 0 ? KillLootQueue.Drain(kills) : 0;
        var staged = new List<KillEvent>();
        while (_stagedKills.TryDequeue(out var s))
        {
            staged.Add(s);
        }

        var tick0 = ctx.Transaction;
        var worldTick = ctx.WorldTick;
        TatooineReplication.FillBodyCharacters(_lootCharacterOf);
        using var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
        for (var i = 0; i < n; i++)
        {
            LootOne(tx, tick0, in kills[i], worldTick);
        }

        foreach (var kill in staged)
        {
            LootOne(tx, tick0, in kill, worldTick);
        }

        EndLoot(tick0, tx.Commit());
    }

    private void LootOne(Transaction tx, Transaction tick0, in KillEvent kill, long worldTick)
    {
        var lair = kill.VictimKind == KillVictimKind.Lair;
        if (lair && !kill.Mission.IsNull)
        {
            return;
        }

        LootKill(tx, tick0, kill.Killer, kill.Victim, lair, kill.VictimLevel, kill.Species, kill.X, kill.Z, kill.Realm, worldTick);
    }

    /// <summary>
    /// One kill, looted: who gets it, then the roll, then each line placed. Called by the <c>Loot</c> system in its side transaction.
    /// </summary>
    /// <returns>Whether a drop was placed (whole or in part) or lost to a full inventory: the killer's body is then told.</returns>
    private bool LootKill(Transaction tx, Transaction tick0, EntityId killer, EntityId victim, bool lair, int level, int species, float x, float z, int realm,
        long worldTick)
    {
        if (killer.IsNull || !_lootCharacterOf.TryGetValue(killer, out var character))
        {
            Interlocked.Increment(ref _lootRefusedCrowd);
            return false;
        }

        if (!_walletOf.ContainsKey((long)character.RawValue))
        {
            Interlocked.Increment(ref _lootRefusedWallet);
            return false;
        }

        if (!tick0.TryOpen(killer, out var body))
        {
            Interlocked.Increment(ref _lootRefusedCrowd);
            return false;
        }

        if (body.Read(Player.Realm).Value != realm)
        {
            Interlocked.Increment(ref _lootRefusedRealm);
            return false;
        }

        var at = body.Read(Player.Bounds);
        if (Distance2(at.X, at.Z, x, z) > LootTables.LootReachM * LootTables.LootReachM)
        {
            Interlocked.Increment(ref _lootRefusedReach);
            return false;
        }

        var source = lair ? DropSource.WildCache : DropSource.Kill;
        var drop = LootTables.Roll((byte)species, level, source, victim.RawValue, worldTick);
        PlaceDrop(tx, character, in drop, lair ? ItemMoveKind.Lair : ItemMoveKind.Kill, source, (long)victim.RawValue, worldTick);
        _lootItemsMoved.Add(killer);
        return true;
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
