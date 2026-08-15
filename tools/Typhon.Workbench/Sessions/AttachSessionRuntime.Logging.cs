using Microsoft.Extensions.Logging;

namespace Typhon.Workbench.Sessions;

/// <summary>Source-generated log messages for <see cref="AttachSessionRuntime"/>.</summary>
public sealed partial class AttachSessionRuntime
{
    [LoggerMessage(Level = LogLevel.Information, Message = "AttachSessionRuntime starting — will connect to {Host}:{Port}")]
    private partial void LogStarting(string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attach: connected to engine at {Host}:{Port}")]
    private partial void LogConnected(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attach: connection to engine lost, reconnecting...")]
    private partial void LogConnectionLost();

    [LoggerMessage(Level = LogLevel.Error, Message = "Attach: unexpected error in read loop")]
    private partial void LogUnexpectedError(System.Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attach: engine sent SHUTDOWN frame")]
    private partial void LogShutdownReceived();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attach: unknown frame type 0x{Type:X2}")]
    private partial void LogUnknownFrame(byte type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attach: malformed {FrameType} frame ({Reason}) — dropping")]
    private partial void LogMalformedFrame(string frameType, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Attach: INIT received — {SystemCount} systems, {WorkerCount} workers, {Rate:F0} Hz")]
    private partial void LogInitReceived(int systemCount, int workerCount, float rate);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Attach: LZ4 decompression mismatch — expected {Expected}, got {Got}")]
    private partial void LogDecompressionMismatch(int expected, int got);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attach: SSE subscriber {Id} connected (total: {Count})")]
    private partial void LogSubscriberConnected(System.Guid id, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attach: SSE subscriber {Id} disconnected (total: {Count})")]
    private partial void LogSubscriberDisconnected(System.Guid id, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Attach: read-loop background task faulted")]
    private partial void LogReadLoopFaulted(System.Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Attach: SSE subscriber {Id} kicked — buffer full longer than the slow-subscriber timeout")]
    private partial void LogSlowSubscriberKicked(System.Guid id);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Attach: reconnect rejected — Init signature changed; session marked unrecoverable")]
    private partial void LogInitMismatchUnrecoverable();

    // ── On-demand tick capture (#805) ────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture: arm requested for {TickCount} tick(s) — takes effect at the next TickStart")]
    private partial void LogCaptureArmRequested(int tickCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Capture: absolute tick numbering established — engine tick {EngineTick} maps to derived tick {DerivedTick} (offset {Offset})")]
    private partial void LogTickOffsetLearned(uint engineTick, uint derivedTick, long offset);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Capture: tick numbering drifted — engine tick {EngineTick} vs derived {DerivedTick}; offset was {ExpectedOffset}, now {ActualOffset}. "
                  + "A TickStart record was lost; reported tick numbers are no longer trustworthy.")]
    private partial void LogTickNumberingDrift(uint engineTick, uint derivedTick, long expectedOffset, long actualOffset);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Capture: session saved to {Path} ({Bytes} bytes) before teardown — {RecordedTicks} tick(s) had been recorded")]
    private partial void LogCaptureAutoSaved(string path, long bytes, long recordedTicks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capture: auto-save failed — {Reason}")]
    private partial void LogCaptureAutoSaveFailed(string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Attach: engine RESTART detected (session start {PreviousCreatedUtcTicks} → {NewCreatedUtcTicks}); ending this session rather than "
                  + "continuing its tick axis into a new run")]
    private partial void LogEngineRestartDetected(long previousCreatedUtcTicks, long newCreatedUtcTicks);
}
