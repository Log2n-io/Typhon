import { describe, it, expect } from 'vitest';
import { pickPrimaryArchetype } from '../pickArchetype';
import type { ArchetypeInfo } from '@/hooks/schema/types';

function arch(id: string, entityCount: number): ArchetypeInfo {
  return {
    archetypeId: id,
    name: '',
    componentTypes: ['Game.CompA'],
    entityCount,
    componentSize: 12,
    storageMode: 'cluster',
    chunkCount: 1,
    chunkCapacity: 500,
    occupancyPct: 50,
  };
}

describe('pickPrimaryArchetype (AC2.7 type-first auto-pick)', () => {
  it('returns the archetype with the most entities', () => {
    const picked = pickPrimaryArchetype([arch('800', 100), arch('806', 2000), arch('801', 500)]);
    expect(picked?.archetypeId).toBe('806');
  });

  it('ignores archetypes with zero entities', () => {
    const picked = pickPrimaryArchetype([arch('800', 0), arch('801', 7)]);
    expect(picked?.archetypeId).toBe('801');
  });

  it('returns null when no archetype has entities (no dead verb, PC-6)', () => {
    expect(pickPrimaryArchetype([arch('800', 0), arch('801', 0)])).toBeNull();
    expect(pickPrimaryArchetype([])).toBeNull();
  });

  it('keeps the first on a tie (stable)', () => {
    const picked = pickPrimaryArchetype([arch('800', 500), arch('801', 500)]);
    expect(picked?.archetypeId).toBe('800');
  });
});
