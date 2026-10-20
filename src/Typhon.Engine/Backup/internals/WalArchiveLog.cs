using Microsoft.Extensions.Logging;

namespace Typhon.Engine.Internals;

/// <summary>The WAL archive's log lines (impl/1418 §3.10). Never per record: per segment at most, and a failing store once per change of state.</summary>
internal static partial class WalArchiveLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "WAL archive: {Held} bytes held for the archive exceed the backlog limit {Limit}; the floor (LSN {Floor}) is released and the checkpoint "
                  + "reclaims up to LSN {Trim} un-archived. The archive will record a gap, and a backup point is requested")]
    public static partial void FloorReleased(ILogger logger, long held, long limit, long floor, long trim);
}
