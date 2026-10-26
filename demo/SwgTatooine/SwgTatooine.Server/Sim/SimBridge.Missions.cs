using SwgTatooine.Server.Terrain;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// The mission desk (claude/design/SwgTatooine/design/missions.md): terminals list offers, players take them, and each held mission has a lair
/// drawn for it — while its holder is online — until the lair is broken by its holder or the mission is cancelled.
/// </summary>
/// <remarks>
/// <para>
/// <b>One serial system, and the race is settled by its order</b> (missions.md § 2): two takers of one offer are handled one after the other, and the second
/// finds the offer gone and is refused <see cref="TatooineReplication.MissionTaken"/>. Nothing optimistic, nothing aborts.
/// </para>
/// <para>
/// <b>Two transactions, for two kinds of write.</b> Offers, held missions and lairs — spawns and destroys — go through a side transaction, the lair director's
/// arrangement. What lands on a body — the journal's revision — goes through the tick's own transaction. A reward is the economy's to pay (<see cref="EconomyLedger"/>):
/// the desk hands it over, and the <c>Ledger</c> system credits the holder's wallet later in the same tick.
/// </para>
/// <para>
/// <b>In memory: which offers each terminal lists, every held mission, and how many each holder has.</b> Rebuilt from the database on the first tick, so a
/// reopened world is read rather than trusted; maintained by this system alone afterwards, which is the only writer of all three.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>How often the desk walks the held missions and the terminals, per second: lairs to draw, broken, expired; offers aged out.</summary>
    internal const int MissionPassHz = 2;

    /// <summary>Tries at an anchor, or at a lair round one, before the desk waits for its next pass.</summary>
    private const int MissionPlacementTries = 20;

    private bool _deskLoaded;
    private uint _offerSerial;

    // One entry per terminal across every planet, and the offers listed at each.
    private MissionTerminalRef[] _terminals = [];
    private List<(EntityId Id, long ExpireTick)>[] _offers = [];
    private readonly Dictionary<EntityId, int> _terminalOf = [];

    // Every held mission, and how many each character holds.
    private readonly List<EntityId> _held = [];

    // The world tick of the desk's tick under way: a reward's ledger row carries it.
    private long _deskWorldTick;

    // What this tick's desk transaction did, applied outside it only once it commits — or answered, when it does not: the completions (told to their
    // bodies and the journal), and the takes and cancels (refused with MissionFailed, so their clients are not left waiting).
    private readonly List<(EntityId Holder, EntityId Body, EntityId Mission, byte Kind, byte Level, int Reward)> _deskCompleted = [];
    private readonly List<ClientCommand<TakeMission>> _takesApplied = [];
    private readonly List<ClientCommand<AbandonMission>> _cancelsApplied = [];
    private bool _deskEverLoaded;
    private readonly Dictionary<EntityId, int> _heldBy = [];

    // Per pass: every character with a body online, and that body.
    private readonly Dictionary<EntityId, EntityId> _bodies = [];

    private long _missionLairsDrawn;
    private long _missionCommits;
    private long _missionEntities;
    private int _deskWrites;
    private long _missionLairsRedrawn;
    private long _missionTakesRefused;
    private long _missionOffersListed;
    private long _missionLairsOrphaned;

    /// <summary>
    /// Ticks on which the desk committed anything: each one a durable write — offers and lairs are structural, a held mission Versioned — like the lair
    /// director's (<c>CausalityChecks.AC2</c>).
    /// </summary>
    public long MissionCommits => Interlocked.Read(ref _missionCommits);

    /// <summary>Entities the desk spawned, wrote or destroyed, a lair's creatures included: what its WAL records are bounded by.</summary>
    public long MissionEntities => Interlocked.Read(ref _missionEntities);

    /// <summary>Offers listed since start, replacements included.</summary>
    public long MissionOffersListed => Interlocked.Read(ref _missionOffersListed);

    /// <summary>Lairs drawn for a mission: at the take, at a login, after a move.</summary>
    public long MissionLairsDrawn => Interlocked.Read(ref _missionLairsDrawn);

    /// <summary>Lairs replaced because they expired, or were broken by somebody other than their mission's holder (missions.md, R6 and Q3).</summary>
    public long MissionLairsRedrawn => Interlocked.Read(ref _missionLairsRedrawn);

    /// <summary>Mission lairs found at load with no held mission naming them, and destroyed.</summary>
    public long MissionLairsOrphaned => Interlocked.Read(ref _missionLairsOrphaned);

    /// <summary>Takes refused: taken already, at the cap, too far, down.</summary>
    public long MissionTakesRefused => Interlocked.Read(ref _missionTakesRefused);

    /// <summary>How many missions are held now.</summary>
    public int MissionsHeld => _held.Count;

    /// <summary>How many offers are listed now.</summary>
    public int MissionOffersStanding
    {
        get
        {
            var n = 0;
            foreach (var list in _offers)
            {
                n += list.Count;
            }

            return n;
        }
    }

    /// <summary>Takes and cancels, and on the pass cadence the held missions' lairs and the terminals' lists.</summary>
    public void MissionDeskTick(TickContext ctx)
    {
        _missionsEndedThisTick.Clear();
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var tick = ctx.TickNumber;
        var subs = ctx.Subscriptions;
        var passTicks = Math.Max(1, _config.TickRateHz / MissionPassHz);
        var pass = !_deskLoaded || tick % passTicks == 0;
        var takes = subs == null ? default : subs.Commands<TakeMission>();
        var abandons = subs == null ? default : subs.Commands<AbandonMission>();
        if (!pass && takes.Count == 0 && abandons.Count == 0)
        {
            return;
        }

        using var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
        var tick0 = ctx.Transaction;
        _deskWorldTick = ctx.WorldTick;
        BeginDeskProgress();
        _deskCompleted.Clear();
        _takesApplied.Clear();
        _cancelsApplied.Clear();
        if (!_deskLoaded)
        {
            LoadDesk(tx, tick);
            _deskLoaded = true;
        }

        // The pass first, while nothing in this transaction has been destroyed yet: a cluster walk later in it could still yield an entity it destroyed
        // (#1482).
        if (pass)
        {
            TatooineReplication.FillCharacterBodies(_bodies);
            PassHeld(tx, tick0, tick);
            PassOffers(tx, tick);
        }

        if (subs != null)
        {
            // Cancels first: a cancel and a take sent together at the cap mean "swap", and a take first would be refused MissionCap. A cancel cannot name a
            // mission taken this tick — its id is only in a journal read after it — so nothing is lost the other way.
            foreach (var command in abandons)
            {
                AbandonFromCommand(tx, tick0, subs, in command);
            }

            foreach (var command in takes)
            {
                TakeFromCommand(tx, tick0, subs, in command, tick);
            }
        }

        // A desk transaction that did not commit took its completions and their rewards with it: the desk is read again from the database, where those
        // missions still stand, and pays them on the next pass. Its takes and cancels are refused, so their clients hear it.
        FlushItemHead(tx);
        var committed = tx.Commit();
        EndDeskEconomy(tick0, committed);
        EndDeskItems(tick0, committed);
        RefreshDeskProgress(ctx, committed);
        if (!committed)
        {
            _deskLoaded = false;
            _deskWrites = 0;
            foreach (var command in _takesApplied)
            {
                subs?.Reject(command, TatooineReplication.MissionFailed);
            }

            foreach (var command in _cancelsApplied)
            {
                subs?.Reject(command, TatooineReplication.MissionFailed);
            }

            return;
        }

        foreach (var (holder, body, mission, kind, level, reward) in _deskCompleted)
        {
            Interlocked.Increment(ref _missionRewards);
            if (tick0.TryOpenMut(body, out var player))
            {
                var state = player.Read(Player.State);
                state.MissionsCompleted++;
                player.Set(Player.State, state);
            }

            MissionJournal.NoteCompleted(holder, mission, kind, level, reward);
            BumpJournal(tick0, body);
        }

        if (_deskWrites > 0)
        {
            Interlocked.Increment(ref _missionCommits);
            Interlocked.Add(ref _missionEntities, _deskWrites);
            _deskWrites = 0;
        }
    }

    // ── Loading ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The terminals from the world's indexes; the offers and held missions from the database, which a reopened world already has.</summary>
    /// <remarks>
    /// <b>Every stored expiry is clamped to its longest life from now.</b> They are absolute ticks, and a reopened world's tick count starts again at 0: an
    /// offer listed late in a long run would otherwise stand for as long as that run lasted, its terminal's list frozen. Clamping gives each a full life again,
    /// which is all a restart can promise.
    /// </remarks>
    private void LoadDesk(Transaction tx, long tick)
    {
        // The completions remembered for the journal are this process's, not the database's: forgotten on the first load only — a reload after a
        // failed commit would wipe every other character's unread "Mission complete".
        if (!_deskEverLoaded)
        {
            MissionJournal.ResetCompletions();
            _deskEverLoaded = true;
        }

        var offerLife = (long)((TatooineData.MissionOfferLifeMinSeconds + TatooineData.MissionOfferLifeSpanSeconds) * _config.TickRateHz);
        var lairLife = (long)(TatooineData.MissionLairLifeSeconds * _config.TickRateHz);
        var terminals = new List<MissionTerminalRef>();
        foreach (var index in PlanetIndexes)
        {
            terminals.AddRange(index.Terminals);
        }

        _terminals = terminals.ToArray();
        _offerRouting = Dbe.ArchetypeIdOf<MissionOffer>();
        _offers = new List<(EntityId, long)>[_terminals.Length];
        _terminalOf.Clear();
        for (var t = 0; t < _terminals.Length; t++)
        {
            _offers[t] = [];
            _terminalOf[_terminals[t].Id] = t;
        }

        var clamped = new List<EntityId>();
        foreach (var cluster in tx.For<MissionOffer>().GetClusterEnumerator())
        {
            var infos = cluster.GetReadOnlySpan(MissionOffer.Info);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                ref readonly var info = ref infos[slot];
                _offerSerial = Math.Max(_offerSerial, info.Serial);
                if (_terminalOf.TryGetValue(info.Terminal, out var t))
                {
                    _offers[t].Add((cluster.GetEntityId(slot), Math.Min(info.ExpireTick, tick + offerLife)));
                    if (info.ExpireTick > tick + offerLife)
                    {
                        clamped.Add(cluster.GetEntityId(slot));
                    }
                }
                else
                {
                    // A terminal the world no longer has: its offer could never be taken.
                    tx.Destroy(cluster.GetEntityId(slot));
                    _deskWrites++;
                }
            }
        }

        // Written back, so the database says what the desk does: a second restart, or a check, reads the clamp.
        foreach (var id in clamped)
        {
            var offer = tx.OpenMut(id);
            var info = offer.Read(MissionOffer.Info);
            info.ExpireTick = tick + offerLife;
            offer.Set(MissionOffer.Info, info);
            _deskWrites++;
        }

        _held.Clear();
        _heldBy.Clear();
        var heldLairs = new HashSet<EntityId>();
        foreach (var cluster in tx.For<HeldMissionArch>().GetClusterEnumerator())
        {
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                _held.Add(cluster.GetEntityId(System.Numerics.BitOperations.TrailingZeroCount(bits)));
            }
        }

        // Opened after the walk, not in it: the clamps below write, and a walk must not see its own transaction's writes (#1482). TryOpen, and the
        // entry dropped, for one the walk yielded that is not there: a tick-path call must not throw.
        for (var i = _held.Count - 1; i >= 0; i--)
        {
            var id = _held[i];
            if (!tx.TryOpen(id, out var heldEntity))
            {
                _held.RemoveAt(i);
                continue;
            }

            var m = heldEntity.Read(HeldMissionArch.Mission);
            CountHolder(m.Holder, +1);
            if (!m.Lair.IsNull && tx.TryOpen(m.Lair, out var lairEntity))
            {
                heldLairs.Add(m.Lair);
                var spawner = lairEntity.Read(CreatureLair.Spawner);
                if (spawner.ExpireTick > tick + lairLife)
                {
                    spawner.ExpireTick = tick + lairLife;
                    tx.OpenMut(m.Lair).Set(CreatureLair.Spawner, spawner);
                    _deskWrites++;
                }
            }
        }

        // A mission lair no held mission names — what Lair.Mission is for: nothing ends a mission without its lair in the same transaction, so this is a
        // world from before a fix, or one a tool wrote. Left, it would stand for ever: the lair director retires only the sites' lairs.
        var orphans = new List<(EntityId Id, ushort Realm)>();
        foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
        {
            var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                var lairId = cluster.GetEntityId(slot);
                if (!spawners[slot].Mission.IsNull && !heldLairs.Contains(lairId))
                {
                    orphans.Add((lairId, cluster.Realm.Value));
                }
            }
        }

        foreach (var (lairId, _) in orphans)
        {
            DestroyLair(tx, lairId);
            _missionLairsOrphaned++;
        }
    }

    // ── The pass ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every held mission: its holder online or not, its lair standing, broken, expired or missing.</summary>
    private void PassHeld(Transaction tx, Transaction tick0, long tick)
    {
        for (var i = _held.Count - 1; i >= 0; i--)
        {
            var id = _held[i];
            if (!tx.TryOpen(id, out var e))
            {
                _held.RemoveAt(i);
                continue;
            }

            var m = e.Read(HeldMissionArch.Mission);
            var body = BodyOf(in m);
            if (body.IsNull)
            {
                // Offline (Q6): its lair goes, and comes back at the next login. A character that is gone ends the mission.
                if (!tx.TryOpen(m.Holder, out _))
                {
                    EndMission(tx, i, in m, completed: false);
                    continue;
                }

                // Except one its holder broke: that mission is done, and is paid at the login, when the pass sees it with its body again.
                if (!m.Lair.IsNull && !BrokenByHolder(tx, m.Lair))
                {
                    DestroyLair(tx, m.Lair);
                    m.Lair = EntityId.Null;
                    tx.OpenMut(id).Set(HeldMissionArch.Mission, m);
                    _deskWrites++;
                }

                continue;
            }

            if (m.Lair.IsNull || !tx.TryOpen(m.Lair, out var lairEntity))
            {
                // Taken while the lair could not be placed, logged back in, or its lair gone some other way: draw one.
                if (TryDrawLair(tx, id, ref m, body, tick))
                {
                    tx.OpenMut(id).Set(HeldMissionArch.Mission, m);
                    _deskWrites++;
                    BumpJournal(tick0, body);
                }

                continue;
            }

            var lair = lairEntity.Read(CreatureLair.Spawner);
            var vitals = lairEntity.Read(CreatureLair.Vitals);
            if (vitals.Health <= 0 && lair.OwnerHit != 0)
            {
                // Done, and paid where it was broken (R5): no going back to the terminal. The journal is told it was completed, not just that it left.
                // Paid in this transaction, and ended in it only if paid: a holder whose wallet opens on the next tick is paid by the next pass. Its cache
                // is minted in the same transaction (inventory-loot.md § 3.4): the reward, the cache and the end commit together, once.
                if (Reward(tx, m.Holder, body, id, m.Reward, _deskWorldTick))
                {
                    MintCache(tx, m.Holder, body, id, in m, _deskWorldTick);
                    // Its XP with its reward, in this transaction: both or neither (character-level.md § 5).
                    GrantMissionXp(tx, m.Holder, body, m.Level);
                    EndMission(tx, i, in m, completed: true);
                    _deskCompleted.Add((m.Holder, body, id, m.Kind, m.Level, m.Reward));
                }

                continue;
            }

            if (vitals.Health <= 0 || (lair.ExpireTick != 0 && tick >= lair.ExpireTick))
            {
                // Broken by somebody else alone (Q3), or its life ran out (R6): another lair round the same anchor, the mission still held.
                DestroyLair(tx, m.Lair);
                m.Lair = EntityId.Null;
                if (TryDrawLair(tx, id, ref m, body, tick))
                {
                    Interlocked.Increment(ref _missionLairsRedrawn);
                }

                tx.OpenMut(id).Set(HeldMissionArch.Mission, m);
                _deskWrites++;
                BumpJournal(tick0, body);
                continue;
            }

            if (lair.Owner.Id != body)
            {
                // Logged out and back in between two passes: the lair stood through it and now belongs to the new body.
                var mut = tx.OpenMut(m.Lair);
                lair.Owner = body;
                mut.Set(CreatureLair.Spawner, lair);
                _deskWrites++;
            }
        }
    }

    /// <summary>Every terminal: offers past their time taken off, and the list topped back up.</summary>
    private void PassOffers(Transaction tx, long tick)
    {
        for (var t = 0; t < _terminals.Length; t++)
        {
            var list = _offers[t];
            for (var k = list.Count - 1; k >= 0; k--)
            {
                if (tick >= list[k].ExpireTick)
                {
                    if (tx.TryOpen(list[k].Id, out _))
                    {
                        tx.Destroy(list[k].Id);
                        _deskWrites++;
                    }

                    list.RemoveAt(k);
                }
            }

            TopUp(tx, t, tick);
        }
    }

    /// <summary>Lists offers at a terminal until it has <see cref="TatooineData.MissionOffersPerTerminal"/>, or the draws run out.</summary>
    private void TopUp(Transaction tx, int t, long tick)
    {
        var list = _offers[t];
        var guard = 0;
        while (list.Count < TatooineData.MissionOffersPerTerminal && guard++ < TatooineData.MissionOffersPerTerminal)
        {
            if (!TryListOffer(tx, t, tick, out var offer, out var expire))
            {
                return;
            }

            list.Add((offer, expire));
        }
    }

    /// <summary>One new offer at terminal <paramref name="t"/>: an anchor 0.5-2 km out, outside every city, inside the world.</summary>
    private bool TryListOffer(Transaction tx, int t, long tick, out EntityId offer, out long expire)
    {
        var terminal = _terminals[t];
        var serial = ++_offerSerial;
        var rng = new Rng(Salt(tick, ((long)serial << 20) ^ terminal.Id.EntityKey, 0x2D5A4E1Bu));
        var scale = _config.ContentScale;
        var minD = TatooineData.MissionMinDistanceM * scale;
        var spanD = (TatooineData.MissionMaxDistanceM - TatooineData.MissionMinDistanceM) * scale;
        var reach = (_config.WorldEdgeM * 0.5f * WorldBuilder.InsideEdge) - WorldBuilder.LairHalfM - (TatooineData.MissionScatterM * scale);
        for (var attempt = 0; attempt < MissionPlacementTries; attempt++)
        {
            var ang = rng.NextFloat() * MathF.PI * 2f;
            var dist = minD + (rng.NextFloat() * spanD);
            var dx = MathF.Sin(ang) * dist;
            var dz = MathF.Cos(ang) * dist;
            var ax = terminal.X + dx;
            var az = terminal.Z + dz;
            if (ax < -reach || ax > reach || az < -reach || az > reach || InsideCity(terminal.Realm, ax, az))
            {
                continue;
            }

            // Difficulty 1 for now (R7). The level is the content level at the anchor (content-levels.md § 4, until B5 picks it per band), and the kind is
            // drawn uniformly over every kind whose range covers it: the beasts' nests, the camps, the blue soldiers, who cover them all.
            const byte difficulty = 1;
            var level = PlanetIndexes[terminal.Realm].Levels.LevelAt(ax, az, ref rng);
            var info = new MissionOfferInfo
            {
                Terminal = terminal.Id,
                Serial = serial,
                Kind = (byte)KindAtLevel(level, ref rng),
                Difficulty = difficulty,
                Level = (byte)level,
                Octant = TatooineData.OctantOf(dx, dz),
                Reward = TatooineData.MissionReward(difficulty, dist / scale),
                DistanceM = (int)(MathF.Round(dist / 100f) * 100f),
                AnchorX = ax,
                AnchorZ = az,
                ExpireTick = tick + (long)((TatooineData.MissionOfferLifeMinSeconds + (rng.NextFloat() * TatooineData.MissionOfferLifeSpanSeconds))
                    * _config.TickRateHz),
            };
            var place = default(MissionOfferPlacement);
            place.SetAt(terminal.X, terminal.Z);
            offer = tx.Spawn<MissionOffer>(
                MissionOffer.Bounds.Set(in place),
                MissionOffer.Info.Set(in info),
                MissionOffer.Region.Set(new MissionOfferRegion(terminal.Region)),
                MissionOffer.Realm.Set(new MissionOfferRealm(terminal.Realm)));
            expire = info.ExpireTick;
            _deskWrites++;
            Interlocked.Increment(ref _missionOffersListed);
            return true;
        }

        offer = EntityId.Null;
        expire = 0;
        return false;
    }

    /// <summary>A lair kind drawn uniformly over every kind — the blue soldiers included — whose range covers <paramref name="level"/>.</summary>
    internal static int KindAtLevel(int level, ref Rng rng)
    {
        var n = 0;
        for (var k = 0; k < CreatureTemplates.Count; k++)
        {
            n += CreatureTemplates.CoversLevel(k, level) ? 1 : 0;
        }

        var pick = rng.NextInt(0, n);
        for (var k = 0; k < CreatureTemplates.Count; k++)
        {
            if (CreatureTemplates.CoversLevel(k, level) && pick-- == 0)
            {
                return k;
            }
        }

        return CreatureTemplates.BlueSoldier;
    }

    // ── Taking ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A person's take, validated against the world rather than the message.</summary>
    private void TakeFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<TakeMission> command, long tick)
    {
        if (!TatooineReplication.TryBodyOfSession(command.Session, out var body, out var character) || !tick0.TryOpen(body, out var player))
        {
            // No avatar bound yet, or a spectator: refused rather than dropped, so the client's pending take is answered.
            Refuse(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (player.Read(Player.Vitals).Down != Downed.Up)
        {
            Refuse(subs, in command, TatooineReplication.PlayerDown);
            return;
        }

        // TryResolve (SUB-26): a client may only name what it was shown. On the tick, in this system, it is correct (#1494): an offer a re-anchor took out
        // of the view — a door, a shuttle, a park — resolves again once its cell is delivered, and a take sent before then is refused "taken" like one
        // for an offer that is gone. The archetype, the serial and the reach below are the take's own rules, not the view's.
        if (!subs.TryResolve(command.Session, command.Value.Offer, out var offerId)
            || offerId.ArchetypeId != _offerRouting
            || !tx.TryOpen(offerId, out var offer)
            || offer.Read(MissionOffer.Info).Serial != command.Value.Serial)
        {
            Refuse(subs, in command, TatooineReplication.MissionTaken);
            return;
        }

        var at = player.Read(Player.Bounds);
        var listed = offer.Read(MissionOffer.Bounds);
        var reach = TatooineData.MissionTerminalReachM;
        if (player.Read(Player.Realm).Value != offer.Read(MissionOffer.Realm).Value
            || Distance2(at.X, at.Z, listed.X, listed.Z) > reach * reach)
        {
            Refuse(subs, in command, TatooineReplication.MissionTooFar);
            return;
        }

        if (HeldCount(character) >= TatooineData.MaxConcurrentMissions)
        {
            Refuse(subs, in command, TatooineReplication.MissionCap);
            return;
        }

        if (Take(tx, tick0, offerId, character, body, tick))
        {
            _takesApplied.Add(command);
        }
    }

    /// <summary>
    /// Takes an offer: it leaves its terminal, a replacement is listed there, the held mission is created and its lair drawn — one transaction.
    /// </summary>
    /// <returns><see langword="false"/> when the offer is not there to take.</returns>
    private bool Take(Transaction tx, Transaction tick0, EntityId offerId, EntityId holder, EntityId body, long tick)
    {
        if (!tx.TryOpen(offerId, out var offer))
        {
            return false;
        }

        var info = offer.Read(MissionOffer.Info);
        var realm = offer.Read(MissionOffer.Realm).Value;
        tx.Destroy(offerId);
        _deskWrites++;
        if (_terminalOf.TryGetValue(info.Terminal, out var t))
        {
            var list = _offers[t];
            for (var k = list.Count - 1; k >= 0; k--)
            {
                if (list[k].Id == offerId)
                {
                    list.RemoveAt(k);
                }
            }

            TopUp(tx, t, tick);
        }

        var m = new HeldMission
        {
            Holder = holder,
            Kind = info.Kind,
            Difficulty = info.Difficulty,
            Level = info.Level,
            Octant = info.Octant,
            DistanceM = info.DistanceM,
            Reward = info.Reward,
            AnchorX = info.AnchorX,
            AnchorZ = info.AnchorZ,
            TakenTick = tick,
            Realm = realm,
        };
        var id = tx.Spawn<HeldMissionArch>(HeldMissionArch.Mission.Set(in m));
        _deskWrites++;
        _held.Add(id);
        CountHolder(holder, +1);
        Interlocked.Increment(ref _missionsIssued);
        Interlocked.Increment(ref _missionsTakenTotal);

        // The lair now, while its holder is here. One that cannot be placed this tick is drawn by the next pass.
        if (TryDrawLair(tx, id, ref m, body, tick))
        {
            tx.OpenMut(id).Set(HeldMissionArch.Mission, m);
            _deskWrites++;
        }

        BumpJournal(tick0, body);
        return true;
    }

    // ── Cancelling and ending ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A person's cancel: the mission must be one its character holds.</summary>
    private void AbandonFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<AbandonMission> command)
    {
        if (!TatooineReplication.TryBodyOfSession(command.Session, out var body, out var character))
        {
            subs.Reject(command, TatooineReplication.NotInWorld);
            return;
        }

        var id = EntityId.FromRawValue(command.Value.Mission);
        var at = _held.IndexOf(id);
        if (at < 0 || !tx.TryOpen(id, out var e) || e.Read(HeldMissionArch.Mission).Holder != character)
        {
            subs.Reject(command, TatooineReplication.MissionUnknown);
            return;
        }

        var m = e.Read(HeldMissionArch.Mission);
        EndMission(tx, at, in m, completed: false);
        BumpJournal(tick0, body);
        _cancelsApplied.Add(command);
    }

    /// <summary>Destroys a held mission and its lair, and forgets it.</summary>
    private void EndMission(Transaction tx, int at, in HeldMission m, bool completed)
    {
        var id = _held[at];
        if (!m.Lair.IsNull)
        {
            DestroyLair(tx, m.Lair);
        }

        tx.Destroy(id);
        _deskWrites++;
        _held.RemoveAt(at);
        CountHolder(m.Holder, -1);

        if (completed)
        {
            // Its lair's KillEvent reaches Progression after this destroy: who held it is read here (character-level.md § 2, L3).
            _missionsEndedThisTick[id] = m.Holder;
            Interlocked.Increment(ref _missionsCompleted);
            Interlocked.Increment(ref _missionsCompletedTotal);
        }
        else
        {
            Interlocked.Increment(ref _missionsAbandoned);
            Interlocked.Increment(ref _missionsCancelledTotal);
        }
    }

    // ── Lairs ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws a lair of the mission's kind within <see cref="TatooineData.MissionScatterM"/> of its anchor, outside every city, its defenders round it, and
    /// records it on the mission (the caller writes the mission back).
    /// </summary>
    private bool TryDrawLair(Transaction tx, EntityId mission, ref HeldMission m, EntityId body, long tick)
    {
        var rng = new Rng(Salt(tick, mission.EntityKey, 0x41C64E6Du));
        var scatter = TatooineData.MissionScatterM * _config.ContentScale;
        for (var attempt = 0; attempt < MissionPlacementTries; attempt++)
        {
            var (x, z) = WorldBuilder.Inside(rng.PointInDisc(m.AnchorX, m.AnchorZ, scatter), _config);
            if (InsideCity(m.Realm, x, z))
            {
                continue;
            }

            var life = (long)(TatooineData.MissionLairLifeSeconds * _config.TickRateHz);
            var terrain = TerrainField.Shared(_config.ContentScale);
            // At the mission's level, whatever the content level here: the mission promised it, and a redraw keeps it (content-levels.md § 4).
            var lairId = WorldBuilder.SpawnLair(tx, ref rng, m.Kind, m.Level, x, z, site: 0, expireTick: tick + life, _config, terrain, m.Realm);
            var lair = tx.OpenMut(lairId);
            var spawner = lair.Read(CreatureLair.Spawner);
            spawner.Mission = mission;
            spawner.Owner = body;
            lair.Set(CreatureLair.Spawner, spawner);
            PlanetIndexes[m.Realm].Lairs.Add(lairId);
            _deskWrites += 2 + spawner.SpawnLimit;   // the lair, its occupants, and the lair written again with its mission
            m.Lair = lairId;
            m.LairX = x;
            m.LairZ = z;
            Interlocked.Increment(ref _missionLairsDrawn);
            return true;
        }

        return false;
    }

    /// <summary>Whether a mission's lair is standing broken after its holder hit it: the mission done, its pay waiting for a body.</summary>
    private static bool BrokenByHolder(Transaction tx, EntityId lairId)
        => tx.TryOpen(lairId, out var lair) && lair.Read(CreatureLair.Vitals).Health <= 0 && lair.Read(CreatureLair.Spawner).OwnerHit != 0;

    /// <summary>Destroys a mission's lair, its creatures with it through <see cref="CreatureHome"/>'s cascade.</summary>
    private void DestroyLair(Transaction tx, EntityId lairId)
    {
        if (!tx.TryOpen(lairId, out var lair))
        {
            return;
        }

        var realm = lair.Read(CreatureLair.Realm).Value;
        _deskWrites += 1 + lair.Read(CreatureLair.Spawner).AliveCount;   // its creatures go with it through the cascade
        tx.Destroy(lairId);
        PlanetIndexes[realm].Lairs.Remove(lairId);
    }

    // ── The body's side, through the tick's transaction ─────────────────────────────────────────────────────────────

    /// <summary>Moves the body's journal revision, so its client re-reads <c>GET /swg/missions</c>.</summary>
    private static void BumpJournal(Transaction tick0, EntityId body)
    {
        if (!tick0.TryOpenMut(body, out var player))
        {
            return;
        }

        var state = player.Read(Player.State);
        state.JournalRev++;
        player.Set(Player.State, state);
        TatooineReplication.Replicate(in player);
    }

    // ── Bookkeeping ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The body a mission's holder has online, or <c>EntityId.Null</c>.</summary>
    private EntityId BodyOf(in HeldMission m) => _bodies.TryGetValue(m.Holder, out var body) ? body : EntityId.Null;

    private int HeldCount(EntityId holder) => _heldBy.TryGetValue(holder, out var n) ? n : 0;

    private void CountHolder(EntityId holder, int delta)
    {
        var n = HeldCount(holder) + delta;
        if (n <= 0)
        {
            _heldBy.Remove(holder);
        }
        else
        {
            _heldBy[holder] = n;
        }
    }

    private void Refuse(SubscriptionsCommands subs, in ClientCommand<TakeMission> command, byte reason)
    {
        subs.Reject(command, reason);
        Interlocked.Increment(ref _missionTakesRefused);
    }

    private static float Distance2(float ax, float az, float bx, float bz) => ((ax - bx) * (ax - bx)) + ((az - bz) * (az - bz));

    /// <summary>The offer archetype's routing id, so a resolved netId can be told to be an offer before it is opened as one.</summary>
    private ushort _offerRouting;

    /// <summary>Is a point inside one of the NPC cities of a planet? [CORE3] a destroy mission may not place its target in one.</summary>
    private bool InsideCity(ushort realm, float x, float z)
    {
        var cities = PlanetIndexes[realm].Cities;
        for (var i = 0; i < cities.Count; i++)
        {
            var c = cities[i];
            var dx = c.X - x;
            var dz = c.Z - z;
            if ((dx * dx) + (dz * dz) < c.Radius * c.Radius)
            {
                return true;
            }
        }

        return false;
    }
}
