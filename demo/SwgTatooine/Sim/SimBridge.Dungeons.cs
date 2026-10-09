using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace SwgTatooine;

/// <summary>
/// Dungeon instances (Realms G2): a realm registered while the engine runs, when a party enters it, and unregistered once it is emptied — the lifecycle
/// run-time registration exists for.
/// </summary>
/// <remarks>
/// <para>Every <see cref="SimConfig.DungeonIntervalS"/> a dungeon opens in the next free slot: its realm is registered (a one-cell 64 m grid, like an
/// interior), pinned active while the party is inside, filled with <see cref="SimConfig.DungeonMobs"/> mobs, and <see cref="SimConfig.DungeonParty"/> idle
/// players of planet 0 are teleported in. After <see cref="SimConfig.DungeonStayS"/> the party is sent home, the mobs are destroyed and the realm is
/// unregistered; the next fence finds it empty and removes it.</para>
/// <para>Each slot's id is used once: an id unregistered in a session is not registrable again in it (RLM-06).</para>
/// </remarks>
public sealed partial class SimBridge
{
    private sealed class Dungeon
    {
        internal ushort Realm;
        internal long CloseTick;
        internal EntityId[] Party;
        internal (float X, float Z)[] Home;
        internal List<EntityId> Mobs;
        internal RealmObserver Pin;
    }

    private readonly List<Dungeon> _openDungeons = [];

    // Players a Close sent home THIS tick: their return is committed but not yet fenced, so they read Idle in realm 0 — never picked again before it lands
    // (review #4: a close and an open in one tick could teleport a returning player straight into the next dungeon).
    private readonly HashSet<EntityId> _closedThisTick = [];
    private int _nextDungeonSlot;
    private long _nextDungeonTick;
    private int _dungeonsOpened;
    private int _dungeonsClosed;
    private long _dungeonOpenTicks;
    private long _dungeonCloseTicks;
    private int _dungeonPartyTotal;

    /// <summary>The first dungeon slot's realm id; <see cref="SimConfig.Dungeons"/> ids from here.</summary>
    public int FirstDungeonRealm { get; init; }

    /// <summary>Dungeons opened and closed so far.</summary>
    public (int Opened, int Closed) DungeonTotals => (_dungeonsOpened, _dungeonsClosed);

    private bool IsDungeonRealm(ushort realm) => _config.Dungeons > 0 && realm >= FirstDungeonRealm && realm < FirstDungeonRealm + _config.Dungeons;

    /// <summary>Whether the slots a previous run left behind have been accounted for. See <see cref="SkipSlotsHeldFromAPreviousRun"/>.</summary>
    private bool _staleSlotsResolved;

    /// <summary>
    /// Step the slot counter past every dungeon id a previous run left registered, once, before the first open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A dungeon realm is registered at run time and persisted like any other, so a run that ends with one open leaves its catalog row Live.</b> The next
    /// open restores it registered — RLM-06: a Closing realm is resolved at open, a Live one is simply live again — and the slot counter, which is process
    /// state, restarts at zero and walks straight into it. <c>Register</c> then throws <i>"Realm N is already registered"</i> from inside the Dungeon system,
    /// which aborts the tick. It aborts every following tick too, so the world never advances: with <c>--serve</c> the process stays up, answers HTTP and
    /// accepts profiler attachments while sitting at tick 0 forever, which reads as a hang rather than as a crash. Reproduced in two runs of
    /// <c>--persist --planets 1 --dungeons 1</c>, and it does not need a kill — a measured run that simply ends with a dungeon open is enough.
    /// </para>
    /// <para>
    /// <b>The slots are skipped, not reclaimed, and that is deliberate.</b> Reclaiming means unregistering them, which under RLM-06 needs their contents gone
    /// before the realm can leave — and their contents include the previous party's <i>players</i>, real persisted entities that must be sent home rather than
    /// destroyed. That is a recovery path with its own design (find the stranded players, teleport them to realm 0, destroy the mobs, let the fence retire the
    /// realm), not a line in an opener. Skipping is the part that is unambiguously right: it costs this run the slots a previous run used, which the class
    /// already documents as the rule within a session, and it leaves nothing in a worse state than it found.
    /// </para>
    /// </remarks>
    private void SkipSlotsHeldFromAPreviousRun()
    {
        _staleSlotsResolved = true;
        if (_config.Dungeons <= 0 || Dbe == null)
        {
            return;
        }

        // Only a prefix can be skipped: the counter is a cursor, not a set, and a hole would be re-entered by the next open anyway.
        while (_nextDungeonSlot < _config.Dungeons
            && Dbe.Realms.IsRegistered(new RealmId((ushort)(FirstDungeonRealm + _nextDungeonSlot))))
        {
            _nextDungeonSlot++;
            _staleDungeonSlots++;
        }
    }

