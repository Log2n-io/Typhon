import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { resetSessionScopedState, installSessionResetSync } from '@/stores/resetSessionScopedState';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { useQueryCatalogStore } from '@/panels/QueryAnalyzer/useQueryCatalogStore';
import { installNavHistorySync } from '@/stores/navHistorySync';
import type { SessionDto } from '@/api/generated/model';

// Switch-without-close: changing the session wipes the previous session's selection state (AC1.10).

const makeDto = (sessionId: string): SessionDto =>
  ({ sessionId, kind: 'open', state: 'Ready', filePath: `${sessionId}.typhon`, loadedComponentTypes: 0 } as unknown as SessionDto);

let stops: Array<() => void> = [];

beforeEach(() => {
  useSelectionStore.getState().clear();
  useNavHistoryStore.getState().clear();
  useSessionStore.getState().clearSession();
});
afterEach(() => {
  stops.forEach((s) => s());
  stops = [];
});

describe('resetSessionScopedState', () => {
  it('clears the bus leaf and nav history', () => {
    stops.push(installNavHistorySync());
    useSelectionStore.getState().select('component', 'Position');
    expect(useSelectionStore.getState().leaf).not.toBeNull();
    expect(useNavHistoryStore.getState().entries.length).toBeGreaterThan(0);

    resetSessionScopedState();

    expect(useSelectionStore.getState().leaf).toBeNull();
    expect(useNavHistoryStore.getState().entries).toHaveLength(0);
  });

  it('AC3.16: clears the Query Analyzer catalog filters but PRESERVES the sort preference', () => {
    // Trace-specific numeric filters (system / archetype ids) must not bleed into a new session;
    // the sort is a PC-1 preference and survives the wipe.
    const qa = useQueryCatalogStore.getState();
    qa.setSearch('damage');
    qa.setSystemFilter(7);
    qa.setArchetypeFilter(42);
    qa.toggleExpanded('0:1');
    qa.setSort({ key: 'count', dir: 'asc' });

    resetSessionScopedState();

    const next = useQueryCatalogStore.getState();
    expect(next.search).toBe('');
    expect(next.systemFilter).toBeNull();
    expect(next.archetypeFilter).toBeNull();
    expect(next.expandedRowId).toBeNull();
    expect(next.sort).toEqual({ key: 'count', dir: 'asc' });
  });
});

describe('installSessionResetSync', () => {
  it('wipes selection state when the active sessionId changes (no cross-session bleed)', () => {
    stops.push(installNavHistorySync());
    stops.push(installSessionResetSync());

    useSessionStore.getState().setSession(makeDto('sessionA'));
    useSelectionStore.getState().select('system', 'Movement');
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'system', ref: 'Movement' });

    // Switching to a different session (without closing) resets the leaf + history.
    useSessionStore.getState().setSession(makeDto('sessionB'));
    expect(useSelectionStore.getState().leaf).toBeNull();
    expect(useNavHistoryStore.getState().entries).toHaveLength(0);
  });

  it('does not reset when an unrelated session field changes (same sessionId)', () => {
    stops.push(installSessionResetSync());
    useSessionStore.getState().setSession(makeDto('sessionA'));
    useSelectionStore.getState().select('system', 'Movement');
    // A no-op-sessionId update (same id) must not wipe the selection.
    useSessionStore.setState({ schemaStatus: 'reloaded' });
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'system', ref: 'Movement' });
  });
});
