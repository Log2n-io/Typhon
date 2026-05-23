// Panel-local camera back/forward history for the Database File Map. Mirrors the Critical Path panel's
// `ViewHistory` model (a bounded, fork-on-push stack walked by the mouse thumb buttons) but stores camera
// snapshots instead of time windows, and is independent of the global Workbench nav history (which records
// cross-panel jumps). Pure functions so the stack semantics are unit-testable without the panel.

import type { Camera } from './camera';

/** Cap on stored camera states — back/forward walks at most this many entries. */
export const CAMERA_HISTORY_CAP = 50;

/** A bounded back/forward stack of camera snapshots; `pointer` indexes the currently-displayed entry (−1 = empty). */
export interface CameraHistory {
  entries: Camera[];
  pointer: number;
}

/** An empty history. */
export function emptyCameraHistory(): CameraHistory {
  return { entries: [], pointer: -1 };
}

function sameCamera(a: Camera, b: Camera): boolean {
  return a.scale === b.scale && a.x === b.x && a.y === b.y;
}

/**
 * Appends `cam` to the history, dropping any forward entries (a fresh navigation forks the timeline) and capping
 * the length at `cap` (the oldest entry falls off the front). A push identical to the current entry is a no-op, so
 * a gesture that lands exactly where it started (e.g. fit-when-already-fit) doesn't bloat the stack. The pointer
 * lands on the new entry. The camera is copied so later mutation of the live camera ref can't corrupt the stack.
 */
export function pushCameraHistory(h: CameraHistory, cam: Camera, cap: number = CAMERA_HISTORY_CAP): CameraHistory {
  if (h.pointer >= 0 && sameCamera(h.entries[h.pointer], cam)) {
    return h;
  }
  const entries = h.entries.slice(0, h.pointer + 1);
  entries.push({ ...cam });
  while (entries.length > cap) {
    entries.shift();
  }
  return { entries, pointer: entries.length - 1 };
}

/**
 * Moves the pointer by `dir` (−1 back, +1 forward). Returns the **same object** when the step would fall off
 * either end, so callers can treat referential equality as "at an end — nothing to navigate to".
 */
export function stepCameraHistory(h: CameraHistory, dir: number): CameraHistory {
  const next = h.pointer + dir;
  if (next < 0 || next >= h.entries.length) {
    return h;
  }
  return { entries: h.entries, pointer: next };
}
