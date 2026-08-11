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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Open-time integrity verification is disabled (VerifyOnOpen = None). Damage that occurred while this "
                + "database was closed — bit rot, a truncated copy, a restore from the wrong place — will be served "
                + "silently rather than reported. Intended for benchmarks and in-memory fixtures only.")]
    private static partial void LogOpenVerificationDisabledCore(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Open-time integrity verification found {Count} non-fatal finding(s) (verdict {Verdict}). The database "
                + "opened. Run `typhon check <bundle> --depth deep` for the full report.")]
    private static partial void LogOpenVerificationFindingsCore(ILogger logger, int count, string verdict);

    private void LogOpenVerificationDisabled() => LogOpenVerificationDisabledCore(Logger);

    private void LogOpenVerificationFindings(int count, string verdict) => LogOpenVerificationFindingsCore(Logger, count, verdict);
}
