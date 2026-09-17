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
});
