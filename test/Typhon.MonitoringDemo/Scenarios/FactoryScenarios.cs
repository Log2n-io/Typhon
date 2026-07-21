using System.Diagnostics;
using Typhon.Engine;
using Typhon.Samples.Swg;
using Typhon.Schema.Definition;

namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Bootstraps a new industrial base: creates harvesters, factories, deposits, and recipes.
/// Heavy on CREATE operations.
/// </summary>
public class FactoryBootstrapScenario : IScenario
{
    public string Name => "Factory Bootstrap";
    public string Description => "Creates buildings, conveyor belts, and resource nodes. Heavy CREATE load.";

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        var workers = Enumerable.Range(0, config.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var localRand = new Random(42 + workerId);
            // Per-worker monotonic sequence for the one unique-indexed field spawned here (Recipe.Name).
            long localSeq = 0;

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var t = engine.CreateQuickTransaction();

                    // Create a batch of industry entities
                    var batchSize = localRand.Next(5, 20);

                    for (var i = 0; i < batchSize && !ct.IsCancellationRequested; i++)
                    {
                        var choice = localRand.Next(100);

                        if (choice < 30)
                        {
                            // Create a harvester (polymorphic leaf of Structure)
                            var st = new Structure { TypeCode = localRand.Next(0, 10), PlacedAt = i, Maintenance = localRand.Next(0, 100) };
                            var owner = new StructureOwner { Owner = EntityLink<PlayerArch>.Null };
                            var hop = new Hopper { Class = EntityLink<ResourceTypeArch>.Null, Amount = localRand.Next(0, 1000), Rate = localRand.Next(1, 50) };
                            var tgt = new HarvesterTarget { Deposit = EntityLink<ResourceDepositArch>.Null };
                            var maint = new MaintenanceState { PaidUntil = localRand.Next() };
                            var pos = new StructurePosition { Bounds = SwgWorkload.RandomBounds(localRand) };
                            t.Spawn<HarvesterArch>(
                                StructureArch.Structure.Set(in st),
                                StructureArch.Owner.Set(in owner),
                                HarvesterArch.Hopper.Set(in hop),
                                HarvesterArch.Target.Set(in tgt),
                                HarvesterArch.Maintenance.Set(in maint),
                                HarvesterArch.Position.Set(in pos));
                        }
                        else if (choice < 60)
                        {
                            // Create a factory (second polymorphic leaf of Structure)
                            var st = new Structure { TypeCode = localRand.Next(0, 10), PlacedAt = i, Maintenance = localRand.Next(0, 100) };
                            var owner = new StructureOwner { Owner = EntityLink<PlayerArch>.Null };
                            var fc = new FactoryConfig { Recipe = EntityLink<RecipeArch>.Null, RemainingRuns = localRand.Next(0, 1000) };
                            var pw = new PowerSupply { CreditsRemaining = localRand.Next() };
                            var pos = new StructurePosition { Bounds = SwgWorkload.RandomBounds(localRand) };
                            t.Spawn<FactoryArch>(
                                StructureArch.Structure.Set(in st),
                                StructureArch.Owner.Set(in owner),
                                FactoryArch.Config.Set(in fc),
                                FactoryArch.Power.Set(in pw),
                                FactoryArch.Position.Set(in pos));
                        }
                        else if (choice < 80)
                        {
                            // Create a resource deposit (static-spatial)
                            var d = new Deposit
                            {
                                Type = EntityLink<ResourceTypeArch>.Null,
                                Quality = localRand.Next(0, 100), Concentration = localRand.Next(0, 100), DepletesAt = localRand.Next(),
                            };
                            var pos = new DepositPosition { Bounds = SwgWorkload.RandomBounds(localRand) };
                            t.Spawn<ResourceDepositArch>(ResourceDepositArch.Deposit.Set(in d), ResourceDepositArch.Position.Set(in pos));
                        }
                        else
                        {
                            // Create a recipe (unique Name + 1..8 RecipeSlot ComponentCollection elements)
                            var r = new Recipe
                            {
                                Name = SwgWorkload.S64($"R-{workerId}-{localSeq++}"),
                                Tier = localRand.Next(0, 5), ProfessionReq = localRand.Next(0, 16),
                                PrimaryClass = EntityLink<ResourceTypeArch>.Null,
                            };
                            {
                                using var cca = t.CreateComponentCollectionAccessor(ref r.Slots);
                                var slotCount = localRand.Next(1, 9); // 1..8
                                for (var s = 0; s < slotCount; s++)
                                {
                                    cca.Add(new RecipeSlot { SlotIndex = s, ClassReq = localRand.Next(0, 60), MinUnits = localRand.Next(1, 100) });
                                }
                            }
                            t.Spawn<RecipeArch>(RecipeArch.Recipe.Set(in r));
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
        }, ct)).ToArray();

