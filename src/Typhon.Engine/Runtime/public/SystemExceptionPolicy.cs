using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Controls what an unhandled exception thrown by a system means for the rest of the tick. Set via <see cref="RuntimeOptions.SystemExceptionPolicy"/>; the
/// default is <see cref="Isolate"/>, which is the behaviour Typhon has always had.
/// </summary>
/// <remarks>
/// This axis is about <b>cancelling work in progress</b>, not about durability. Under every policy the tick fence and the Unit-of-Work flush run to completion
/// (rule <c>TP-01a</c>), because SingleVersion writes receive their WAL record at the fence — skipping it would leave un-logged mutations on dirty pages for
/// the checkpoint thread to persist. Transactions committed by systems that ran before the failure stay committed; a commit is irrevocable (rule
/// <c>AP-02</c>). See <c>claude/design/Runtime/08-strict-tick-abort.md</c>.
/// </remarks>
[PublicAPI]
public enum SystemExceptionPolicy
{
    /// <summary>
    /// Fault isolation — the default, and Typhon's behaviour before issue #567. A throwing system is marked failed and its successors are skipped with
    /// <see cref="SkipReason.DependencyFailed"/>, but independent branches keep running and tick-end processing is unaffected. The runtime stays usable.
    /// </summary>
    Isolate = 0,

    /// <summary>
    /// Strict abort — the first unhandled system exception cancels the remainder of the tick. No system that has not already started is executed (they
    /// report <see cref="SkipReason.TickAborted"/>); systems already running finish normally, since nothing is interrupted mid-body. The subscription output
    /// phase is suppressed, so a failed tick is never published. The runtime then enters a <b>terminal</b> failed state — subsequent ticks do not run.
    /// </summary>
    AbortTickAndStop = 1
}
