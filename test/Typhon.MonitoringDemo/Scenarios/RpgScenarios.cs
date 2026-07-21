using System.Diagnostics;
using Typhon.Engine;
using Typhon.Samples.Swg;
using Typhon.Schema.Definition;

namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Simulates an active world: players spawning and roaming (PlayerPosition writes).
/// Balanced CREATE/READ/UPDATE operations.
/// </summary>
public class RpgWorldSimulationScenario : IScenario
{
    public string Name => "RPG World Simulation";
    public string Description => "Simulates NPC movement, player interactions. Balanced CRUD operations.";

    private readonly List<EntityId> _playerIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap world entities
        await BootstrapEntitiesAsync(engine, rand, ct);

        var workers = Enumerable.Range(0, config.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var localRand = new Random(42 + workerId);
            // Per-worker unique AccountId range (Player.AccountId is a unique index).
            var accountBase = (long)(workerId + 1) << 40;
            long localSeq = 0;

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var t = engine.CreateQuickTransaction();
                    var ops = localRand.Next(5, 20);

                    for (var i = 0; i < ops && !ct.IsCancellationRequested; i++)
                    {
                        var opType = localRand.Next(100);

                        if (opType < 20)
                        {
                            // Spawn new player
                            var player = new Player
                            {
                                Name = SwgWorkload.S64($"NPC-{workerId}-{localSeq}"), AccountId = accountBase + localSeq++,
                                Level = localRand.Next(1, 91), ProfessionId = localRand.Next(0, 16), CreatedAt = 0,
                            };
                            var membership = new Membership { Guild = EntityLink<GuildArch>.Null, GuildRank = localRand.Next(0, 6) };
                            var wallet = new Wallet { Credits = localRand.Next(0, 1_000_000), BankCredits = localRand.Next(0, 100_000_000) };
                            var pos = new PlayerPosition { Bounds = SwgWorkload.RandomBounds(localRand) };
                            var session = new Session { ConnectionId = 0, LatencyMs = localRand.Next(5, 300) };
                            var id = t.Spawn<PlayerArch>(
                                PlayerArch.Player.Set(in player),
                                PlayerArch.Membership.Set(in membership),
                                PlayerArch.Wallet.Set(in wallet),
                                PlayerArch.Position.Set(in pos),
                                PlayerArch.Session.Set(in session));
                            lock (_playerIds)
                            {
                                _playerIds.Add(id);
                            }

                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (opType < 60 && _playerIds.Count > 0)
                        {
                            // Update position (movement)
                            var id = _playerIds[localRand.Next(_playerIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var cur = entity.Read(PlayerArch.Position);
                                var dx = (float)(localRand.NextDouble() - 0.5) * 20f;
                                var dy = (float)(localRand.NextDouble() - 0.5) * 20f;
                                ref var wp = ref t.OpenMut(id).Write(PlayerArch.Position);
                                wp.Bounds = SwgWorkload.Move(cur.Bounds, dx, dy);
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (opType < 80 && _playerIds.Count > 0)
                        {
                            // Read player (visibility check, AI)
                            var id = _playerIds[localRand.Next(_playerIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                _ = entity.Read(PlayerArch.Player);
                            }
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (_playerIds.Count > 0)
                        {
                            // Update player wallet
                            var id = _playerIds[localRand.Next(_playerIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var wallet = entity.Read(PlayerArch.Wallet);
                                ref var ww = ref t.OpenMut(id).Write(PlayerArch.Wallet);
                                ww.Credits = Math.Max(0, wallet.Credits + localRand.Next(-100, 200));
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                    }

                    if (t.Commit())
                    {
                        stats.RecordCommit();
                    }
                    else
                    {
                        stats.RecordRollback();
                    }
                }
                catch (Exception ex)
                {
                    stats.RecordFailure(ex);
                }

                if (delayMs > 0)
                {
                    await Task.Delay((int)delayMs, ct);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers);
    }

    private async Task BootstrapEntitiesAsync(DatabaseEngine engine, Random rand, CancellationToken ct)
    {
        using var t = engine.CreateQuickTransaction();

        // Create initial players
        for (var i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            var player = new Player
            {
                Name = SwgWorkload.S64($"Player-{i}"), AccountId = i,
                Level = rand.Next(1, 91), ProfessionId = rand.Next(0, 16), CreatedAt = i,
            };
            var membership = new Membership { Guild = EntityLink<GuildArch>.Null, GuildRank = rand.Next(0, 6) };
            var wallet = new Wallet { Credits = rand.Next(0, 1_000_000), BankCredits = rand.Next(0, 100_000_000) };
            var pos = new PlayerPosition { Bounds = SwgWorkload.RandomBounds(rand) };
            var session = new Session { ConnectionId = i, LatencyMs = rand.Next(5, 300) };
            var id = t.Spawn<PlayerArch>(
                PlayerArch.Player.Set(in player),
                PlayerArch.Membership.Set(in membership),
                PlayerArch.Wallet.Set(in wallet),
                PlayerArch.Position.Set(in pos),
                PlayerArch.Session.Set(in session));
            _playerIds.Add(id);
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Simulates intense combat: rapid wallet + player-stat updates.
/// UPDATE-heavy with high frequency.
/// </summary>
public class RpgCombatScenario : IScenario
{
    public string Name => "RPG Combat";
    public string Description => "Intense battle simulation: damage, healing, skill cooldowns. High-frequency UPDATEs.";

    private readonly List<EntityId> _playerIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap combat entities
        await BootstrapEntitiesAsync(engine, rand, ct);

        var workers = Enumerable.Range(0, config.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var localRand = new Random(42 + workerId);

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var t = engine.CreateQuickTransaction();

                    // Simulate a combat round
                    var actions = localRand.Next(5, 15);

                    for (var i = 0; i < actions && !ct.IsCancellationRequested; i++)
                    {
                        if (_playerIds.Count == 0)
                        {
                            break;
                        }

                        var actionType = localRand.Next(100);
                        var id = _playerIds[localRand.Next(_playerIds.Count)];

                        if (actionType < 40)
                        {
                            // Deal damage / heal → wallet credit swing (bounty / repair cost)
                            if (t.TryOpen(id, out var entity))
                            {
                                var wallet = entity.Read(PlayerArch.Wallet);
                                var swing = localRand.Next(-50, 100); // Negative = cost
                                ref var ww = ref t.OpenMut(id).Write(PlayerArch.Wallet);
                                ww.Credits = Math.Max(0, wallet.Credits + swing);
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (actionType < 70)
                        {
                            // Progression → player level / profession tweak
                            if (t.TryOpen(id, out var entity))
                            {
                                var player = entity.Read(PlayerArch.Player);
                                ref var wp = ref t.OpenMut(id).Write(PlayerArch.Player);
                                wp.Level = Math.Clamp(player.Level + localRand.Next(-1, 2), 1, 90);
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else
                        {
                            // Bank deposit → wallet bank-credit update
                            if (t.TryOpen(id, out var entity))
                            {
                                var wallet = entity.Read(PlayerArch.Wallet);
                                ref var ww = ref t.OpenMut(id).Write(PlayerArch.Wallet);
                                ww.BankCredits = wallet.BankCredits + localRand.Next(0, 500);
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                    }

                    if (t.Commit())
                    {
                        stats.RecordCommit();
                    }
                    else
                    {
                        stats.RecordRollback();
                    }
                }
                catch (Exception ex)
                {
                    stats.RecordFailure(ex);
                }

                if (delayMs > 0)
                {
                    await Task.Delay((int)delayMs, ct);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers);
    }

    private async Task BootstrapEntitiesAsync(DatabaseEngine engine, Random rand, CancellationToken ct)
    {
        using var t = engine.CreateQuickTransaction();

        for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            var player = new Player
            {
                Name = SwgWorkload.S64($"Combatant-{i}"), AccountId = i,
                Level = rand.Next(1, 91), ProfessionId = rand.Next(0, 16), CreatedAt = i,
            };
            var membership = new Membership { Guild = EntityLink<GuildArch>.Null, GuildRank = rand.Next(0, 6) };
            var wallet = new Wallet { Credits = rand.Next(0, 1_000_000), BankCredits = rand.Next(0, 100_000_000) };
            var pos = new PlayerPosition { Bounds = SwgWorkload.RandomBounds(rand) };
            var session = new Session { ConnectionId = i, LatencyMs = rand.Next(5, 300) };
            var id = t.Spawn<PlayerArch>(
                PlayerArch.Player.Set(in player),
                PlayerArch.Membership.Set(in membership),
                PlayerArch.Wallet.Set(in wallet),
                PlayerArch.Position.Set(in pos),
                PlayerArch.Session.Set(in session));
            _playerIds.Add(id);
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Simulates the loot/quest loop: crafting items (ItemArch spawn with affix collections) and reading player guild state.
/// Mixed operations.
/// </summary>
public class RpgQuestingScenario : IScenario
{
    public string Name => "RPG Questing";
    public string Description => "Quest acceptance, progress tracking, inventory rewards. Mixed CRUD.";

    private readonly List<EntityId> _playerIds = [];
    private readonly List<EntityId> _itemIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap quest entities
        await BootstrapEntitiesAsync(engine, rand, ct);

        var workers = Enumerable.Range(0, config.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var localRand = new Random(42 + workerId);

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var t = engine.CreateQuickTransaction();
                    var ops = localRand.Next(5, 15);

                    for (var i = 0; i < ops && !ct.IsCancellationRequested; i++)
                    {
                        var opType = localRand.Next(100);

                        if (opType < 40)
                        {
                            // Craft/loot a new item (reward) — FK Owner → a bootstrapped player, 0..4 affix CC elements
                            var it = new Item
                            {
                                Recipe = EntityLink<RecipeArch>.Null,
                                ItemType = localRand.Next(0, 50), Quality = localRand.Next(0, 100), Decay = localRand.Next(0, 100),
                            };
                            var affixes = localRand.Next(0, 5);
                            if (affixes > 0)
                            {
                                using var cca = t.CreateComponentCollectionAccessor(ref it.Affixes);
                                for (var a = 0; a < affixes; a++)
                                {
                                    cca.Add(new ItemAffix { AffixType = localRand.Next(0, 20), Value = localRand.Next(1, 100) });
                                }
                            }
                            var owner = new ItemOwner { Owner = EntityLink<PlayerArch>.Null };
                            if (_playerIds.Count > 0)
                            {
                                owner.Owner = _playerIds[localRand.Next(_playerIds.Count)];
                            }
                            var itemId = t.Spawn<ItemArch>(ItemArch.Item.Set(in it), ItemArch.Owner.Set(in owner));
                            lock (_itemIds)
                            {
                                _itemIds.Add(itemId);
                            }
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (opType < 70 && _itemIds.Count > 0)
                        {
                            // Item wear/repair (progress) — update Decay/Quality
                            var id = _itemIds[localRand.Next(_itemIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var item = entity.Read(ItemArch.Item);
                                ref var wi = ref t.OpenMut(id).Write(ItemArch.Item);
                                wi.Decay = Math.Clamp(item.Decay + localRand.Next(-5, 10), 0, 100);
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (opType < 90 && _playerIds.Count > 0)
                        {
                            // Read player guild membership (quest-giver check)
                            var id = _playerIds[localRand.Next(_playerIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                _ = entity.Read(PlayerArch.Membership);
                            }
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (_itemIds.Count > 0)
                        {
                            // Read item (inventory browse)
                            var id = _itemIds[localRand.Next(_itemIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                _ = entity.Read(ItemArch.Item);
                            }
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                    }

                    if (t.Commit())
                    {
                        stats.RecordCommit();
                    }
                    else
                    {
                        stats.RecordRollback();
                    }
                }
                catch (Exception ex)
                {
                    stats.RecordFailure(ex);
                }

                if (delayMs > 0)
                {
                    await Task.Delay((int)delayMs, ct);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers);
    }

    private async Task BootstrapEntitiesAsync(DatabaseEngine engine, Random rand, CancellationToken ct)
    {
        using var t = engine.CreateQuickTransaction();

        for (var i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            var player = new Player
            {
                Name = SwgWorkload.S64($"Hero-{i}"), AccountId = i,
                Level = rand.Next(1, 91), ProfessionId = rand.Next(0, 16), CreatedAt = i,
            };
            var membership = new Membership { Guild = EntityLink<GuildArch>.Null, GuildRank = rand.Next(0, 6) };
            var wallet = new Wallet { Credits = rand.Next(0, 1_000_000), BankCredits = rand.Next(0, 100_000_000) };
            var pos = new PlayerPosition { Bounds = SwgWorkload.RandomBounds(rand) };
            var session = new Session { ConnectionId = i, LatencyMs = rand.Next(5, 300) };
            var charId = t.Spawn<PlayerArch>(
                PlayerArch.Player.Set(in player),
                PlayerArch.Membership.Set(in membership),
                PlayerArch.Wallet.Set(in wallet),
                PlayerArch.Position.Set(in pos),
                PlayerArch.Session.Set(in session));
            _playerIds.Add(charId);

            // Give each player some starting items
            for (var j = 0; j < 5; j++)
            {
                var it = new Item
                {
                    Recipe = EntityLink<RecipeArch>.Null,
                    ItemType = rand.Next(0, 50), Quality = rand.Next(0, 100), Decay = rand.Next(0, 100),
                };
                var owner = new ItemOwner { Owner = charId };
                var itemId = t.Spawn<ItemArch>(ItemArch.Item.Set(in it), ItemArch.Owner.Set(in owner));
                _itemIds.Add(itemId);
            }
        }

        t.Commit();
        await Task.CompletedTask;
    }
}
