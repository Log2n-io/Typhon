// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest';
import { createShiftShiftHandler } from '../useShiftShift';

function down(repeat = false): KeyboardEvent {
  return new KeyboardEvent('keydown', { key: 'Shift', repeat, bubbles: true });
}
function up(): KeyboardEvent {
  return new KeyboardEvent('keyup', { key: 'Shift', bubbles: true });
}
function otherDown(): KeyboardEvent {
  return new KeyboardEvent('keydown', { key: 'a', bubbles: true });
}

describe('createShiftShiftHandler', () => {
  function make(cb: () => void, editable: () => boolean = () => false) {
    let t = 0;
    const h = createShiftShiftHandler(cb, editable, () => t);
    return { h, at: (ms: number) => { t = ms; } };
  }

  it('fires on a double-tap (press → release → press) within 300ms', () => {
    const cb = vi.fn();
    const { h, at } = make(cb);
    h.onKeyDown(down()); h.onKeyUp(up()); at(100); h.onKeyDown(down());
    expect(cb).toHaveBeenCalledOnce();
  });

  it('does NOT fire while Shift is HELD — auto-repeat keydowns (event.repeat) are ignored', () => {
    const cb = vi.fn();
    const { h, at } = make(cb);
    h.onKeyDown(down()); // initial press
    at(40); h.onKeyDown(down(true)); // OS auto-repeat while held
    at(80); h.onKeyDown(down(true));
    at(120); h.onKeyDown(down(true));
    expect(cb).not.toHaveBeenCalled();
  });

  it('does NOT fire on two keydowns with no intervening keyup (held, even if the repeat flag is absent)', () => {
    const cb = vi.fn();
    const { h, at } = make(cb);
    h.onKeyDown(down()); at(50); h.onKeyDown(down()); // no keyup between → not a real second tap
    expect(cb).not.toHaveBeenCalled();
  });

  it('does not fire when the gap exceeds 300ms', () => {
    const cb = vi.fn();
    const { h, at } = make(cb);
    h.onKeyDown(down()); h.onKeyUp(up()); at(400); h.onKeyDown(down());
    expect(cb).not.toHaveBeenCalled();
  });

  it('does not fire on a non-Shift key', () => {
    const cb = vi.fn();
    const { h, at } = make(cb);
    h.onKeyDown(otherDown()); at(50); h.onKeyDown(otherDown());
    expect(cb).not.toHaveBeenCalled();
  });

  it('does not fire when an editable target is focused', () => {
    const cb = vi.fn();
    const { h, at } = make(cb, () => true);
    h.onKeyDown(down()); h.onKeyUp(up()); at(50); h.onKeyDown(down());
    expect(cb).not.toHaveBeenCalled();
  });

  it('resets after a successful double-tap (no immediate third trigger)', () => {
    const cb = vi.fn();
    const { h, at } = make(cb);
    h.onKeyDown(down()); h.onKeyUp(up()); at(50); h.onKeyDown(down()); // double #1 → fires
    h.onKeyUp(up()); at(100); h.onKeyDown(down()); h.onKeyUp(up()); at(150); h.onKeyDown(down()); // double #2 → fires
    expect(cb).toHaveBeenCalledTimes(2);
  });
});
