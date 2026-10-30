using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Character level and XP (character-level.md § 4-5): the kills the combat drain pushed into <see cref="KillXpQueue"/>, paid to their killers' characters, and
/// the body each one drives brought up to its level.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kill XP through a side transaction, committed once a tick with a kill</b>, as the ledger's. Not committed, the tick's kill XP is lost — counted and
/// logged: XP is not conserved like credits, and replaying it would need a kill ledger nobody asked for. Mission XP is the desk's, atomic with the reward
/// (<see cref="GrantMissionXp"/>).
/// </para>
/// <para>
/// <b>The body is refreshed through the tick's transaction</b> after that commit (<see cref="RefreshBody"/>), the <c>BumpJournal</c> pattern: its level and its
/// XP byte, and on a level-up its max health, healing the increase.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>Body → character, filled once a tick with a kill. Reused: the system is serial.</summary>
    private readonly Dictionary<EntityId, EntityId> _progressCharacterOf = [];

    /// <summary>The bodies whose character gained XP this tick, with the progress to show; deduplicated by body.</summary>
    private readonly Dictionary<EntityId, CharacterProgress> _progressed = [];

    /// <summary>The level-ups of the tick, logged once committed: rare, so they may allocate their line.</summary>
    private readonly List<LevelUp> _levelUps = [];

    /// <summary>The desk's mission XP of its pass: (body, the character's progress), noted in its transaction.</summary>
    private readonly List<(EntityId Body, CharacterProgress Progress)> _deskProgress = [];

    /// <summary>
    /// The bodies the desk's committed mission XP is to reach: pushed by the desk after its commit, drained by <see cref="ProgressionTick"/> later in the
    /// same tick. Created on the schedule.
    /// </summary>
    internal EventQueue<ProgressRefresh> ProgressQueue { get; set; }

    private long _xpGrantedKills;
    private long _xpGrantedMissions;
    private long _killsGrey;
    private long _killsWorthless;
    private long _killsNoCharacter;
    private long _killsByCrowd;
    private long _killsNoKiller;
    private long _missionLairKills;
    private long _levelUpsTotal;
    private long _progressCommitsFailed;
    private long _killsDrained;
    private long _killXpLost;

    /// <summary>XP granted for kills, committed.</summary>
    public long XpGrantedKills => Interlocked.Read(ref _xpGrantedKills);

    /// <summary>XP granted for completed missions, committed with their rewards.</summary>
    public long XpGrantedMissions => Interlocked.Read(ref _xpGrantedMissions);

    /// <summary>Kills by a character of a victim ten or more levels below it: no XP.</summary>
    public long KillsGrey => Interlocked.Read(ref _killsGrey);

    /// <summary>Kills worth nothing for another reason than the level gap: a victim without a level or of no known kind, or <c>--xp-rate 0</c>.</summary>
    public long KillsWorthless => Interlocked.Read(ref _killsWorthless);

    /// <summary>Kills by a body whose character could not be opened: it is mapped, but the side transaction cannot see it (deleted, or not yet committed).</summary>
    public long KillsNoCharacter => Interlocked.Read(ref _killsNoCharacter);

    /// <summary>Kills by a body with no character — the scripted crowd's.</summary>
    public long KillsByCrowd => Interlocked.Read(ref _killsByCrowd);

    /// <summary>Kills whose blow was not a player's.</summary>
    public long KillsNoKiller => Interlocked.Read(ref _killsNoKiller);

    /// <summary>Mission lairs broken by their holder: their XP is the mission's, paid by the desk. A stranger's break is an ordinary lair kill.</summary>
    public long MissionLairKills => Interlocked.Read(ref _missionLairKills);

    /// <summary>Levels reached, from kills and missions.</summary>
    public long LevelUps => Interlocked.Read(ref _levelUpsTotal);

    /// <summary>Kill-XP transactions that did not commit; their grants are lost (<see cref="KillXpLost"/>).</summary>
    public long ProgressCommitsFailed => Interlocked.Read(ref _progressCommitsFailed);

    /// <summary>Kill events the XP system drained.</summary>
    public long KillsDrained => Interlocked.Read(ref _killsDrained);

    /// <summary>Kill XP lost to a failed commit.</summary>
    public long KillXpLost => Interlocked.Read(ref _killXpLost);

    /// <summary>The <c>--xp-rate</c> percent every grant is scaled by.</summary>
    public int XpRatePercent => _config.XpRatePercent;

    /// <summary>A level reached, for its log line.</summary>
    private readonly record struct LevelUp(EntityId Character, EntityId Body, int From, int To, int Xp, bool Mission, byte SourceKind, int SourceLevel);

    /// <summary>
    /// The <c>Progression</c> system: every kill of the tick, paid to its killer's character; the bodies of those who gained, brought up to date.
    /// </summary>
    public void ProgressionTick(TickContext ctx)
    {
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        // The mission desk's XP of this tick, committed with its rewards: its holders' bodies, brought up to it (§ 5).
        var refreshes = ProgressQueue.Count;
        if (refreshes != 0)
        {
            Span<ProgressRefresh> bodies = refreshes <= 16 ? stackalloc ProgressRefresh[16] : new ProgressRefresh[refreshes];
            var m = ProgressQueue.Drain(bodies);
            for (var i = 0; i < m; i++)
            {
                RefreshBody(ctx.Transaction, bodies[i].Body, in bodies[i].Progress);
            }
        }

        var pending = KillXpQueue.Count;
        if (pending == 0)
        {
            return;
        }

        // Drain THROWS on a short span rather than truncating, so the size comes from Count — see CombatResolveTick.
        Span<KillEvent> kills = pending <= 64 ? stackalloc KillEvent[64] : new KillEvent[pending];
        var n = KillXpQueue.Drain(kills);
        Interlocked.Add(ref _killsDrained, n);

        TatooineReplication.FillBodyCharacters(_progressCharacterOf);
        _progressed.Clear();
        _levelUps.Clear();
        long granted = 0;
        long grey = 0;
        long worthless = 0;
        long noCharacter = 0;
        long crowd = 0;
        long noKiller = 0;
        long missionLairs = 0;
        var rate = _config.XpRatePercent;

        using var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
        for (var i = 0; i < n; i++)
        {
            ref readonly var kill = ref kills[i];
            var missionLair = kill.VictimKind == KillVictimKind.Lair && !kill.Mission.IsNull;
            if (kill.Killer.IsNull)
            {
                if (missionLair)
                {
                    missionLairs++;
                }
                else
                {
                    noKiller++;
                }

                continue;
            }

            if (!_progressCharacterOf.TryGetValue(kill.Killer, out var character))
            {
                if (missionLair)
                {
                    missionLairs++;
                }
                else
                {
                    crowd++;
                }

                continue;
            }

            // A mission's lair broken by its holder: its XP is the mission's, paid by the desk with the reward in the transaction that ends it (§ 5). Broken
            // by anybody else, it is a lair like any other (orchestrator, review 2 L3), and the mission is drawn again for its holder.
            if (missionLair && !IsStranger(tx, in kill, character))
            {
                missionLairs++;
                continue;
            }

            if (!tx.TryOpenMut(character, out var entity))
            {
                noCharacter++;
                continue;
            }

            var progress = entity.Read(CharacterArch.Progress);
            var from = Math.Max(1, (int)progress.Level);
            var xp = Progression.KillXp(kill.VictimKind, kill.VictimLevel, from, rate);
            if (xp == 0)
            {
                if (kill.VictimLevel > 0 && kill.VictimLevel <= from - Progression.GreyGap)
                {
                    grey++;
                }
                else
                {
                    worthless++;
                }

                continue;
            }

            var applied = Progression.AddXp(ref progress, xp, mission: false);
            if (applied == 0)
            {
                continue;   // CL 50 with its XP full: nothing to write, nothing to show
            }

            if (progress.Level > from)
            {
                _levelUps.Add(new LevelUp(character, kill.Killer, from, progress.Level, progress.Xp, false, kill.VictimKind, kill.VictimLevel));
            }

            entity.Set(CharacterArch.Progress, progress);
            _progressed[kill.Killer] = progress;
            granted += applied;
        }

        Interlocked.Add(ref _killsGrey, grey);
        Interlocked.Add(ref _killsWorthless, worthless);
        Interlocked.Add(ref _killsNoCharacter, noCharacter);
        Interlocked.Add(ref _killsByCrowd, crowd);
        Interlocked.Add(ref _killsNoKiller, noKiller);
        Interlocked.Add(ref _missionLairKills, missionLairs);
        if (_progressed.Count == 0)
        {
            return;
        }

        if (!tx.Commit())
        {
            Interlocked.Increment(ref _progressCommitsFailed);
            Interlocked.Add(ref _killXpLost, granted);
            Console.WriteLine($"progression: commit failed, {_progressed.Count} kill grants lost (tick {ctx.TickNumber})");
            return;
        }

        Interlocked.Add(ref _xpGrantedKills, granted);
        var tick0 = ctx.Transaction;
        foreach (var (body, progress) in _progressed)
        {
            RefreshBody(tick0, body, in progress);
        }

        LogLevelUps(tick0);
    }

    /// <summary>
    /// A completed mission's XP, in the desk's transaction that pays its reward and ends it (character-level.md § 5): both or neither. The holder's body, when
    /// online, is refreshed once that commits — by <see cref="ProgressionTick"/>, later in the tick, the only writer of a body's level in the tick's last phases.
    /// </summary>
    /// <param name="tx">The desk's side transaction.</param>
    /// <param name="holder">The character holding the mission.</param>
    /// <param name="body">Its body, or <c>EntityId.Null</c> when offline.</param>
    /// <param name="level">The mission's level (<see cref="HeldMission.Level"/>).</param>
    private void GrantMissionXp(Transaction tx, EntityId holder, EntityId body, int level)
    {
        if (!tx.TryOpenMut(holder, out var entity))
        {
            return;
        }

        var progress = entity.Read(CharacterArch.Progress);
        var from = Math.Max(1, (int)progress.Level);
        var xp = Progression.MissionXp(level, from, _config.XpRatePercent);
        var applied = xp == 0 ? 0 : Progression.AddXp(ref progress, xp, mission: true);
        if (applied == 0)
        {
            return;
        }

        if (progress.Level > from)
        {
            _levelUps.Add(new LevelUp(holder, body, from, progress.Level, progress.Xp, true, 0, level));
        }

        entity.Set(CharacterArch.Progress, progress);
        _pendingMissionXp += applied;
        if (!body.IsNull)
        {
            _deskProgress.Add((body, progress));
        }
    }

    /// <summary>
    /// The missions the desk completed this tick, with their holders: what <see cref="IsStranger"/> reads for a mission whose entity the desk has destroyed
    /// before Progression sees its lair's KillEvent. Cleared at the desk's every tick.
    /// </summary>
    private readonly Dictionary<EntityId, EntityId> _missionsEndedThisTick = [];

    /// <summary>
    /// Whether a mission lair's break by <paramref name="character"/> is a stranger's — an ordinary lair kill, paid in XP and in the wild cache. The one
    /// predicate the XP and the loot systems share, and the desk's own: not when the holder's blow was among those that broke it
    /// (<see cref="KillEvent.OwnerHit"/>), for then the desk completes the mission and pays it; not when the killer is the holder; and not when the holder
    /// cannot be told (the mission gone, and not completed this tick) — nothing rather than twice.
    /// </summary>
    private bool IsStranger(Transaction tx, in KillEvent kill, EntityId character)
    {
        if (kill.OwnerHit != 0)
        {
            return false;
        }

        var mission = kill.Mission;
        EntityId holder;
        if (tx.TryOpen(mission, out var held))
        {
            holder = held.Read(HeldMissionArch.Mission).Holder;
        }
        else if (!_missionsEndedThisTick.TryGetValue(mission, out holder))
        {
            return false;
        }

        return holder != character;
    }

    /// <summary>Mission XP granted in the desk transaction under way; counted when it commits.</summary>
    private long _pendingMissionXp;

    /// <summary>Starts the desk's bookkeeping of mission XP for one desk transaction.</summary>
    private void BeginDeskProgress()
    {
        _deskProgress.Clear();
        _levelUps.Clear();
        _pendingMissionXp = 0;
    }

    /// <summary>After the desk's commit: the XP counted, the holders' bodies handed to Progression, the level-ups logged. Nothing when it did not commit.</summary>
    private void RefreshDeskProgress(TickContext ctx, bool committed)
    {
        if (committed)
        {
            Interlocked.Add(ref _xpGrantedMissions, _pendingMissionXp);
            if (_deskProgress.Count != 0)
            {
                var writer = ctx.Writer(ProgressQueue);
                foreach (var (body, progress) in _deskProgress)
                {
                    writer.Push(new ProgressRefresh { Body = body, Progress = progress });
                }
            }

            LogLevelUps(ctx.Transaction);
        }

        _deskProgress.Clear();
        _levelUps.Clear();
        _pendingMissionXp = 0;
    }

    /// <summary>
    /// A body brought up to its character's progress, through the tick's transaction: its level and XP byte, and when the level rose its max health — healing
    /// the increase if it is up, never lowering anything. A body gone (logged out) is skipped: its next spawn reads the character.
    /// </summary>
    internal static void RefreshBody(Transaction tick0, EntityId body, in CharacterProgress p)
    {
        if (body.IsNull || !tick0.TryOpenMut(body, out var player))
        {
            return;
        }

        var look = player.Read(Player.Look);
        var xpByte = Progression.XpByte(in p);
        if (look.Level == p.Level && look.Xp == xpByte)
        {
            return;
        }

        if (p.Level > look.Level)
        {
            var v = player.Read(Player.Vitals);
            var max = Progression.HealthFor(p.Level);
            if (max > v.MaxHealth)
            {
                if (v.Down == Downed.Up && v.Health > 0)
                {
                    v.Health += max - v.MaxHealth;
                }

                v.MaxHealth = max;
                player.Set(Player.Vitals, v);
            }

            look.Level = p.Level;
        }

        look.Xp = xpByte;
        player.Set(Player.Look, look);
        TatooineReplication.Replicate(in player);
    }

    /// <summary>One line per level reached: who, bot or person, from and to, at what XP, and from what.</summary>
    private void LogLevelUps(Transaction tick0)
    {
        if (_levelUps.Count == 0)
        {
            return;
        }

        foreach (var up in _levelUps)
        {
            Interlocked.Add(ref _levelUpsTotal, up.To - up.From);
            var name = tick0.TryOpen(up.Character, out var character) ? character.Read(CharacterArch.Profile).Name.AsString : "?";
            var bot = !up.Body.IsNull && tick0.TryOpen(up.Body, out var body) && body.Read(Player.Look).Bot != 0;
            var source = up.Mission ? $"mission L{up.SourceLevel}" : $"kill {KindName(up.SourceKind)} L{up.SourceLevel}";
            for (var level = up.From; level < up.To; level++)
            {
                Console.WriteLine($"progression: {name} ({(bot ? "bot" : "person")}) CL {level} -> {level + 1} at {up.Xp:N0} XP ({source})");
            }
        }

        _levelUps.Clear();
    }

    private static string KindName(byte kind) => kind switch
    {
        KillVictimKind.Foe => "foe",
        KillVictimKind.Lair => "lair",
        _ => "beast",
    };

    /// <summary>Test hook: when set, every <see cref="KillEvent"/> the combat drain pushes into both kill queues, as it pushed it. Null in a run.</summary>
    internal System.Collections.Concurrent.ConcurrentQueue<KillEvent> KillTap { get; set; }

    /// <summary>Test hook: a death pushed into both kill queues as the combat drain pushes it (shared with inventory-loot's checks).</summary>
    internal void StageKill(TickContext ctx, in KillEvent kill)
    {
        ctx.Writer(KillXpQueue).Push(kill);
        ctx.Writer(KillLootQueue).Push(kill);
    }
}
