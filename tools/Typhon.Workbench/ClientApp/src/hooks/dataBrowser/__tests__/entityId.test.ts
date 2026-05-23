import { describe, it, expect } from 'vitest';
import { archetypeIdFromRawEntityId, isRawEntityId } from '../entityId';

describe('entityId helpers', () => {
  it('isRawEntityId accepts digit runs and rejects junk', () => {
    expect(isRawEntityId('84586596')).toBe(true);
    expect(isRawEntityId('  42  ')).toBe(true);
    expect(isRawEntityId('')).toBe(false);
    expect(isRawEntityId('0xFF')).toBe(false);
    expect(isRawEntityId('12.3')).toBe(false);
    expect(isRawEntityId('abc')).toBe(false);
  });

  it('archetypeIdFromRawEntityId extracts the low 12 bits', () => {
    // raw = (key << 12) | archetypeId. key=1, arch=100 -> (1<<12)|100 = 4196.
    expect(archetypeIdFromRawEntityId('4196')).toBe('100');
    // From the live fixture: entity 770848 -> 770848 & 0xFFF = 800 (CompA archetype).
    expect(archetypeIdFromRawEntityId('770848')).toBe('800');
    // arch 0.
    expect(archetypeIdFromRawEntityId('4096')).toBe('0');
  });

  it('archetypeIdFromRawEntityId handles values beyond 2^53 (BigInt path)', () => {
    // 100 << 48 | 7 — far past Number.MAX_SAFE_INTEGER; low bits still resolve to 7.
    const raw = ((100n << 48n) | 7n).toString();
    expect(archetypeIdFromRawEntityId(raw)).toBe('7');
  });

  it('archetypeIdFromRawEntityId returns null for non-numeric input', () => {
    expect(archetypeIdFromRawEntityId('nope')).toBeNull();
    expect(archetypeIdFromRawEntityId('')).toBeNull();
  });
});