    /// <summary>Dungeon slots this run inherited already registered, and therefore never used. Reported so a short run does not read as "dungeons are broken".</summary>
    public int StaleDungeonSlots => _staleDungeonSlots;

    private int _staleDungeonSlots;

    /// <summary>Close the dungeons whose stay is over, then open the next one when it is due. Serial.</summary>
    public void DungeonTick(TickContext ctx)
    {
        // Before anything else, and once: the first open must not collide with a slot a previous run left registered.
        if (!_staleSlotsResolved)
        {
            SkipSlotsHeldFromAPreviousRun();
        }

        // Stopped by a client (TatooineReplication.SetPaused, a demo control). The simulation does nothing; replication,
        // the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (TatooineReplication.SimulationPaused)
        {
            return;
        }

        var tick = ctx.TickNumber;
        _closedThisTick.Clear();
        for (var i = _openDungeons.Count - 1; i >= 0; i--)
        {
            if (tick >= _openDungeons[i].CloseTick)
            {
                Close(ctx, _openDungeons[i]);
                _openDungeons.RemoveAt(i);
            }
        }

        if (_nextDungeonSlot < _config.Dungeons && tick >= _nextDungeonTick)
        {
            // Scheduled BEFORE the open: a throw inside it must not make every following tick burn the next slot (review #4).
            _nextDungeonTick = tick + Math.Max(1, (long)(_config.DungeonIntervalS * _config.TickRateHz));
            Open(ctx, tick);
        }
    }

    private void Open(TickContext ctx, long tick)
    {
        var start = Stopwatch.GetTimestamp();
        var realm = (ushort)(FirstDungeonRealm + _nextDungeonSlot++);
        var edge = WorldBuilder.InteriorEdgeM;
        // Under planet 0, whose players it draws from, and served like an interior: the party's sessions follow their players in (Realms G3).
        Dbe.Realms.Register(new RealmId(realm), new RealmConfig
        {
            Grid = Typhon.Engine.SpatialGridConfig.Flat(Vector2.Zero, new Vector2(edge, edge), edge),
            WhenUnobserved = RealmUnobserved.Sleep,
            UnobservedTickDivisor = 1,
            SleepAfterTicks = Math.Max(1, _config.TickRateHz),
            Parent = RealmId.Default,
            Replication = TatooineSim.DungeonReplication(_nextDungeonSlot - 1),
        });

        var dungeon = new Dungeon
        {
            Realm = realm,
            CloseTick = tick + Math.Max(1, (long)(_config.DungeonStayS * _config.TickRateHz)),
            Pin = Dbe.Realms.Observe(new RealmId(realm)),
            Mobs = [],
        };

        var party = new List<EntityId>(_config.DungeonParty);
        var home = new List<(float X, float Z)>(_config.DungeonParty);
        using (var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            // Mobs: wandering NPCs around the dungeon's centre.
            var c = edge * 0.5f;
            for (var m = 0; m < _config.DungeonMobs; m++)
            {
                // Not an entity draw — the mob does not exist yet. The realm and the member index are the logical identity, and both are stable.
                var mobKey = ((long)realm << 32) | (uint)m;
                var a = Hash01(Salt(tick, mobKey, 0x3E1F7A93u)) * MathF.PI * 2f;
                var r = edge * 0.25f * MathF.Sqrt(Hash01(Salt(tick, mobKey, 0x71C5B2D1u)));
                var x = c + (MathF.Cos(a) * r);
                var z = c + (MathF.Sin(a) * r);
                var bounds = default(NpcPlacement);
                // Inside a dungeon: its own realm, its own flat floor.
                bounds.SetAt(x, z, 0f, 0.5f);
                var ai = new NpcBrain { Mode = AiMode.Wander, HomeX = x, HomeZ = z, LeashRadius = 6f };
                var timers = new NpcTimers { MoveUntilTick = 0, RestUntilTick = tick + 1 + m };
                var move = new NpcMotion { SpeedMps = 1.2f };
                var key = new NpcRealm(realm);
                dungeon.Mobs.Add(tx.Spawn<CityNpc>(CityNpc.Bounds.Set(in bounds), CityNpc.Ai.Set(in ai), CityNpc.Timers.Set(in timers),
                    CityNpc.Move.Set(in move), CityNpc.Realm.Set(in key)));
            }

            // The party: idle players on planet 0, from a rotating start so every dungeon draws different ones.
            var players = _index.Players;
            var offset = (int)(Hash01(Salt(tick, realm, 0x5F3759DFu)) * Math.Max(1, players.Count));
            for (var i = 0; i < players.Count && party.Count < _config.DungeonParty; i++)
            {
                var id = players[(offset + i) % players.Count];

                // A possessed player is never drawn (SWG-01). Without this, a client that had connected and not yet sent an intent was still Idle, so a
                // dungeon took it, teleported it into another realm and pinned ActivityTicks at int.MaxValue / 2 — a pin PlayerThink respects and which
                // therefore survived the client disconnecting, leaving a player parked in a dungeon for the life of the process.
                if (_interiorPins.ContainsKey(id) || _closedThisTick.Contains(id) || !tx.TryOpen(id, out var candidate)
                    || candidate.Read(Player.Control).Kind != ControllerKind.InProcess
                    || candidate.Read(Player.Realm).Value != 0 || candidate.Read(Player.State).Activity != PlayerActivity.Idle)
                {
                    continue;   // read-only first: a rejected candidate's page is not dirtied
                }

                var player = tx.OpenMut(id);

                var p = player.Read(Player.Bounds);
                home.Add((p.X, p.Z));
                party.Add(id);
                var state = player.Read(Player.State);
                state.Activity = PlayerActivity.Inside;
                state.ActivityTicks = int.MaxValue / 2;
                player.Set(Player.State, state);   // PlayerThink leaves a dungeon party alone; Close sends it home
                var motion = player.Read(Player.Move);
                motion.VelX = 0f;
                motion.VelZ = 0f;
                player.Set(Player.Move, motion);
                var at = default(PlayerPlacement);
                at.SetAt(c + ((party.Count % 5) - 2) * 2f, 6f, 0f, p.HalfExtent);
                tx.Teleport(id, Player.Bounds, new RealmId(realm), in at);
            }

            tx.Commit();
        }

        dungeon.Party = party.ToArray();
        dungeon.Home = home.ToArray();
        _openDungeons.Add(dungeon);
        TatooineReplication.Announce(ctx, new RealmNews { Realm = 0, What = RealmNews.DungeonOpened, Subject = realm, Count = (ushort)party.Count });
        _dungeonsOpened++;
        _dungeonPartyTotal += party.Count;
        _dungeonOpenTicks += Stopwatch.GetTimestamp() - start;
    }

