import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installNavHistorySync } from '@/stores/navHistorySync';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { registerNavFocus } from '@/stores/navFocusBridge';
import {
  applySelectionToStore,
  installSelectionUrlSync,
  parseSelectionFromSearch,
} from '@/stores/selectionUrlSync';

// Conformance suite B — nav history & deep links (GAP-06). The focus-restore half (B.5-B.7) injects a
// fake nav-focus seam so the bus-leaf panel stamping and Back/Forward focus restore can be asserted
// headless — the real dockview wiring is proven by e2e/nav-focus-restore.spec.ts.

let stopNav: () => void;
const nav = () => useNavHistoryStore.getState();
const bus = () => useSelectionStore.getState();

// Injected nav-focus seam: `activePanel` is what the store reads when stamping an entry; `focusedCalls`
// records every panel the store asks to focus on restore.
let activePanel: string | undefined;
const focusedCalls: string[] = [];
const setActive = (id: string | undefined) => { activePanel = id; };

beforeEach(() => {
  useSelectionStore.getState().clear();
  useNavHistoryStore.getState().clear();
  activePanel = undefined;
  focusedCalls.length = 0;
  registerNavFocus(() => activePanel, (id) => { focusedCalls.push(id); });
  stopNav = installNavHistorySync();
});
afterEach(() => {
  stopNav();
  registerNavFocus(null, null);
});

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

  // ── Focus-restore half (conformance B.2: "restore the bus slot AND focus/open the relevant view") ──

  it('B.5 every entry records the active panel id at push time', () => {
    setActive('schema-explorer');
    bus().select('archetype', '2001');
    expect(nav().entries[0]).toMatchObject({ kind: 'bus-leaf', panelId: 'schema-explorer' });
  });

  it('B.6 back/forward restore focus to the panel recorded at each location', () => {
    setActive('nav-A');
    bus().select('system', 'Movement'); // entry0 @ nav-A
    setActive('nav-B');
    bus().select('system', 'Render'); // entry1 @ nav-B
    focusedCalls.length = 0;
    nav().back();
    expect(bus().leaf).toMatchObject({ type: 'system', ref: 'Movement' });
    expect(focusedCalls.at(-1)).toBe('nav-A');
    nav().forward();
    expect(bus().leaf).toMatchObject({ type: 'system', ref: 'Render' });
    expect(focusedCalls.at(-1)).toBe('nav-B');
  });

  it('B.1c consecutive selections in the SAME view update one entry in place (no extra Back stop)', () => {
    setActive('schema-explorer');
    bus().select('archetype', '800');
    bus().select('archetype', '801');
    bus().select('archetype', '802');
    expect(nav().entries).toHaveLength(1);
    expect(nav().entries[0]).toMatchObject({ kind: 'bus-leaf', panelId: 'schema-explorer', leaf: { ref: '802' } });
  });

  it('B.1d selections in DIFFERENT views push separate entries', () => {
    setActive('schema-explorer');
    bus().select('archetype', '802');
    setActive('archetype-inspector');
    bus().select('component', 'CompA');
    expect(nav().entries).toHaveLength(2);
  });

  it('B.7 a view-transition entry restores its leaf snapshot AND focuses its panel', () => {
    setActive('schema-explorer');
    bus().select('archetype', '802'); // E0 (schema, arch:802)
    setActive('archetype-inspector');
    nav().recordViewTransition('archetype-inspector', bus().leaf); // E1 (arch-insp, arch:802)
    bus().select('component', 'CompA'); // same view → updates E1 → (arch-insp, CompA)
    setActive('component-inspector');
    nav().recordViewTransition('component-inspector', bus().leaf); // E2 (comp-insp, CompA)
    focusedCalls.length = 0;
    nav().back(); // → E1: restore its snapshot leaf + focus its panel
    expect(focusedCalls.at(-1)).toBe('archetype-inspector');
    expect(bus().leaf).toMatchObject({ type: 'component', ref: 'CompA' });
  });

  it('B.8 Schema → Archetype → Component is exactly one Back step per view (the user drill)', () => {
    // Schema: select an archetype, then double-click → Archetype Inspector.
    setActive('schema-explorer');
    bus().select('archetype', '802'); // E0 (schema, arch:802)
    setActive('archetype-inspector');
    nav().recordViewTransition('archetype-inspector', bus().leaf); // E1 (arch-insp)
    // Archetype Inspector: select a component, then double-click → Component Inspector.
    bus().select('component', 'CompA'); // updates E1 in place (same view)
    setActive('component-inspector');
    nav().recordViewTransition('component-inspector', bus().leaf); // E2 (comp-insp)
    expect(nav().entries).toHaveLength(3); // exactly one entry per view — no redundant stops

    focusedCalls.length = 0;
    nav().back(); // Component → Archetype
    expect(focusedCalls.at(-1)).toBe('archetype-inspector');
    nav().back(); // Archetype → Schema
    expect(focusedCalls.at(-1)).toBe('schema-explorer');
    expect(nav().canBack).toBe(false); // Schema is the floor
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
