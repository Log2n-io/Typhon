// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDataBrowserStore } from '@/stores/useDataBrowserStore';

/**
 * #620 §4.4 — what the entity lens is allowed to claim.
 *
 * The arithmetic lives in `entityCohort.ts` and is tested there. These tests cover the statements the panel makes on
 * screen: that a cohort's two archetype ids are both labelled, that values are marked as *current* rather than
 * trace-time (§4.7 / B2), and that a blocked join explains itself instead of rendering a disabled mystery (§5.7).
 */
const ROUTING = 1;
const CATALOG = 10;
const raw = (key: number, routing = ROUTING) => ((BigInt(key) << 16n) | BigInt(routing)).toString();

const hoisted = vi.hoisted(() => ({
  series: [{ tickNumber: 7, entityCount: 3, runCount: 1 }],
  cohort: null as unknown,
  resolution: null as unknown,
  archetypes: [] as unknown[],
  fullCohort: null as { ids: string[]; complete: boolean; total: number } | null,
  /** Ids the panel handed to the survival query — the assertion target for "resolve the cohort, not the preview page". */
  resolvedWith: null as string[] | null,
}));

vi.mock('@/hooks/profiles/useEntityCohort', () => ({
  MAX_RESOLVED_COHORT: 5000,
  useLifecycleSeries: () => ({ data: hoisted.series }),
  useEntityCohort: () => ({ data: hoisted.cohort }),
  useFullCohortIds: () => ({ data: hoisted.fullCohort }),
  useCohortSurvival: (_archetypeId: string | null, ids: string[] | null) => {
    hoisted.resolvedWith = ids;
    return { data: hoisted.resolution };
  },
}));
vi.mock('@/hooks/schema/useArchetypeList', () => ({ useArchetypeList: () => ({ list: hoisted.archetypes }) }));
vi.mock('@/shell/commands/openSchemaBrowser', () => ({ openDataBrowser: vi.fn() }));

import CohortsPanel from '@/panels/EntityLifecycle/CohortsPanel';

const COHORT = {
  kind: 'spawn',
  fromTick: 7,
  toTick: 7,
  totalEntities: 3,
  offset: 0,
  entityIds: [raw(1), raw(2), raw(3)],
  hasMore: false,
  routingId: ROUTING,
  catalogArchetypeId: CATALOG,
  archetypeName: 'Swg.Shard.Character',
};

const RESOLUTION = {
  archetypeId: '10',
  routingId: ROUTING,
  revision: 132,
  aliveIds: [raw(1), raw(2)],
  missingIds: [raw(3)],
  foreignRoutingCount: 0,
};

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const renderPanel = () => render(<CohortsPanel {...({} as any)} />);

function selectTick() {
  fireEvent.click(screen.getByTestId('strip-bar-7'));
}

