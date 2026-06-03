import { useEffect } from 'react';

const DOUBLE_SHIFT_WINDOW_MS = 300;

const INPUT_TAGS = new Set(['INPUT', 'TEXTAREA', 'SELECT']);

function isEditableTarget(): boolean {
  const el = document.activeElement;
  if (!el) return false;
  if (INPUT_TAGS.has(el.tagName)) return true;
  return el.getAttribute('contenteditable') != null;
}

/**
 * Pure handler factory — exported for unit testing. Detects a *double-tap* of Shift: two distinct presses within
 * {@link DOUBLE_SHIFT_WINDOW_MS}.
 *
 * **HOLDING Shift must NOT trigger it.** While a key is held the OS emits auto-repeat `keydown` events, which a
 * naive "two keydowns within the window" check reads as a double-tap. Two guards prevent that: we drop
 * `event.repeat` events, AND we require an **intervening keyup** — a real second tap is preceded by a release, a
 * held key is not. The keyup guard also covers platforms whose modifier auto-repeat doesn't set the `repeat` flag.
 *
 * Returns both handlers (sharing one closure of state); the caller wires `onKeyDown`/`onKeyUp` to the window.
 */
export function createShiftShiftHandler(
  callback: () => void,
  getEditableTarget: () => boolean = isEditableTarget,
  now: () => number = () => performance.now(),
): { onKeyDown: (e: KeyboardEvent) => void; onKeyUp: (e: KeyboardEvent) => void } {
  let lastShiftAt = -Infinity;
  let releasedSinceLast = true; // a fresh tap counts only once the previous Shift has been released

  return {
    onKeyDown(e: KeyboardEvent) {
      if (e.key !== 'Shift' || e.repeat) return; // ignore the OS auto-repeat fired while Shift is held
      if (getEditableTarget()) return;
      const t = now();
      if (releasedSinceLast && t - lastShiftAt <= DOUBLE_SHIFT_WINDOW_MS) {
        lastShiftAt = -Infinity; // reset so a third Shift doesn't immediately re-fire
        callback();
      } else {
        lastShiftAt = t;
      }
      releasedSinceLast = false; // the next tap counts only after a keyup re-arms it
    },
    onKeyUp(e: KeyboardEvent) {
      if (e.key !== 'Shift') return;
      releasedSinceLast = true;
    },
  };
}

export function useShiftShift(callback: () => void): void {
  useEffect(() => {
    const { onKeyDown, onKeyUp } = createShiftShiftHandler(callback);
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
    };
  }, [callback]);
}
