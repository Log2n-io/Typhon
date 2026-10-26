using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// The mission desk (claude/design/SwgTatooine/design/missions.md): terminals list offers, players take them, and each offer names a standing WILD lair —
/// taking it reserves that lair for the held mission, until the lair is broken by its holder or the mission is cancelled (missions.md § 8).
/// </summary>
/// <remarks>
/// <para>
/// <b>One serial system, and the race is settled by its order</b> (missions.md § 2): two takers of one offer are handled one after the other, and the second
/// finds the offer gone and is refused <see cref="TatooineReplication.MissionTaken"/>. Nothing optimistic, nothing aborts.
/// </para>
/// <para>
/// <b>Missions spawn nothing</b> (Loïc, 2026-10-26): an offer names a lair the world already has, at the level the desk chose for it — weighted by the levels
/// of the characters around the terminal, with one offer per band always listed — and a take sets that lair's <see cref="Lair.Mission"/>, which keeps the
/// crowd off it and the lair director from retiring it. A cancel, a completion or an orphaned reservation clears it again: the lair stays, a wild lair, and the
/// director retires it if it is broken or spent.
/// </para>
/// <para>
/// <b>Two transactions, for two kinds of write.</b> Offers, held missions and reservations go through a side transaction, the lair director's arrangement. What
/// lands on a body — the journal's revision — goes through the tick's own transaction. A reward is the economy's to pay (<see cref="EconomyLedger"/>).
/// </para>
/// <para>
/// <b>In memory: which offers each terminal lists and the lair each names, every held mission and the lair it holds, and how many each holder has.</b> Rebuilt
/// from the database on the first tick, so a reopened world is read rather than trusted; maintained by this system alone afterwards.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>How often the desk walks the held missions and the terminals, per second: lairs broken or gone, offers aged out or withdrawn.</summary>
    internal const int MissionPassHz = 2;

    private bool _deskLoaded;
    private uint _offerSerial;

    // One entry per terminal across every planet, the offers listed at each, and the city whose characters its offers are levelled for.
    private MissionTerminalRef[] _terminals = [];
    private List<OfferRef>[] _offers = [];
    private int[] _terminalCity = [];
    private readonly Dictionary<EntityId, int> _terminalOf = [];

    // Every lair a live offer names, with the terminal listing it, and every lair a held mission holds: an offer never names one of either, and a held
    // mission never takes one another holds — but may take an offered one (a held mission outranks a listing, review 5 M3).
    private readonly Dictionary<EntityId, int> _offeredLairs = [];
    private readonly HashSet<EntityId> _heldLairs = [];

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

    // The standing, unreserved wild lairs by planet and level ([realm × LevelSlots + level]), read once per pass at the start of the desk's tick, before it
    // writes anything (#1482), and kept for the takes until the next: a pick scans one level's lairs, and the lair it picks is checked again on the spot.
    private List<LairCandidate>[] _wild = [];
    private int _wildRealms;
    private const int LevelSlots = ContentLevels.MaxLevel + 1;

    // The demand: per city of every planet, its characters by combat level (0 unused), counted at most once a second when an offer is to be levelled.
    private int[] _cityBase = [];
    private int[][] _demand = [];
    private long _demandTick = long.MinValue / 2;

    private long _missionCommits;
    private long _missionEntities;
    private int _deskWrites;
    private long _missionsRetargeted;
    private long _missionsLostLair;
    private long _missionOffersWithdrawn;
    private long _missionTakesRefused;
    private long _missionTakesAboveLevel;
    private long _missionOffersListed;
    private long _missionReservationsOrphaned;
    private bool _withdrawAllOffers;

    /// <summary>An offer as the desk remembers it: its entity, when it ages out, the lair it names and that lair's level.</summary>
    private struct OfferRef
    {
        public EntityId Id;
        public long ExpireTick;
        public EntityId Lair;
        public byte Level;
    }

    /// <summary>A standing wild lair an offer or a re-target may name, as the pass's walk read it.</summary>
    private struct LairCandidate
    {
        public EntityId Id;
        public float X;
        public float Z;
        public long ExpireTick;
        public byte Level;
        public byte Kind;
    }

    /// <summary>
    /// Ticks on which the desk committed anything: each one a durable write — offers are structural, a held mission Versioned — like the lair director's
    /// (<c>CausalityChecks.AC2</c>).
    /// </summary>
    public long MissionCommits => Interlocked.Read(ref _missionCommits);

    /// <summary>Entities the desk spawned, wrote or destroyed: what its WAL records are bounded by.</summary>
    public long MissionEntities => Interlocked.Read(ref _missionEntities);

    /// <summary>Offers listed since start, replacements included.</summary>
    public long MissionOffersListed => Interlocked.Read(ref _missionOffersListed);

    /// <summary>Offers taken off their terminal because the lair they named fell, or was reserved, before anybody took them.</summary>
    public long MissionOffersWithdrawn => Interlocked.Read(ref _missionOffersWithdrawn);

    /// <summary>Held missions moved to another standing wild lair of their level: their lair broken by a stranger, or gone.</summary>
    public long MissionsRetargeted => Interlocked.Read(ref _missionsRetargeted);

    /// <summary>Held missions cancelled because their lair fell and no other of their level stood within reach (missions.md § 8, MS-6).</summary>
    public long MissionsLostLair => Interlocked.Read(ref _missionsLostLair);

    /// <summary>Reservations found at load with no held mission behind them, and released: the lair stays, wild again.</summary>
    public long MissionReservationsOrphaned => Interlocked.Read(ref _missionReservationsOrphaned);

    /// <summary>Takes refused: taken already, at the cap, too far, down, above the taker's level.</summary>
    public long MissionTakesRefused => Interlocked.Read(ref _missionTakesRefused);

    /// <summary>Takes refused because the offer's level was above the taker's combat level + <see cref="TatooineData.MissionLevelAbove"/>.</summary>
    public long MissionTakesAboveLevel => Interlocked.Read(ref _missionTakesAboveLevel);

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

    /// <summary>For a check: the desk's next pass withdraws every offer and lists them all again, levelled by a fresh count of the characters around.</summary>
    internal void WithdrawAllOffersForChecks() => _withdrawAllOffers = true;

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
        _deskCounts = default;
        _deskLairRemovals.Clear();
        _deskCompleted.Clear();
        _takesApplied.Clear();
        _cancelsApplied.Clear();
        // The lairs an offer or a re-target may name, read once a pass before this transaction writes any — a cluster walk must not meet its own
        // transaction's writes (#1482) — and by the load's own walk on the tick it loads. A take tick reuses the pass's.
        if (!_deskLoaded)
        {
            LoadDesk(tx, tick);
            _deskLoaded = true;
        }
        else if (pass)
        {
            ReadCandidates(tx, tick);
        }

        if (pass)
        {
            TatooineReplication.FillCharacterBodies(_bodies);
            PassHeld(tx, tick0, tick);
            PassOffers(tx, tick0, tick);
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
            // Nothing it did happened: its counts and its index removals go with it, and the retry counts them once (review 4, L2).
            _deskCounts = default;
            _deskLairRemovals.Clear();
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

        PublishDeskCounts();
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

    /// <summary>
    /// What one desk transaction did, counted as it goes and published only once it commits (review 4, L2): a failed commit is retried on the next pass,
    /// and counting before the commit counted its completions twice — <c>missions.completed</c> drifting from <c>economy.rewardsPaid</c>, which is counted
    /// on commit.
    /// </summary>
    private struct DeskCounts
    {
        public long Issued;
        public long Taken;
        public long Completed;
        public long Abandoned;
        public long Cancelled;
        public long OffersListed;
        public long OffersWithdrawn;
        public long Retargeted;
        public long LostLair;
        public long AboveLevel;
        public long ReservationsOrphaned;
    }

    private DeskCounts _deskCounts;

    // The mission lairs of a world from before missions named wild lairs (spawned for a mission, of no site), destroyed at load: taken out of the planet's
    // lair index only once that commits.
    private readonly List<(ushort Realm, EntityId Lair)> _deskLairRemovals = [];

    /// <summary>Publishes a committed desk transaction's counts and its lair-index removals.</summary>
    private void PublishDeskCounts()
    {
        Interlocked.Add(ref _missionsIssued, _deskCounts.Issued);
        Interlocked.Add(ref _missionsTakenTotal, _deskCounts.Taken);
        Interlocked.Add(ref _missionsCompleted, _deskCounts.Completed);
        Interlocked.Add(ref _missionsCompletedTotal, _deskCounts.Completed);
        Interlocked.Add(ref _missionsAbandoned, _deskCounts.Abandoned);
        Interlocked.Add(ref _missionsCancelledTotal, _deskCounts.Cancelled);
        Interlocked.Add(ref _missionOffersListed, _deskCounts.OffersListed);
        Interlocked.Add(ref _missionOffersWithdrawn, _deskCounts.OffersWithdrawn);
        Interlocked.Add(ref _missionsRetargeted, _deskCounts.Retargeted);
        Interlocked.Add(ref _missionsLostLair, _deskCounts.LostLair);
        Interlocked.Add(ref _missionTakesAboveLevel, _deskCounts.AboveLevel);
        Interlocked.Add(ref _missionReservationsOrphaned, _deskCounts.ReservationsOrphaned);
        _deskCounts = default;

        foreach (var (realm, lair) in _deskLairRemovals)
        {
            PlanetIndexes[realm].Lairs.Remove(lair);
        }

        _deskLairRemovals.Clear();
    }

    // ── Loading ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The terminals from the world's indexes; the offers, held missions and reservations from the database, which a reopened world already has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An offer is matched to its lair by position</b>: its anchor is the lair's, and a wild lair never moves. One whose lair no longer stands wild there
    /// — retired while the world was down, or a world from before offers named lairs — is withdrawn, and the first pass lists a replacement. So the offer's
    /// stored layout did not change for it.
    /// </para>
    /// <para>
    /// <b>Every stored offer expiry is clamped to its longest life from now.</b> They are absolute ticks, and a reopened world's tick count starts again at
    /// 0: an offer listed late in a long run would otherwise stand for as long as that run lasted (#1495).
    /// </para>
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
        LoadTerminals();

        // Every walk first, every write after: a walk must not meet its own transaction's writes (#1482). The lair walk is also this tick's candidates.
        var wildAt = new Dictionary<(ushort Realm, float X, float Z), EntityId>();
        var reserved = new List<(EntityId Lair, ushort Realm, EntityId Mission, int Site)>();
        ClearCandidates();
        foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
        {
            var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
            var vitals = cluster.GetReadOnlySpan(CreatureLair.Vitals);
            var places = cluster.GetReadOnlySpan(CreatureLair.Bounds);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                ref readonly var s = ref spawners[slot];
                if (!s.Mission.IsNull)
                {
                    reserved.Add((cluster.GetEntityId(slot), cluster.Realm.Value, s.Mission, s.Site));
                }
                else if (s.Site != 0 && vitals[slot].Health > 0)
                {
                    wildAt[(cluster.Realm.Value, places[slot].X, places[slot].Z)] = cluster.GetEntityId(slot);
                    AddCandidate(cluster.GetEntityId(slot), cluster.Realm.Value, in s, places[slot].X, places[slot].Z, tick);
                }
            }
        }

        var offers = new List<(EntityId Id, MissionOfferInfo Info, ushort Realm)>();
        foreach (var cluster in tx.For<MissionOffer>().GetClusterEnumerator())
        {
            var infos = cluster.GetReadOnlySpan(MissionOffer.Info);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                offers.Add((cluster.GetEntityId(slot), infos[slot], cluster.Realm.Value));
            }
        }

        _held.Clear();
        _heldBy.Clear();
        _heldLairs.Clear();
        foreach (var cluster in tx.For<HeldMissionArch>().GetClusterEnumerator())
        {
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                _held.Add(cluster.GetEntityId(System.Numerics.BitOperations.TrailingZeroCount(bits)));
            }
        }

        // TryOpen, and the entry dropped, for one the walk yielded that is not there: a tick-path call must not throw.
        var heldOf = new Dictionary<EntityId, EntityId>();
        for (var i = _held.Count - 1; i >= 0; i--)
        {
            if (!tx.TryOpen(_held[i], out var heldEntity))
            {
                _held.RemoveAt(i);
                continue;
            }

            var m = heldEntity.Read(HeldMissionArch.Mission);
            CountHolder(m.Holder, +1);
            if (!m.Lair.IsNull)
            {
                heldOf[m.Lair] = _held[i];
                _heldLairs.Add(m.Lair);
            }
        }

        // Reservations. A lair of no site holding a mission is one spawned for it before missions named wild lairs: destroyed, and its mission re-targeted
        // by the first pass. A reservation no held mission stands behind is released — the lair stays, wild again.
        foreach (var (lairId, realm, mission, site) in reserved)
        {
            if (site == 0)
            {
                _deskWrites += 1 + (tx.TryOpen(lairId, out var legacy) ? legacy.Read(CreatureLair.Spawner).AliveCount : 0);
                tx.Destroy(lairId);
                _deskLairRemovals.Add((realm, lairId));
            }
            else if (!heldOf.TryGetValue(lairId, out var holder) || holder != mission)
            {
                Release(tx, lairId, mission);
                _heldLairs.Remove(lairId);
                _deskCounts.ReservationsOrphaned++;
            }
        }

        _offeredLairs.Clear();
        foreach (var (id, info, realm) in offers)
        {
            if (!_terminalOf.TryGetValue(info.Terminal, out var t)
                || !wildAt.TryGetValue((realm, info.AnchorX, info.AnchorZ), out var lair)
                || _heldLairs.Contains(lair)
                || !_offeredLairs.TryAdd(lair, t))
            {
                // A terminal the world no longer has, or a lair that no longer stands wild where the offer points: withdrawn, replaced by the first pass.
                tx.Destroy(id);
                _deskWrites++;
                continue;
            }

            var expire = Math.Min(info.ExpireTick, tick + offerLife);
            _offers[t].Add(new OfferRef { Id = id, ExpireTick = expire, Lair = lair, Level = info.Level });
            if (info.ExpireTick > expire)
            {
                // Written back, so the database says what the desk does: a second restart, or a check, reads the clamp.
                var offer = tx.OpenMut(id);
                var clamped = offer.Read(MissionOffer.Info);
                clamped.ExpireTick = expire;
                offer.Set(MissionOffer.Info, clamped);
                _deskWrites++;
            }
        }
    }

    /// <summary>The terminals of every planet, the city each lists for, and the per-city level counts the offers are levelled by.</summary>
    private void LoadTerminals()
    {
        var terminals = new List<MissionTerminalRef>();
        _cityBase = new int[PlanetIndexes.Length];
        var cities = 0;
        for (var planet = 0; planet < PlanetIndexes.Length; planet++)
        {
            terminals.AddRange(PlanetIndexes[planet].Terminals);
            _cityBase[planet] = cities;
            cities += PlanetIndexes[planet].Cities.Count;
        }

        _demand = new int[cities][];
        for (var c = 0; c < cities; c++)
        {
            _demand[c] = new int[ContentLevels.MaxLevel + 1];
        }

        _demandTick = long.MinValue / 2;
        _terminals = terminals.ToArray();
        _offerRouting = Dbe.ArchetypeIdOf<MissionOffer>();
        _offers = new List<OfferRef>[_terminals.Length];
        _terminalCity = new int[_terminals.Length];
        _terminalOf.Clear();
        for (var t = 0; t < _terminals.Length; t++)
        {
            _offers[t] = [];
            _terminalOf[_terminals[t].Id] = t;
            var city = CityAround(_terminals[t].Realm, _terminals[t].X, _terminals[t].Z, float.MaxValue);
            _terminalCity[t] = city < 0 ? -1 : _cityBase[_terminals[t].Realm] + city;
        }
    }

    // ── What an offer may name ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The standing wild lairs — a site's, unreserved, unbroken, not past their life — of every planet, by level. One walk of the lairs, as the lair
    /// director's pass, once a pass; whether an offer or a held mission names one is asked of a lair only when a pick meets it.
    /// </summary>
    private void ReadCandidates(Transaction tx, long tick)
    {
        ClearCandidates();
        foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
        {
            var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
            var vitals = cluster.GetReadOnlySpan(CreatureLair.Vitals);
            var places = cluster.GetReadOnlySpan(CreatureLair.Bounds);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                ref readonly var s = ref spawners[slot];
                if (s.Site != 0 && s.Mission.IsNull && vitals[slot].Health > 0)
                {
                    AddCandidate(cluster.GetEntityId(slot), cluster.Realm.Value, in s, places[slot].X, places[slot].Z, tick);
                }
            }
        }
    }

    /// <summary>Empties the candidates, sized for the planets.</summary>
    private void ClearCandidates()
    {
        _wildRealms = PlanetIndexes.Length;
        if (_wild.Length != _wildRealms * LevelSlots)
        {
            _wild = new List<LairCandidate>[_wildRealms * LevelSlots];
            for (var b = 0; b < _wild.Length; b++)
            {
                _wild[b] = [];
            }

            return;
        }

        foreach (var bucket in _wild)
        {
            bucket.Clear();
        }
    }

    /// <summary>One standing wild lair into its planet's and level's bucket; one past its life, or off every planet, is left out.</summary>
    private void AddCandidate(EntityId id, ushort realm, in Lair s, float x, float z, long tick)
    {
        if (realm >= _wildRealms || (s.ExpireTick != 0 && s.ExpireTick <= tick))
        {
            return;
        }

        var level = Math.Clamp(s.Level, 1, ContentLevels.MaxLevel);
        _wild[(realm * LevelSlots) + level].Add(new LairCandidate
        {
            Id = id,
            X = x,
            Z = z,
            ExpireTick = s.ExpireTick,
            Level = (byte)level,
            Kind = (byte)Math.Clamp(s.CreatureTemplate, 0, byte.MaxValue),
        });
    }

    /// <summary>
    /// <see cref="PickLair"/>, the lair it picks checked again on the spot: the candidates are the last pass's, and one that fell or was reserved since is
    /// dropped and the pick drawn again.
    /// </summary>
    private bool TryPickLair(Transaction tx, ushort realm, float x, float z, int lo, int hi, bool inWindowOnly, float reachM, bool inReachOnly, long liveTo,
        bool offered, ref Rng rng, out LairCandidate lair)
    {
        while (PickLair(realm, x, z, lo, hi, inWindowOnly, reachM, inReachOnly, liveTo, offered, ref rng, out var bucket, out var index))
        {
            var list = _wild[bucket];
            lair = list[index];
            if (StandsWild(tx, lair.Id))
            {
                return true;
            }

            list[index] = list[^1];
            list.RemoveAt(list.Count - 1);
        }

        lair = default;
        return false;
    }

    /// <summary>
    /// A standing wild lair of <paramref name="realm"/> to name, by its level first: one in <paramref name="lo"/>..<paramref name="hi"/>, else — unless
    /// <paramref name="inWindowOnly"/> — the nearest level to that window, scanned one level a side at a time; among equals, one within
    /// <paramref name="reachM"/> of the point, drawn uniformly, else the nearest. Its life must last to <paramref name="liveTo"/>. Never one a held mission
    /// holds; one an offer names only when <paramref name="offered"/>.
    /// </summary>
    private bool PickLair(ushort realm, float x, float z, int lo, int hi, bool inWindowOnly, float reachM, bool inReachOnly, long liveTo, bool offered,
        ref Rng rng, out int bucket, out int index)
    {
        bucket = -1;
        index = -1;
        if (realm >= _wildRealms)
        {
            return false;
        }

        lo = Math.Clamp(lo, 1, ContentLevels.MaxLevel);
        hi = Math.Clamp(hi, lo, ContentLevels.MaxLevel);
        var pick = new LevelScan
        {
            X = x,
            Z = z,
            Reach2 = reachM * reachM,
            InReachOnly = inReachOnly,
            LiveTo = liveTo,
            Offered = offered,
            Bucket = -1,
            BestBeyond = float.MaxValue,
        };
        var baseSlot = realm * LevelSlots;
        for (var level = lo; level <= hi; level++)
        {
            ScanLevel(baseSlot + level, ref pick, ref rng);
        }

        for (var gap = 1; pick.Bucket < 0 && !inWindowOnly && (lo - gap >= 1 || hi + gap <= ContentLevels.MaxLevel); gap++)
        {
            if (lo - gap >= 1)
            {
                ScanLevel(baseSlot + lo - gap, ref pick, ref rng);
            }

            if (hi + gap <= ContentLevels.MaxLevel)
            {
                ScanLevel(baseSlot + hi + gap, ref pick, ref rng);
            }
        }

        bucket = pick.Bucket;
        index = pick.Index;
        return bucket >= 0;
    }

    /// <summary>What a pick has found so far, across the levels it scans.</summary>
    private struct LevelScan
    {
        public float X;
        public float Z;
        public float Reach2;
        public bool InReachOnly;
        public long LiveTo;
        public bool Offered;
        public int Bucket;
        public int Index;
        public float BestBeyond;
        public int Ties;
    }

    /// <summary>One level's lairs into a pick: within reach, drawn uniformly (reservoir); beyond it, the nearest.</summary>
    private void ScanLevel(int bucket, ref LevelScan pick, ref Rng rng)
    {
        var all = CollectionsMarshal.AsSpan(_wild[bucket]);
        for (var i = 0; i < all.Length; i++)
        {
            ref readonly var c = ref all[i];
            if ((c.ExpireTick != 0 && c.ExpireTick < pick.LiveTo) || _heldLairs.Contains(c.Id) || (!pick.Offered && _offeredLairs.ContainsKey(c.Id)))
            {
                continue;
            }

            var d2 = Distance2(c.X, c.Z, pick.X, pick.Z);
            if (d2 > pick.Reach2 && pick.InReachOnly)
            {
                continue;
            }

            // Within reach every lair is as good as another, so the offers spread over the lairs there; beyond it, the nearest.
            var beyond = d2 <= pick.Reach2 ? 0f : d2;
            if (beyond < pick.BestBeyond)
            {
                pick.Bucket = bucket;
                pick.Index = i;
                pick.BestBeyond = beyond;
                pick.Ties = 1;
            }
            else if (beyond == pick.BestBeyond && rng.NextInt(0, ++pick.Ties) == 0)
            {
                pick.Bucket = bucket;
                pick.Index = i;
            }
        }
    }

    /// <summary>The characters around each city, by combat level, counted again when the last count is a second old.</summary>
    /// <remarks>
    /// A character counts for a city when its body stands in the city or in its ramp (<see cref="ContentLevels.RampM"/>): the people who would walk to its
    /// terminals. Its level is its body's <see cref="PlayerLook.Level"/>, the CL the character system keeps on it.
    /// </remarks>
    private void CountDemand(Transaction tick0, long tick)
    {
        if (tick - _demandTick < Math.Max(1, _config.TickRateHz))
        {
            return;
        }

        _demandTick = tick;
        foreach (var counts in _demand)
        {
            Array.Clear(counts);
        }

        var ramp = ContentLevels.RampM * _config.ContentScale;
        foreach (var body in _bodies.Values)
        {
            if (!tick0.TryOpen(body, out var player))
            {
                continue;
            }

            var realm = player.Read(Player.Realm).Value;
            if (realm >= PlanetIndexes.Length)
            {
                continue;   // a room, a dungeon, space: not around any city's terminals
            }

            var at = player.Read(Player.Bounds);
            var city = CityAround(realm, at.X, at.Z, ramp);
            if (city >= 0)
            {
                _demand[_cityBase[realm] + city][Math.Clamp((int)player.Read(Player.Look).Level, 1, ContentLevels.MaxLevel)]++;
            }
        }
    }

    /// <summary>The city of a planet whose edge is nearest a point, if the point is within <paramref name="beyondEdgeM"/> of it; -1 otherwise.</summary>
    private int CityAround(ushort realm, float x, float z, float beyondEdgeM)
    {
        var cities = PlanetIndexes[realm].Cities;
        var best = -1;
        var bestEdge = float.MaxValue;
        for (var i = 0; i < cities.Count; i++)
        {
            var c = cities[i];
            var edge = MathF.Sqrt(Distance2(c.X, c.Z, x, z)) - c.Radius;
            if (edge <= beyondEdgeM && edge < bestEdge)
            {
                best = i;
                bestEdge = edge;
            }
        }

        return best;
    }

    /// <summary>
    /// The level an offer is listed at by demand: a character drawn from those around the terminal's city, and a level drawn uniformly in what that character
    /// is shown — its CL − <see cref="TatooineData.MissionLevelBelow"/> to CL + <see cref="TatooineData.MissionLevelAbove"/>, which is returned too. Nobody
    /// around: a new character's, CL 1.
    /// </summary>
    internal static (int Level, int Lo, int Hi) DemandLevel(int[] demand, ref Rng rng)
    {
        var cl = 1;
        var total = 0;
        if (demand != null)
        {
            for (var l = 1; l < demand.Length; l++)
            {
                total += demand[l];
            }
        }

        if (total > 0)
        {
            var pick = rng.NextInt(0, total);
            for (var l = 1; l < demand.Length; l++)
            {
                pick -= demand[l];
                if (pick < 0)
                {
                    cl = l;
                    break;
                }
            }
        }

        var lo = Math.Max(1, cl - TatooineData.MissionLevelBelow);
        var hi = Math.Min(ContentLevels.MaxLevel, cl + TatooineData.MissionLevelAbove);
        return (rng.NextInt(lo, hi + 1), lo, hi);
    }

    // ── The pass ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every held mission: its holder gone or not, its lair standing, broken by its holder or by a stranger, or gone.</summary>
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
            if (body.IsNull && !tx.TryOpen(m.Holder, out _))
            {
                // A character that is gone ends the mission; its reservation goes with it.
                EndMission(tx, i, id, in m, completed: false);
                continue;
            }

            Lair lair = default;
            var health = 0;
            var holds = false;
            var exists = false;
            if (!m.Lair.IsNull && tx.TryOpen(m.Lair, out var lairEntity))
            {
                exists = true;
                holds = Holds(lairEntity, id, out lair, out health);
            }

            if (!holds)
            {
                // No longer this mission's to hold — gone, or standing unreserved and broken — unless another mission reserved it since.
                if (!exists || lair.Mission.IsNull || lair.Mission == id)
                {
                    _heldLairs.Remove(m.Lair);
                }

                // Its lair gone — destroyed with a world from before, or reserved by nothing any more — or taken by another: another of its level near it.
                Retarget(tx, tick0, i, id, ref m, body, tick);
                continue;
            }

            if (lair.Mission.IsNull)
            {
                // Standing wild where the mission says it holds it: a release that did not commit with its mission's end. Reserved again.
                Reserve(tx, m.Lair, id, body);
                continue;
            }

            if (health <= 0 && lair.OwnerHit != 0)
            {
                // Done, and paid where it was broken (R5) — once its holder is online to be paid: a holder who broke it and logged out is paid at the login.
                // Paid in this transaction, and ended in it only if paid; its cache and its XP with it (inventory-loot.md § 3.4, character-level.md § 5).
                if (!body.IsNull && Reward(tx, m.Holder, body, id, m.Reward, _deskWorldTick))
                {
                    MintCache(tx, m.Holder, body, id, in m, _deskWorldTick);
                    GrantMissionXp(tx, m.Holder, body, m.Level);
                    EndMission(tx, i, id, in m, completed: true);
                    _deskCompleted.Add((m.Holder, body, id, m.Kind, m.Level, m.Reward));
                }

                continue;
            }

            if (health <= 0)
            {
                // Broken by a stranger alone (Q3): the stranger was paid as for a wild lair (character-level.md D4a); this mission moves to another.
                Release(tx, m.Lair, id);
                Retarget(tx, tick0, i, id, ref m, body, tick);
                continue;
            }

            if (lair.Owner.Id != body)
            {
                // Logged in again, or out: the blows that count are its holder's body's, and an offline holder has none.
                var mut = tx.OpenMut(m.Lair);
                lair.Owner = body;
                mut.Set(CreatureLair.Spawner, lair);
                _deskWrites++;
            }
        }
    }

    /// <summary>Whether a lair is the one a held mission holds — reserved for it, or standing unreserved where the mission says it is.</summary>
    private static bool Holds(EntityRef lairEntity, EntityId mission, out Lair lair, out int health)
    {
        lair = lairEntity.Read(CreatureLair.Spawner);
        health = lairEntity.Read(CreatureLair.Vitals).Health;
        return lair.Site != 0 && (lair.Mission == mission || (lair.Mission.IsNull && health > 0));
    }

    /// <summary>
    /// Moves a held mission to another standing wild lair of its level within <see cref="TatooineData.MissionReachM"/> of where its lair stood — one no offer
    /// names first, else one an offer names, whose offer is withdrawn (a held mission outranks a listing; the pass lists a replacement), never one another
    /// mission holds; with none there, cancels it (missions.md § 8, MS-6).
    /// </summary>
    private void Retarget(Transaction tx, Transaction tick0, int at, EntityId id, ref HeldMission m, EntityId body, long tick)
    {
        var unplaced = m.LairX == 0f && m.LairZ == 0f;
        var x = unplaced ? m.AnchorX : m.LairX;
        var z = unplaced ? m.AnchorZ : m.LairZ;
        var rng = new Rng(Salt(tick, id.EntityKey, 0x41C64E6Du));
        var reach = TatooineData.MissionReachM * _config.ContentScale;
        var found = TryPickLair(tx, m.Realm, x, z, m.Level, m.Level, inWindowOnly: true, reach, inReachOnly: true, liveTo: 0, offered: false, ref rng,
                out var c)
            || TryPickLair(tx, m.Realm, x, z, m.Level, m.Level, inWindowOnly: true, reach, inReachOnly: true, liveTo: 0, offered: true, ref rng, out c);
        if (!found)
        {
            EndMission(tx, at, id, in m, completed: false);
            _deskCounts.LostLair++;
            if (!body.IsNull)
            {
                BumpJournal(tick0, body);
            }

            return;
        }

        if (_offeredLairs.ContainsKey(c.Id))
        {
            WithdrawOffer(tx, c.Id);
        }

        Reserve(tx, c.Id, id, body);
        m.Lair = c.Id;
        m.Kind = c.Kind;
        m.LairX = c.X;
        m.LairZ = c.Z;
        tx.OpenMut(id).Set(HeldMissionArch.Mission, m);
        _deskWrites++;
        _deskCounts.Retargeted++;
        if (!body.IsNull)
        {
            BumpJournal(tick0, body);
        }
    }

    /// <summary>Every terminal: offers past their time, or whose lair fell or was reserved, taken off; the list topped back up.</summary>
    private void PassOffers(Transaction tx, Transaction tick0, long tick)
    {
        var all = _withdrawAllOffers;
        _withdrawAllOffers = false;
        if (all)
        {
            _demandTick = long.MinValue / 2;
        }

        for (var t = 0; t < _terminals.Length; t++)
        {
            var list = _offers[t];
            for (var k = list.Count - 1; k >= 0; k--)
            {
                var o = list[k];
                if (!all && tick < o.ExpireTick && StandsWild(tx, o.Lair))
                {
                    continue;
                }

                if (tx.TryOpen(o.Id, out _))
                {
                    tx.Destroy(o.Id);
                    _deskWrites++;
                }

                if (tick < o.ExpireTick)
                {
                    _deskCounts.OffersWithdrawn++;
                }

                // Its lair, still standing wild — the offer only aged out — may be named again in this very pass.
                _offeredLairs.Remove(o.Lair);
                list.RemoveAt(k);
            }

            TopUp(tx, tick0, t, tick);
        }
    }

    /// <summary>Takes the offer naming a lair off its terminal: a held mission is taking the lair (<see cref="Retarget"/>).</summary>
    private void WithdrawOffer(Transaction tx, EntityId lairId)
    {
        if (!_offeredLairs.Remove(lairId, out var t))
        {
            return;
        }

        var list = _offers[t];
        for (var k = list.Count - 1; k >= 0; k--)
        {
            if (list[k].Lair == lairId)
            {
                if (tx.TryOpen(list[k].Id, out _))
                {
                    tx.Destroy(list[k].Id);
                    _deskWrites++;
                }

                list.RemoveAt(k);
                _deskCounts.OffersWithdrawn++;
            }
        }
    }

    /// <summary>Whether a lair stands, unbroken and unreserved: what an offer naming it promises.</summary>
    private static bool StandsWild(Transaction tx, EntityId lairId)
        => tx.TryOpen(lairId, out var lair) && lair.Read(CreatureLair.Vitals).Health > 0 && lair.Read(CreatureLair.Spawner).Mission.IsNull;

    /// <summary>
    /// Lists offers at a terminal until it has <see cref="TatooineData.MissionOffersPerTerminal"/>, or the lairs run out: first one per band it lists none of
    /// (the floor), then by demand.
    /// </summary>
    private void TopUp(Transaction tx, Transaction tick0, int t, long tick)
    {
        var list = _offers[t];
        if (list.Count >= TatooineData.MissionOffersPerTerminal)
        {
            return;
        }

        Span<int> bands = stackalloc int[6];
        Span<bool> noneInBand = stackalloc bool[6];
        foreach (var o in list)
        {
            bands[ContentLevels.Band(o.Level)]++;
        }

        CountDemand(tick0, tick);
        var guard = 0;
        while (list.Count < TatooineData.MissionOffersPerTerminal && guard++ < TatooineData.MissionOffersPerTerminal)
        {
            if (!TryListOffer(tx, t, tick, bands, noneInBand, out var offer))
            {
                return;
            }

            list.Add(offer);
            bands[ContentLevels.Band(offer.Level)]++;
        }
    }

    /// <summary>
    /// One new offer at terminal <paramref name="t"/>, naming a standing wild lair: its level chosen first — a band the terminal lists nothing of, else by the
    /// characters around (<see cref="DemandLevel"/>) — then a lair of that level, preferably within <see cref="TatooineData.MissionReachM"/>.
    /// </summary>
    private bool TryListOffer(Transaction tx, int t, long tick, Span<int> bands, Span<bool> noneInBand, out OfferRef offer)
    {
        var terminal = _terminals[t];
        var serial = ++_offerSerial;
        var rng = new Rng(Salt(tick, ((long)serial << 20) ^ terminal.Id.EntityKey, 0x2D5A4E1Bu));
        var reach = TatooineData.MissionReachM * _config.ContentScale;
        var liveTo = tick + (long)(TatooineData.MissionOfferLifeMinSeconds * _config.TickRateHz);
        var found = false;
        LairCandidate lair = default;

        // The floor: every band listed at least once, so higher content stays in view whoever is around (Loïc, 2026-10-26).
        for (var band = 1; band <= 5 && !found; band++)
        {
            if (bands[band] > 0 || noneInBand[band])
            {
                continue;
            }

            var floor = ContentLevels.BandFloor(band);
            found = TryPickLair(tx, terminal.Realm, terminal.X, terminal.Z, floor, Math.Min(ContentLevels.MaxLevel, floor + 9), inWindowOnly: true, reach,
                inReachOnly: false, liveTo, offered: false, ref rng, out lair);
            noneInBand[band] = !found;
        }

        if (!found)
        {
            // The level first; a lair of it within reach, else one anywhere in the character's window within reach, else the nearest to that window.
            var (level, lo, hi) = DemandLevel(_terminalCity[t] < 0 ? null : _demand[_terminalCity[t]], ref rng);
            found = TryPickLair(tx, terminal.Realm, terminal.X, terminal.Z, level, level, inWindowOnly: true, reach, inReachOnly: true, liveTo,
                    offered: false, ref rng, out lair)
                || TryPickLair(tx, terminal.Realm, terminal.X, terminal.Z, lo, hi, inWindowOnly: true, reach, inReachOnly: true, liveTo, offered: false,
                    ref rng, out lair)
                || TryPickLair(tx, terminal.Realm, terminal.X, terminal.Z, lo, hi, inWindowOnly: false, reach, inReachOnly: false, liveTo, offered: false,
                    ref rng, out lair);
        }

        if (!found)
        {
            offer = default;
            return false;
        }

        _offeredLairs[lair.Id] = t;
        var dx = lair.X - terminal.X;
        var dz = lair.Z - terminal.Z;
        var dist = MathF.Sqrt((dx * dx) + (dz * dz));
        var life = TatooineData.MissionOfferLifeMinSeconds + (rng.NextFloat() * TatooineData.MissionOfferLifeSpanSeconds);
        var expire = tick + (long)(life * _config.TickRateHz);
        var info = new MissionOfferInfo
        {
            Terminal = terminal.Id,
            Serial = serial,
            Kind = lair.Kind,
            Difficulty = 1,
            Level = lair.Level,
            Octant = TatooineData.OctantOf(dx, dz),
            Reward = TatooineData.MissionReward(lair.Level),
            DistanceM = (int)(MathF.Round(dist / 100f) * 100f),
            AnchorX = lair.X,
            AnchorZ = lair.Z,

            // Off the list before its lair's life runs out: an offer never names a lair the director is about to retire.
            ExpireTick = lair.ExpireTick == 0 ? expire : Math.Min(expire, lair.ExpireTick),
        };
        var place = default(MissionOfferPlacement);
        place.SetAt(terminal.X, terminal.Z);
        var id = tx.Spawn<MissionOffer>(
            MissionOffer.Bounds.Set(in place),
            MissionOffer.Info.Set(in info),
            MissionOffer.Region.Set(new MissionOfferRegion(terminal.Region)),
            MissionOffer.Realm.Set(new MissionOfferRealm(terminal.Realm)));
        _deskWrites++;
        _deskCounts.OffersListed++;
        offer = new OfferRef { Id = id, ExpireTick = info.ExpireTick, Lair = lair.Id, Level = lair.Level };
        return true;
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

        // Above what this character is shown, it is not theirs to take (Loïc, 2026-10-26): its level is read off the character, not the message.
        if (offer.Read(MissionOffer.Info).Level > CombatLevelOf(tx, character) + TatooineData.MissionLevelAbove)
        {
            Refuse(subs, in command, TatooineReplication.MissionAboveLevel);
            _deskCounts.AboveLevel++;
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
        else
        {
            Refuse(subs, in command, TatooineReplication.MissionTaken);
        }
    }

    /// <summary>A character's combat level, 1 when it has none yet.</summary>
    private static int CombatLevelOf(Transaction tx, EntityId character)
        => tx.TryOpen(character, out var c) ? Math.Max(1, (int)c.Read(CharacterArch.Progress).Level) : 1;

    /// <summary>
    /// Takes an offer: it leaves its terminal, a replacement is listed there, the held mission is created and the lair the offer names is reserved for it —
    /// one transaction.
    /// </summary>
    /// <returns><see langword="false"/> when the offer is not there to take, or its lair fell or was reserved since it was listed — withdrawn then.</returns>
    private bool Take(Transaction tx, Transaction tick0, EntityId offerId, EntityId holder, EntityId body, long tick)
    {
        if (!tx.TryOpen(offerId, out var offer) || !_terminalOf.TryGetValue(offer.Read(MissionOffer.Info).Terminal, out var t))
        {
            return false;
        }

        var info = offer.Read(MissionOffer.Info);
        var realm = offer.Read(MissionOffer.Realm).Value;
        var list = _offers[t];
        var lairId = EntityId.Null;
        for (var k = list.Count - 1; k >= 0; k--)
        {
            if (list[k].Id == offerId)
            {
                lairId = list[k].Lair;
                _offeredLairs.Remove(lairId);
                list.RemoveAt(k);
            }
        }

        tx.Destroy(offerId);
        _deskWrites++;
        if (lairId.IsNull || !StandsWild(tx, lairId))
        {
            // Its lair broken or reserved since the last pass: the offer is withdrawn and replaced, and this take finds it taken.
            _deskCounts.OffersWithdrawn++;
            TopUp(tx, tick0, t, tick);
            return false;
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
            LairX = info.AnchorX,
            LairZ = info.AnchorZ,
            Lair = lairId,
            TakenTick = tick,
            Realm = realm,
        };
        var id = tx.Spawn<HeldMissionArch>(HeldMissionArch.Mission.Set(in m));
        _deskWrites++;
        Reserve(tx, lairId, id, body);
        _held.Add(id);
        CountHolder(holder, +1);
        _deskCounts.Issued++;
        _deskCounts.Taken++;
        BumpJournal(tick0, body);

        // The replacement after the reservation, so it cannot name the lair just taken.
        TopUp(tx, tick0, t, tick);
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
        EndMission(tx, at, id, in m, completed: false);
        BumpJournal(tick0, body);
        _cancelsApplied.Add(command);
    }

    /// <summary>Destroys a held mission, releases its lair — which stays in the world, wild again — and forgets it.</summary>
    private void EndMission(Transaction tx, int at, EntityId id, in HeldMission m, bool completed)
    {
        Release(tx, m.Lair, id);
        tx.Destroy(id);
        _deskWrites++;
        _held.RemoveAt(at);
        CountHolder(m.Holder, -1);

        // Its lair's KillEvent reaches Progression and Loot after this destroy: who held it is read here (character-level.md § 2, L3). Every end, not
        // only a completion: a stranger whose break left the mission no lair, or broke it on the tick its holder cancelled, is still a stranger to pay
        // (review 5 M2). A completion's breaker is told apart by OwnerHit first.
        _missionsEndedThisTick[id] = m.Holder;
        if (completed)
        {
            _deskCounts.Completed++;
        }
        else
        {
            _deskCounts.Abandoned++;
            _deskCounts.Cancelled++;
        }
    }

    // ── Reservations ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reserves a wild lair for a held mission: <see cref="Lair.Mission"/> keeps the crowd off it (<c>TryNearestWildLair</c>, <c>TryAcquireTarget</c>) and
    /// the lair director from retiring it; <see cref="Lair.Owner"/> is the body whose blows complete it.
    /// </summary>
    private void Reserve(Transaction tx, EntityId lairId, EntityId mission, EntityId body)
    {
        if (!tx.TryOpenMut(lairId, out var lair))
        {
            return;
        }

        var spawner = lair.Read(CreatureLair.Spawner);
        spawner.Mission = mission;
        spawner.Owner = body;
        spawner.OwnerHit = 0;
        lair.Set(CreatureLair.Spawner, spawner);
        _heldLairs.Add(lairId);
        _deskWrites++;
    }

    /// <summary>
    /// Releases a lair a mission held: wild again, its owner and the owner's blow forgotten. Broken, or past its life, the lair director retires it on its
    /// next pass like any wild lair; standing, the crowd may fight it.
    /// </summary>
    private void Release(Transaction tx, EntityId lairId, EntityId mission)
    {
        if (lairId.IsNull || !tx.TryOpenMut(lairId, out var lair))
        {
            _heldLairs.Remove(lairId);
            return;
        }

        var spawner = lair.Read(CreatureLair.Spawner);
        if (spawner.Mission != mission)
        {
            return;   // another mission's now: its reservation is not this one's to release
        }

        _heldLairs.Remove(lairId);

        spawner.Mission = EntityId.Null;
        spawner.Owner = EntityId.Null;
        spawner.OwnerHit = 0;
        lair.Set(CreatureLair.Spawner, spawner);
        _deskWrites++;
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
}
