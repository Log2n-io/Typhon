import { describe, expect, it } from 'vitest';
import { focusChordTarget } from '@/shell/commands/openSchemaBrowser';

// AC2.3 — the g-leader chord routing table. With no dock api registered the reveal commands no-op, but the
// router still reports which second keys name a known view (true) vs not (false) — that's the contract the
// chord handler relies on to decide whether to swallow the key.

describe('focusChordTarget (g-leader routing)', () => {
  it('routes c / a / s / d / m / q to a view', () => {
    for (const k of ['c', 'a', 's', 'd', 'm', 'q']) {
      expect(focusChordTarget(k), `g ${k}`).toBe(true);
    }
  });

  it('rejects the leader itself and unknown second keys', () => {
    for (const k of ['g', 'x', 'z', '']) {
      expect(focusChordTarget(k), `g ${k}`).toBe(false);
    }
  });
});
