namespace Typhon.Profiler;

// ═══════════════════════════════════════════════════════════════════════
// Wire-format enums shared between the producer ([TraceEvent] ref structs
// in Typhon.Engine) and consumers (typed DTO decoders, viewers, tests).
// Used to live next to the per-kind codec classes; consolidated here when
// the codecs themselves were retired in favor of the generator-emitted
// EncodeTo / EmitX paths.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Reason a transaction was rolled back. Carried on the wire for <see cref="TraceEventKind.TransactionRollback"/>.</summary>
public enum TransactionRollbackReason : byte
{
    /// <summary>Caller invoked <c>Transaction.Rollback()</c> directly.</summary>
    Explicit = 0,
    /// <summary>Implicit rollback from <c>Dispose()</c> when the transaction was never committed.</summary>
    AutoOnDispose = 1,
    /// <summary>Rollback triggered by a concurrency-conflict resolver outcome.</summary>
    Conflict = 2,
    /// <summary>Rollback triggered by a transaction-level deadline / timeout.</summary>
    TimedOut = 3,
}

/// <summary>Variant of an <see cref="TraceEventKind.ConcurrencyAdaptiveWaiterYieldOrSleep"/> event.</summary>
public enum AdaptiveWaiterTransitionKind : byte
{
    /// <summary>The current SpinOnce yielded the thread (Thread.Yield / Sleep(0)).</summary>
    Yield = 1,
    /// <summary>The current SpinOnce slept (Sleep(1) or longer).</summary>
    Sleep = 2,
}
