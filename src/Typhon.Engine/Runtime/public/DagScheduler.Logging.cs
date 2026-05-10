using System;
using Microsoft.Extensions.Logging;

namespace Typhon.Engine;

public sealed partial class DagScheduler
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "DAG Scheduler started: {SystemCount} systems, {WorkerCount} workers, {TickRate}Hz")]
    private partial void LogStarted(int systemCount, int workerCount, int tickRate);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "DAG Scheduler shutdown requested")]
    private partial void LogShutdownRequested();

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Worker {WorkerId} started")]
    private partial void LogWorkerStarted(int workerId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Tick {TickNumber} overran: {ActualMs:F2}ms > {TargetMs:F2}ms (ratio: {Ratio:F2})")]
    private partial void LogTickOverrun(long tickNumber, float actualMs, float targetMs, float ratio);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "System {SystemIndex} '{SystemName}' threw an exception during execution")]
    private partial void LogSystemException(int systemIndex, string systemName, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Overload level changed: {PreviousLevel} -> {NewLevel} at tick {TickNumber}")]
    private partial void LogOverloadLevelChanged(OverloadLevel previousLevel, OverloadLevel newLevel, long tickNumber);
}
