using Typhon.Engine;

namespace SimpleSpaceBattle;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("SSB_VIEWBENCH") == "1")
        {
            ViewRefreshBenchmark.Run();
            return 0;
        }

        SimulationConfig config = ParseConfig(args);
        int workerCount = ReadInt("SSB_WORKERS", Math.Max(1, Environment.ProcessorCount - 2));
        string runName = Environment.GetEnvironmentVariable("SSB_RUN") ?? "default";
        string databaseLocation = Path.Combine(AppContext.BaseDirectory, $"{runName}.typhon");

        if (Environment.GetEnvironmentVariable("SSB_FRESH") != "0" && Directory.Exists(databaseLocation))
        {
            Directory.Delete(databaseLocation, recursive: true);
        }

        PrintBanner(config, workerCount);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        using BattleHost host = BattleHost.Create(config, databaseLocation, workerCount);
        BattleWorld world = host.World;

        Console.WriteLine(
            $"spawned {config.ShipCount:N0} ships in {host.SpawnDuration.TotalSeconds:F2} s  " +
            $"({world.ClusterCount:N0} clusters, N={world.ClusterSize}, " +
            $"{config.ShipCount / (double)Math.Max(1, world.ClusterCount * world.ClusterSize):P0} slot occupancy, " +
            $"{world.Lane.Capacity * 8 / 1024.0:F0} KB target lane)");
        Console.WriteLine();

        host.Start();
        host.WaitForTerminal(cancellation.Token);

        Console.WriteLine();
        Console.WriteLine(Describe(world));
        return world.Outcome == BattleOutcome.TimedOut ? 1 : 0;
    }

    private static void PrintBanner(SimulationConfig config, int workerCount)
    {
        int gridWidth = (int)MathF.Ceiling(config.WorldX / config.CellSize);
        int gridHeight = (int)MathF.Ceiling(config.WorldY / config.CellSize);

        Console.WriteLine("SimpleSpaceBattle");
        Console.WriteLine(
            $"  world      {config.WorldX:F0} x {config.WorldY:F0} x {config.WorldZ:F0}   " +
            $"cell {config.CellSize:F0} -> {gridWidth}x{gridHeight} = {gridWidth * gridHeight} cells");
        Console.WriteLine(
            $"  fleet      {config.ShipCount:N0} ships   density {config.Density:E2}/unit^3   " +
            $"~{config.ExpectedNeighbours:F0} in weapon range");
        Console.WriteLine(
            $"  combat     acquire {config.AcquisitionRange:F0}   weapon {config.WeaponRange:F0}   " +
            $"fire every {config.FireIntervalTicks} ticks   {config.DamagePerHit} dmg   {config.MaximumHealth} hp");
        Console.WriteLine(
            $"  runtime    {config.TickRate} Hz ({1000f / config.TickRate:F0} ms budget)   " +
            $"{workerCount} workers of {Environment.ProcessorCount} logical processors   " +
            $"Resolution chunks {workerCount * config.ResolutionChunksPerWorker}");
        Console.WriteLine($"  predicted  {config.PredictedCandidates:F0} narrowphase candidates/query " +
            $"({config.ExpectedNeighbours / config.PredictedCandidates * 100f:F1}% hit rate)");
        Console.WriteLine();
    }

    private static SimulationConfig ParseConfig(string[] args)
    {
        SimulationConfig defaults = SimulationConfig.Default;
        var config = defaults with
        {
            ShipCount = ReadInt("SSB_SHIPS", defaults.ShipCount),
            CellSize = ReadFloat("SSB_CELL", defaults.CellSize),
            WorldZ = ReadFloat("SSB_WORLD_Z", defaults.WorldZ),
            MaximumCompletedTicks = (ulong)ReadInt("SSB_TICKS", (int)defaults.MaximumCompletedTicks),
            AcquisitionRange = ReadFloat("SSB_ACQ", defaults.AcquisitionRange),
            WeaponRange = ReadFloat("SSB_WEAPON", defaults.WeaponRange),
            ResolutionChunksPerWorker = ReadInt("SSB_RESCHUNKS", defaults.ResolutionChunksPerWorker),
            Seed = defaults.Seed,
        };

        // Positional overrides keep the cellSize sweep a one-liner: `run 50000 30`.
        if (args.Length >= 1 && int.TryParse(args[0], out int ships) && ships > 0)
        {
            config = config with { ShipCount = ships };
        }

        if (args.Length >= 2 && float.TryParse(args[1], out float cell) && cell > 0f)
        {
            config = config with { CellSize = cell };
        }

        return config;
    }

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int parsed) && parsed > 0 ? parsed : fallback;

    private static float ReadFloat(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out float parsed) && parsed > 0f ? parsed : fallback;

    private static string Describe(BattleWorld world) => world.Outcome switch
    {
        BattleOutcome.Winner => $"winner after {world.CompletedTicks:N0} ticks — {world.TotalDeaths:N0} destroyed, {world.TotalShots:N0} shots, {world.TotalHits:N0} hits",
        BattleOutcome.Draw => $"mutual annihilation after {world.CompletedTicks:N0} ticks — {world.TotalShots:N0} shots, {world.TotalHits:N0} hits",
        BattleOutcome.TimedOut => $"timed out after {world.CompletedTicks:N0} ticks with {world.AliveCount:N0} alive",
        _ => $"interrupted at tick {world.CompletedTicks:N0} with {world.AliveCount:N0} alive",
    };
}
