using System.Threading.Channels;
using Typhon.Engine;

namespace SpaceBattle;

internal readonly record struct SpaceBattleRunSample(
    ulong Seed,
    ulong CompletedTicks,
    uint RulesetVersion,
    uint InitialShipCount,
    uint AliveShipCount,
    uint ProcessSegment,
    SimulationRunStatus Status,
    SimulationRunOutcome Outcome,
    long WinnerEntityKey,
    bool HasWinner);

internal readonly record struct SpaceBattleTickSample(
    long RuntimeTickNumber,
    ulong CompletedTicks,
    uint ProcessSegment,
    SpaceBattleRunSample Run,
    SpaceBattleModeCounts Modes,
    SpaceBattleCounters Counters);

internal sealed class SpaceBattleObservationPublisher : IDisposable
{
    private readonly DatabaseEngine _engine;
    private readonly ISpaceBattleObservationSink _sink;
    private readonly IResourceGraph _resourceGraph;
    private readonly Channel<SpaceBattleTickSample> _samples = Channel.CreateUnbounded<SpaceBattleTickSample>(
        new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource _cancellation = new();
    private Task _consumer = null!;
    private TickTelemetryRing _telemetry = null!;
    private int _disposed;

    public SpaceBattleObservationPublisher(
        DatabaseEngine engine,
        ISpaceBattleObservationSink sink)
    {
        _engine = engine;
        _sink = sink;
        _resourceGraph = new ResourceGraph(engine.Owner);
    }

    public void Start(TyphonRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (_consumer != null)
        {
            throw new InvalidOperationException("SpaceBattle observation publisher 只能启动一次。");
        }

        _telemetry = runtime.Telemetry;
        _consumer = Task.Run(ConsumeAsync);
    }

    public bool TryPublish(in SpaceBattleTickSample sample)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        return _samples.Writer.TryWrite(sample);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _samples.Writer.TryComplete();
        if (_consumer != null)
        {
            _consumer.GetAwaiter().GetResult();
        }

        _cancellation.Dispose();
    }

