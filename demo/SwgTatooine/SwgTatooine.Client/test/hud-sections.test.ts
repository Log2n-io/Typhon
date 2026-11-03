import { describe, expect, it } from 'vitest';
import { HUD_OPEN_KEY, loadOpen, parseOpen, saveOpen, serializeOpen } from '../src/data/hud-sections';

/** A storage that keeps what it is given, or throws on every call (site data blocked). */
function memory(throws = false) {
  const values = new Map<string, string>();
  return {
    values,
    getItem(key: string): string | null {
      if (throws) {
        throw new Error('blocked');
      }

      return values.get(key) ?? null;
    },
    setItem(key: string, value: string): void {
      if (throws) {
        throw new Error('blocked');
      }

      values.set(key, value);
    },
  };
}

describe('the HUD sections left open (Loïc, 2026-11-02)', () => {
  it('are none on a first visit', () => {
    expect(loadOpen(memory()).size).toBe(0);
    expect(loadOpen(null).size).toBe(0);
  });

  it('come back as they were saved, in the HUD order', () => {
    const storage = memory();
    saveOpen(new Set(['session', 'client'] as const), storage);
    expect(storage.values.get(HUD_OPEN_KEY)).toBe('client,session');
    expect([...loadOpen(storage)]).toEqual(['client', 'session']);
  });

  it('ignore anything that is not a section', () => {
    expect([...parseOpen(' server ,bogus,,client')]).toEqual(['server', 'client']);
    expect(serializeOpen(new Set())).toBe('');
  });

  it('start closed, and save nothing, where storage refuses', () => {
    const blocked = memory(true);
    expect(loadOpen(blocked).size).toBe(0);
    expect(() => {
      saveOpen(new Set(['server'] as const), blocked);
    }).not.toThrow();
  });
});
