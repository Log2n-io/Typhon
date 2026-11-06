using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Potions drunk (crafting-catalog.md § 5.4, crafting-build.md C2; Loïc, 2026-11-05: the stim crafted, sold and drunk by players and bots), in the
/// <c>Workshop</c>'s transaction: a dose spent — the potion's condition — and its last one a <see cref="ItemMoveKind.Consume"/> row; then, once that has
/// committed, the heal: <c>Progression</c>'s, in the tick's Report phase, and the <see cref="Healed"/> everyone near sees.
/// </summary>
/// <remarks>
/// <para><b>The stim only, so far</b>: an instant heal of its potency, within what the body lacks, at most one every
/// <see cref="TatooineData.StimCooldownSec"/>. The regen draught and the tonics are refused <see cref="TatooineReplication.ConsumeBad"/> until they do
/// something.</para>
/// <para><b>Refused where it would do nothing</b>: at full health (<see cref="TatooineReplication.ConsumeFull"/>), down, or within the cooldown — a dose is
/// never spent for nothing.</para>
/// <para><b>The cooldown is the server's memory, by character, not a component on the body</b> (a deviation from crafting-build.md § 2.4's
/// <c>PlayerBuffs</c>, measured 2026-11-05): four bytes on <c>Player</c> took its cluster from 24 bodies to 23 (IL-14), for a wait of 15 s. Lost on a
/// restart, which costs a player at most one early stim; kept over a relog, the character's. The tonics, which combat must read, will need the body.</para>
/// <para><b>Healed after the economy, not in it.</b> The economy's systems read vitals, which are SingleVersion: a writer among them would have to run after
/// every reader, Missions included, which the Workshop runs after. So the Workshop pushes a <see cref="HealEvent"/> once its transaction has committed and
/// <c>Progression</c>, a writer of vitals in the Report phase, applies it in the same tick — a queue the DAG sees, and the runtime wakes Progression
/// for.</para>
/// </remarks>
public sealed partial class SimBridge
{
    private readonly List<ClientCommand<ConsumeItem>> _consumesApplied = [];

    // This pass's heals, pushed once its transaction has committed: the body, the stim's potency; and the characters who drank, for their cooldown.
    private readonly List<(EntityId Body, int Potency)> _heals = [];
    private readonly List<long> _drinkers = [];

    // When each character may drink a stim again, world ticks: the Workshop's alone, swept of the past as it grows.
    private readonly Dictionary<long, long> _stimReadyAt = [];
    private const int StimReadySweepAt = 4096;

    /// <summary>The potions drunk: produced by <c>Workshop</c>, consumed by <c>Progression</c>.</summary>
    internal EventQueue<HealEvent> HealQueue { get; set; }

    private long _stimsDrunk;
    private long _healthHealed;
    private long _pendingEmptied;
    private long _potionsEmptied;
    private long _healsDropped;

    /// <summary>Stims drunk since start.</summary>
    public long StimsDrunk => Interlocked.Read(ref _stimsDrunk);

    /// <summary>Health the stims gave since start, within what the bodies lacked.</summary>
    public long HealthHealed => Interlocked.Read(ref _healthHealed);

    /// <summary>Potions drunk to their last dose, and destroyed, since start.</summary>
    public long PotionsEmptied => Interlocked.Read(ref _potionsEmptied);

    /// <summary>Heals the queue had no room for: the dose was spent and nothing healed. 0, or the queue is too small.</summary>
    public long HealsDropped => Interlocked.Read(ref _healsDropped);

