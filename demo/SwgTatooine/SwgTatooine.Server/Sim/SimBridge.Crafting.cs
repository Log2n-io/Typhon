using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Crafting (crafting-build.md C2, crafting-catalog.md § 3.4): a craft started at a station, its inputs consumed and its assembly rolled; its points and its
/// add-on, one command each; its finish, the result minted and the bench fee paid. Run by <c>Workshop</c>, in its side transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inputs ⇔ craft ⇔ result, never neither and never both</b> (C2-3): the start destroys the inputs (<see cref="ItemMoveKind.Craft"/> rows naming the
/// craft) in the commit that spawns the <see cref="CraftArch"/>; the finish mints the result (<see cref="ItemMoveKind.Make"/>) and pays the fee in the
/// commit that destroys it. A crash between two commands leaves a craft, which the crafter resumes.
/// </para>
/// <para><b>One craft per crafter at a time</b>, kept in memory by crafter and read whole on the first use after a start or a failed commit.</para>
/// </remarks>
public sealed partial class SimBridge
{
    private struct CraftCounts
    {
        public long Started;
        public long Points;
        public long Enhanced;
        public long Finished;
        public long Fees;
    }

    private bool _craftsLoaded;
    private IndexRef _craftByCrafter;
    private readonly Dictionary<long, (EntityId Id, CraftState S)> _crafts = [];
    private readonly List<(SessionId Session, ushort Seq)> _craftsApplied = [];
    private readonly HashSet<EntityId> _craftBodies = [];
    private readonly HashSet<EntityId> _craftWalletsMoved = [];
    private CraftCounts _craftCounts;

    // A write failed after its validation passed — a bug: the workshop's transaction is not committed, and every command in it is refused.
    private bool _craftAbort;
    private long _craftsStarted;
    private long _craftsFinished;
    private long _craftPoints;
    private long _craftFees;

    /// <summary>Crafts started since start.</summary>
    public long CraftsStarted => Interlocked.Read(ref _craftsStarted);

    /// <summary>Crafts finished since start: results minted.</summary>
    public long CraftsFinished => Interlocked.Read(ref _craftsFinished);

    private long _craftsEnhanced;

    /// <summary>Add-ons applied to crafts.</summary>
    public long CraftsEnhanced => Interlocked.Read(ref _craftsEnhanced);

    /// <summary>Experimentation points spent since start.</summary>
    public long CraftPoints => Interlocked.Read(ref _craftPoints);

    /// <summary>Bench fees paid to World since start, credits: a sink.</summary>
    public long CraftFees => Interlocked.Read(ref _craftFees);

    /// <summary>The craft a character has under way, as the workshop holds it; <see langword="false"/> for none.</summary>
    internal bool TryCraftOf(long crafter, out CraftState state)
    {
        if (_crafts.TryGetValue(crafter, out var c))
        {
            state = c.S;
            return true;
        }

        state = default;
        return false;
    }

    private void EnsureCrafts(Transaction tx)
    {
        if (_craftsLoaded)
        {
            return;
        }

        _craftByCrafter = Dbe.GetIndexRef<CraftState, long>(c => c.Crafter);
        _crafts.Clear();
        using (var found = tx.EnumerateIndex<CraftState, long>(_craftByCrafter, long.MinValue, long.MaxValue))
        {
            while (found.MoveNext())
            {
                _crafts[found.CurrentComponent.Crafter] = (EntityId.FromRawValue((ulong)found.CurrentEntityPK), found.CurrentComponent);
            }
        }

        _craftsLoaded = true;
    }

    /// <summary>The workshop's transaction ended: counts published, or — not committed — the crafts read again on the next use.</summary>
    private void EndCrafts(bool committed)
    {
        if (committed)
        {
            Interlocked.Add(ref _craftsStarted, _craftCounts.Started);
            Interlocked.Add(ref _craftsFinished, _craftCounts.Finished);
            Interlocked.Add(ref _craftPoints, _craftCounts.Points);
            Interlocked.Add(ref _craftsEnhanced, _craftCounts.Enhanced);
            Interlocked.Add(ref _craftFees, _craftCounts.Fees);
        }
        else if (_craftsApplied.Count > 0)
        {
            _craftsLoaded = false;
        }

        EndMade(committed);
        _craftCounts = default;
    }

    /// <summary>The nearest crafting station within <see cref="TatooineData.StationReachM"/> of a point on a planet.</summary>
    private bool StationNear(ushort realm, float x, float z)
    {
        var reach = TatooineData.StationReachM * TatooineData.StationReachM;
        if (realm >= PlanetIndexes.Length)
        {
            return false;
        }

        foreach (var s in PlanetIndexes[realm].Stations)
        {
            if (Distance2(s.X, s.Z, x, z) <= reach)
            {
                return true;
            }
        }

        return false;
    }

