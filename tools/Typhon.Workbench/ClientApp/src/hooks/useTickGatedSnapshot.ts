import { useEffect, useRef, useState } from 'react';

/**
 * Default debounce window used by the Data Flow / Access Matrix panels for tick-gated metadata
 * snapshots. Tuned for "feels responsive while still coalescing live-stream bursts" — at 60 Hz tick
 * cadence a 150 ms window captures ~9 ticks.
 */
export const REFRESH_DEBOUNCE_MS = 150;

/**
 * Returns a debounced copy of `value`. The output trails the input by `delayMs` and only updates after
 * `value` stops changing for that long. Used to coalesce bursts of upstream updates (e.g., live SSE tick
 * appends or rapid scrubs of a time selection) into one downstream re-render instead of N.
 */
export function useDebouncedValue<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState<T>(value);
  useEffect(() => {
    if (delayMs <= 0) {
      setDebounced(value);
      return;
    }
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

/**
 * Snapshot `value` only when `key` changes. Other mutations to `value` (different reference, same key) do
 * not propagate — the hook keeps returning the previous snapshot. Used by the Data Flow + Access Matrix
 * panels to gate their heavy memos on tick-number deltas while ignoring orthogonal metadata mutations
 * (thread info appended, chunk manifest extended, global metrics updated, etc.).
 *
 * The latest `value` is captured at the moment `key` changes, not at the moment the effect's closure was
 * created — so the snapshot always reflects the freshest data when the gate trips. A one-time first-fill
 * effect handles the common null → non-null transition (panels start with metadata=null and would
 * otherwise wait for the first tick advance to ever paint).
 */
export function useTickGatedSnapshot<T>(value: T, key: number | string): T {
  const valueRef = useRef(value);
  valueRef.current = value;
  const [snapshot, setSnapshot] = useState<T>(value);
  useEffect(() => {
    setSnapshot(valueRef.current);
  }, [key]);
  useEffect(() => {
    if (snapshot == null && value != null) {
      setSnapshot(value);
    }
  }, [snapshot, value]);
  return snapshot;
}
