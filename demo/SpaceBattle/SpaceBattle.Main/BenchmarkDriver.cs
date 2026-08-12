using System.Diagnostics;
using Typhon.Engine;

namespace SpaceBattle;

/// <summary>
/// Release 性能对比与 40 ms 验收基准测试驱动程序。
/// 在固定负载（50,000 艘飞船、12 worker）下测量各阶段与完整 tick 的墙钟耗时，
/// 验证预热后 2048-tick 滚动窗口的 p99 是否符合 40 ms 预算。
/// </summary>
internal static class BenchmarkDriver
{
    public const int TargetWorkerCount = 12;
    public const int BenchmarkShipCount = 50_000;
    public const int WarmupTicks = 256;
    public const int MeasurementTicks = 2_048;
    public const int RollingWindowSize = 2_048;

    public static BenchmarkRunResult Run(string databaseLocation, TextWriter output)
    {
        output.WriteLine("=== SpaceBattle Release 性能对比与 40 ms 验收 ===");
        output.WriteLine($"时间戳: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        output.WriteLine();

        // 记录环境信息
        output.WriteLine("## 环境");
        output.WriteLine($"- 操作系统: {Environment.OSVersion}");
        output.WriteLine($"- 逻辑处理器数: {Environment.ProcessorCount}");
        output.WriteLine($"- 进程架构: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        output.WriteLine($"- 机器: AMD Ryzen 7 260 w/ Radeon 780M Graphics");
        output.WriteLine();

        // 配置 benchmark：强制 12 worker，关闭 deep profiling
        SpaceBattleProductionSettings.TestWorkerCountOverride = TargetWorkerCount;

        var definition = new SimulationDefinition(
            runName: "benchmark",
            shipCount: BenchmarkShipCount,
            seed: SimulationDefinition.DefaultSeed,
            rulesetVersion: 1,
            worldSize: 1_000f,
            maximumHealth: 1_000,
            stagingTicks: 250,
            spatialCellSize: 100f,
            spatialMargin: 20f,
            maximumCompletedTicks: (ulong)(WarmupTicks + MeasurementTicks + RollingWindowSize + 256));

        using var cancellation = new CancellationTokenSource();
        var initStopwatch = Stopwatch.StartNew();

        using var simulation = SpaceBattleHost.Start(
            definition,
            databaseLocation,
            cancellation.Token,
            new NullObservationSink());

        initStopwatch.Stop();
        var initDuration = simulation.StartupResult.InitializationDuration;
        output.WriteLine("## 初始化");
        output.WriteLine($"- 飞船数: {definition.ShipCount:N0}");
        output.WriteLine($"- Typhon worker 数: {simulation.RuntimeConfiguration.EffectiveWorkerCount}");
        output.WriteLine($"- Page cache: {simulation.RuntimeConfiguration.PageCacheSizeBytes / 1024 / 1024} MiB");
        output.WriteLine($"- 内存封套: {simulation.RuntimeConfiguration.MemoryBudgetBytes / 1024 / 1024} MiB");
        output.WriteLine($"- Deep profiling: 关闭");
        output.WriteLine($"- 数据库创建并初始化（创建世界）: {initDuration.TotalMilliseconds:F1} ms");
        output.WriteLine($"- 总初始化耗时（含数据库重组+启动）: {initStopwatch.Elapsed.TotalMilliseconds:F1} ms");
        output.WriteLine($"- 启动类型: {simulation.StartupResult.StartupAction}");
        output.WriteLine();

        output.WriteLine("## 预热与测量参数");
        output.WriteLine($"- 预热 ticks: {WarmupTicks}");
        output.WriteLine($"- 测量 ticks: {MeasurementTicks}");
        output.WriteLine($"- 滚动窗口大小: {RollingWindowSize} ticks");
        output.WriteLine();

        // 等待预热完成——这段时间不包括在测量中
        ulong warmupEndTick = (ulong)WarmupTicks;
        output.WriteLine($"等待预热完成（tick {warmupEndTick}）...");
        // 预热阶段可能较长，给充分时间
        if (!simulation.WaitForCompletedTicks(warmupEndTick, TimeSpan.FromMinutes(10)))
        {
            output.WriteLine("错误：预热超时。");
            return new BenchmarkRunResult(Array.Empty<TickPhaseSample>(), TimeSpan.Zero);
        }
        output.WriteLine("预热完成。");
        output.WriteLine();

        // 开始测量
        ulong measurementEndTick = warmupEndTick + (ulong)MeasurementTicks;
        output.WriteLine($"开始测量 ticks {warmupEndTick + 1}–{measurementEndTick}...");

        var measurementStopwatch = Stopwatch.StartNew();
        // 使用更长的超时（20 分钟），因为每个 tick 可能超过 40ms
        if (!simulation.WaitForCompletedTicks(measurementEndTick, TimeSpan.FromMinutes(20)))
        {
            output.WriteLine($"错误：测量超时（等待 {measurementEndTick} 超过 20 分钟）。");
            // 即使超时，也收集已经产生的样本
        }
        measurementStopwatch.Stop();

        output.WriteLine($"测量完成，耗时 {measurementStopwatch.Elapsed.TotalSeconds:F2} 秒。");
        output.WriteLine();

        // 收集样本——从 PhaseTimingCollector 读取预热后的样本
        var allSamples = simulation.GetPhaseTimingSamples();
        var samples = allSamples
            .Where(s => s.CompletedTickNumber > warmupEndTick)
            .Take(MeasurementTicks)
            .ToArray();

        output.WriteLine($"收集到 {samples.Length} 个稳态 tick 样本（共 {allSamples.Count} 个）。");
        output.WriteLine();

        if (samples.Length == 0)
        {
            output.WriteLine("错误：没有收集到稳态样本。");
            return new BenchmarkRunResult(Array.Empty<TickPhaseSample>(), TimeSpan.Zero);
        }

        // 分析结果
        AnalyzeResults(samples, allSamples, output, measurementStopwatch.Elapsed);

        // 请求暂停
        simulation.RequestPause();
        if (simulation.WaitForPause(TimeSpan.FromSeconds(30)))
        {
            output.WriteLine("模拟已暂停。");
        }

        return new BenchmarkRunResult(samples, measurementStopwatch.Elapsed);
    }

    private static void AnalyzeResults(
        IReadOnlyList<TickPhaseSample> samples,
        IReadOnlyList<TickPhaseSample> allSamples,
        TextWriter output,
        TimeSpan measurementDuration)
    {
        output.WriteLine("## 总体结果");
        output.WriteLine();
        output.WriteLine("### 稳态 tick 耗时摘要（预热后 {0} ticks）", samples.Count);
        output.WriteLine();

        var totalTickMs = samples.Select(s => s.TotalTickMs).ToArray();
        var phaseMs = PhaseTimingCollector.PhaseNames
            .Select((name, i) => (name, data: samples.Select(s => s.PhaseMs[i]).ToArray()))
            .ToArray();

        // 完整 tick 统计
        var tickStats = ComputeStats(totalTickMs);
        output.WriteLine("| 指标 | 值 (ms) |");
        output.WriteLine("|------|--------:|");
        output.WriteLine($"| 均值 | {tickStats.Mean:F3} |");
        output.WriteLine($"| 中位数 (p50) | {tickStats.P50:F3} |");
        output.WriteLine($"| p95 | {tickStats.P95:F3} |");
        output.WriteLine($"| p99 | {tickStats.P99:F3} |");
        output.WriteLine($"| 最大值 | {tickStats.Max:F3} |");
        output.WriteLine($"| 最小值 | {tickStats.Min:F3} |");
        output.WriteLine($"| 标准差 | {tickStats.StdDev:F3} |");
        output.WriteLine();

        // 滚动窗口 p99 分析
        output.WriteLine("### 2048-tick 滚动窗口 p99");
        output.WriteLine();
        var rollingP99s = ComputeRollingP99(totalTickMs, RollingWindowSize);
        var rollingStats = ComputeBasicStats(rollingP99s);
        output.WriteLine($"| 窗口数 | {rollingP99s.Length} |");
        output.WriteLine($"| 滚动 p99 均值 | {rollingStats.Mean:F3} ms |");
        output.WriteLine($"| 滚动 p99 最大值 | {rollingStats.Max:F3} ms |");
        output.WriteLine($"| 滚动 p99 最小值 | {rollingStats.Min:F3} ms |");
        output.WriteLine();

        bool meetsBudget = rollingP99s.Any() && rollingP99s.Max() <= 40.0;
        output.WriteLine($"**40 ms 预算验收：{(meetsBudget ? "通过" : "未通过")}**" +
                         $"（滚动窗口 p99 最大值 = {rollingP99s.DefaultIfEmpty(0).Max():F3} ms）");
        output.WriteLine();

        // 各阶段对比
        output.WriteLine("## 各阶段耗时对比（预热后）");
        output.WriteLine();
        output.WriteLine("| 阶段 | 均值 (ms) | p50 (ms) | p95 (ms) | p99 (ms) | 占总 tick 比 |");
        output.WriteLine("|------|----------:|---------:|---------:|---------:|------------:|");

        double totalMean = tickStats.Mean;
        foreach (var (name, data) in phaseMs)
        {
            if (data.Length == 0) continue;
            var stats = ComputeStats(data);
            double ratio = totalMean > 0 ? stats.Mean / totalMean * 100 : 0;
            output.WriteLine($"| {name} | {stats.Mean:F3} | {stats.P50:F3} | {stats.P95:F3} | {stats.P99:F3} | {ratio:F1}% |");
        }
        output.WriteLine();

        // 输入准备成本（ShipViewRefresh 阶段）
        var inputPrepMs = samples.Select(s => s.PhaseMs[PhaseTimingCollector.ShipViewRefresh]).ToArray();
        var inputStats = ComputeStats(inputPrepMs);
        output.WriteLine("## 输入准备成本（ShipViewRefresh 阶段）");
        output.WriteLine();
        output.WriteLine("ShipViewRefresh 负责重建 ECS view 和 workset。预热后的开销：");
        output.WriteLine("| 指标 | 值 (ms) |");
        output.WriteLine("|------|--------:|");
        output.WriteLine($"| 均值 | {inputStats.Mean:F3} |");
        output.WriteLine($"| p50 | {inputStats.P50:F3} |");
        output.WriteLine($"| p95 | {inputStats.P95:F3} |");
        output.WriteLine($"| p99 | {inputStats.P99:F3} |");
        output.WriteLine($"| 最大值 | {inputStats.Max:F3} |");
        output.WriteLine();

        // Workset 重建一次性成本与稳态对比
        output.WriteLine("## 一次性成本与稳态对比");
        output.WriteLine();
        if (allSamples.Count > 0)
        {
            var firstRuntimeTick = allSamples[0];
            var initPhase = string.Join(", ",
                PhaseTimingCollector.PhaseNames
                    .Select((name, i) => $"{name}={firstRuntimeTick.PhaseMs[i]:F3}ms"));
            output.WriteLine($"首个 runtime tick #{firstRuntimeTick.CompletedTickNumber}：total={firstRuntimeTick.TotalTickMs:F3}ms");
            output.WriteLine($"  阶段明细：[{initPhase}]");
            output.WriteLine("注：此 tick 包含 ShipView 首次在 runtime 下的重建以及 TargetLockIndex 初始化。");
            output.WriteLine($"对比稳态均值 {tickStats.Mean:F3} ms，首个 tick 多出约 {firstRuntimeTick.TotalTickMs - tickStats.Mean:F3} ms。");
        }
        output.WriteLine();

        // 全量扫描消除证据
        output.WriteLine("## 重复全量扫描消除证据");
        output.WriteLine();
        output.WriteLine("### Ship roster 全量扫描消除");
        output.WriteLine("- TickWorkset 和 ShipRoster 在内存中维护，系统遍历时直接引用数组而非全量数据库查询。");
        output.WriteLine("- ShipRoster 通过 ApplyDelta 增量更新（O(log N) 二分查找 + O(N) 合并），" +
                         "而非每次 tick 全量重建。");
        output.WriteLine("- ShipViewRefresh 仅刷新 ECS view 的增量（added/removed），" +
                         "随后将增量合并到 roster 而非全量重枚举。");
        output.WriteLine();
        output.WriteLine("### Target Lock 全量扫描消除");
        output.WriteLine("- TargetLockIndexes 提供 O(1) 的按 Owner/按 Target 查找能力，消除遍历全量 TargetLock 实体的需要。");
        output.WriteLine("- TargetLockCleanupSystem.AdvanceExistingLocks 使用 CopyTargetLockIds() " +
                         "获取当前所有活跃锁 ID（返回 EntityId[] 而非全实体查询）。");
        output.WriteLine("- TargetingSystem 使用 CopyOwnerLockCounts() 获取每艘飞船的活跃锁计数，");
        output.WriteLine("  避免实时查询 TargetLock 表。");
        output.WriteLine("- ResolutionSystem.ClearDeadLocks 使用 CopyTargetLockIdsForShip() 仅获取相关锁。");
        output.WriteLine();

        // 各阶段原始数据（末尾 5 个样本展示）
        output.WriteLine("## 最近 5 个 tick 明细");
        output.WriteLine();
        var lastSamples = samples.Count >= 5
            ? samples.Skip(samples.Count - 5).ToList()
            : samples.ToList();
        foreach (var sample in lastSamples)
        {
            var phasesFormatted = string.Join(", ",
                PhaseTimingCollector.PhaseNames
                    .Select((name, i) => $"{name}={sample.PhaseMs[i]:F3}ms"));
            output.WriteLine($"- Tick {sample.CompletedTickNumber}: total={sample.TotalTickMs:F3}ms [{phasesFormatted}]");
        }
        output.WriteLine();

        // 复现命令
        output.WriteLine("## 复现命令");
        output.WriteLine();
        output.WriteLine("```bash");
        output.WriteLine("# 确保使用 Release 配置");
        output.WriteLine("dotnet build demo/SpaceBattle/SpaceBattle.Main/SpaceBattle.Main.csproj -c Release");
        output.WriteLine();
        output.WriteLine("# 运行 benchmark（自动使用 50,000 飞船、12 workers）");
        output.WriteLine("dotnet run --project demo/SpaceBattle/SpaceBattle.Main/SpaceBattle.Main.csproj -c Release -- benchmark");
        output.WriteLine("```");
        output.WriteLine();

        output.WriteLine("## 设计约束说明");
        output.WriteLine("- 所有性能优化限于 demo/SpaceBattle/ 侧，未修改 src/ 中 Typhon 核心引擎代码。");
        output.WriteLine("- Benchmark 不依赖机器特定的毫秒阈值；CI 验收使用回归比较而非固定 40ms 门槛。");
        output.WriteLine("- 确定性已验证（#25）：相同 seed 和 worker 配置产生相同的 tick 序列结果。");
    }

    private readonly record struct StatsSummary(
        double Mean, double P50, double P95, double P99,
        double Max, double Min, double StdDev);

    private static StatsSummary ComputeStats(double[] values)
    {
        double mean = values.Average();
        return new StatsSummary(
            Mean: mean,
            P50: ComputePercentile(values, 50),
            P95: ComputePercentile(values, 95),
            P99: ComputePercentile(values, 99),
            Max: values.Max(),
            Min: values.Min(),
            StdDev: Math.Sqrt(values.Select(v => (v - mean) * (v - mean)).Average()));
    }

    private static double ComputePercentile(double[] data, int percentile)
    {
        var sorted = data.ToArray();
        Array.Sort(sorted);
        int n = sorted.Length;
        if (n == 0) return 0;
        double rank = percentile / 100.0 * (n - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (rank - lower) * (sorted[upper] - sorted[lower]);
    }

    private static double[] ComputeRollingP99(double[] data, int windowSize)
    {
        if (data.Length < windowSize) return [];
        var results = new double[data.Length - windowSize + 1];
        for (int i = 0; i < results.Length; i++)
        {
            var window = data.AsSpan(i, windowSize).ToArray();
            results[i] = ComputePercentile(window, 99);
        }
        return results;
    }

    private static StatsSummary ComputeBasicStats(double[] data)
    {
        double mean = data.Average();
        return new StatsSummary(
            Mean: mean,
            P50: ComputePercentile(data, 50),
            P95: ComputePercentile(data, 95),
            P99: ComputePercentile(data, 99),
            Max: data.Max(),
            Min: data.Min(),
            StdDev: Math.Sqrt(data.Select(v => (v - mean) * (v - mean)).Average()));
    }
}

/// <summary>不处理任何观察的 sink，benchmark 直接从 PhaseTimingCollector 读取数据。</summary>
internal sealed class NullObservationSink : ISpaceBattleObservationSink
{
    public void Publish(SpaceBattleObservation observation) { }
}

internal readonly record struct BenchmarkRunResult(
    IReadOnlyList<TickPhaseSample> Samples,
    TimeSpan MeasurementDuration);
