import { describe, expect, it } from 'vitest';
import { PendingSeqs } from '../src/data/pending-seqs';

/**
 * The commands a refusal is matched to (`TyphonSource`: takes, cancels, tips): an accepted one is never answered, so each
 * waits only for its answer window.
 */
describe('pending seqs', () => {
  it('hand back what a refused command was about, once', () => {
    const pending = new PendingSeqs<number>(5000);
    pending.add(7, 1000, 0);
    expect(pending.take(8, 10)).toBeUndefined();
    expect(pending.take(7, 10)).toBe(1000);
    expect(pending.take(7, 20)).toBeUndefined();
  });

  it('keep a null value apart from no entry: a take is a mission ask with no mission named', () => {
    const pending = new PendingSeqs<string | null>(5000);
    pending.add(3, null, 0);
    expect(pending.take(3, 1)).toBeNull();
    expect(pending.take(3, 2)).toBeUndefined();
  });

  it('do not match a refusal that arrives after the window — the seq may belong to a later command by then', () => {
    const pending = new PendingSeqs<number>(5000);
    pending.add(1, 50, 0);
    expect(pending.take(1, 5001)).toBeUndefined();
  });

  it('forget accepted commands as new ones are sent, and everything on clear', () => {
    const pending = new PendingSeqs<number>(5000);
    pending.add(1, 1, 0);
    pending.add(2, 2, 4000);
    pending.add(3, 3, 6000);
    expect(pending.size).toBe(2);
    expect(pending.take(1, 6000)).toBeUndefined();
    pending.clear();
    expect(pending.size).toBe(0);
  });
});
