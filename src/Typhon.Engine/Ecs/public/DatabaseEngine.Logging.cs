using Microsoft.Extensions.Logging;
using System;

namespace Typhon.Engine;

/// <summary>
/// Source-generated log messages for <see cref="DatabaseEngine"/>. Using <c>[LoggerMessage]</c> keeps
/// level checks in the generated wrapper (no boxing or allocation when the level is filtered out),
/// per CLAUDE.md guidance.
/// </summary>
public partial class DatabaseEngine
{
    [LoggerMessage(LogLevel.Warning,
        "An index rebuild worker failed after another had already failed the open; the first failure is the one rethrown.")]
    private partial void LogIndexRebuildWorkerFailure(Exception exception);
}
