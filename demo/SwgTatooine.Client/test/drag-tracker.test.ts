import { describe, expect, it } from 'vitest';
import { DragKind, DragTracker } from '../src/camera/drag-tracker';

const MOUSE = 1;
const FINGER_2 = 7;
const LEFT = 0;
const MIDDLE = 1;
const RIGHT = 2;
const BACK = 3;
const LEFT_BIT = 1;
const RIGHT_BIT = 2;
const MIDDLE_BIT = 4;

describe('DragTracker', () => {
  it('turns a left press and release without movement into a click', () => {
    const t = new DragTracker();
    expect(t.down(MOUSE, LEFT, 100, 100)).toBe(true);
    expect(t.kind).toBe(DragKind.Pan);
    expect(t.move(MOUSE, LEFT_BIT, 102, 101)).toBe(false); // under the threshold
    expect(t.up(MOUSE, 0)).toBe(true);
    expect(t.kind).toBe(DragKind.None);
  });

  it('turns a left press past the threshold into a pan, not a click', () => {
    const t = new DragTracker();
    t.down(MOUSE, LEFT, 100, 100);
    expect(t.move(MOUSE, LEFT_BIT, 110, 100)).toBe(true);
    t.moveTo(110, 100);
    expect([t.lastX, t.lastY]).toEqual([110, 100]);
    // Once past the threshold, small moves act too.
    expect(t.move(MOUSE, LEFT_BIT, 111, 100)).toBe(true);
    expect(t.up(MOUSE, 0)).toBe(false);
  });

  it('orbits with the right or middle button, and never clicks with them', () => {
    const t = new DragTracker();
    expect(t.down(MOUSE, RIGHT, 0, 0)).toBe(true);
    expect(t.kind).toBe(DragKind.Orbit);
    expect(t.up(MOUSE, 0)).toBe(false);
    expect(t.down(MOUSE, MIDDLE, 0, 0)).toBe(true);
    expect(t.kind).toBe(DragKind.Orbit);
    expect(t.move(MOUSE, MIDDLE_BIT, 20, 0)).toBe(true);
    expect(t.up(MOUSE, 0)).toBe(false);
  });

  it('ignores the back and forward buttons', () => {
    const t = new DragTracker();
    expect(t.down(MOUSE, BACK, 0, 0)).toBe(false);
    expect(t.kind).toBe(DragKind.None);
  });

  it('ignores a second button while a drag is in progress, and ends the drag when its own button is released first', () => {
    const t = new DragTracker();
    t.down(MOUSE, LEFT, 0, 0);
    expect(t.down(MOUSE, RIGHT, 0, 0)).toBe(false);
    expect(t.kind).toBe(DragKind.Pan);
    // Right pressed while left is held: browsers report it as a move with both bits.
    expect(t.move(MOUSE, LEFT_BIT | RIGHT_BIT, 20, 0)).toBe(true);
    // Left released while right is held: a move without the left bit ends the pan.
    expect(t.move(MOUSE, RIGHT_BIT, 30, 0)).toBe(false);
    expect(t.kind).toBe(DragKind.None);
    // The final pointerup (right released) finds nothing to finish and reports no click.
    expect(t.up(MOUSE, 0)).toBe(false);
  });

  it('does not end on the release of a different button', () => {
    const t = new DragTracker();
    t.down(MOUSE, RIGHT, 0, 0);
    expect(t.up(MOUSE, RIGHT_BIT)).toBe(false);
    expect(t.kind).toBe(DragKind.Orbit);
  });

  it('follows one pointer: a second finger neither moves nor ends the drag', () => {
    const t = new DragTracker();
    t.down(MOUSE, LEFT, 0, 0);
    expect(t.down(FINGER_2, LEFT, 500, 500)).toBe(false);
    expect(t.move(FINGER_2, LEFT_BIT, 520, 500)).toBe(false);
    expect(t.up(FINGER_2, 0)).toBe(false);
    expect(t.kind).toBe(DragKind.Pan);
    t.cancel(FINGER_2);
    expect(t.kind).toBe(DragKind.Pan);
    expect(t.up(MOUSE, 0)).toBe(true);
  });

  it('cancels', () => {
    const t = new DragTracker();
    t.down(MOUSE, LEFT, 0, 0);
    t.cancel();
    expect(t.kind).toBe(DragKind.None);
    expect(t.move(MOUSE, LEFT_BIT, 50, 50)).toBe(false);
    expect(t.up(MOUSE, 0)).toBe(false);
  });

  /**
   * The defect a user hit: a press that wandered four pixels selected nothing, because the PAN threshold was also the
   * click threshold. Measured through synthetic pointer events against the live client, 0-3 px selected 12 of 12 and 4 px
   * selected 0 of 12 — "it works sometimes, on the same object".
   */
  describe('a click survives a shaky hand', () => {
    const release = (travel: number): boolean => {
      const t = new DragTracker();
      t.down(MOUSE, LEFT, 100, 100);
      t.move(MOUSE, LEFT_BIT, 100 + travel, 100);
      return t.up(MOUSE, 0);
    };

    it('still selects at the pan threshold and beyond it', () => {
      for (const travel of [0, 1, 3, 4, 5, 8, 10]) {
        expect(release(travel), `${travel} px`).toBe(true);
      }
    });

    it('is a drag, not a click, once the pointer really travelled', () => {
      for (const travel of [11, 20, 200]) {
        expect(release(travel), `${travel} px`).toBe(false);
      }
    });

    it('judges the FARTHEST the pointer got, not where it ended', () => {
      // Wandering out and coming back is a drag: the camera panned, and selecting as well would be a second action the
      // user did not ask for.
      const t = new DragTracker();
      t.down(MOUSE, LEFT, 100, 100);
      t.move(MOUSE, LEFT_BIT, 300, 100);
      t.move(MOUSE, LEFT_BIT, 100, 100);
      expect(t.maxMoved).toBe(200);
      expect(t.up(MOUSE, 0)).toBe(false);
    });

    it('starts panning well before it stops being a click, so a drag never feels late', () => {
      const t = new DragTracker();
      t.down(MOUSE, LEFT, 0, 0);
      expect(t.move(MOUSE, LEFT_BIT, 3, 0), 'under the pan threshold').toBe(false);
      expect(t.move(MOUSE, LEFT_BIT, 6, 0), 'past the pan threshold').toBe(true);
      expect(t.up(MOUSE, 0), 'and still within the click slop').toBe(true);
    });

    it('forgets the travel between presses', () => {
      const t = new DragTracker();
      t.down(MOUSE, LEFT, 0, 0);
      t.move(MOUSE, LEFT_BIT, 500, 0);
      t.up(MOUSE, 0);
      t.down(MOUSE, LEFT, 0, 0);
      expect(t.maxMoved).toBe(0);
      expect(t.up(MOUSE, 0)).toBe(true);
    });
  });
});
