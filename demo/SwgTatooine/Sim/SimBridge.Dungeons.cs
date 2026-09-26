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

    /// <summary>Close the dungeons whose stay is over, then open the next one when it is due. Serial.</summary>
    public void DungeonTick(TickContext ctx)
    {
        var tick = ctx.TickNumber;
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
            Open(ctx, tick);
            _nextDungeonTick = tick + Math.Max(1, (long)(_config.DungeonIntervalS * _config.TickRateHz));
        }
    }

    private void Open(TickContext ctx, long tick)
    {
        var start = Stopwatch.GetTimestamp();
        var realm = (ushort)(FirstDungeonRealm + _nextDungeonSlot++);
        var edge = WorldBuilder.InteriorEdgeM;
        Dbe.Realms.Register(new RealmId(realm), new RealmConfig
        {
            Grid = Typhon.Engine.SpatialGridConfig.Flat(Vector2.Zero, new Vector2(edge, edge), edge),
            WhenUnobserved = RealmUnobserved.Sleep,
            UnobservedTickDivisor = 1,
            SleepAfterTicks = Math.Max(1, _config.TickRateHz),
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
                var a = Hash01(Salt(tick, realm, m, 0x3E1F7A93u)) * MathF.PI * 2f;
                var r = edge * 0.25f * MathF.Sqrt(Hash01(Salt(tick, realm, m, 0x71C5B2D1u)));
                var x = c + (MathF.Cos(a) * r);
                var z = c + (MathF.Sin(a) * r);
                var bounds = default(NpcPlacement);
                bounds.SetAt(x, z, 0.5f);
                var ai = new NpcBrain { Mode = AiMode.Wander, HomeX = x, HomeZ = z, LeashRadius = 6f };
                var timers = new NpcTimers { MoveUntilTick = 0, RestUntilTick = tick + 1 + m };
                var move = new NpcMotion { SpeedMps = 1.2f };
                var key = new NpcRealm(realm);
                dungeon.Mobs.Add(tx.Spawn<CityNpc>(CityNpc.Bounds.Set(in bounds), CityNpc.Ai.Set(in ai), CityNpc.Timers.Set(in timers),
                    CityNpc.Move.Set(in move), CityNpc.Realm.Set(in key)));
            }

            // The party: idle players on planet 0, from a rotating start so every dungeon draws different ones.
            var players = _index.Players;
            var offset = (int)(Hash01(Salt(tick, realm, 0, 0x5F3759DFu)) * Math.Max(1, players.Count));
            for (var i = 0; i < players.Count && party.Count < _config.DungeonParty; i++)
            {
                var id = players[(offset + i) % players.Count];
                if (_interiorPins.ContainsKey(id) || !tx.TryOpenMut(id, out var player))
                {
                    continue;
                }

                if (player.Read(Player.Realm).Value != 0 || player.Read(Player.State).Activity != PlayerActivity.Idle)
                {
                    continue;
                }

                var p = player.Read(Player.Bounds);
                home.Add((p.X, p.Z));
                party.Add(id);
                ref var state = ref player.Write(Player.State);
                state.Activity = PlayerActivity.Inside;
                state.ActivityTicks = int.MaxValue / 2;   // PlayerThink leaves a dungeon party alone; Close sends it home
                ref var motion = ref player.Write(Player.Move);
                motion.VelX = 0f;
                motion.VelZ = 0f;
                var at = default(PlayerPlacement);
                at.SetAt(c + ((party.Count % 5) - 2) * 2f, 6f, p.HalfExtent);
                tx.Teleport(id, Player.Bounds, new RealmId(realm), in at);
            }

            tx.Commit();
        }

        dungeon.Party = party.ToArray();
        dungeon.Home = home.ToArray();
        _openDungeons.Add(dungeon);
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
                if (!tx.TryOpenMut(id, out var player))
                {
                    continue;
                }

                ref var state = ref player.Write(Player.State);
                state.Activity = PlayerActivity.Idle;
                state.ActivityTicks = 10 * _config.TickRateHz;
                var at = default(PlayerPlacement);
                at.SetAt(dungeon.Home[i].X, dungeon.Home[i].Z, player.Read(Player.Bounds).HalfExtent);
                tx.Teleport(id, Player.Bounds, RealmId.Default, in at);
            }

            foreach (var mob in dungeon.Mobs)
            {
                tx.Destroy(mob);
            }

            tx.Commit();
        }

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
