using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Player installations (crafting-build.md § 10, C5a; <see cref="Installations"/>): an extractor placed from its kit on a 64 m lot, producing lazily,
/// collected, paid for and picked up — in the Workshop's transaction, through the one item path and the one credit path, so both audits replay it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The truth is <see cref="InstallationArch"/>, the view <see cref="InstallationMarker"/>.</b> A command writes the truth in the Workshop's side
/// transaction; once that commits, the marker is spawned, rewritten or destroyed on the tick's own, so a failed commit leaves no marker behind. The markers
/// are rebuilt from the truth on the first pass after a start: a crash that lost one, or kept a stale one, costs nothing.
/// </para>
/// <para>
/// <b>Nothing is written while an installation runs.</b> What it holds is computed from the time since it last settled (<see cref="Installations.HeldMilli"/>);
/// a collect, a payment or a pick-up settles it. Its state on the marker — running, full, unpaid — is swept every <see cref="StateSweepSeconds"/> and
/// written only when it changed.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>How often the markers' states are checked against their installations, seconds.</summary>
    private const int StateSweepSeconds = 10;

    // In memory, read with the first pass: every installation by its raw id, the lots taken, each owner's lots, each one's marker and its state drawn.
    private readonly Dictionary<long, (EntityId Id, InstallationCore C)> _installations = [];
    private readonly Dictionary<long, long> _installationOnLot = [];
    private readonly Dictionary<long, int> _lotsUsed = [];
    private readonly Dictionary<long, EntityId> _markerOf = [];
    private readonly Dictionary<long, byte> _markerState = [];
    private bool _installationsLoaded;
    private bool _markersBuilt;
    private long _installationsSweptAt;

    // The pass's marker work, applied on the tick's transaction once the Workshop's commits; and its commands, refused if it does not.
    private readonly List<long> _markersToSpawn = [];
    private readonly List<EntityId> _markersToDestroy = [];
    private readonly List<long> _markersToRedraw = [];
    private readonly List<(SessionId Session, ushort Seq)> _installsApplied = [];
    private bool _installationsDirty;

    // Counters: the pass's, published when it commits.
    private InstallCounts _pendingInstall;
    private InstallCounts _install;
    private long _worldTickNow;

    private struct InstallCounts
    {
        public long Placed;
        public long PickedUp;
        public long Collects;
        public long Extracted;
        public long UpkeepPaid;
        public long Writes;
    }

    /// <summary>Installations standing now.</summary>
    public int InstallationCount => Volatile.Read(ref _installationCount);

    private int _installationCount;

    public long InstallationsPlaced => Interlocked.Read(ref _install.Placed);

    public long InstallationsPickedUp => Interlocked.Read(ref _install.PickedUp);

    public long InstallationCollects => Interlocked.Read(ref _install.Collects);

    /// <summary>Units collected from installations since start.</summary>
    public long UnitsExtracted => Interlocked.Read(ref _install.Extracted);

    /// <summary>Upkeep paid into installations since start, credits.</summary>
    public long UpkeepPaid => Interlocked.Read(ref _install.UpkeepPaid);

    /// <summary>Writes to an installation's truth since start — a spawn, a settle, a destroy — committed: only commands write one (C5a-2).</summary>
    public long InstallationWrites => Interlocked.Read(ref _install.Writes);

    // The checks' hours: ticks added to the world tick wherever an installation's time is computed, so a case runs an hour in a second.
    private long _installClockOffset;

    private long InstallClockOffset => Interlocked.Read(ref _installClockOffset);

    /// <summary>The checks' clock: installations see the world <paramref name="ticks"/> later from now on.</summary>
    internal void AdvanceInstallationClockForChecks(long ticks) => Interlocked.Add(ref _installClockOffset, ticks);

    /// <summary>The world tick of the last Workshop pass: what an off-tick reader computes an installation's units at.</summary>
    public long WorldTickNow => Interlocked.Read(ref _worldTickNow);

    /// <summary>Ticks in an hour of world time.</summary>
    internal long TicksPerHour => (long)_config.TickRateHz * 3600;

    /// <summary>Whether the Workshop has installation work without a command: the markers to build after a start, a state sweep due.</summary>
    private bool InstallationsDue(long tick)
        => !_markersBuilt || (_installations.Count > 0 && tick - _installationsSweptAt >= SweepTicks);

    /// <summary>The installations, read on the first use after a start or a failed commit.</summary>
    private void EnsureInstallations(Transaction tx)
    {
        if (_installationsLoaded)
        {
            return;
        }

        _installations.Clear();
        _installationOnLot.Clear();
        _lotsUsed.Clear();
        foreach (var found in tx.Query<InstallationArch>())
        {
            var c = found.Read(InstallationArch.Core);
            var key = unchecked((long)found.Id.RawValue);
            _installations[key] = (found.Id, c);
            _installationOnLot[c.Lot] = key;
            _lotsUsed[c.Owner] = LotsOf(c.Owner) + LotsTaken(c.Kind);
        }

        _installationsLoaded = true;
        Volatile.Write(ref _installationCount, _installations.Count);
    }

    private int LotsOf(long owner) => _lotsUsed.TryGetValue(owner, out var n) ? n : 0;

    private static int LotsTaken(byte kind) => kind == InstallationKind.Extractor ? Installations.ExtractorLots : 1;

    /// <summary>Lots a character uses now, for the endpoint and the bots; 0 before the first load.</summary>
    internal int LotsUsedBy(long owner) => LotsOf(owner);

    private void RefuseInstall<T>(SubscriptionsCommands subs, in ClientCommand<T> command, byte reason) where T : unmanaged
        => RefuseWorkshop(subs, in command, reason);

    // ── Place ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void PlaceFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<PlaceInstallation> command, long worldTick)
    {
        var now = worldTick + InstallClockOffset;
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out var realm, out var x, out var z))
        {
            RefuseInstall(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var v = command.Value;
        var item = EntityId.FromRawValue(v.Item);
        var items = ItemsOf(owner);
        var s = SlotOf(items, item);
        if (s == 0 || !items.Slots[s].Instance || items.Slots[s].Count != 1 || items.Slots[s].Template != ItemCatalog.Extractor
            || !TryStatsOf(tx, item, out var kit) || kit.Stat0 <= 0 || kit.Stat1 <= 0 || !Installations.Extracts(v.Family))
        {
            RefuseInstall(subs, in command, TatooineReplication.InstallationBad);
            return;
        }

        // On a planet, outside its cities: the lot's centre, where it will stand, a margin past every city's edge.
        var lot = Installations.LotOf(realm, x, z);
        var (cx, cz) = Installations.CentreOf(lot);
        if (realm >= PlanetIndexes.Length || PlanetIndexes[realm].Levels.NearCity(cx, cz, Installations.CityMarginM))
        {
            RefuseInstall(subs, in command, TatooineReplication.InstallationInTown);
            return;
        }

        if (_installationOnLot.ContainsKey(lot))
        {
            RefuseInstall(subs, in command, TatooineReplication.LotTaken);
            return;
        }

        if (LotsOf(owner) + Installations.ExtractorLots > Installations.LotsPerCharacter)
        {
            RefuseInstall(subs, in command, TatooineReplication.NoLotsLeft);
            return;
        }

        // Validated: the installation, unpaid — it makes nothing until its upkeep is paid — and the kit it was, one place row naming it.
        var c = new InstallationCore
        {
            Owner = owner,
            Lot = lot,
            T0 = now,
            PaidUntil = now,
            RatePerHour = kit.Stat0,
            Hopper = kit.Stat1,
            KitCrafter = kit.Crafter,
            KitSerial = kit.Serial,
            KitLifeMilli = kit.LifeMilli,
            KitTemplate = ItemCatalog.Extractor,
            X = cx,
            Z = cz,
            Y = GroundAt(new RealmId(realm), cx, cz),
            Realm = realm,
            Family = v.Family,
            Kind = InstallationKind.Extractor,
        };
        var id = tx.Spawn<InstallationArch>(InstallationArch.Core.Set(in c));
        _pendingInstall.Writes++;
        var key = unchecked((long)id.RawValue);
        if (ItemTransfer(tx, owner, ItemLedger.World, ItemCatalog.Extractor, 0, 1, ItemMoveKind.Place, key, worldTick, item) != 1)
        {
            _craftAbort = true;
            _installsApplied.Add((command.Session, command.Seq));
            return;
        }

        _installations[key] = (id, c);
        _installationOnLot[lot] = key;
        _lotsUsed[owner] = LotsOf(owner) + Installations.ExtractorLots;
        _markersToSpawn.Add(key);
        _installationsDirty = true;
        _pendingInstall.Placed++;
        _craftBodies.Add(body);
        _installsApplied.Add((command.Session, command.Seq));
    }

    /// <summary>The installation a command names, the actor's own and within reach; the reason it is not, or 0.</summary>
    private byte Reach(Transaction tick0, SessionId session, ulong raw, out long owner, out EntityId body, out long key, out InstallationCore c)
    {
        key = unchecked((long)raw);
        c = default;
        if (!CraftActor(tick0, session, out owner, out body, out var realm, out var x, out var z))
        {
            return TatooineReplication.NotInWorld;
        }

        if (!_installations.TryGetValue(key, out var found) || found.C.Owner != owner)
        {
            return TatooineReplication.InstallationBad;
        }

        c = found.C;
        var dx = c.X - x;
        var dz = c.Z - z;
        return c.Realm != realm || (dx * dx) + (dz * dz) > Installations.ReachM * Installations.ReachM ? TatooineReplication.InstallationNotHere : (byte)0;
    }

    // ── Collect ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void CollectFromCommand(
        Transaction tx,
        Transaction tick0,
        SubscriptionsCommands subs,
        in ClientCommand<CollectInstallation> command,
        long worldTick)
    {
        var now = worldTick + InstallClockOffset;
        var refused = Reach(tick0, command.Session, command.Value.Installation, out var owner, out var body, out var key, out var c);
        if (refused != 0)
        {
            RefuseInstall(subs, in command, refused);
            return;
        }

        var heldMilli = Installations.HeldMilli(in c, now, TicksPerHour);
        var held = (int)(heldMilli / 1000);
        if (held == 0)
        {
            RefuseInstall(subs, in command, TatooineReplication.InstallationEmpty);
            return;
        }

        var take = Math.Min(held, RoomFor(ItemsOf(owner), c.Family, 1));
        if (take == 0)
        {
            RefuseInstall(subs, in command, TatooineReplication.CraftNoRoom);
            return;
        }

        // What fits is collected; the rest, and the fraction of a unit, stay in the hopper, settled at this tick.
        if (ItemTransfer(tx, ItemLedger.World, owner, c.Family, 1, take, ItemMoveKind.Extract, key, worldTick) != take)
        {
            _craftAbort = true;
            _installsApplied.Add((command.Session, command.Seq));
            return;
        }

        c.BankedMilli = heldMilli - (take * 1000L);
        c.T0 = now;
        Settle(tx, key, in c);
        _pendingInstall.Collects++;
        _pendingInstall.Extracted += take;
        _craftBodies.Add(body);
        _installsApplied.Add((command.Session, command.Seq));
    }

    // ── Upkeep ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void PayFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<PayUpkeep> command, long worldTick)
    {
        var now = worldTick + InstallClockOffset;
        var refused = Reach(tick0, command.Session, command.Value.Installation, out var owner, out var body, out var key, out var c);
        if (refused != 0)
        {
            RefuseInstall(subs, in command, refused);
            return;
        }

        // Running time bought from now, or from when it already runs to; never more than the cap ahead.
        var credits = (long)command.Value.Credits;
        var from = Math.Max(now, c.PaidUntil);
        var until = from + Installations.TicksBought(credits, TicksPerHour);
        if (credits < 1 || until - now > Installations.MaxPrepaidHours * TicksPerHour)
        {
            RefuseInstall(subs, in command, TatooineReplication.InstallationBad);
            return;
        }

        if (BalanceOf(tx, owner) < credits)
        {
            RefuseInstall(subs, in command, TatooineReplication.CraftNotEnough);
            return;
        }

        if (!Transfer(tx, owner, EconomyLedger.World, credits, TransferKind.Upkeep, key, worldTick))
        {
            _craftAbort = true;
            _installsApplied.Add((command.Session, command.Seq));
            return;
        }

        // Settled first: a lapse before this payment made nothing, and is not made up by it.
        c.BankedMilli = Installations.HeldMilli(in c, now, TicksPerHour);
        c.T0 = now;
        c.PaidUntil = until;
        Settle(tx, key, in c);
        _pendingInstall.UpkeepPaid += credits;
        _craftWalletsMoved.Add(body);
        _installsApplied.Add((command.Session, command.Seq));
    }

    // ── Pick up ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void PickUpFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<PickUpInstallation> command, long worldTick)
    {
        var now = worldTick + InstallClockOffset;
        var refused = Reach(tick0, command.Session, command.Value.Installation, out var owner, out var body, out var key, out var c);
        if (refused != 0)
        {
            RefuseInstall(subs, in command, refused);
            return;
        }

        // Its hopper collected first: a pick-up never throws units away. What upkeep is left is not refunded.
        if (Installations.Held(in c, now, TicksPerHour) > 0)
        {
            RefuseInstall(subs, in command, TatooineReplication.InstallationNotEmpty);
            return;
        }

        if (FreeSlot(ItemsOf(owner)) == 0)
        {
            RefuseInstall(subs, in command, TatooineReplication.CraftNoRoom);
            return;
        }

        var kit = new ItemInstance
        {
            Crafter = c.KitCrafter,
            Serial = c.KitSerial,
            Stat0 = c.RatePerHour,
            Stat1 = c.Hopper,
            LifeMilli = c.KitLifeMilli,
            Flags = ItemFlags.Pristine,
        };
        if (MintInstance(tx, owner, c.KitTemplate, 0, in kit, ItemMoveKind.Pickup, key, worldTick).IsNull)
        {
            _craftAbort = true;
            _installsApplied.Add((command.Session, command.Seq));
            return;
        }

        tx.Destroy(_installations[key].Id);
        _pendingInstall.Writes++;
        DeletePinsOf(tx, key);
        _installations.Remove(key);
        _alertMailed.Remove(key);
        _installationOnLot.Remove(c.Lot);
        _lotsUsed[owner] = Math.Max(0, LotsOf(owner) - LotsTaken(c.Kind));
        if (_markerOf.TryGetValue(key, out var marker))
        {
            _markersToDestroy.Add(marker);
        }

        _installationsDirty = true;
        _pendingInstall.PickedUp++;
        _craftBodies.Add(body);
        _installsApplied.Add((command.Session, command.Seq));
    }

    /// <summary>Writes an installation settled, in memory and in the transaction, and its marker redrawn once that commits.</summary>
    private void Settle(Transaction tx, long key, in InstallationCore c)
    {
        var id = _installations[key].Id;
        tx.OpenMut(id).Set(InstallationArch.Core, c);
        _pendingInstall.Writes++;
        _installations[key] = (id, c);
        _markersToRedraw.Add(key);
        _installationsDirty = true;
    }

    // ── Markers ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Workshop's transaction ended. Committed: the markers its commands changed, on the tick's own transaction — and every marker rebuilt on the first
    /// pass after a start; the states swept when due. Not: what memory holds of the installations is read again, the commands refused.
    /// </summary>
    private void EndInstallations(Transaction tick0, SubscriptionsCommands subs, bool committed, long tick, long worldTick)
    {
        worldTick += InstallClockOffset;
        Interlocked.Exchange(ref _worldTickNow, worldTick);
        if (!committed)
        {
            foreach (var (session, seq) in _installsApplied)
            {
                subs?.Reject(session, seq, TatooineReplication.CraftFailed);
            }

            if (_installationsDirty)
            {
                _installationsLoaded = false;
            }

            ClearInstallationWork();
            return;
        }

        Interlocked.Add(ref _install.Placed, _pendingInstall.Placed);
        Interlocked.Add(ref _install.PickedUp, _pendingInstall.PickedUp);
        Interlocked.Add(ref _install.Collects, _pendingInstall.Collects);
        Interlocked.Add(ref _install.Extracted, _pendingInstall.Extracted);
        Interlocked.Add(ref _install.UpkeepPaid, _pendingInstall.UpkeepPaid);
        Interlocked.Add(ref _install.Writes, _pendingInstall.Writes);

        if (!_markersBuilt && _installationsLoaded)
        {
            RebuildMarkers(tick0);
        }

        foreach (var marker in _markersToDestroy)
        {
            if (tick0.TryOpen(marker, out _))
            {
                tick0.Destroy(marker);
            }
        }

        foreach (var key in _markersToSpawn)
        {
            SpawnMarker(tick0, key, worldTick);
        }

        foreach (var key in _markersToRedraw)
        {
            Redraw(tick0, key, worldTick);
        }

        if (_installations.Count > 0 && tick - _installationsSweptAt >= SweepTicks)
        {
            _installationsSweptAt = tick;
            foreach (var key in _installations.Keys)
            {
                Redraw(tick0, key, worldTick);
                MailIfAlerted(key, worldTick);
            }

            _alertsSeeded = true;
        }

        Volatile.Write(ref _installationCount, _installations.Count);
        ClearInstallationWork();
    }

    private void ClearInstallationWork()
    {
        _markersToSpawn.Clear();
        _markersToDestroy.Clear();
        _markersToRedraw.Clear();
        _installsApplied.Clear();
        _installationsDirty = false;
        _pendingInstall = default;
    }

    /// <summary>Every marker destroyed and one spawned for each installation: the view rebuilt from the truth, once after a start.</summary>
    private void RebuildMarkers(Transaction tick0)
    {
        var stale = new List<EntityId>();
        foreach (var found in tick0.Query<InstallationMarker>())
        {
            stale.Add(found.Id);
        }

        foreach (var id in stale)
        {
            tick0.Destroy(id);
        }

        _markerOf.Clear();
        _markerState.Clear();
        foreach (var key in _installations.Keys)
        {
            _markersToSpawn.Add(key);
        }

        _markersBuilt = true;
    }

    private void SpawnMarker(Transaction tick0, long key, long worldTick)
    {
        if (!_installations.TryGetValue(key, out var found) || _markerOf.ContainsKey(key))
        {
            return;
        }

        var c = found.C;
        var placement = default(InstallationPlacement);
        placement.SetAt(c.X, c.Z, c.Y, InstallationHalfM);
        var state = InstallationState.Of(in c, worldTick, TicksPerHour);
        var view = new InstallationView
        {
            Kind = StructureKind.Harvester,
            Asset = ExtractorAsset,
            Family = c.Family,
            State = state,
            Core = key,
        };
        var id = tick0.Spawn<InstallationMarker>(
            InstallationMarker.Bounds.Set(in placement),
            InstallationMarker.View.Set(in view),
            InstallationMarker.Realm.Set(new InstallationRealm(c.Realm)));
        _markerOf[key] = id;
        _markerState[key] = state;
    }

    /// <summary>An installation's marker brought to its state, written only when it changed.</summary>
    private void Redraw(Transaction tick0, long key, long worldTick)
    {
        if (!_installations.TryGetValue(key, out var found) || !_markerOf.TryGetValue(key, out var marker))
        {
            return;
        }

        var state = InstallationState.Of(in found.C, worldTick, TicksPerHour);
        if (_markerState.TryGetValue(key, out var was) && was == state)
        {
            return;
        }

        if (tick0.TryOpenMut(marker, out var e))
        {
            var view = e.Read(InstallationMarker.View);
            view.State = state;
            e.Set(InstallationMarker.View, view);
            TatooineReplication.Replicate(in e);
            _markerState[key] = state;
        }
    }

    // The sweep's period in ticks: StateSweepSeconds, or shorter for a check (SetInstallationSweepTicksForChecks).
    private long _sweepTicksForChecks;

    private long SweepTicks => _sweepTicksForChecks > 0 ? _sweepTicksForChecks : (long)_config.TickRateHz * StateSweepSeconds;

    /// <summary>Shortens the state sweep — and so the installations' mail — to <paramref name="ticks"/>, for a check that cannot wait ten seconds.</summary>
    internal void SetInstallationSweepTicksForChecks(long ticks) => _sweepTicksForChecks = ticks;

    // What each installation last mailed its owner about (mail.md § 5): 0 nothing, else the MailKind. Seeded silently by the first sweep after a start, so a
    // restart never mails twice — and a transition while the server was down is not mailed.
    private readonly Dictionary<long, byte> _alertMailed = [];
    private bool _alertsSeeded;

    /// <summary>What an installation should have told its owner: full, stopped unpaid, upkeep under an hour, or nothing (0).</summary>
    private byte AlertOf(in InstallationCore c, long worldTick)
    {
        var state = InstallationState.Of(in c, worldTick, TicksPerHour);
        return state == InstallationState.Full ? MailKind.HopperFull
            : state == InstallationState.Unpaid ? MailKind.Stopped
            : c.PaidUntil - worldTick < TicksPerHour ? MailKind.UpkeepLow
            : (byte)0;
    }

    /// <summary>Queues the mail of a transition into an alert, once (mail.md § 5); a return to running clears it, so the next one is told again.</summary>
    private void MailIfAlerted(long key, long worldTick)
    {
        if (!_installations.TryGetValue(key, out var found))
        {
            return;
        }

        var alert = AlertOf(in found.C, worldTick);
        var was = _alertMailed.GetValueOrDefault(key);
        _alertMailed[key] = alert;
        if (alert == 0 || alert == was || !_alertsSeeded)
        {
            return;
        }

        var c = found.C;
        var what = ItemCatalog.TryGet(c.Family, out var t) ? t.Name : "Unknown";
        var where = FormattableString.Invariant($"{c.X:0}, {c.Z:0}");
        var held = Installations.Held(in c, worldTick, TicksPerHour);
        var it = $"Your {what} extractor at {where}";
        var (subject, text) = alert switch
        {
            MailKind.HopperFull => ($"Extractor full: {what}",
                FormattableString.Invariant($"{it} has filled its hopper ({held} / {c.Hopper}) and stopped. Collect it to start it again.")),
            MailKind.Stopped => ($"Extractor stopped: {what}",
                FormattableString.Invariant($"{it} ran out of upkeep and stopped, holding {held}. Pay its upkeep to start it again.")),
            _ => ($"Upkeep running out: {what}", $"{it} has under an hour of upkeep left. Pay it, or it stops."),
        };
        QueueSystemMail(c.Owner, alert, key, subject, text);
    }

    /// <summary>The extractor's model, as the structure asset table numbers it (<see cref="StructureAssets.Table"/>).</summary>
    private static readonly int ExtractorAsset = Array.IndexOf(StructureAssets.Table, "resource_extractor");

    /// <summary>A marker's half-extent, metres: the extractor model's footprint.</summary>
    private const float InstallationHalfM = 4f;
}
