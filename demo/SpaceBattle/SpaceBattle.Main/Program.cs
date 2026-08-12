using Typhon.Engine;

namespace SpaceBattle;

internal static class Program
{
    private const string RunName = "default";
    private const bool EnableDeepProfiling = false;

    public static int Main(string[] args)
    {
        if (args is ["benchmark"])
        {
            return RunBenchmark();
        }

        var databaseLocation = Path.Combine(AppContext.BaseDirectory, $"{RunName}.typhon");
        ConfigureDeepProfiling(databaseLocation);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using var simulation = SpaceBattleHost.Start(
            SimulationDefinition.Default,
            databaseLocation,
            cancellation.Token,
            new ConsoleObservationSink());

        if (simulation.StartupResult.StartupAction == SimulationStartupAction.Resumed)
        {
            Console.WriteLine($"恢复运行 {RunName}，当前存活飞船数 {simulation.StartupResult.ShipCount:N0}。");
        }

        SpaceBattleRuntimeConfiguration runtimeConfiguration = simulation.RuntimeConfiguration;
        Console.WriteLine(
            $"运行资源：逻辑处理器 {Environment.ProcessorCount}，Typhon worker {runtimeConfiguration.EffectiveWorkerCount}，" +
            $"page cache {runtimeConfiguration.PageCacheSizeBytes / 1024 / 1024} MiB，" +
            $"内存封套 {runtimeConfiguration.MemoryBudgetBytes / 1024 / 1024} MiB，" +
            $"overload {runtimeConfiguration.CurrentOverloadLevel}。");

        Console.WriteLine("模拟运行中。按 Ctrl+C 安全暂停。");
        while (!cancellation.IsCancellationRequested &&
               !simulation.WaitForTerminal(TimeSpan.FromMilliseconds(100)))
        {
        }

        if (!cancellation.IsCancellationRequested)
        {
            InitialWorldSnapshot snapshot = simulation.GetSnapshot();
            Console.WriteLine(DescribeTerminalResult(snapshot.Run));
            return snapshot.Run.Status == SimulationRunStatus.TimedOut ? 1 : 0;
        }

        if (simulation.WaitForTerminal(TimeSpan.FromSeconds(5)))
        {
            InitialWorldSnapshot snapshot = simulation.GetSnapshot();
            Console.WriteLine(DescribeTerminalResult(snapshot.Run));
            return snapshot.Run.Status == SimulationRunStatus.TimedOut ? 1 : 0;
        }

        if (!simulation.WaitForPause(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("等待 Ctrl+C 时正在执行的模拟 tick 完成超时。");
        }

        InitialWorldSnapshot snapshotAfterPause = simulation.GetSnapshot();
        if (snapshotAfterPause.Run.Status != SimulationRunStatus.Running)
        {
            Console.WriteLine(DescribeTerminalResult(snapshotAfterPause.Run));
            return snapshotAfterPause.Run.Status == SimulationRunStatus.TimedOut ? 1 : 0;
        }

        Console.WriteLine("已在完成当前模拟 tick 后暂停；下次启动将继续运行。");
        return 0;
    }

    private static void ConfigureDeepProfiling(string databaseLocation)
    {
        if (!IsDeepProfilingEnabled())
        {
            return;
        }

        // 必须先于 Typhon.Engine 的静态 telemetry 初始化设置，避免 JIT 固化关闭的事件门控。
        Environment.SetEnvironmentVariable("TYPHON__PROFILER__ENABLED", "true");
        Environment.SetEnvironmentVariable(
            "TYPHON__PROFILER__TRACE",
            SpaceBattleProductionSettings.GetProfilerTracePath(databaseLocation));
    }

    private static bool IsDeepProfilingEnabled() => EnableDeepProfiling;

    private static string DescribeTerminalResult(SimulationRunSnapshot run) => run.Outcome switch
    {
        SimulationRunOutcome.Winner => $"模拟完成：胜者飞船 {run.WinnerEntityKey}，完成 tick {run.CompletedTicks:N0}。",
        SimulationRunOutcome.Draw => $"模拟完成：平局，完成 tick {run.CompletedTicks:N0}。",
        SimulationRunOutcome.TimedOut => $"模拟超时：在 {run.CompletedTicks:N0} tick 后仍有 {run.AliveShipCount:N0} 艘飞船存活。",
        _ => throw new InvalidOperationException($"终态运行缺少有效结果：{run.Outcome}。"),
    };

    private sealed class ConsoleObservationSink : ISpaceBattleObservationSink
    {
        public void Publish(SpaceBattleObservation observation)
        {
            switch (observation)
            {
                case InitializationCompleted completed:
                    Console.WriteLine(
                        $"初始化完成：{completed.ShipCount:N0} 艘飞船，耗时 {completed.Duration.TotalSeconds:F2} 秒。");
                    break;
                case SpaceBattleLogSnapshot log:
                    Console.WriteLine(
                        $"战况：segment {log.ProcessSegment}，tick {log.CompletedTicks:N0}，存活 {log.Counters.AliveShipCount:N0}，" +
                        $"锁定 {log.Counters.ActiveLockCount:N0}，射击 {log.Counters.ShotsFired:N0}，命中 {log.Counters.Hits:N0}，" +
                        $"死亡 {log.Counters.Deaths:N0}；tick p50/p95/p99 " +
                        $"{log.Performance.P50ActualDurationMilliseconds:F2}/" +
                        $"{log.Performance.P95ActualDurationMilliseconds:F2}/" +
                        $"{log.Performance.P99ActualDurationMilliseconds:F2} ms，" +
                        $"超预算 {log.Performance.OverrunCount:N0} 次，状态 {log.Run.Status}。");
                    break;
                case SpaceBattleResourceSnapshot resources:
                    NodeSnapshot bottleneck = resources.Snapshot.FindMostUtilized();
                    Console.WriteLine(
                        $"资源：segment {resources.ProcessSegment}，tick {resources.CompletedTicks:N0}，" +
                        $"节点 {resources.Snapshot.Nodes.Count:N0}，" +
                        $"最高容量利用率 {(bottleneck?.Capacity?.Utilization ?? 0d):P1}" +
                        (bottleneck is null ? string.Empty : $"（{bottleneck.Path}）"));
                    break;
            }
        }
    }

    private static int RunBenchmark()
    {
        // 基准模式下不使用 deep profiling
        var databaseLocation = Path.Combine(AppContext.BaseDirectory, "benchmark.typhon");
        if (Directory.Exists(databaseLocation))
        {
            Directory.Delete(databaseLocation, recursive: true);
        }

        var reportPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "benchmark", "reports",
            $"performance-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        using var reportWriter = new StreamWriter(reportPath, append: false);
        var consoleWriter = new DualTextWriter(Console.Out, reportWriter);

        Console.WriteLine($"性能报告将写入: {reportPath}");
        Console.WriteLine();

        try
        {
            var result = BenchmarkDriver.Run(databaseLocation, consoleWriter);

            if (result.Samples.Count == 0)
            {
                Console.Error.WriteLine("基准测试失败：未收集到样本。");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"基准测试完成。报告已保存至: {reportPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"基准测试异常：{ex}");
            return 1;
        }
        finally
        {
            // 清理数据库
            if (Directory.Exists(databaseLocation))
            {
                try { Directory.Delete(databaseLocation, recursive: true); }
                catch { /* 清理非关键 */ }
            }
        }
    }

    /// <summary>同时写入两个 TextWriter 的包装器。</summary>
    private sealed class DualTextWriter(TextWriter primary, TextWriter secondary) : TextWriter
    {
        public override System.Text.Encoding Encoding => primary.Encoding;

        public override void Write(char value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void Write(string value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void WriteLine(string value)
        {
            primary.WriteLine(value);
            secondary.WriteLine(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                primary.Dispose();
                secondary.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
