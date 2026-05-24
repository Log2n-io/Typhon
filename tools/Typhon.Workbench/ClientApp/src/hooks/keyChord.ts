// A two-key "leader" chord state machine (PC-8): press a leader key (e.g. `g`), then a second key within a
// short window resolves to an action (`g c` → focus Component Inspector). Extracted as a pure factory — like
// `createShiftShiftHandler` — so it unit-tests with fake timers, no `renderHook`.

export interface ChordHandler {
  /**
   * Feed a keydown. Returns `true` when the chord consumed the event (the caller should then stop its own
   * processing of this key): either the leader armed the chord, or an armed chord resolved the second key.
   * Returns `false` for any key the chord ignores, so normal shortcut handling continues.
   */
  handle: (e: KeyboardEvent) => boolean;
  /** Disarm + clear the pending timer (call on unmount). */
  cancel: () => void;
}

export interface ChordOptions {
  /** The leader key, matched case-insensitively (e.g. `'g'`). */
  leader: string;
  /** Resolve the second key (lower-cased) to an action; return `true` if it named a known target. */
  resolve: (key: string) => boolean;
  /** Window after the leader within which the second key counts. Default 1000 ms. */
  timeoutMs?: number;
  /** Guard: don't arm the leader while the user is typing in a text field. */
  isTyping?: () => boolean;
  /**
   * Notified on every armed-state transition: `true` the instant the leader arms (the window opens), `false`
   * when it closes — the second key was pressed, the timeout elapsed, or the handler was cancelled. Lets the
   * UI show/hide a "waiting for the second key" hint without reaching into the chord's internals.
   */
  onArmedChange?: (armed: boolean) => void;
  /** Injectable timers for tests; default to the global window timers. */
  setTimer?: (fn: () => void, ms: number) => number;
  clearTimer?: (id: number) => void;
}

export function createChordHandler(opts: ChordOptions): ChordHandler {
  const timeoutMs = opts.timeoutMs ?? 1000;
  const setTimer = opts.setTimer ?? ((fn, ms) => setTimeout(fn, ms) as unknown as number);
  const clearTimer = opts.clearTimer ?? ((id) => clearTimeout(id));

  let armed = false;
  let timer: number | null = null;

  const disarm = () => {
    const wasArmed = armed;
    armed = false;
    if (timer !== null) {
      clearTimer(timer);
      timer = null;
    }
    if (wasArmed) {
      opts.onArmedChange?.(false);
    }
  };

  return {
    handle(e: KeyboardEvent): boolean {
      if (armed) {
        disarm();
        // A modified key (Ctrl+K, etc.) after the leader isn't a chord target — let it through unconsumed.
        if (e.ctrlKey || e.altKey || e.metaKey) {
          return false;
        }
        if (opts.resolve(e.key.toLowerCase())) {
          e.preventDefault();
        }
        return true; // the second bare key belongs to the chord attempt (matched or not)
      }
      const isLeader = !e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && e.key.toLowerCase() === opts.leader.toLowerCase();
      if (!isLeader) {
        return false;
      }
      if (opts.isTyping?.()) {
        return false; // typing — let the leader key type normally
      }
      e.preventDefault();
      armed = true;
      timer = setTimer(disarm, timeoutMs);
      opts.onArmedChange?.(true);
      return true;
    },
    cancel: disarm,
  };
}
