using System.Diagnostics;

namespace SpaceBattle;

/// <summary>
/// 轻量级每阶段计时收集器，用于 benchmark 模式下记录每个模拟 tick 中各阶段的墙钟耗时。
/// 线程安全设计：单线程阶段直接读写字段，并行阶段（Combat）用 Interlocked 协调首末线程。
/// </summary>
internal sealed class PhaseTimingCollector
{
    // 阶段序数（必须与 SpaceBattlePhases 声明顺序一致）
    public const int ShipViewRefresh = 0;
    public const int State = 1;
    public const int Steering = 2;
    public const int Movement = 3;
    public const int TargetLockCleanup = 4;
    public const int Targeting = 5;
    public const int Combat = 6;
    public const int Resolution = 7;
    public const int Output = 8;
    public const int PhaseCount = 9;

    public static readonly string[] PhaseNames =
    [
        "ShipViewRefresh",
        "State",
        "Steering",
        "Movement",
        "TargetLockCleanup",
        "Targeting",
        "Combat",
        "Resolution",
        "Output",
    ];

    // 当前 tick 的计时累积区（Stopwatch ticks）
    private long _tickStartTimestamp;
    private readonly long[] _phaseStartTimestamps = new long[PhaseCount];
    private long[] _phaseElapsedTicks = new long[PhaseCount];
    private long _tickElapsedTicks;

    // 并行阶段协调（Combat）
    private long _parallelStartTimestamp;
    private int _parallelActiveWorkers;

    // Resolution 阶段跨两个 System（DamageResolution + Resolution），用标志位避免重复 Begin。
    private bool _resolutionPhaseBegan;

    // 样本存储
    private readonly List<TickPhaseSample> _samples = [];

    public IReadOnlyList<TickPhaseSample> Samples => _samples;
    public int SampleCount => _samples.Count;

    /// <summary>重置所有状态。</summary>
    public void Reset()
    {
        _samples.Clear();
        _tickStartTimestamp = 0;
        _tickElapsedTicks = 0;
        Array.Clear(_phaseElapsedTicks, 0, PhaseCount);
        Array.Clear(_phaseStartTimestamps, 0, PhaseCount);
        _parallelStartTimestamp = 0;
        _parallelActiveWorkers = 0;
    }

    /// <summary>在每个 tick 开始时调用。</summary>
    public void BeginTick()
    {
        _tickStartTimestamp = Stopwatch.GetTimestamp();
        Array.Clear(_phaseElapsedTicks, 0, PhaseCount);
        _resolutionPhaseBegan = false;
    }

    /// <summary>单线程阶段开始。</summary>
    public void BeginPhase(int phaseIndex)
    {
        _phaseStartTimestamps[phaseIndex] = Stopwatch.GetTimestamp();
    }

    /// <summary>单线程阶段结束。</summary>
    public void EndPhase(int phaseIndex)
    {
        _phaseElapsedTicks[phaseIndex] += Stopwatch.GetTimestamp() - _phaseStartTimestamps[phaseIndex];
    }

    /// <summary>
    /// 标记 Resolution 阶段开始（仅首次有效）。DamageResolutionSystem 和 ResolutionSystem
    /// 在同一个 Resolution 阶段中依次执行，但 DamageResolutionSystem 可能因无伤害意图而跳过。
    /// </summary>
    public void BeginResolutionPhase()
    {
        if (!_resolutionPhaseBegan)
        {
            _resolutionPhaseBegan = true;
            BeginPhase(Resolution);
        }
    }

    /// <summary>标记 Resolution 阶段结束（仅当确已开始）。由 ResolutionSystem 在末尾调用。</summary>
    public void EndResolutionPhase()
    {
        if (_resolutionPhaseBegan)
        {
            _resolutionPhaseBegan = false;
            EndPhase(Resolution);
        }
    }

    /// <summary>并行阶段开始。首线程负责记录基准时间。</summary>
    public void BeginParallelPhase()
    {
        int active = Interlocked.Increment(ref _parallelActiveWorkers);
        if (active == 1)
        {
            Interlocked.Exchange(ref _parallelStartTimestamp, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>并行阶段结束。末线程负责记录总耗时。</summary>
    public void EndParallelPhase(int phaseIndex)
    {
        int remaining = Interlocked.Decrement(ref _parallelActiveWorkers);
        if (remaining == 0)
        {
            long elapsed = Stopwatch.GetTimestamp() - Interlocked.Read(ref _parallelStartTimestamp);
            _phaseElapsedTicks[phaseIndex] += elapsed;
        }
    }

    /// <summary>每个 tick 结束时调用，若处于收集状态则记录样本。</summary>
    public void EndTick(ulong completedTickNumber)
    {
        _tickElapsedTicks = Stopwatch.GetTimestamp() - _tickStartTimestamp;

        double freqPerMs = Stopwatch.Frequency / 1000.0;
        var phasesMs = new double[PhaseCount];
        for (int i = 0; i < PhaseCount; i++)
        {
            phasesMs[i] = _phaseElapsedTicks[i] / freqPerMs;
        }

        _samples.Add(new TickPhaseSample(
            completedTickNumber,
            _tickElapsedTicks / freqPerMs,
            phasesMs));
    }
}

/// <summary>
/// 一个 tick 的各阶段耗时样本。
/// </summary>
internal readonly record struct TickPhaseSample(
    ulong CompletedTickNumber,
    double TotalTickMs,
    double[] PhaseMs);