    private bool CraftActor(Transaction tick0, SessionId session, out long crafter, out EntityId body, out ushort realm, out float x, out float z)
    {
        crafter = 0;
        realm = 0;
        x = z = 0f;
        if (!TatooineReplication.TryBodyOfSession(session, out body, out var character) || !tick0.TryOpen(body, out var player))
        {
            return false;
        }

        crafter = (long)character.RawValue;
        var at = player.Read(Player.Bounds);
        realm = player.Read(Player.Realm).Value;
        x = at.X;
        z = at.Z;
        return _walletOf.ContainsKey(crafter);
    }

    private void RefuseCraft<T>(SubscriptionsCommands subs, in ClientCommand<T> command, byte reason) where T : unmanaged
    {
        subs.Reject(command, reason);
        Interlocked.Increment(ref _workshopRefusedBy[reason]);
    }

    // ── Start ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void CraftStartFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<CraftStart> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var crafter, out var body, out var realm, out var x, out var z))
        {
            RefuseCraft(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (!StationNear(realm, x, z))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNotAtStation);
            return;
        }

        if (_crafts.ContainsKey(crafter))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftBusy);
            return;
        }

        var v = command.Value;
        if (!Schematics.TryGet(v.Schematic, out var s) || !ResolveInputs(tx, s, in v, crafter, out var plan))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftBadInputs);
            return;
        }

        // The bench fee is paid at the finish, but a crafter who cannot pay it is refused here, before its inputs are consumed: a craft it could never
        // finish would hold them for good (crafting-catalog.md § 15.4).
        if (BalanceOf(tx, crafter) < BenchFee(plan.Output, plan.Mk))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNotEnough);
            return;
        }

        // Validated: the craft, then its inputs consumed into it, its ceilings and its assembly — one commit.
        var serial = _itemHead.LastSeq;
        var state = new CraftState
        {
            Crafter = crafter,
            Serial = serial,
            StartedTick = worldTick,
            Schematic = s.Id,
            Output = plan.Output,
            Mk = plan.Mk,
            PointsLeft = (byte)s.Points,
            NextStep = 1,
        };
        Span<int> q = stackalloc int[Crafting.MaxLines];
        Span<int> f = stackalloc int[Crafting.MaxLines];
        Crafting.Ceilings(s, plan.Inputs, q);
        Crafting.Assemble(s, crafter, serial, f);
        SetLines(ref state, q, f);
        var id = tx.Spawn<CraftArch>(CraftArch.State.Set(in state));
        var reference = (long)id.RawValue;
        foreach (var take in plan.Takes)
        {
            if ((take.Item.IsNull
                    ? ItemTransfer(tx, crafter, ItemLedger.World, take.Template, take.Mk, take.Count, ItemMoveKind.Craft, reference, worldTick)
                    : ItemTransfer(tx, crafter, ItemLedger.World, take.Template, take.Mk, 1, ItemMoveKind.Craft, reference, worldTick, take.Item))
                != take.Count)
            {
                // Validated above, so a bug: the transaction is not committed, and everything in it refused.
                _craftAbort = true;
                _craftsApplied.Add((command.Session, command.Seq));
                return;
            }
        }

        _crafts[crafter] = (id, state);
        _craftCounts.Started++;
        _craftBodies.Add(body);
        _craftsApplied.Add((command.Session, command.Seq));
    }

    // One input the start consumes: units of a (template, Mk), or one instance.
    private readonly record struct CraftTake(ushort Template, byte Mk, int Count, EntityId Item);

    private sealed class StartPlan
    {
        public readonly Schematics.Input[] Inputs;
        public readonly List<CraftTake> Takes = [];
        public ushort Output;
        public byte Mk;

        public StartPlan(int slots) => Inputs = new Schematics.Input[slots];
    }

    /// <summary>
    /// The command's inputs against the crafter's grid, slot by slot: the units held at the Mk asked (a bulk slot's lowest that covers it), the cores, the
    /// parts by id — every one of the slot's template, none named twice. What the start consumes, and what the ceilings read.
    /// </summary>
    private bool ResolveInputs(Transaction tx, Schematic s, in CraftStart v, long crafter, out StartPlan plan)
    {
        plan = new StartPlan(s.Slots.Length);
        plan.Output = s.Output;
        plan.Mk = s.Kind == SchematicKind.Core ? (byte)1 : (byte)0;
        var items = ItemsOf(crafter);
        Span<byte> mks = [v.Mk0, v.Mk1, v.Mk2, v.Mk3, v.Mk4, v.Mk5];
        Span<ulong> parts = [v.Part0, v.Part1, v.Part2, v.Part3, v.Part4, v.Part5];
        var nextMk = 0;
        var nextPart = 0;
        var used = new HashSet<ulong>();
        var padded = 0;
        var composite = 0;
        for (var k = 0; k < s.Slots.Length; k++)
        {
            var slot = s.Slots[k];
            if (slot.Kind == SlotKind.Part)
            {
                double grade = 0, kinetic = 0, energy = 0;
                for (var n = 0; n < slot.Count; n++)
                {
                    if (nextPart >= parts.Length)
                    {
                        return false;
                    }

                    var id = EntityId.FromRawValue(parts[nextPart++]);
                    var src = SlotOf(items, id);
                    if (src == 0 || !items.Slots[src].Instance || items.Slots[src].Count != 1 || !used.Add(id.RawValue))
                    {
                        return false;
                    }

                    var template = items.Slots[src].Template;
                    var segment = slot.Template == 0;
                    if (segment ? template is not (Schematics.PaddedSegment or Schematics.CompositeSegment) : template != slot.Template)
                    {
                        return false;
                    }

                    if (!TryStatsOf(tx, id, out var stats))
                    {
                        return false;
                    }

                    var g = stats.Stat0 / 1000d;

                    var (bk, be) = Schematics.BiasOf(template);
                    grade += g;
                    kinetic += bk;
                    energy += be;
                    padded += template == Schematics.PaddedSegment ? 1 : 0;
                    composite += template == Schematics.CompositeSegment ? 1 : 0;
                    plan.Takes.Add(new CraftTake(template, 0, 1, id));
                }

                plan.Inputs[k] = new Schematics.Input(slot.Template, 0, grade / slot.Count, kinetic / slot.Count, energy / slot.Count);
                continue;
            }

            if (nextMk >= mks.Length)
            {
                return false;
            }

            var mk = mks[nextMk++];
            if (slot.Kind == SlotKind.Bulk && mk == 0)
            {
                mk = LowestCovering(items, slot.Template, slot.Count);
            }

            if (mk is < 1 or > 5 || UnitsOf(items, slot.Template, mk) < slot.Count)
            {
                return false;
            }

            if (slot.Kind == SlotKind.Core)
            {
                plan.Mk = mk;
            }

            plan.Inputs[k] = new Schematics.Input(slot.Template, mk, 0, 0, 0);
            plan.Takes.Add(new CraftTake(slot.Template, mk, slot.Count, EntityId.Null));
        }

        // An armour piece is drawn by its segments (§ 13.6): composite when most are.
        if (s.Kind == SchematicKind.Armor && composite > padded)
        {
            plan.Output = (ushort)(s.Output - ItemCatalog.PaddedHelmet + ItemCatalog.CompositeHelmet);
        }

        // The same (template, Mk) asked by two slots must be held for both.
        var need = new Dictionary<(ushort, byte), int>();
        foreach (var t in plan.Takes)
        {
            if (t.Item.IsNull)
            {
                need[(t.Template, t.Mk)] = (need.TryGetValue((t.Template, t.Mk), out var c) ? c : 0) + t.Count;
            }
        }

        foreach (var ((template, mk), count) in need)
        {
            if (UnitsOf(items, template, mk) < count)
            {
                return false;
            }
        }

        return true;
    }

    private static byte LowestCovering(OwnerItems items, ushort template, int count)
    {
        for (byte mk = 1; mk <= 5; mk++)
        {
            if (UnitsOf(items, template, mk) >= count)
            {
                return mk;
            }
        }

        return 0;
    }

    // ── Points, add-on ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void CraftExperimentFromCommand(Transaction tx, SubscriptionsCommands subs, in ClientCommand<CraftExperiment> command)
    {
        if (!TatooineReplication.TryBodyOfSession(command.Session, out _, out var character)
            || !_crafts.TryGetValue((long)character.RawValue, out var craft))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNone);
            return;
        }

        // A craft of a schematic this build does not know (a later build renumbered it) is refused, never thrown on: the tick does not throw.
        if (!Schematics.TryGet(craft.S.Schematic, out var s))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNone);
            return;
        }

        var line = command.Value.Line;
        if (craft.S.PointsLeft == 0 || line >= s.Lines.Length)
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNoPoints);
            return;
        }

        var state = craft.S;
        Span<int> q = stackalloc int[Crafting.MaxLines];
        Span<int> f = stackalloc int[Crafting.MaxLines];
        GetLines(in state, q, f);
        Crafting.Experiment(state.Crafter, state.Serial, state.NextStep, f, line);
        SetLines(ref state, q, f);
        state.NextStep++;
        state.PointsLeft--;
        tx.OpenMut(craft.Id).Set(CraftArch.State, state);
        _crafts[state.Crafter] = (craft.Id, state);
        _craftCounts.Points++;
        if (TatooineReplication.TryBodyOfSession(command.Session, out var body, out _))
        {
            // The client re-reads its craft on the items revision.
            _craftBodies.Add(body);
        }

        _craftsApplied.Add((command.Session, command.Seq));
    }

    private void CraftEnhanceFromCommand(Transaction tx, SubscriptionsCommands subs, in ClientCommand<CraftEnhance> command, long worldTick)
    {
        if (!TatooineReplication.TryBodyOfSession(command.Session, out var body, out var character)
            || !_crafts.TryGetValue((long)character.RawValue, out var craft))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNone);
            return;
        }

        if (!Schematics.TryGet(craft.S.Schematic, out var s))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNone);
            return;
        }

        var addon = AddonOf(s.Kind);
        if (craft.S.AddonMk != 0 || addon == 0)
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNoAddon);
            return;
        }

        var mk = command.Value.Mk;
        var crafter = (long)character.RawValue;
        if (mk is < 1 or > 5 || UnitsOf(ItemsOf(crafter), addon, mk) < 1)
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftBadInputs);
            return;
        }

        if (ItemTransfer(tx, crafter, ItemLedger.World, addon, mk, 1, ItemMoveKind.Craft, (long)craft.Id.RawValue, worldTick) != 1)
        {
            _craftAbort = true;
            _craftsApplied.Add((command.Session, command.Seq));
            return;
        }

        var state = craft.S;
        state.AddonMk = mk;
        tx.OpenMut(craft.Id).Set(CraftArch.State, state);
        _crafts[crafter] = (craft.Id, state);
        _craftCounts.Enhanced++;
        _craftBodies.Add(body);
        _craftsApplied.Add((command.Session, command.Seq));
    }

    /// <summary>The bench fee of a result, whole credits: 3 % of its market value, rounded down (crafting-catalog.md § 15.4).</summary>
    private static long BenchFee(ushort output, byte mk) => MarketValues.MilliOf(output, mk) * Schematics.BenchFeePerMille / 1000 / 1000;

    /// <summary>The add-on a schematic's kind takes (§ 13.3); 0 for none.</summary>
    private static ushort AddonOf(SchematicKind kind) => kind switch
    {
        SchematicKind.Weapon => ItemCatalog.AddonWeapon,
        SchematicKind.Armor => ItemCatalog.AddonArmor,
        SchematicKind.Potion => ItemCatalog.AddonAdditive,
        _ => 0,
    };

    // ── Finish ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void CraftFinishFromCommand(Transaction tx, SubscriptionsCommands subs, in ClientCommand<CraftFinish> command, long worldTick)
    {
        if (!TatooineReplication.TryBodyOfSession(command.Session, out var body, out var character)
            || !_crafts.TryGetValue((long)character.RawValue, out var craft) || craft.S.Schematic != command.Value.Schematic)
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNone);
            return;
        }

        var state = craft.S;
        var crafter = state.Crafter;
        if (!Schematics.TryGet(state.Schematic, out var s))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNone);
            return;
        }

        var items = ItemsOf(crafter);
        if (s.MakesInstance ? FreeSlot(items) == 0 : RoomFor(items, state.Output, state.Mk) < 1)
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNoRoom);
            return;
        }

        var fee = BenchFee(state.Output, state.Mk);
        if (fee > 0 && BalanceOf(tx, crafter) < fee)
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNotEnough);
            return;
        }

        // Validated: the result, the fee, the craft gone — one commit.
        var reference = (long)craft.Id.RawValue;
        var result = s.MakesInstance ? ResultOf(s, in state) : default;
        var made = s.MakesInstance
            ? !MintInstance(tx, crafter, state.Output, state.Mk, in result, ItemMoveKind.Make, reference, worldTick).IsNull
            : ItemTransfer(tx, ItemLedger.World, crafter, state.Output, state.Mk, 1, ItemMoveKind.Make, reference, worldTick) == 1;
        if (!made || (fee > 0 && !Transfer(tx, crafter, EconomyLedger.World, fee, TransferKind.Bench, reference, worldTick)))
        {
            _craftAbort = true;
            _craftsApplied.Add((command.Session, command.Seq));
            return;
        }

        tx.Destroy(craft.Id);
        _crafts.Remove(crafter);
        _craftCounts.Finished++;
        _craftCounts.Fees += fee;
        NoteMade(crafter, state.Output, state.Mk, in result, s.MakesInstance, worldTick);
        _craftBodies.Add(body);
        if (fee > 0)
        {
            _craftWalletsMoved.Add(body);
        }

        _craftsApplied.Add((command.Session, command.Seq));
    }

    /// <summary>
    /// A finished craft's instance: its lines as its category stores them (<see cref="Equipment"/>), its condition with its life factor, its crafter and
    /// serial. A weapon's condition is combat-milliseconds; an armour piece's, damage points for its band (§ 9.1); a potion's, its doses.
    /// </summary>
    internal static ItemInstance ResultOf(Schematic s, in CraftState state)
    {
        Span<int> q = stackalloc int[Crafting.MaxLines];
        Span<int> f = stackalloc int[Crafting.MaxLines];
        GetLines(in state, q, f);
        Span<double> v = stackalloc double[Crafting.MaxLines];
        for (var i = 0; i < s.Lines.Length; i++)
        {
            v[i] = Crafting.ValueOf(s, i, q[i], f[i], state.AddonMk);
        }

        var life = Crafting.LifeMilli(state.Crafter, state.Serial);
        var r = new ItemInstance { Crafter = state.Crafter, Serial = unchecked((int)state.Serial), LifeMilli = 1000 };
        switch (s.Kind)
        {
            case SchematicKind.Weapon:
                r.Stat0 = (int)Math.Round(v[0]);
                r.Stat1 = (int)Math.Round(v[1]);
                r.Stat2 = (int)Math.Round(v[2]);
                r.MaxCondition = (int)Math.Round(v[3] * life / 1000);
                r.LifeMilli = life;
                r.Flags = ItemFlags.Pristine;
                break;
            case SchematicKind.Armor:
                r.Stat0 = (int)Math.Round(v[0]);
                r.Stat1 = (int)Math.Round(v[1]);
                r.MaxCondition = (int)Math.Round(ArmorCondition(state.Output, state.Mk, v[2]) * life / 1000);
                r.LifeMilli = life;
                r.Flags = ItemFlags.Pristine;
                break;
            case SchematicKind.Potion:
                r.Stat0 = (int)Math.Round(v[0]);
                r.Stat1 = s.Lines.Length > 1 ? (int)Math.Round(v[1]) : 0;
                r.MaxCondition = Schematics.DosesOf(state.Output);
                break;
            case SchematicKind.Structure:
                // An installation kit: its rate an hour and its hopper (Installations), its life factor carried to what it places.
                r.Stat0 = (int)Math.Round(v[0]);
                r.Stat1 = (int)Math.Round(v[1]);
                r.LifeMilli = life;
                r.Flags = ItemFlags.Pristine;
                break;
            default:
                // A part: its grade, per mille.
                r.Stat0 = (int)Math.Round(v[0]);
                break;
        }

        r.Condition = r.MaxCondition;
        return r;
    }

    /// <summary>
    /// An armour piece's condition in damage points (crafting-catalog.md § 9.1): its set's life, 120 to 600 combat-minutes by <paramref name="share"/>,
    /// at 15 on-level hits a minute of its band's mid level, by the piece's share of the set.
    /// </summary>
    internal static double ArmorCondition(ushort template, byte mk, double share)
    {
        var slot = Equipment.SlotOf(template);
        var pieceShare = slot == 0 ? 0 : EquipSlot.SharePerMille[slot - EquipSlot.First] / 1000d;
        var minutes = 120 + (480 * Math.Clamp(share, 0d, 1d));
        var hit = ContentLevels.CreatureHit((10 * Math.Max(1, (int)mk)) - 5, foe: false);
        return minutes * 15 * hit * pieceShare;
    }

    private static void GetLines(in CraftState s, Span<int> q, Span<int> f)
    {
        q[0] = s.Q0;
        q[1] = s.Q1;
        q[2] = s.Q2;
        q[3] = s.Q3;
        f[0] = s.F0;
        f[1] = s.F1;
        f[2] = s.F2;
        f[3] = s.F3;
    }

    private static void SetLines(ref CraftState s, ReadOnlySpan<int> q, ReadOnlySpan<int> f)
    {
        s.Q0 = q[0];
        s.Q1 = q[1];
        s.Q2 = q[2];
        s.Q3 = q[3];
        s.F0 = f[0];
        s.F1 = f[1];
        s.F2 = f[2];
        s.F3 = f[3];
    }
}
