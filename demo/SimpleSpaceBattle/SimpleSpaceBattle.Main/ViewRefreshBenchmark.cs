using System.Diagnostics;
using Typhon.Engine;

namespace SimpleSpaceBattle;

/// <summary>
/// Isolates the cost of <c>EcsView&lt;T&gt;.Refresh</c> in pull mode — the refresh the runtime performs at tick start
/// for every system input view (<c>TyphonRuntime.RefreshSystemInputViewsAtTickStart</c>, #718).
///
/// <para>Motivation: the demo measured ~394 ms of per-tick residual at 50 000 ships that belonged to no system. This
/// measures the refresh alone — no runtime, no systems, no fence — so the residual can be attributed rather than
/// guessed at. Run with <c>SSB_VIEWBENCH=1</c>.</para>
/// </summary>
internal static class ViewRefreshBenchmark
{
    public static void Run()
    {
        Console.WriteLine("EcsView pull-mode refresh cost (no runtime, no systems, no fence)");
        Console.WriteLine();

        foreach (int shipCount in (int[])[10_000, 25_000, 50_000])
        {
            Measure(shipCount);
        }
    }

    private static void Measure(int shipCount)
    {
        string location = Path.Combine(AppContext.BaseDirectory, $"viewbench-{shipCount}.typhon");
        if (Directory.Exists(location))
        {
            Directory.Delete(location, recursive: true);
        }

        SimulationConfig config = SimulationConfig.Default with { ShipCount = shipCount };
        int workerCount = Math.Max(1, Environment.ProcessorCount - 2);

        DatabaseEngine dbe = DatabaseEngine.Open(location, options => options
            .Register<HullComponent>()
            .Register<MotionComponent>()
            .Register<VitalsComponent>()
            .Register<TargetingComponent>()
            .ConfigureSpatialGrid(new SpatialGridConfig(
                System.Numerics.Vector2.Zero,
                new System.Numerics.Vector2(config.WorldX, config.WorldY),
                config.CellSize)));

        try
        {
            var world = new BattleWorld(dbe, config, workerCount);
            world.SpawnFleet();

            EcsView<Ship> view;
            using (Transaction tx = dbe.CreateQuickTransaction())
            {
                view = tx.Query<Ship>().ToView();
            }

            try
            {
                for (int i = 0; i < 3; i++)
                {
                    using Transaction tx = dbe.CreateQuickTransaction();
                    view.Refresh(tx);
                }

                long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
                const int iterations = 20;
                long start = Stopwatch.GetTimestamp();

                for (int i = 0; i < iterations; i++)
                {
                    using Transaction tx = dbe.CreateQuickTransaction();
                    view.Refresh(tx);
                }

                double perRefreshMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
                double allocKb = (GC.GetTotalAllocatedBytes(precise: true) - allocBefore) / (double)iterations / 1024.0;

                // Split the refresh: Execute() builds the candidate HashSet, and the remainder is the
                // add/remove diff against the view's own HashMap. Attributing the cost to one or the other is what
                // decides whether this is an engine fix or a demo workaround.
                long execStart = Stopwatch.GetTimestamp();
                int lastCount = 0;
                for (int i = 0; i < iterations; i++)
                {
                    using Transaction tx = dbe.CreateQuickTransaction();
                    lastCount = tx.Query<Ship>().Execute().Count;
                }

                double perExecuteMs = Stopwatch.GetElapsedTime(execStart).TotalMilliseconds / iterations;

                Console.WriteLine(
                    $"  n={shipCount,6:N0}   refresh={perRefreshMs,8:F2} ms   " +
                    $"per-entity={perRefreshMs / shipCount * 1_000_000,7:F0} ns   " +
                    $"alloc/refresh={allocKb,9:N0} KB");
                Console.WriteLine(
                    $"              of which Execute()={perExecuteMs,8:F2} ms   diff={perRefreshMs - perExecuteMs,8:F2} ms   (result {lastCount:N0})");
            }
            finally
            {
                view.Dispose();
            }
        }
        finally
        {
            dbe.Dispose();
            try
            {
                Directory.Delete(location, recursive: true);
            }
            catch (IOException)
            {
                // Best effort — a leftover benchmark database is not worth failing the run over.
            }
        }
    }
}
