using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// The <c>Workshop</c> (crafting-build.md § 1 rule 1, C1): equip and unequip, and the body's cache of what its character wears (<see cref="PlayerGear"/>).
/// C2 adds crafting, C3 repair and salvage, C4 refine and crates — every one a serial writer of items after <c>Market</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Equipping moves an item between the grid and slots 101-107 of its owner</b> (§ 2.2): its <see cref="ItemStack.Slot"/>, nothing else. The owner does
/// not change, so no ledger row is written and the audit's replay is untouched; the audit checks instead that a worn item sits in its template's slot.
/// </para>
/// <para>
/// <b>The combat path never reads an item</b> (§ 1 rule 3). The body carries the numbers in <see cref="PlayerGear"/>, written here on a bind (the body is new and holds the fist) and after every equip or unequip.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    // Bodies whose gear is to be written from what their character wears: bound this tick (from the Ledger's drain), or re-equipped.
    private readonly List<(EntityId Character, EntityId Body)> _gearDue = [];
    private readonly List<(EntityId Character, EntityId Body)> _gearScratch = [];
    private readonly List<(EntityId Body, PlayerGear Gear)> _gearComputed = [];
    private readonly List<ClientCommand<Equip>> _equipsApplied = [];
    private readonly List<ClientCommand<Unequip>> _unequipsApplied = [];
    private readonly long[] _workshopRefusedBy = new long[256];
    private long _equips;
    private long _unequips;
    private long _gearWrites;
    private long _workshopCommitsFailed;
    private long _workshopAborted;
    private bool _failNextWorkshop;

    /// <summary>The checks' failure injection: the Workshop's next pass that writes is aborted, as if one of its writes had failed.</summary>
    internal void FailNextWorkshopWriteForChecks() => Volatile.Write(ref _failNextWorkshop, true);

    /// <summary>Workshop passes aborted: a command's write failed after its checks, so the whole pass was refused.</summary>
    public long WorkshopAborted => Interlocked.Read(ref _workshopAborted);

    /// <summary>Workshop passes whose transaction did not commit, aborts included.</summary>
    public long WorkshopCommitsFailed => Interlocked.Read(ref _workshopCommitsFailed);

    /// <summary>Equips applied since start.</summary>
    public long Equips => Interlocked.Read(ref _equips);

    /// <summary>Unequips applied since start.</summary>
    public long Unequips => Interlocked.Read(ref _unequips);

    /// <summary>Bodies whose gear was written from their character's equipped items since start.</summary>
    public long GearWrites => Interlocked.Read(ref _gearWrites);

    /// <summary>Workshop commands refused with <paramref name="reason"/> since start.</summary>
    public long WorkshopRefused(byte reason) => Interlocked.Read(ref _workshopRefusedBy[reason]);

    /// <summary>A body bound this tick: its gear is written from its character's on the next <c>Workshop</c> pass. Called by the <c>Ledger</c>, which drains the binds.</summary>
    private void GearDueOnBind(EntityId character, EntityId body) => _gearDue.Add((character, body));

    // Characters whose worn items another system changed (the welcome pack): their bodies, if online, are found on the next Workshop pass.
    private readonly List<long> _gearDueCharacters = [];
    private readonly Dictionary<EntityId, EntityId> _gearBodies = [];

    /// <summary>
    /// Puts a just-minted instance on, in the transaction that minted it: from its grid slot into its template's equip slot, which is free on a new
    /// character. Its body's gear is due on the next <c>Workshop</c> pass. A failed commit drops it with the mint.
    /// </summary>
    private void WearOnGrant(Transaction tx, long character, EntityId item)
    {
        var items = ItemsOf(character);
        var src = SlotOf(items, item);
        var slot = src == 0 ? (byte)0 : Equipment.SlotOf(items.Slots[src].Template);
        if (slot == 0 || items.Equipped[slot - EquipSlot.First].Count != 0)
        {
            return;
        }

        SetSlot(tx, item, slot);
        items.Equipped[slot - EquipSlot.First] = items.Slots[src];
        items.Slots[src] = default;
        _gearDueCharacters.Add(character);
    }

    /// <summary>
    /// The <c>Workshop</c> system: this tick's unequips then equips, in arrival order, in one side transaction for the items; then the gear of every body
    /// due, through the tick's own.
    /// </summary>
    public void WorkshopTick(TickContext ctx)
    {
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var subs = ctx.Subscriptions;
        var unequips = subs == null ? default : subs.Commands<Unequip>();
        var equips = subs == null ? default : subs.Commands<Equip>();
        var starts = subs == null ? default : subs.Commands<CraftStart>();
        var experiments = subs == null ? default : subs.Commands<CraftExperiment>();
        var enhances = subs == null ? default : subs.Commands<CraftEnhance>();
        var finishes = subs == null ? default : subs.Commands<CraftFinish>();
        var repairs = subs == null ? default : subs.Commands<RepairItem>();
        var salvages = subs == null ? default : subs.Commands<SalvageItem>();
        var refines = subs == null ? default : subs.Commands<RefineItems>();
        var packs = subs == null ? default : subs.Commands<PackItem>();
        var unpacks = subs == null ? default : subs.Commands<UnpackItem>();
        var moves = subs == null ? default : subs.Commands<MoveItem>();
        var discards = subs == null ? default : subs.Commands<DiscardItem>();
        var consumes = subs == null ? default : subs.Commands<ConsumeItem>();
        var sorts = subs == null ? default : subs.Commands<SortItems>();
        var places = subs == null ? default : subs.Commands<PlaceInstallation>();
        var collects = subs == null ? default : subs.Commands<CollectInstallation>();
        var pays = subs == null ? default : subs.Commands<PayUpkeep>();
        var pickups = subs == null ? default : subs.Commands<PickUpInstallation>();
        var installing = places.Count + collects.Count + pays.Count + pickups.Count;
        var waypointCreates = subs == null ? default : subs.Commands<CreateWaypoint>();
        var waypointDeletes = subs == null ? default : subs.Commands<DeleteWaypoint>();
        var waypointActives = subs == null ? default : subs.Commands<SetActiveWaypoint>();
        var waypointing = waypointCreates.Count + waypointDeletes.Count + waypointActives.Count;
        var mailing = subs == null ? 0 : subs.Commands<ReadMail>().Count + subs.Commands<DeleteMail>().Count + subs.Commands<TellPlayer>().Count;
        var crafting = starts.Count + experiments.Count + enhances.Count + finishes.Count + repairs.Count + salvages.Count + refines.Count + packs.Count
            + unpacks.Count + moves.Count + discards.Count + consumes.Count + sorts.Count + installing + waypointing;
        var flushDue = ctx.TickNumber % Math.Max(1, _config.TickRateHz) == 0;
        PublishMailTick(ctx.WorldTick);
        if (unequips.Count + equips.Count + crafting + mailing == 0 && _gearDue.Count == 0 && _gearDueCharacters.Count == 0 && !WearPending(flushDue)
            && !AdminPending && !InstallationsDue(ctx.TickNumber) && !MailPending)
        {
            Interlocked.Exchange(ref _worldTickNow, ctx.WorldTick + InstallClockOffset);
            return;
        }

        // A character whose worn items changed elsewhere: its body now, if it is online; one offline gets its gear when it binds.
        if (_gearDueCharacters.Count > 0)
        {
            TatooineReplication.FillCharacterBodies(_gearBodies);
            foreach (var character in _gearDueCharacters)
            {
                var id = EntityId.FromRawValue((ulong)character);
                if (_gearBodies.TryGetValue(id, out var body))
                {
                    _gearDue.Add((id, body));
                }
            }

            _gearDueCharacters.Clear();
            _gearBodies.Clear();
        }

        var tick0 = ctx.Transaction;
        _equipsApplied.Clear();
        _unequipsApplied.Clear();
        _craftsApplied.Clear();
        _craftBodies.Clear();
        _craftWalletsMoved.Clear();
        _craftCounts = default;
        _craftAbort = false;
        _repairsApplied.Clear();
        _salvagesApplied.Clear();
        _refinesApplied.Clear();
        _packsApplied.Clear();
        _unpacksApplied.Clear();
        _movesApplied.Clear();
        _discardsApplied.Clear();
        _consumesApplied.Clear();
        _heals.Clear();
        _gearScratch.Clear();
        _gearScratch.AddRange(_gearDue);
        _gearDue.Clear();
        GatherWear(tick0, subs, flushDue);
        bool committed;
        using (var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            EnsureItems(tx);
            EnsureInstallations(tx);
            if (waypointing + pickups.Count > 0)
            {
                EnsureWaypoints(tx);
            }
            if (crafting > 0)
            {
                EnsureEconomy(tx);
                EnsureCrafts(tx);
            }

            // The wear counted on the bodies first: an item about to move or be repaired has its condition up to date (crafting-catalog.md § 9.1).
            WearTick(tx, tick0, ctx.WorldTick);

            if (subs != null)
            {
                // Unequips first: one frees a grid slot an equip's swap may need.
                foreach (var command in unequips)
                {
                    UnequipFromCommand(tx, tick0, subs, in command);
                }

                foreach (var command in equips)
                {
                    EquipFromCommand(tx, tick0, subs, in command);
                }

                // A craft's steps in their order: a start, its points, its add-on, its finish — a client that sends them in one tick gets them applied so.
                foreach (var command in starts)
                {
                    CraftStartFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in experiments)
                {
                    CraftExperimentFromCommand(tx, subs, in command);
                }

                foreach (var command in enhances)
                {
                    CraftEnhanceFromCommand(tx, subs, in command, ctx.WorldTick);
                }

                foreach (var command in finishes)
                {
                    CraftFinishFromCommand(tx, subs, in command, ctx.WorldTick);
                }

                foreach (var command in repairs)
                {
                    RepairFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in salvages)
                {
                    SalvageFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in refines)
                {
                    RefineFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                // Packs, then unpacks: a client that packs and unpacks in one tick sees them applied so.
                foreach (var command in packs)
                {
                    PackFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in unpacks)
                {
                    UnpackFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                // The inventory window's (inventory-loot.md § 10), after the crates': a drag in the same tick as an unpack lands in the grid it left.
                foreach (var command in moves)
                {
                    MoveFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in discards)
                {
                    DiscardFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in consumes)
                {
                    ConsumeFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in sorts)
                {
                    SortFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                // Installations (crafting-build.md § 10): a place, then collects and payments, then pick-ups — a client that pays and picks up in one tick
                // has its payment settled first.
                foreach (var command in places)
                {
                    PlaceFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in collects)
                {
                    CollectFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in pays)
                {
                    PayFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                foreach (var command in pickups)
                {
                    PickUpFromCommand(tx, tick0, subs, in command, ctx.WorldTick);
                }

                // Waypoints (client.md C38): after the pick-ups, whose pins they delete — a pin asked for an installation picked up this tick is refused.
                foreach (var command in waypointCreates)
                {
                    CreateWaypointFromCommand(tx, tick0, subs, in command);
                }

                foreach (var command in waypointDeletes)
                {
                    DeleteWaypointFromCommand(tx, tick0, subs, in command);
                }

                foreach (var command in waypointActives)
                {
                    SetActiveWaypointFromCommand(tx, tick0, subs, in command);
                }
            }

            // The operators' requests (SimBridge.Admin.cs), after the players': a worn item they rewrite or take is in the gear computed below.
            AdminTick(tx, ctx.WorldTick);

            // Mail (SimBridge.Mail.cs): the game's queued since the last pass, the players' sent by HTTP, read, deleted; /tell said.
            MailTick(tx, tick0, ctx, subs);

            // The gear from what the transaction leaves worn, read through it before it commits; written to the bodies once it has.
            _gearComputed.Clear();
            foreach (var (character, body) in _gearScratch)
            {
                ComputeGear(tx, character, out var gear);
                _gearComputed.Add((body, gear));
            }

            var written = _equipsApplied.Count + _unequipsApplied.Count + _craftsApplied.Count + _repairsApplied.Count + _salvagesApplied.Count
                + _refinesApplied.Count + _packsApplied.Count + _unpacksApplied.Count + _movesApplied.Count + _discardsApplied.Count + _consumesApplied.Count
                + _sortsApplied.Count
                + _installsApplied.Count + _waypointsApplied.Count + _wearCounts.Writes
                + _adminApplied.Count + _mailApplied.Count + _mailCommandsApplied.Count + _systemMailTaken.Count > 0;
            if (written)
            {
                FlushItemHead(tx);
            }

            // Committed even when nothing counted was written: a staged write the count does not see (the item head, spawned by a first load) would
            // otherwise be rolled back while memory believes it (review, 2026-11-03). An abort — a command whose write failed after its checks — is
            // counted apart from a commit that failed.
            if (written && Volatile.Read(ref _failNextWorkshop))
            {
                Volatile.Write(ref _failNextWorkshop, false);
                _craftAbort = true;
            }

            if (_craftAbort)
            {
                Interlocked.Increment(ref _workshopAborted);
            }

            committed = !_craftAbort && tx.Commit();
        }

        if (crafting > 0 || _adminEconomy)
        {
            EndEconomy(committed);
        }

        if (crafting > 0)
        {
            EndCrafts(committed);
            EndRefines(subs, committed);
            EndCrates(subs, committed);
            EndGrid(subs, committed);
            EndPotions(ctx, subs, committed);
        }

        EndMail(ctx, subs, committed);
        EndInstallations(tick0, subs, committed, ctx.TickNumber, ctx.WorldTick);
        EndWaypoints(subs, committed);

        EndItems(committed);
        EndWear(tick0, committed);
        EndAdmin(ctx, committed);

        if (committed)
        {
            foreach (var (body, gear) in _gearComputed)
            {
                WriteGear(tick0, body, in gear);
            }
        }

        if (!committed)
        {
            // Nothing moved: the items it touched are read again (EndItems), the commands refused so their clients hear it, the bodies' gear due again next
            // tick.
            Interlocked.Increment(ref _workshopCommitsFailed);
            _gearDue.AddRange(_gearScratch);
            foreach (var c in _equipsApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.EquipFailed);
            }

            foreach (var c in _unequipsApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.EquipFailed);
            }

            foreach (var (session, id) in _craftsApplied)
            {
                subs?.Reject(session, id, TatooineReplication.CraftFailed);
                Interlocked.Increment(ref _workshopRefusedBy[TatooineReplication.CraftFailed]);
            }

            foreach (var c in _repairsApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.CraftFailed);
            }

            foreach (var c in _salvagesApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.CraftFailed);
            }

            return;
        }

        Interlocked.Add(ref _equips, _equipsApplied.Count);
        Interlocked.Add(ref _unequips, _unequipsApplied.Count);
        foreach (var body in _craftBodies)
        {
            BumpItems(tick0, body);
        }

        foreach (var body in _craftWalletsMoved)
        {
            BumpWallet(tick0, body);
        }
    }

    private void EquipFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<Equip> command)
    {
        if (!TatooineReplication.TryBodyOfSession(command.Session, out var body, out var character) || !tick0.TryOpen(body, out var player))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var owner = (long)character.RawValue;
        var items = ItemsOf(owner);
        var item = EntityId.FromRawValue(command.Value.Item);
        var src = SlotOf(items, item);
        if (src == 0 || !items.Slots[src].Instance)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.EquipNotYours);
            return;
        }

        if (items.Slots[src].Count != 1)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.ItemCrated);
            return;
        }

        var slot = Equipment.SlotOf(items.Slots[src].Template);
        if (slot == 0)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.EquipNotYours);
            return;
        }

        if (player.Read(Player.Look).Level < Equipment.LevelFor(items.Slots[src].Mk))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.EquipLevel);
            return;
        }

        // Validated: the item into its slot, and whatever was there into the grid slot it left — a swap, so it always fits.
        var k = slot - EquipSlot.First;
        var worn = items.Equipped[k];
        SetSlot(tx, items.Slots[src].Stack, slot);
        if (worn.Count > 0)
        {
            SetSlot(tx, worn.Stack, (byte)src);
        }

        (items.Equipped[k], items.Slots[src]) = (items.Slots[src], worn);
        _gearScratch.Add((character, body));
        _equipsApplied.Add(command);
    }

    private void UnequipFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<Unequip> command)
    {
        if (!TatooineReplication.TryBodyOfSession(command.Session, out var body, out var character) || !tick0.TryOpen(body, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var slot = command.Value.Slot;
        var items = ItemsOf((long)character.RawValue);
        if (!EquipSlot.IsEquip(slot) || items.Equipped[slot - EquipSlot.First].Count == 0)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.EquipNotYours);
            return;
        }

        var dst = FreeSlot(items);
        if (dst == 0)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.EquipNoRoom);
            return;
        }

        var k = slot - EquipSlot.First;
        SetSlot(tx, items.Equipped[k].Stack, (byte)dst);
        items.Slots[dst] = items.Equipped[k];
        items.Equipped[k] = default;
        _gearScratch.Add((character, body));
        _unequipsApplied.Add(command);
    }

    private static void SetSlot(Transaction tx, EntityId item, byte slot)
    {
        var e = tx.OpenMut(item);
        var s = e.Read(ItemStackArch.Stack);
        s.Slot = slot;
        e.Set(ItemStackArch.Stack, s);
    }

    /// <summary>
    /// A character's gear from what it wears: the weapon's damage, delay, reach, type and stance, or the fist; the armour's mitigation per type, Σ share ×
    /// protection, capped.
    /// </summary>
    private void ComputeGear(Transaction tx, EntityId character, out PlayerGear gear)
    {
        var items = ItemsOf((long)character.RawValue);
        gear = default;
        // A broken item is worn but does nothing (crafting-catalog.md § 9.2): a broken weapon leaves the fist, a broken piece no protection.
        var weapon = items.Equipped[0];
        if (weapon.Count > 0 && TryStatsOf(tx, weapon.Stack, out var w) && (w.Flags & ItemFlags.Broken) == 0
            && Equipment.TryWeapon(weapon.Template, out var traits))
        {
            gear.Damage = (short)Math.Clamp(w.Stat0, 1, short.MaxValue);
            gear.DelayMs = (short)Math.Clamp(w.Stat1, 1, short.MaxValue);
            gear.RangeDm = (short)Math.Clamp(w.Stat2, 1, short.MaxValue);
            gear.DamageType = traits.DamageType;
            gear.Stance = traits.Stance;
        }
        else
        {
            var fist = PlayerGear.Unarmed;
            gear.Damage = fist.Damage;
            gear.DelayMs = fist.DelayMs;
            gear.RangeDm = fist.RangeDm;
            gear.DamageType = fist.DamageType;
            gear.Stance = fist.Stance;
        }

        long kinetic = 0, energy = 0;
        for (var k = 1; k < EquipSlot.Count; k++)
        {
            var piece = items.Equipped[k];
            if (piece.Count > 0 && TryStatsOf(tx, piece.Stack, out var a) && (a.Flags & ItemFlags.Broken) == 0)
            {
                kinetic += (long)EquipSlot.SharePerMille[k] * a.Stat0;
                energy += (long)EquipSlot.SharePerMille[k] * a.Stat1;
            }
        }

        gear.MitKinetic = (short)Math.Min(Equipment.MitigationCapPerMille, kinetic / 1000);
        gear.MitEnergy = (short)Math.Min(Equipment.MitigationCapPerMille, energy / 1000);
    }

    /// <summary>A body's gear written; then its items revision, so its client reads the inventory again.</summary>
    private void WriteGear(Transaction tick0, EntityId body, in PlayerGear computed)
    {
        if (!tick0.TryOpenMut(body, out var player))
        {
            return;
        }

        var gear = player.Read(Player.Gear);
        gear.Damage = computed.Damage;
        gear.DelayMs = computed.DelayMs;
        gear.RangeDm = computed.RangeDm;
        gear.MitKinetic = computed.MitKinetic;
        gear.MitEnergy = computed.MitEnergy;
        gear.DamageType = computed.DamageType;
        gear.Stance = computed.Stance;
        player.Set(Player.Gear, gear);

        // A stance whose weapon was taken off or swapped for another kind follows what is worn now — its stance, or the fists — rather than leaving a
        // body that cannot fight and is not told (review, 2026-11-02).
        var state = player.Read(Player.State);
        if (state.Stance > Stances.Unarmed && state.Stance != gear.Stance)
        {
            state.Stance = gear.Stance != 0 ? gear.Stance : Stances.Unarmed;
            player.Set(Player.State, state);
            Interlocked.Increment(ref _stancesFollowed);
        }

        Interlocked.Increment(ref _gearWrites);
        BumpItems(tick0, body);
    }

    private long _stancesFollowed;

    /// <summary>Stances moved to what is worn after an equip took their weapon away.</summary>
    public long StancesFollowed => Interlocked.Read(ref _stancesFollowed);

    private void RefuseWorkshop<T>(SubscriptionsCommands subs, in ClientCommand<T> command, byte reason) where T : unmanaged
    {
        subs?.Reject(command, reason);
        Interlocked.Increment(ref _workshopRefusedBy[reason]);
    }
}
