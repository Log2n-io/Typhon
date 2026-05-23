import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installNavHistorySync } from '@/stores/navHistorySync';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import {
  applySelectionToStore,
  installSelectionUrlSync,
  parseSelectionFromSearch,
} from '@/stores/selectionUrlSync';

// Conformance suite B — nav history & deep links (GAP-06).

let stopNav: () => void;
const nav = () => useNavHistoryStore.getState();
const bus = () => useSelectionStore.getState();

beforeEach(() => {
  useSelectionStore.getState().clear();
  useNavHistoryStore.getState().clear();
  stopNav = installNavHistorySync();
});
afterEach(() => stopNav());

describe('suite B — nav history', () => {
  it('B.1 every recordable bus change pushes a history entry', () => {
    bus().select('system', 'Movement');
    bus().select('component', 'Position');
    expect(nav().entries).toHaveLength(2);
    expect(nav().entries[0]).toMatchObject({ kind: 'bus-leaf', leaf: { type: 'system', ref: 'Movement' } });
    expect(nav().entries[1]).toMatchObject({ kind: 'bus-leaf', leaf: { type: 'component', ref: 'Position' } });
  });

  it('B.1 viewport-carrying / resource leaves are not double-recorded here', () => {
    bus().select('span', { kind: 'span' }); // profiler — has its own viewport entry
    bus().select('page', { kind: 'page', pageIndex: 3 }); // file-map — its own entry
    bus().select('resource', { resourceId: 'r1' }); // resource — uses resource-selected
    expect(nav().entries).toHaveLength(0);
  });

  it('B.2 back/forward restore the bus leaf', () => {
    bus().select('system', 'A');
    bus().select('system', 'B');
    nav().back();
    expect(bus().leaf).toMatchObject({ type: 'system', ref: 'A' });
    nav().forward();
    expect(bus().leaf).toMatchObject({ type: 'system', ref: 'B' });
  });

  it('B.3 capacity is 100, oldest dropped', () => {
    for (let i = 0; i < 105; i++) bus().select('system', `S${i}`);
    expect(nav().entries).toHaveLength(100);
    expect(nav().entries[0]).toMatchObject({ leaf: { ref: 'S5' } });
    expect(nav().entries[99]).toMatchObject({ leaf: { ref: 'S104' } });
  });
});

describe('suite B — deep links (leaf param)', () => {
  it('B.4 parses ?leaf=type:ref', () => {
    expect(parseSelectionFromSearch('?leaf=system:Movement').leaf).toEqual({ type: 'system', ref: 'Movement' });
    expect(parseSelectionFromSearch('?leaf=component:Position').leaf).toEqual({ type: 'component', ref: 'Position' });
    expect(parseSelectionFromSearch('?leaf=bogus:x').leaf).toBeNull(); // unsupported type rejected
  });

  it('B.4 applies a parsed leaf to the bus', () => {
    applySelectionToStore({
      viewRange: null, system: null, component: null, queue: null, resource: null, entity: null,
      leaf: { type: 'component', ref: 'Position' },
    });
    expect(bus().leaf).toMatchObject({ type: 'component', ref: 'Position' });
  });

  it('B.4 round-trip: leaf change → URL → parse is stable', () => {
    const replaceState = vi.fn<(s: string) => void>();
    const unsub = installSelectionUrlSync({ replaceState, readSearch: () => '' });
    bus().select('archetype', '2002');
    const last = replaceState.mock.calls.at(-1)?.[0] ?? '';
    expect(last).toContain('leaf=archetype%3A2002');
    expect(parseSelectionFromSearch(last).leaf).toEqual({ type: 'archetype', ref: '2002' });
    unsub();
  });
});
