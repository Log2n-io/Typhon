// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDbMapStore } from '@/stores/useDbMapStore';
import { useDataBrowserStore } from '@/stores/useDataBrowserStore';

/**
 * #619 — a segment's owner is not always a component.
 *
 * `ResolveSegmentOwnerName` labels `Cluster`, `EntityMap` and per-archetype `Index` segments with an **archetype**
 * name; everything else with a component's. The Detail pane's segment verbs assumed the latter unconditionally, so
 * selecting an archetype-owned segment produced four live-looking buttons that all led nowhere — plus a 404 on
 * `components/{t}/archetypes`. Found by following #619's new archetype reveal into the Detail pane, where the
 * reveal selects the cluster segment on the bus and every verb fired against an archetype name.
 */
const hoisted = vi.hoisted(() => ({ archetypes: [] as unknown[], forComponent: [] as unknown[] }));

vi.mock('@/hooks/schema/useArchetypeList', () => ({ useArchetypeList: () => ({ list: hoisted.archetypes }) }));
vi.mock('@/hooks/schema/useArchetypesForComponent', () => ({
  useArchetypesForComponent: () => ({ archetypes: hoisted.forComponent }),
}));
vi.mock('@/hooks/dbmap/useDbMapSegment', () => ({ useDbMapSegment: () => ({ data: null }), useDbMapSegmentSummary: () => ({ data: null }) }));

import DetailPanel from '@/panels/DetailPanel';

// The segment card's storage detail fetches through TanStack, so the panel needs a client even though nothing
// under test reads it (same harness shape as Inspector.test.tsx).
function renderDetail() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <DetailPanel />
    </QueryClientProvider>,
  );
}

const ARCHETYPE = { archetypeId: '10', name: 'Swg.Shard.Character', componentTypes: [], entityCount: 1500 };

function selectSegment(typeName: string) {
  useSelectionStore.getState().select('segment', { kind: 'segment', segmentId: 47, typeName });
}

describe('Detail pane segment verbs (#619)', () => {
  beforeEach(() => {
    hoisted.archetypes = [ARCHETYPE];
    hoisted.forComponent = [];
    useSelectionStore.getState().clear();
    useSessionStore.setState({ kind: 'open', capabilities: ['database'] });
    useDbMapStore.getState().clearPendingFocus();
    useDataBrowserStore.getState().reset();
  });
  afterEach(cleanup);

  it('an archetype-owned segment gets ARCHETYPE verbs, never the component ones', () => {
    selectSegment('Swg.Shard.Character');
    renderDetail();

    expect(screen.getByTestId('segment-open-archetype')).toBeTruthy();
    // These would each have led nowhere: a Component Inspector on a type that does not exist, and a resource-tree
    // reveal of `ComponentTable_Swg.Shard.Character`, which is not a node.
    expect(screen.queryByTestId('segment-open-schema')).toBeNull();
    expect(screen.queryByTestId('segment-reveal-resource')).toBeNull();
  });

  it('its File Map reveal frames the archetype’s segments, not one component’s', () => {
    selectSegment('Swg.Shard.Character');
    renderDetail();

    fireEvent.click(screen.getByTestId('segment-reveal-file-map'));

    expect(useDbMapStore.getState().pendingFocus).toEqual({ kind: 'archetype', name: 'Swg.Shard.Character' });
  });

  it('its Data Browser verb scopes to the archetype directly — no component lookup to fail', () => {
    selectSegment('Swg.Shard.Character');
    renderDetail();

    fireEvent.click(screen.getByTestId('segment-open-data-browser'));

    expect(useDataBrowserStore.getState().archetypeId).toBe('10');
  });

  it('a component-table segment keeps the original four verbs', () => {
    hoisted.forComponent = [{ archetypeId: '10', name: '', componentTypes: [], entityCount: 1500 }];
    selectSegment('Swg.Shard.Transform');
    renderDetail();

    expect(screen.getByTestId('segment-open-schema')).toBeTruthy();
    expect(screen.getByTestId('segment-reveal-resource')).toBeTruthy();
    expect(screen.queryByTestId('segment-open-archetype')).toBeNull();
  });

  it('a component-table segment’s reveal still targets the component', () => {
    selectSegment('Swg.Shard.Transform');
    renderDetail();

    fireEvent.click(screen.getByTestId('segment-reveal-file-map'));

    expect(useDbMapStore.getState().pendingFocus).toEqual({ kind: 'component', name: 'Swg.Shard.Transform' });
  });
});
