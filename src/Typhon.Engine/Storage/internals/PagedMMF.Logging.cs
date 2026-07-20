using Microsoft.Extensions.Logging;

namespace Typhon.Engine.Internals;

public partial class PagedMMF
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Page cache is {SizeMiB} MiB — below the recommended minimum of {RecommendedMiB} MiB. A small cache risks "
                + "PageCacheBackpressureTimeout when a transaction's working set exceeds it; raise it for production workloads "
                + "(e.g. TyphonOptions.PageCacheSize(...) or ManagedPagedMMFOptions.DatabaseCacheSize).")]
    private static partial void LogSmallPageCache(ILogger logger, ulong sizeMiB, ulong recommendedMiB);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CopyPageWithSeqlock: page {MemPageIndex} has odd ModificationCounter={Counter} but is not Exclusive-latched — "
                + "stale seqlock counter, skipping without wait.")]
    private static partial void LogStaleSeqlockCounterSkip(ILogger logger, int memPageIndex, int counter);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CopyPageWithSeqlock: skipping page after {ElapsedMs}ms — writer holding odd ModificationCounter={Counter}")]
    private static partial void LogSeqlockWriterHeldSkip(ILogger logger, int elapsedMs, int counter);
}
