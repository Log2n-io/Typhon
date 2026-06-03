/**
 * Single-vs-double click disambiguation for tree rows.
 *
 * A row's single click and double click want different, conflicting actions — e.g. a single click on an archetype
 * toggles its expand/collapse, but a *double* click opens the Archetype Inspector and must NOT toggle the node. The
 * two are only distinguishable in time (a double click arrives as click → click → dblclick), so the single-click
 * action is *deferred* by one double-click window; if a double click lands inside that window it cancels the pending
 * single-click action and runs its own.
 *
 * Pure (no React, no DOM) so it is unit-testable with fake timers, à la `createShiftShiftHandler`. Hold one instance
 * per row (a ref) and call `cancel()` on unmount so a deferred action can't fire against an unmounted/stale node.
 */
export function createClickDisambiguator(delayMs: number) {
  let timer: ReturnType<typeof setTimeout> | null = null;

  const cancel = (): void => {
    if (timer != null) {
      clearTimeout(timer);
      timer = null;
    }
  };

  return {
    /** Schedule the deferred single-click `action`, replacing any still-pending one. */
    onSingle(action: () => void): void {
      cancel();
      timer = setTimeout(() => {
        timer = null;
        action();
      }, delayMs);
    },
    /** Cancel a pending single-click action — call this from the double-click handler (and on unmount). */
    cancel,
  };
}
