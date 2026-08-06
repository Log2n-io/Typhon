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

    // Error, not Warning: the thread is still alive and still consuming CPU, and nothing will ever reclaim it. Silence here is what let stranded workers
    // accumulate one per shutdown until the process was starved of cores.
    [LoggerMessage(Level = LogLevel.Error,
        Message = "Worker {WorkerId} (thread {ManagedThreadId}) did not exit within the shutdown join window and is still running — it will keep consuming CPU "
                  + "for the lifetime of this process")]
    private partial void LogWorkerJoinTimeout(int workerId, int managedThreadId);

    // Expected only when Shutdown races a tick dispatch, which leaves systems nobody will ever run. The work of that tick is lost — say so, rather than
    // letting a silently truncated tick look like a clean stop.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Track {TrackIndex} was abandoned during shutdown with {SystemsRemaining} system(s) unfinished — that tick's work did not complete")]
    private partial void LogTickDrainAbandonedOnShutdown(int trackIndex, int systemsRemaining);
}
