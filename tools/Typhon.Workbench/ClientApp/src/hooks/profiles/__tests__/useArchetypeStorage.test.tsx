// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, renderHook } from '@testing-library/react';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * #619 §4.2 — the database half of the physical lens.
 *
 * Two behaviours are worth pinning here rather than in the pure resolver. First, a plain trace session must not
 * reach for a database it does not have: the hook is mounted by a profiler panel that opens in both session kinds,
 * and an ungated fetch would 409 on every open (the wart #618 had to fix in `useQueryDefinitions`). Second, the
 * page size is read from the file, not assumed — an assumed constant would survive a format change and keep
 * reporting confident wrong byte counts.
 */
const hoisted = vi.hoisted(() => ({
  health: null as unknown,
  healthCalls: [] as (string | null)[],
  archetypes: [] as unknown[],
  components: [] as unknown[],
}));

vi.mock('@/hooks/dbmap/useDbMapHealth', () => ({
  useDbMapHealth: (sessionId: string | null) => {
    hoisted.healthCalls.push(sessionId);
    return { data: sessionId ? hoisted.health : null };
  },
}));
vi.mock('@/hooks/schema/useArchetypeList', () => ({ useArchetypeList: () => ({ list: hoisted.archetypes }) }));
vi.mock('@/hooks/schema/useComponentList', () => ({ useComponentList: () => ({ list: hoisted.components }) }));

import { useArchetypeStorage } from '../useArchetypeStorage';

const UNIT = { name: 'Swg.Shard.Unit', componentTypes: ['Typhon.Samples.Swg.Shard.Transform'] };
const COMPONENTS = [{ typeName: 'Swg.Shard.Transform', fullName: 'Typhon.Samples.Swg.Shard.Transform' }];

function segment(over: Record<string, unknown>) {
  return {
    id: 0, kind: 'Cluster', typeName: 'Swg.Shard.Unit', pageCount: 0, allocatedChunkCount: 0,
    chunkCapacity: 0, chunkFillPct: 0, reclaimableBytes: 0, entityCount: 0, occupancyPct: 0, ...over,
  };
}

describe('useArchetypeStorage (#619 §4.2)', () => {
  beforeEach(() => {
    hoisted.healthCalls = [];
    hoisted.archetypes = [UNIT];
    hoisted.components = COMPONENTS;
    hoisted.health = {
      dataFileBytes: 4_096 * 1_000,
      dataFilePageCount: 1_000,
      segments: [
        segment({ id: 0, kind: 'Cluster', pageCount: 100, allocatedChunkCount: 62, chunkCapacity: 100, entityCount: 340_182 }),
        segment({ id: 1, kind: 'EntityMap', pageCount: 4 }),
        segment({ id: 2, kind: 'Component', typeName: 'Swg.Shard.Transform', pageCount: 900 }),
      ],
    };
    useSessionStore.setState({ sessionId: 'sid', kind: 'open', capabilities: ['database', 'profiler'] });
  });
  afterEach(cleanup);

  it('rolls the archetype’s owned storage up from the health table', () => {
    const { result } = renderHook(() => useArchetypeStorage('Swg.Shard.Unit'));

    expect(result.current.resolved).toBe(true);
    expect(result.current.totalPages).toBe(104);
    expect(result.current.entityCount).toBe(340_182);
    expect(result.current.shared.map((s) => s.typeName)).toEqual(['Swg.Shard.Transform']);
  });

  it('derives the page size from the file rather than assuming one', () => {
    hoisted.health = { ...(hoisted.health as object), dataFileBytes: 8_192 * 1_000, dataFilePageCount: 1_000 };

    const { result } = renderHook(() => useArchetypeStorage('Swg.Shard.Unit'));

    expect(result.current.totalBytes).toBe(104 * 8_192);
  });

  it('a session with no database asks for nothing and stays unresolved', () => {
    // The Data Flow panel opens in a plain trace session too. An ungated `useDbMapHealth` would fire /dbmap/health
    // and log a 409 on every open — cosmetic, but it is how a real defect hides.
    useSessionStore.setState({ kind: 'trace', capabilities: ['profiler'] });

    const { result } = renderHook(() => useArchetypeStorage('Swg.Shard.Unit'));

    expect(result.current.resolved).toBe(false);
    expect(hoisted.healthCalls.every((s) => s === null)).toBe(true);
  });

  it('an archetype the database does not know stays unresolved rather than reporting zero', () => {
    const { result } = renderHook(() => useArchetypeStorage('Swg.Shard.Vanished'));

    expect(result.current.resolved).toBe(false);
    expect(result.current.totalPages).toBe(0);
    expect(result.current.entityCount).toBeNull();
  });

  it('a null target resolves to nothing', () => {
    const { result } = renderHook(() => useArchetypeStorage(null));

    expect(result.current.resolved).toBe(false);
  });

  it('stays unresolved while the health table is still loading', () => {
    hoisted.health = null;

    const { result } = renderHook(() => useArchetypeStorage('Swg.Shard.Unit'));

    expect(result.current.resolved).toBe(false);
  });
});