    private void Close(TickContext ctx, Dungeon dungeon)
    {
        var start = Stopwatch.GetTimestamp();
        using (var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            for (var i = 0; i < dungeon.Party.Length; i++)
            {
                var id = dungeon.Party[i];
                _closedThisTick.Add(id);
                if (!tx.TryOpenMut(id, out var player))
                {
                    continue;
                }

                var state = player.Read(Player.State);
                state.Activity = PlayerActivity.Idle;
                state.ActivityTicks = 10 * _config.TickRateHz;
                player.Set(Player.State, state);
                var at = default(PlayerPlacement);
                // Home is on the planet — the teleport below names RealmId.Default — so the ground is the planet's.
                var home = dungeon.Home[i];
                at.SetAt(home.X, home.Z, GroundAt(RealmId.Default, home.X, home.Z), player.Read(Player.Bounds).HalfExtent);
                tx.Teleport(id, Player.Bounds, RealmId.Default, in at);
            }

            foreach (var mob in dungeon.Mobs)
            {
                tx.Destroy(mob);
            }

            tx.Commit();
        }

        // Heard on planet 0's subtree when the tick's frames are built — by then the fence has sent the party home, so it hears it on the planet.
        TatooineReplication.Announce(ctx,
            new RealmNews { Realm = 0, What = RealmNews.DungeonClosed, Subject = dungeon.Realm, Count = (ushort)dungeon.Party.Length });

        // Closing from here: nothing may enter; the fence that applies the teleports and the destroys finds it empty and removes it.
        Dbe.Realms.Unregister(new RealmId(dungeon.Realm));
        dungeon.Pin.Dispose();
        _dungeonsClosed++;
        _dungeonCloseTicks += Stopwatch.GetTimestamp() - start;
    }

    /// <summary>What the dungeons did over the run.</summary>
    public void PrintDungeonReport()
    {
        if (_config.Dungeons == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  dungeons: {_dungeonsOpened} opened, {_dungeonsClosed} closed, {(double)_dungeonPartyTotal / Math.Max(1, _dungeonsOpened):F1} players "
            + $"per party; open {Stopwatch.GetElapsedTime(0, _dungeonOpenTicks / Math.Max(1, _dungeonsOpened)).TotalMicroseconds:F0} us, close "
            + $"{Stopwatch.GetElapsedTime(0, _dungeonCloseTicks / Math.Max(1, _dungeonsClosed)).TotalMicroseconds:F0} us (register/unregister included)");
    }
}