        await Task.WhenAll(workers);
    }
}

/// <summary>
/// Simulates ongoing factory production: updates harvester maintenance/hoppers and factory power.
/// Heavy on UPDATE operations.
/// </summary>
public class FactoryProductionScenario : IScenario
{
    public string Name => "Factory Production";
    public string Description => "Updates production progress, item quantities. Heavy UPDATE load.";

    private readonly List<EntityId> _harvesterIds = [];
    private readonly List<EntityId> _factoryIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // First, bootstrap some entities to update
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
                    var updates = localRand.Next(5, 15);

                    for (var i = 0; i < updates && !ct.IsCancellationRequested; i++)
                    {
                        if (_harvesterIds.Count > 0 && localRand.Next(2) == 0)
                        {
                            // Update a harvester's maintenance pool + hopper fill
                            var id = _harvesterIds[localRand.Next(_harvesterIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var hopper = entity.Read(HarvesterArch.Hopper);
                                var mut = t.OpenMut(id);
                                ref var wm = ref mut.Write(HarvesterArch.Maintenance);
                                wm.PaidUntil = localRand.Next();
                                ref var wh = ref mut.Write(HarvesterArch.Hopper);
                                wh.Amount = Math.Max(0, hopper.Amount + localRand.Next(-50, 100));
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (_factoryIds.Count > 0)
                        {
                            // Update factory power reserve
                            var id = _factoryIds[localRand.Next(_factoryIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var power = entity.Read(FactoryArch.Power);
                                ref var wp = ref t.OpenMut(id).Write(FactoryArch.Power);
                                wp.CreditsRemaining = Math.Max(0, power.CreditsRemaining + localRand.Next(-500, 1000));
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
        // Create initial harvesters + factories for updates
        using var t = engine.CreateQuickTransaction();

        for (var i = 0; i < 100 && !ct.IsCancellationRequested; i++)
        {
            var st = new Structure { TypeCode = rand.Next(0, 10), PlacedAt = i, Maintenance = rand.Next(0, 100) };
            var owner = new StructureOwner { Owner = EntityLink<PlayerArch>.Null };
            var hop = new Hopper { Class = EntityLink<ResourceTypeArch>.Null, Amount = rand.Next(0, 1000), Rate = rand.Next(1, 50) };
            var tgt = new HarvesterTarget { Deposit = EntityLink<ResourceDepositArch>.Null };
            var maint = new MaintenanceState { PaidUntil = rand.Next() };
            var hpos = new StructurePosition { Bounds = SwgWorkload.RandomBounds(rand) };
            var hid = t.Spawn<HarvesterArch>(
                StructureArch.Structure.Set(in st),
                StructureArch.Owner.Set(in owner),
                HarvesterArch.Hopper.Set(in hop),
                HarvesterArch.Target.Set(in tgt),
                HarvesterArch.Maintenance.Set(in maint),
                HarvesterArch.Position.Set(in hpos));
            _harvesterIds.Add(hid);

            var fst = new Structure { TypeCode = rand.Next(0, 10), PlacedAt = i, Maintenance = rand.Next(0, 100) };
            var fowner = new StructureOwner { Owner = EntityLink<PlayerArch>.Null };
            var fc = new FactoryConfig { Recipe = EntityLink<RecipeArch>.Null, RemainingRuns = rand.Next(0, 1000) };
            var pw = new PowerSupply { CreditsRemaining = rand.Next() };
            var fpos = new StructurePosition { Bounds = SwgWorkload.RandomBounds(rand) };
            var fid = t.Spawn<FactoryArch>(
                StructureArch.Structure.Set(in fst),
                StructureArch.Owner.Set(in fowner),
                FactoryArch.Config.Set(in fc),
                FactoryArch.Power.Set(in pw),
                FactoryArch.Position.Set(in fpos));
            _factoryIds.Add(fid);
        }

        t.Commit();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Simulates supply chain: read-heavy queries over harvesters/factories interleaved with updates.
/// Mixed READ/UPDATE operations.
/// </summary>
public class FactorySupplyChainScenario : IScenario
{
    public string Name => "Factory Supply Chain";
    public string Description => "Simulates belts, item movement, and logistics queries. Mixed READ/UPDATE.";

    private readonly List<EntityId> _harvesterIds = [];
    private readonly List<EntityId> _factoryIds = [];

    public async Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var rand = new Random(42);
        var engine = context.Engine;
        var delayMs = config.TargetOpsPerSecond < int.MaxValue ? 1000.0 / config.TargetOpsPerSecond : 0;

        // Bootstrap supply chain entities
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
                    var ops = localRand.Next(10, 30);

                    for (var i = 0; i < ops && !ct.IsCancellationRequested; i++)
                    {
                        var opType = localRand.Next(100);

                        if (opType < 40 && _harvesterIds.Count > 0)
                        {
                            // Read harvester hopper status (logistics query)
                            var id = _harvesterIds[localRand.Next(_harvesterIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                _ = entity.Read(HarvesterArch.Hopper);
                            }
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (opType < 70 && _harvesterIds.Count > 0)
                        {
                            // Update harvester hopper fill (item movement)
                            var id = _harvesterIds[localRand.Next(_harvesterIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var hopper = entity.Read(HarvesterArch.Hopper);
                                ref var wh = ref t.OpenMut(id).Write(HarvesterArch.Hopper);
                                wh.Amount = Math.Max(0, hopper.Amount + localRand.Next(-30, 50));
                                stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                            }
                        }
                        else if (opType < 90 && _factoryIds.Count > 0)
                        {
                            // Read factory config for supply check
                            var id = _factoryIds[localRand.Next(_factoryIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                _ = entity.Read(FactoryArch.Config);
                            }
                            stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                        }
                        else if (_factoryIds.Count > 0)
                        {
                            // Update factory power reserve
                            var id = _factoryIds[localRand.Next(_factoryIds.Count)];
                            if (t.TryOpen(id, out var entity))
                            {
                                var power = entity.Read(FactoryArch.Power);
                                ref var wp = ref t.OpenMut(id).Write(FactoryArch.Power);
                                wp.CreditsRemaining = Math.Max(0, power.CreditsRemaining + localRand.Next(-100, 200));
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

        // Create harvesters
        for (var i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            var st = new Structure { TypeCode = rand.Next(0, 10), PlacedAt = i, Maintenance = rand.Next(0, 100) };
            var owner = new StructureOwner { Owner = EntityLink<PlayerArch>.Null };
            var hop = new Hopper { Class = EntityLink<ResourceTypeArch>.Null, Amount = rand.Next(0, 1000), Rate = rand.Next(1, 50) };
            var tgt = new HarvesterTarget { Deposit = EntityLink<ResourceDepositArch>.Null };
            var maint = new MaintenanceState { PaidUntil = rand.Next() };
            var pos = new StructurePosition { Bounds = SwgWorkload.RandomBounds(rand) };
            var id = t.Spawn<HarvesterArch>(
                StructureArch.Structure.Set(in st),
                StructureArch.Owner.Set(in owner),
                HarvesterArch.Hopper.Set(in hop),
                HarvesterArch.Target.Set(in tgt),
                HarvesterArch.Maintenance.Set(in maint),
                HarvesterArch.Position.Set(in pos));
            _harvesterIds.Add(id);
        }

        // Create factories
        for (var i = 0; i < 100 && !ct.IsCancellationRequested; i++)
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

        t.Commit();
        await Task.CompletedTask;
    }
}
