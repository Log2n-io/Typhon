using System.Diagnostics;
using Typhon.Engine;
using Typhon.Samples.Swg;
using Typhon.Schema.Definition;

namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Runs factory (FactoryArch) and player (PlayerArch) workloads simultaneously.
/// Tests mixed component types and transaction isolation.
/// </summary>
public class MixedWorkloadScenario : IScenario
{
    public string Name => "Mixed Workload";
    public string Description => "Factory + RPG simultaneously. Tests mixed component types and isolation.";

    private readonly List<EntityId> _factoryIds = [];
    private readonly List<EntityId> _playerIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap both domains
        await BootstrapEntitiesAsync(engine, rand, ct);

        // Split workers between factory and RPG
        var factoryWorkers = config.WorkerCount / 2;
        var rpgWorkers = config.WorkerCount - factoryWorkers;

        var workers = new List<Task>();

        // Factory workers
        for (var workerId = 0; workerId < factoryWorkers; workerId++)
        {
            var wid = workerId;
            workers.Add(Task.Run(async () =>
            {
                var localRand = new Random(42 + wid);

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

                            if (opType < 30)
                            {
                                var st = new Structure { TypeCode = localRand.Next(0, 10), PlacedAt = i, Maintenance = localRand.Next(0, 100) };
                                var owner = new StructureOwner { Owner = EntityLink<PlayerArch>.Null };
                                var fc = new FactoryConfig { Recipe = EntityLink<RecipeArch>.Null, RemainingRuns = localRand.Next(0, 1000) };
                                var pw = new PowerSupply { CreditsRemaining = localRand.Next() };
                                var pos = new StructurePosition { Bounds = SwgWorkload.RandomBounds(localRand) };
                                var id = t.Spawn<FactoryArch>(
                                    StructureArch.Structure.Set(in st),
                                    StructureArch.Owner.Set(in owner),
                                    FactoryArch.Config.Set(in fc),
                                    FactoryArch.Power.Set(in pw),
                                    FactoryArch.Position.Set(in pos));
                                lock (_factoryIds)
                                {
                                    _factoryIds.Add(id);
                                }
                            }
                            else if (opType < 70 && _factoryIds.Count > 0)
                            {
                                var id = _factoryIds[localRand.Next(_factoryIds.Count)];
                                if (t.TryOpen(id, out var entity))
                                {
                                    var power = entity.Read(FactoryArch.Power);
                                    ref var wp = ref t.OpenMut(id).Write(FactoryArch.Power);
                                    wp.CreditsRemaining = Math.Max(0, power.CreditsRemaining + localRand.Next(-100, 200));
                                }
                            }
                            else if (_factoryIds.Count > 0)
                            {
                                var id = _factoryIds[localRand.Next(_factoryIds.Count)];
                                if (t.TryOpen(id, out var entity))
                                {
                                    _ = entity.Read(FactoryArch.Config);
                                }
                            }

                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
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
            }, ct));
        }

        // RPG (player) workers
        for (var workerId = 0; workerId < rpgWorkers; workerId++)
        {
            var wid = workerId + factoryWorkers;
            workers.Add(Task.Run(async () =>
            {
                var localRand = new Random(42 + wid);
                // Per-worker unique AccountId range (Player.AccountId is a unique index).
                var accountBase = (long)(wid + 1) << 40;
                long localSeq = 0;

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

                            if (opType < 30)
                            {
                                var player = new Player
                                {
                                    Name = SwgWorkload.S64($"P-{wid}-{localSeq}"), AccountId = accountBase + localSeq++,
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
                            }
                            else if (opType < 70 && _playerIds.Count > 0)
                            {
                                var id = _playerIds[localRand.Next(_playerIds.Count)];
                                if (t.TryOpen(id, out var entity))
                                {
                                    var wallet = entity.Read(PlayerArch.Wallet);
                                    ref var ww = ref t.OpenMut(id).Write(PlayerArch.Wallet);
                                    ww.Credits = Math.Max(0, wallet.Credits + localRand.Next(-20, 30));
                                    ww.BankCredits = wallet.BankCredits + localRand.Next(10, 100);
                                }
                            }
                            else if (_playerIds.Count > 0)
                            {
                                var id = _playerIds[localRand.Next(_playerIds.Count)];
                                if (t.TryOpen(id, out var entity))
                                {
                                    _ = entity.Read(PlayerArch.Player);
                                }
                            }

                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
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
            }, ct));
        }

        await Task.WhenAll(workers);
    }

    private async Task BootstrapEntitiesAsync(DatabaseEngine engine, Random rand, CancellationToken ct)
    {
        using var t = engine.CreateQuickTransaction();

        // Factory entities
        for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            var st = new Structure { TypeCode = rand.Next(0, 10), PlacedAt = i, Maintenance = rand.Next(0, 100) };
            var owner = new StructureOwner { Owner = EntityLink<PlayerArch>.Null };
            var fc = new FactoryConfig { Recipe = EntityLink<RecipeArch>.Null, RemainingRuns = rand.Next(0, 1000) };
            var pw = new PowerSupply { CreditsRemaining = rand.Next() };
            var pos = new StructurePosition { Bounds = SwgWorkload.RandomBounds(rand) };
            var id = t.Spawn<FactoryArch>(
                StructureArch.Structure.Set(in st),
                StructureArch.Owner.Set(in owner),
                FactoryArch.Config.Set(in fc),
                FactoryArch.Power.Set(in pw),
                FactoryArch.Position.Set(in pos));
            _factoryIds.Add(id);
        }

        // Player entities
        for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
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
/// Intentionally creates high contention by having many workers update the same few entities.
/// Used to test lock behavior and observe contention metrics.
/// </summary>
public class HighContentionScenario : IScenario
{
    public string Name => "High Contention";
    public string Description => "Multiple workers updating same entities. Stress tests locking and MVCC.";

    private readonly List<EntityId> _hotspotIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;

        // Create a small number of "hotspot" entities that everyone fights over
        await BootstrapHotspotsAsync(engine, rand, ct);

        // No rate limiting - we want maximum contention
        var workers = Enumerable.Range(0, config.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var localRand = new Random(42 + workerId);

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var t = engine.CreateQuickTransaction();

                    // All workers try to update the SAME hotspot entities
                    var updateCount = localRand.Next(3, 8);

                    for (var i = 0; i < updateCount && !ct.IsCancellationRequested; i++)
                    {
                        // Always pick from the small hotspot pool (high contention)
                        var id = _hotspotIds[localRand.Next(_hotspotIds.Count)];

                        if (t.TryOpen(id, out var entity))
                        {
                            var wallet = entity.Read(PlayerArch.Wallet);
                            // Every worker tries to update the same player's wallet
                            ref var ww = ref t.OpenMut(id).Write(PlayerArch.Wallet);
                            ww.Credits = Math.Max(0, wallet.Credits + localRand.Next(-50, 100));
                            ww.BankCredits = wallet.BankCredits + localRand.Next(0, 50);

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
                catch (Exception)
                {
                    stats.RecordFailure();
                }

                // Minimal delay to maximize contention
                await Task.Yield();
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers);
    }

    private async Task BootstrapHotspotsAsync(DatabaseEngine engine, Random rand, CancellationToken ct)
    {
        using var t = engine.CreateQuickTransaction();

        // Create only 5 players - these will be the hotspots
        for (var i = 0; i < 5 && !ct.IsCancellationRequested; i++)
        {
            var player = new Player
            {
                Name = SwgWorkload.S64($"Hotspot-{i}"), AccountId = i,
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
            _hotspotIds.Add(id);
        }

        t.Commit();
        await Task.CompletedTask;
    }
}
