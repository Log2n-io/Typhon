import { describe, expect, it } from 'vitest';
import { friendlyComponentCellLabel } from '../cellLabel';
import type { DbContentCell } from '../types';

// The cluster-entity decode labels component rows with the registered (often namespace-qualified) component name.
// friendlyComponentCellLabel swaps that for the smart short label while keeping the field part — so the Detail
// pane reads `StatusEffects` / `StatusEffects.Stacks`, not the full `Typhon.ARPG.Combat.StatusEffects[.Stacks]`.

// Stub session labeller: shortens a known dotted name, passes anything else through (mirrors useComponentNames'
// leaf-segment fallback for the cases we assert on).
const shorten = (name: string): string => (name === 'Typhon.ARPG.Combat.StatusEffects' ? 'StatusEffects' : name);

const cell = (over: Partial<DbContentCell> & { kind: string; label: string }): DbContentCell => ({
  value: '',
  offset: 0,
  size: 0,
  colorKey: 0,
  ...over,
});

describe('friendlyComponentCellLabel', () => {
  it('shortens a componentHeader cell (label is the whole component name)', () => {
    expect(friendlyComponentCellLabel(cell({ kind: 'componentHeader', label: 'Typhon.ARPG.Combat.StatusEffects' }), shorten)).toBe('StatusEffects');
  });

  it('shortens the component part of a field cell, keeping the field after the last dot', () => {
    expect(friendlyComponentCellLabel(cell({ kind: 'field', label: 'Typhon.ARPG.Combat.StatusEffects.Stacks' }), shorten)).toBe('StatusEffects.Stacks');
  });

  it('splits on the LAST dot — namespace dots in the component name do not confuse the field split', () => {
    // Field names are plain identifiers, so everything before the final dot is the component name.
    const out = friendlyComponentCellLabel(cell({ kind: 'field', label: 'A.B.C.value' }), (n) => (n === 'A.B.C' ? 'C' : n));
    expect(out).toBe('C.value');
  });

  it('leaves a field cell with no dot unchanged (defensive — should not happen for component fields)', () => {
    expect(friendlyComponentCellLabel(cell({ kind: 'field', label: 'bareField' }), shorten)).toBe('bareField');
  });

  it('leaves non-component cell kinds untouched', () => {
    expect(friendlyComponentCellLabel(cell({ kind: 'entitySlot', label: 'slot 3' }), shorten)).toBe('slot 3');
    expect(friendlyComponentCellLabel(cell({ kind: 'vsbsMeta', label: 'Elements' }), shorten)).toBe('Elements');
  });
});
