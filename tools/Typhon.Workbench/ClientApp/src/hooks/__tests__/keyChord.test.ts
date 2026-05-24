import { describe, expect, it, vi } from 'vitest';
import { createChordHandler } from '@/hooks/keyChord';

// AC2.3 (PC-8) — the g-leader chord state machine. Pure factory → unit-tested with injected timers, no DOM.

function key(k: string, mods: Partial<Pick<KeyboardEvent, 'ctrlKey' | 'altKey' | 'metaKey' | 'shiftKey'>> = {}): KeyboardEvent {
  return {
    key: k,
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    shiftKey: false,
    preventDefault: vi.fn(),
    ...mods,
  } as unknown as KeyboardEvent;
}

describe('createChordHandler (g-leader)', () => {
  it('arms on the leader, then resolves the second key', () => {
    const resolve = vi.fn().mockReturnValue(true);
    const h = createChordHandler({ leader: 'g', resolve, setTimer: () => 1, clearTimer: () => {} });
    const g = key('g');
    expect(h.handle(g)).toBe(true);
    expect(g.preventDefault).toHaveBeenCalled();
    expect(resolve).not.toHaveBeenCalled();
    const c = key('c');
    expect(h.handle(c)).toBe(true);
    expect(resolve).toHaveBeenCalledWith('c');
    expect(c.preventDefault).toHaveBeenCalled();
  });

  it('ignores non-leader keys when not armed', () => {
    const h = createChordHandler({ leader: 'g', resolve: () => true, setTimer: () => 1, clearTimer: () => {} });
    expect(h.handle(key('x'))).toBe(false);
  });

  it('does not arm while typing in a text field', () => {
    const h = createChordHandler({ leader: 'g', resolve: () => true, isTyping: () => true, setTimer: () => 1, clearTimer: () => {} });
    const g = key('g');
    expect(h.handle(g)).toBe(false);
    expect(g.preventDefault).not.toHaveBeenCalled();
  });

  it('consumes an unknown second key but does not preventDefault it', () => {
    const resolve = vi.fn().mockReturnValue(false);
    const h = createChordHandler({ leader: 'g', resolve, setTimer: () => 1, clearTimer: () => {} });
    h.handle(key('g'));
    const z = key('z');
    expect(h.handle(z)).toBe(true);
    expect(resolve).toHaveBeenCalledWith('z');
    expect(z.preventDefault).not.toHaveBeenCalled();
  });

  it('lets a modified key after the leader through (Ctrl+K still works mid-chord)', () => {
    const resolve = vi.fn();
    const h = createChordHandler({ leader: 'g', resolve, setTimer: () => 1, clearTimer: () => {} });
    h.handle(key('g'));
    expect(h.handle(key('k', { ctrlKey: true }))).toBe(false);
    expect(resolve).not.toHaveBeenCalled();
  });

  it('requires the leader to be unmodified', () => {
    const h = createChordHandler({ leader: 'g', resolve: () => true, setTimer: () => 1, clearTimer: () => {} });
    expect(h.handle(key('g', { ctrlKey: true }))).toBe(false);
    expect(h.handle(key('g', { shiftKey: true }))).toBe(false);
  });

  it('disarms after the timeout window', () => {
    const resolve = vi.fn().mockReturnValue(true);
    let fire: () => void = () => {};
    const h = createChordHandler({ leader: 'g', resolve, setTimer: (fn) => { fire = fn; return 1; }, clearTimer: () => {} });
    h.handle(key('g'));
    fire(); // window elapses
    expect(h.handle(key('c'))).toBe(false); // disarmed → not a resolution
    expect(resolve).not.toHaveBeenCalled();
  });

  describe('onArmedChange (status-bar hint)', () => {
    it('reports true when the leader arms, false when the second key resolves it', () => {
      const onArmedChange = vi.fn();
      const h = createChordHandler({ leader: 'g', resolve: () => true, onArmedChange, setTimer: () => 1, clearTimer: () => {} });
      h.handle(key('g'));
      expect(onArmedChange).toHaveBeenLastCalledWith(true);
      h.handle(key('c'));
      expect(onArmedChange).toHaveBeenLastCalledWith(false);
    });

    it('reports false when the window times out', () => {
      const onArmedChange = vi.fn();
      let fire: () => void = () => {};
      const h = createChordHandler({ leader: 'g', resolve: () => true, onArmedChange, setTimer: (fn) => { fire = fn; return 1; }, clearTimer: () => {} });
      h.handle(key('g'));
      onArmedChange.mockClear();
      fire();
      expect(onArmedChange).toHaveBeenCalledWith(false);
    });

    it('reports false on cancel, and never fires false twice (no spurious hide)', () => {
      const onArmedChange = vi.fn();
      const h = createChordHandler({ leader: 'g', resolve: () => true, onArmedChange, setTimer: () => 1, clearTimer: () => {} });
      h.handle(key('g'));
      h.handle(key('c')); // resolves → false (once)
      onArmedChange.mockClear();
      h.cancel(); // already disarmed → no further fire
      expect(onArmedChange).not.toHaveBeenCalled();
    });
  });
});
