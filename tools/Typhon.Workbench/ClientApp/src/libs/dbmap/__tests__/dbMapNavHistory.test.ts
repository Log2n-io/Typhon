import { describe, expect, it } from 'vitest';
import {
  CAMERA_HISTORY_CAP,
  emptyCameraHistory,
  pushCameraHistory,
  stepCameraHistory,
  type CameraHistory,
} from '../dbMapNavHistory';
import type { Camera } from '../camera';

const cam = (scale: number, x: number, y: number): Camera => ({ scale, x, y });

describe('pushCameraHistory', () => {
  it('appends and advances the pointer', () => {
    let h = emptyCameraHistory();
    h = pushCameraHistory(h, cam(1, 0, 0));
    h = pushCameraHistory(h, cam(2, 10, 10));
    expect(h.entries.length).toBe(2);
    expect(h.pointer).toBe(1);
    expect(h.entries[1]).toEqual(cam(2, 10, 10));
  });

  it('forks the timeline — a push after stepping back drops the forward entries', () => {
    let h: CameraHistory = { entries: [cam(1, 0, 0), cam(2, 0, 0), cam(3, 0, 0)], pointer: 1 };
    h = pushCameraHistory(h, cam(9, 9, 9));
    expect(h.entries).toEqual([cam(1, 0, 0), cam(2, 0, 0), cam(9, 9, 9)]);
    expect(h.pointer).toBe(2);
  });

  it('is a no-op when the camera equals the current entry', () => {
    const h0: CameraHistory = { entries: [cam(1, 2, 3)], pointer: 0 };
    const h1 = pushCameraHistory(h0, cam(1, 2, 3));
    expect(h1).toBe(h0);
  });

  it('caps the length, dropping the oldest', () => {
    let h = emptyCameraHistory();
    for (let i = 0; i < CAMERA_HISTORY_CAP + 5; i++) {
      h = pushCameraHistory(h, cam(i + 1, 0, 0));
    }
    expect(h.entries.length).toBe(CAMERA_HISTORY_CAP);
    expect(h.pointer).toBe(CAMERA_HISTORY_CAP - 1);
    // Oldest five fell off the front; the first surviving entry is scale 6.
    expect(h.entries[0]).toEqual(cam(6, 0, 0));
  });

  it('copies the camera so later mutation of the source cannot corrupt the stack', () => {
    const live = cam(1, 0, 0);
    const h = pushCameraHistory(emptyCameraHistory(), live);
    live.scale = 99;
    expect(h.entries[0].scale).toBe(1);
  });
});

describe('stepCameraHistory', () => {
  const h: CameraHistory = { entries: [cam(1, 0, 0), cam(2, 0, 0), cam(3, 0, 0)], pointer: 1 };

  it('moves back and forward', () => {
    expect(stepCameraHistory(h, -1).pointer).toBe(0);
    expect(stepCameraHistory(h, 1).pointer).toBe(2);
  });

  it('returns the same object at either end (no-op signal)', () => {
    const atStart: CameraHistory = { entries: h.entries, pointer: 0 };
    const atEnd: CameraHistory = { entries: h.entries, pointer: 2 };
    expect(stepCameraHistory(atStart, -1)).toBe(atStart);
    expect(stepCameraHistory(atEnd, 1)).toBe(atEnd);
  });
});