    private void ConsumeFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<ConsumeItem> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out _, out _, out _) || !tick0.TryOpen(body, out var player))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var vitals = player.Read(Player.Vitals);
        if (vitals.Down != Downed.Up || vitals.Health <= 0)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.ConsumeBad);
            return;
        }

        // A second drink in the pass that heals the first is within its cooldown too.
        if ((_stimReadyAt.TryGetValue(owner, out var ready) && worldTick < ready) || Healing(body))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.ConsumeCooldown);
            return;
        }

        if (vitals.Health >= vitals.MaxHealth)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.ConsumeFull);
            return;
        }

        var item = EntityId.FromRawValue(command.Value.Item);
        var items = ItemsOf(owner);
        var src = SlotOf(items, item);
        if (src == 0 || !items.Slots[src].Instance || items.Slots[src].Template != ItemCatalog.Stim || !TryStatsOf(tx, item, out var stats)
            || stats.Condition <= 0 || (items.Slots[src].Count > 1 && FreeSlots(items) == 0))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.ConsumeBad);
            return;
        }

        // From a crate: one out of it, a dose short already — a crate holds identical full potions, never one a dose short.
        var crate = items.Slots[src].Count > 1;
        var mk = items.Slots[src].Mk;
        stats.Condition--;
        stats.Flags &= unchecked((byte)~ItemFlags.Pristine);
        var potion = crate ? Unpack(tx, owner, items, src, item, in stats, 1, worldTick) : item;
        if (stats.Condition <= 0)
        {
            if (ItemTransfer(tx, owner, ItemLedger.World, ItemCatalog.Stim, mk, 1, ItemMoveKind.Consume, 0, worldTick, potion) != 1)
            {
                _craftAbort = true;
                _consumesApplied.Add(command);
                return;
            }

            _pendingEmptied++;
        }
        else if (!crate)
        {
            tx.OpenMut(potion).Set(ItemInstanceArch.Stats, stats);
        }

        _heals.Add((body, stats.Stat0));
        _drinkers.Add(owner);
        _craftBodies.Add(body);
        _consumesApplied.Add(command);
    }

    // Whether the body drank already in this pass: the second waits for the first's cooldown.
    private bool Healing(EntityId body)
    {
        foreach (var (b, _) in _heals)
        {
            if (b == body)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The Workshop's transaction ended: committed, its heals are pushed for <c>Progression</c>; not, every drink refused, nothing of it having happened.
    /// </summary>
    private void EndPotions(TickContext ctx, SubscriptionsCommands subs, bool committed)
    {
        if (!committed)
        {
            foreach (var c in _consumesApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.CraftFailed);
            }
        }
        else if (_heals.Count > 0)
        {
            var writer = ctx.Writer(HealQueue);
            foreach (var (body, potency) in _heals)
            {
                if (!writer.Push(new HealEvent { Body = body, Potency = potency }))
                {
                    Interlocked.Increment(ref _healsDropped);
                }
            }

            var ready = ctx.WorldTick + Math.Max(1, (long)MathF.Round(_config.StimCooldownSec * Math.Max(1, _config.TickRateHz)));
            if (_stimReadyAt.Count >= StimReadySweepAt)
            {
                SweepStimCooldowns(ctx.WorldTick);
            }

            foreach (var character in _drinkers)
            {
                _stimReadyAt[character] = ready;
            }

            Interlocked.Add(ref _potionsEmptied, _pendingEmptied);
        }

        _pendingEmptied = 0;
        _consumesApplied.Clear();
        _heals.Clear();
        _drinkers.Clear();
    }

    // The cooldowns past: a character who drank once in a long session is no entry for ever. Removed while enumerated, which a Dictionary allows.
    private void SweepStimCooldowns(long worldTick)
    {
        foreach (var (character, ready) in _stimReadyAt)
        {
            if (ready <= worldTick)
            {
                _stimReadyAt.Remove(character);
            }
        }
    }

    /// <summary>
    /// The tick's potions, through <c>Progression</c>'s transaction: each body healed by its stim's potency within what it lacks, and shown to whoever knows
    /// it. A body gone, or downed since, keeps nothing: the dose was spent.
    /// </summary>
    private void ApplyHeals(TickContext ctx)
    {
        var pending = HealQueue?.Count ?? 0;
        if (pending == 0)
        {
            return;
        }

        Span<HealEvent> heals = pending <= 64 ? stackalloc HealEvent[64] : new HealEvent[pending];
        var n = HealQueue.Drain(heals);
        var tx = ctx.Transaction;
        for (var i = 0; i < n; i++)
        {
            var (body, potency) = (heals[i].Body, heals[i].Potency);
            if (!tx.TryOpenMut(body, out var player))
            {
                continue;
            }

            var v = player.Read(Player.Vitals);
            if (v.Down != Downed.Up || v.Health <= 0)
            {
                continue;
            }

            var gain = Math.Clamp(Math.Min(potency, v.MaxHealth - v.Health), 0, ushort.MaxValue);
            v.Health += gain;
            player.Set(Player.Vitals, v);
            TatooineReplication.Replicate(in player);
            TatooineReplication.Heal(ctx, new Healed { Body = body, Amount = (ushort)gain });
            Interlocked.Increment(ref _stimsDrunk);
            Interlocked.Add(ref _healthHealed, gain);
        }
    }
}
