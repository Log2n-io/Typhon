// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, renderHook } from '@testing-library/react';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * #618 §4.3 — the database half of the query diagnosis.
 *
 * The case worth pinning is the SOURCE of "is this field indexed?". The obvious endpoint,
 * `components/{t}/indexes`, reports each index's offset within the whole record (payload + per-component overhead)
 * while a field's own offset is payload-relative — so its name lookup misses and it emits a synthetic `@12`. Observed
 * live on an indexed `Faction.Value` at payload offset 0 behind a 12-byte overhead. Matching evaluator names against
 * that set reports "no index on Value" for an indexed field. The field list answers by name and cannot drift that way.
 */
const hoisted = vi.hoisted(() => ({
  components: [] as unknown[],
  archetypes: [] as unknown[],
  schema: null as unknown,
}));

vi.mock('@/hooks/schema/useComponentList', () => ({ useComponentList: () => ({ list: hoisted.components }) }));
vi.mock('@/hooks/schema/useArchetypeList', () => ({ useArchetypeList: () => ({ list: hoisted.archetypes }) }));
vi.mock('@/hooks/schema/useComponentSchema', () => ({ useComponentSchema: () => ({ schema: hoisted.schema }) }));

import { useQueryTargetReality } from '../useQueryTargetReality';

describe('useQueryTargetReality (#618 §4.3)', () => {
  beforeEach(() => {
    hoisted.components = [];
    hoisted.archetypes = [];
    hoisted.schema = null;
    useSessionStore.setState({ sessionId: 'sid', kind: 'open', capabilities: ['database', 'profiler'] });
  });
  afterEach(cleanup);

  it('does not resolve when the session has no database', () => {
    useSessionStore.setState({ capabilities: ['profiler'] });
    hoisted.components = [{ typeName: 'Swg.Faction', fullName: 'Game.Faction', entityCount: 5 }];

    const { result } = renderHook(() => useQueryTargetReality('Game.Faction', true));

    expect(result.current.resolved).toBe(false);
    expect(result.current.entityCount).toBeNull();
  });

  it('joins the capture’s target to the database on the full name', () => {
    // The display name (`Swg.Faction`) differs from what the capture records (`Game.Faction`); joining on the display
    // name is the bug this feature exists to fix.
    hoisted.components = [{ typeName: 'Swg.Faction', fullName: 'Game.Faction', entityCount: 340_000 }];
    hoisted.schema = { fields: [{ name: 'Value', isIndexed: true }] };

    const { result } = renderHook(() => useQueryTargetReality('Game.Faction', true));

    expect(result.current.resolved).toBe(true);
    expect(result.current.entityCount).toBe(340_000);
    expect(result.current.indexedFields.has('Value')).toBe(true);
  });

  it('reads indexed-ness from the field list, by name — never from record offsets', () => {
    hoisted.components = [{ typeName: 'Swg.Faction', fullName: 'Game.Faction', entityCount: 1 }];
    hoisted.schema = { fields: [{ name: 'Value', isIndexed: true }, { name: 'Rank', isIndexed: false }] };

    const { result } = renderHook(() => useQueryTargetReality('Game.Faction', true));

    expect(Array.from(result.current.indexedFields)).toEqual(['Value']);
    // Nothing synthetic ever reaches the verdict, so "no index on Value" cannot be printed for an indexed field.
    expect(Array.from(result.current.indexedFields).some((f) => f.startsWith('@'))).toBe(false);
  });

  it('stays unresolved while the field list is still loading', () => {
    // Otherwise the strip renders "no index on <field>" from an empty set and corrects itself a moment later.
    hoisted.components = [{ typeName: 'Swg.Faction', fullName: 'Game.Faction', entityCount: 1 }];
    hoisted.schema = null;

    const { result } = renderHook(() => useQueryTargetReality('Game.Faction', true));

    expect(result.current.resolved).toBe(false);
  });

  it('an unknown target stays unresolved rather than reporting zero', () => {
    hoisted.components = [{ typeName: 'Swg.Faction', fullName: 'Game.Faction', entityCount: 1 }];

    const { result } = renderHook(() => useQueryTargetReality('Game.SomethingElse', true));

    expect(result.current.resolved).toBe(false);
    expect(result.current.entityCount).toBeNull();
  });

  it('an archetype target reports its entity count and claims no indexes', () => {
    hoisted.archetypes = [{ name: 'Unit', entityCount: 340_000 }];

    const { result } = renderHook(() => useQueryTargetReality('Unit', false));

    expect(result.current.resolved).toBe(true);
    expect(result.current.entityCount).toBe(340_000);
    expect(result.current.indexedFields.size).toBe(0);
  });
});