    private async Task ConsumeAsync()
    {
        try
        {
            TickDurationHistogram histogram = new();
            uint processSegment = 0;
            bool hasProcessSegment = false;

            await foreach (SpaceBattleTickSample sample in _samples.Reader.ReadAllAsync(_cancellation.Token))
            {
                if (!hasProcessSegment || processSegment != sample.ProcessSegment)
                {
                    histogram = new TickDurationHistogram();
                    processSegment = sample.ProcessSegment;
                    hasProcessSegment = true;
                }

                TickTelemetry telemetry = await ReadTelemetryAsync(
                    sample.RuntimeTickNumber,
                    _cancellation.Token);
                histogram.Add(telemetry);

                if (sample.CompletedTicks % SimulationDefinition.ObservationLogIntervalTicks == 0 ||
                    sample.Run.Status != SimulationRunStatus.Running)
                {
                    SimulationRunSnapshot run = new(
                        sample.Run.Seed,
                        sample.Run.CompletedTicks,
                        sample.Run.RulesetVersion,
                        sample.Run.InitialShipCount,
                        sample.Run.AliveShipCount,
                        sample.Run.ProcessSegment,
                        sample.Run.Status,
                        sample.Run.Outcome,
                        sample.Run.HasWinner ? sample.Run.WinnerEntityKey : null);
                    Publish(new SpaceBattleLogSnapshot(
                        sample.CompletedTicks,
                        sample.ProcessSegment,
                        run,
                        sample.Modes,
                        sample.Counters,
                        histogram.CreateSnapshot()));
                }

                if (sample.CompletedTicks % SimulationDefinition.ResourceSnapshotIntervalTicks == 0)
                {
                    Publish(new SpaceBattleResourceSnapshot(
                        sample.CompletedTicks,
                        sample.ProcessSegment,
                        _resourceGraph.GetSnapshot(_engine)));
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    private async ValueTask<TickTelemetry> ReadTelemetryAsync(
        long runtimeTickNumber,
        CancellationToken cancellationToken)
    {
        TickTelemetryRing telemetry = _telemetry;

        while (telemetry.NewestTick < runtimeTickNumber)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }

        if (runtimeTickNumber < telemetry.OldestAvailableTick)
        {
            throw new InvalidOperationException(
                $"SpaceBattle telemetry tick {runtimeTickNumber} 已被 ring 覆盖；保留范围为 " +
                $"[{telemetry.OldestAvailableTick}, {telemetry.NewestTick}]。");
        }

        ref readonly TickTelemetry snapshot = ref telemetry.GetTick(runtimeTickNumber);
        return snapshot;
    }

    private void Publish(SpaceBattleObservation observation)
    {
        try
        {
            _sink.Publish(observation);
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class TickDurationHistogram
{
    private const int LinearBucketCount = 512;
    private const int BucketCount = 1024;
    private const double LinearBucketWidthMilliseconds = 0.5;
    private const double LinearRangeMilliseconds = LinearBucketCount * LinearBucketWidthMilliseconds;
    private readonly int[] _bucketCounts = new int[BucketCount];
    private int _sampleCount;
    private double _maximumActualDurationMilliseconds;
    private int _overrunCount;
    private double _maximumOverrunRatio;
    private double _lastActualDurationMilliseconds;
    private double _lastTargetDurationMilliseconds;
    private double _lastOverrunRatio;

    public void Add(TickTelemetry telemetry)
    {
        double actualDurationMilliseconds = Math.Max(0d, telemetry.ActualDurationMs);
        int bucket = GetBucket(actualDurationMilliseconds);
        _bucketCounts[bucket]++;
        _sampleCount++;
        _maximumActualDurationMilliseconds = Math.Max(
            _maximumActualDurationMilliseconds,
            actualDurationMilliseconds);
        if (telemetry.OverrunRatio > 1d)
        {
            _overrunCount++;
        }

        _maximumOverrunRatio = Math.Max(_maximumOverrunRatio, telemetry.OverrunRatio);
        _lastActualDurationMilliseconds = telemetry.ActualDurationMs;
        _lastTargetDurationMilliseconds = telemetry.TargetDurationMs;
        _lastOverrunRatio = telemetry.OverrunRatio;
    }

    public SpaceBattleTickPerformance CreateSnapshot()
    {
        return new SpaceBattleTickPerformance(
            _sampleCount,
            Percentile(0.50),
            Percentile(0.95),
            Percentile(0.99),
            _maximumActualDurationMilliseconds,
            _overrunCount,
            _maximumOverrunRatio,
            _lastActualDurationMilliseconds,
            _lastTargetDurationMilliseconds,
            _lastOverrunRatio);
    }

    private double Percentile(double percentile)
    {
        if (_sampleCount == 0)
        {
            return 0;
        }

        int rank = Math.Max(1, (int)Math.Ceiling(_sampleCount * percentile));
        int seen = 0;
        for (var bucket = 0; bucket < _bucketCounts.Length; bucket++)
        {
            seen += _bucketCounts[bucket];
            if (seen >= rank)
            {
                return GetBucketRepresentative(bucket);
            }
        }

        return GetBucketRepresentative(BucketCount - 1);
    }

    private static int GetBucket(double durationMilliseconds)
    {
        if (durationMilliseconds < LinearRangeMilliseconds)
        {
            return (int)(durationMilliseconds / LinearBucketWidthMilliseconds);
        }

        double logarithmicRange = Math.Log2(durationMilliseconds / LinearRangeMilliseconds);
        return Math.Min(BucketCount - 1, LinearBucketCount + (int)logarithmicRange);
    }

    private static double GetBucketRepresentative(int bucket)
    {
        if (bucket < LinearBucketCount)
        {
            return (bucket + 0.5) * LinearBucketWidthMilliseconds;
        }

        return LinearRangeMilliseconds * Math.Pow(2, bucket - LinearBucketCount + 0.5);
    }
}
