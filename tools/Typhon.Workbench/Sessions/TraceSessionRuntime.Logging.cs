using Microsoft.Extensions.Logging;

namespace Typhon.Workbench.Sessions;

/// <summary>Source-generated log messages for <see cref="TraceSessionRuntime"/>.</summary>
public sealed partial class TraceSessionRuntime
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Trace cache build failed for {Path}")]
    private partial void LogBuildFailed(System.Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Trace cache build background task faulted for {Path}")]
    private partial void LogBuildTaskFaulted(System.Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Source trace file overwritten on disk — newer version available for {Path}")]
    private partial void LogSourceFileVersionDetected(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to install source-file watcher for {Path}")]
    private partial void LogSourceWatchFailed(System.Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to build the span-instance index for {Path} — span-kind scoping unavailable")]
    private partial void LogSpanInstanceIndexFailed(System.Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to build the sample classifier for {Path} — off-CPU classification unavailable")]
    private partial void LogSampleClassifierFailed(System.Exception exception, string path);
}