describe('CohortsPanel (#620 §4.4)', () => {
  beforeEach(() => {
    hoisted.series = [{ tickNumber: 7, entityCount: 3, runCount: 1 }];
    hoisted.cohort = COHORT;
    hoisted.resolution = RESOLUTION;
    hoisted.archetypes = [{ archetypeId: '10', name: 'Swg.Shard.Character', componentTypes: [], entityCount: 3 }];
    hoisted.fullCohort = { ids: [raw(1), raw(2), raw(3)], complete: true, total: 3 };
    hoisted.resolvedWith = null;
    useSessionStore.setState({ kind: 'open', capabilities: ['database', 'profiler'] });
    useDataBrowserStore.getState().reset();
  });
  afterEach(cleanup);

  it('shows both archetype identifiers, each labelled for what it is', () => {
    // They are usually different numbers for the same archetype (§5.3). Showing one unlabelled would let the reader
    // assume it was the other — which is the whole failure mode.
    renderPanel();
    selectTick();

    expect(screen.getByText(/Routing id \(durable\)/)).toBeTruthy();
    expect(screen.getByText(/Catalog id \(this capture\)/)).toBeTruthy();
    // Asserted by test id, not by text: `1` and `10` also occur as tick numbers and counts elsewhere on screen, and a
    // text match would pass for the wrong element — which is precisely the confusion these two labels exist to prevent.
    expect(screen.getByTestId('cohort-routing-id').textContent).toBe(String(ROUTING));
    expect(screen.getByTestId('cohort-catalog-id').textContent).toBe(String(CATALOG));
  });

  it('states that the values are current, not trace-time', () => {
    // §4.7 / B2: MVCC reclaims old versions, so trace-time data is unrecoverable. The panel must not let the adjacency
    // of "spawned at tick 7" and "here are their values" imply the values are from tick 7.
    renderPanel();
    selectTick();

    const survival = screen.getByTestId('cohort-survival');
    expect(survival.textContent).toMatch(/now/i);
    expect(survival.textContent).toMatch(/not as they were at tick/i);
    expect(survival.textContent).toMatch(/TSN 132/);
  });

  it('reports the survival split', () => {
    renderPanel();
    selectTick();

    expect(screen.getByTestId('cohort-survival').textContent).toMatch(/2 of 3/);
  });

  it('a routing-id mismatch is refused WITH a reason, not silently answered', () => {
    // The database's archetype carries a different routing id — the §5.3 landmine. Answering would produce a confident
    // "0 of 3 alive".
    hoisted.cohort = { ...COHORT, routingId: 999 };
    renderPanel();
    selectTick();

    expect(screen.queryByTestId('cohort-survival')).toBeNull();
    expect(screen.getByTestId('cohort-blocked').textContent).toMatch(/different archetype/i);
  });

  it('without a database the survival section is ABSENT, not zeroed', () => {
    useSessionStore.setState({ kind: 'open', capabilities: ['profiler'] });
    renderPanel();
    selectTick();

    expect(screen.queryByTestId('cohort-survival')).toBeNull();
    expect(screen.getByTestId('cohort-blocked').textContent).toMatch(/No database is open/i);
  });

  it('a capture with no lifecycle records says so instead of rendering an empty strip', () => {
    hoisted.series = [];
    renderPanel();

    expect(screen.getByText(/No spawns recorded in this capture/i)).toBeTruthy();
  });

  it('the Data Browser handoff carries the alive ids and a label that names the tick', () => {
    renderPanel();
    selectTick();

    fireEvent.click(screen.getByTestId('open-alive-in-data-browser'));

    const cohort = useDataBrowserStore.getState().cohort;
    expect(cohort?.entityIds).toEqual(RESOLUTION.aliveIds);
    expect(cohort?.label).toMatch(/tick 7/);
    expect(useDataBrowserStore.getState().archetypeId).toBe('10');
  });

  it('resolves survival over the WHOLE cohort, not the preview page', () => {
    // Found in the browser, not by a test: the panel showed "Alive 160 of 200" beside "620 spawned", because the split
    // was computed over one page of ids. The obvious reading — that the cohort was 200 — was wrong, and the design's
    // sentence ("1,240 spawned here — 830 still alive") is only true if every member was asked about.
    hoisted.cohort = { ...COHORT, totalEntities: 620, entityIds: [raw(1), raw(2)] }; // preview page: 2 of 620
    hoisted.fullCohort = { ids: [raw(1), raw(2), raw(3), raw(4)], complete: true, total: 620 };
    renderPanel();
    selectTick();

    expect(hoisted.resolvedWith).toEqual(hoisted.fullCohort.ids);
    expect(hoisted.resolvedWith).not.toEqual((hoisted.cohort as { entityIds: string[] }).entityIds);
  });

  it('says so when the cohort was too large to resolve whole', () => {
    // Past the cap the split covers a prefix. Printing the number without that qualifier would restate the same lie in
    // a rarer case.
    hoisted.cohort = { ...COHORT, totalEntities: 200_000 };
    hoisted.fullCohort = { ids: [raw(1)], complete: false, total: 200_000 };
    renderPanel();
    selectTick();

    expect(screen.getByTestId('cohort-survival').textContent).toMatch(/Sampled the first/i);
  });

  it('offers no reverse lookup — "what was this entity doing?" is absent, not disabled', () => {
    // §4.7 lists database → trace as a bridge that does not work (API gap G8). IA §7 requires an unimplemented verb to
    // be absent rather than a greyed control, so there must be no such affordance at all.
    renderPanel();
    selectTick();

    expect(screen.queryByText(/what was this entity doing/i)).toBeNull();
    expect(screen.queryByText(/view in trace/i)).toBeNull();
    expect(screen.queryByText(/at tick 7 values/i)).toBeNull();
  });
});
