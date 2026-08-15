// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import DataFlowSidePanel from '../DataFlowSidePanel';
import { useDbMapStore } from '@/stores/useDbMapStore';
import type { Bar } from '../barBuilding';
import type { ArchetypeStorage } from '@/libs/schema/archetypeStorage';
import { UNRESOLVED_ARCHETYPE_STORAGE } from '@/libs/schema/archetypeStorage';

/**
 * #619 §4.2 — the origin of the physical lens.
 *
 * Before this feature the panel printed `Archetype id: 12` and stopped. That number is a **per-process catalog
 * id**: meaningless to a reader, and an active invitation to the join design §5.3 calls the landmine, since the
 * other `ushort` in the same trace is the database's persisted routing id. The tests below pin the name-first
 * rendering, the honest degradation when an id resolves to nothing, and the staleness caveat §4.2 requires
 * whenever present-tense storage figures sit beside recorded ones.
 */

const BAR: Bar = {
  trackId: 't0',
  tickNumber: 800,
  xStart: 0,
  xEnd: 1,
  phaseName: 'Simulation',
  systemName: 'MovementSystem',
  archetypeId: 12,
  entityCount: 12_400,
  chunkCount: 97,
};

const STORAGE: ArchetypeStorage = {
  owned: [
    { id: 0, kind: 'Cluster', typeName: 'Swg.Shard.Unit', pageCount: 1_200, chunkFillPct: 62, reclaimableBytes: 1_800_000, entityCount: 340_182, occupancyPct: 71 },
    { id: 1, kind: 'EntityMap', typeName: 'Swg.Shard.Unit', pageCount: 4, chunkFillPct: 20, reclaimableBytes: 0, entityCount: 0, occupancyPct: 20 },
  ],
  shared: [
    { id: 2, kind: 'Component', typeName: 'Swg.Shard.Transform', pageCount: 900, chunkFillPct: 50, reclaimableBytes: 0, entityCount: 0, occupancyPct: 50 },
  ],
  totalPages: 1_204,
  totalBytes: 1_204 * 4_096,
  chunkFillPct: 62,
  reclaimableBytes: 1_800_000,
  entityCount: 340_182,
  resolved: true,
};

function renderPanel(over: Partial<React.ComponentProps<typeof DataFlowSidePanel>> = {}) {
  return render(
    <DataFlowSidePanel
      focusedBar={BAR}
      tracks={[]}
      systems={[]}
      archetypeName="Swg.Shard.Unit"
      storage={UNRESOLVED_ARCHETYPE_STORAGE}
      {...over}
    />,
  );
}

describe('DataFlowSidePanel — the physical lens origin (#619 §4.2)', () => {
  afterEach(cleanup);

  it('names the archetype the system touched, and keeps the catalog id as secondary detail', () => {
    renderPanel();

    const named = screen.getByTestId('dataflow-archetype-name');
    expect(named.textContent).toContain('Swg.Shard.Unit');
    // The id stays visible — it is what the trace actually recorded, and dropping it would hide the provenance.
    expect(named.textContent).toContain('#12');
    expect(named.getAttribute('title')).toContain('catalog #12');
  });

  it('an id that resolves to no archetype says so instead of guessing a name', () => {
    // §5.7: absent, never a plausible label. A capture recorded before an archetype existed has ids that
    // resolve to nothing, and inventing one would be the exact failure this epic exists to prevent.
    renderPanel({ archetypeName: null });

    expect(screen.getByTestId('dataflow-archetype-unresolved').textContent).toContain('not in this capture');
    expect(screen.queryByTestId('dataflow-archetype-name')).toBeNull();
    expect(screen.queryByTestId('dataflow-reveal-file-map')).toBeNull();
  });

  it('shows the database’s live storage for the touched archetype', () => {
    renderPanel({ storage: STORAGE });

    expect(screen.getByText('Cluster · EntityMap')).toBeTruthy();
    expect(screen.getByText(/1,204/)).toBeTruthy();
    expect(screen.getByText('62.0%')).toBeTruthy();
    expect(screen.getByText('340,182')).toBeTruthy();
  });

  it('carries the staleness caveat §4.2 requires, and no causal claim', () => {
    renderPanel({ storage: STORAGE });

    const caveat = screen.getByTestId('dataflow-storage-caveat').textContent ?? '';
    expect(caveat).toContain('not at the recorded tick');
    expect(caveat).toContain('cannot explain a spike');
    // The one wording that would turn a correlation into a diagnosis.
    expect(caveat.toLowerCase()).not.toContain('caused');
  });

  it('says shared component tables are excluded rather than folding them into the total', () => {
    renderPanel({ storage: STORAGE });

    expect(screen.getByTestId('dataflow-storage-shared').textContent).toContain('not counted above');
  });

  it('renders no storage section at all in a session with no database', () => {
    // A plain trace session: the rollup is unresolved, and §5.7 says the bridge is then absent — not a row of
    // zeroes, which would read as "this archetype occupies nothing".
    renderPanel({ storage: UNRESOLVED_ARCHETYPE_STORAGE });

    expect(screen.queryByTestId('dataflow-storage-caveat')).toBeNull();
    expect(screen.queryByTestId('dataflow-reveal-file-map')).toBeNull();
  });

  it('reveals the archetype — by name, and as an archetype rather than a component', () => {
    useDbMapStore.getState().clearPendingFocus();
    renderPanel({ storage: STORAGE });

    fireEvent.click(screen.getByTestId('dataflow-reveal-file-map'));

    expect(useDbMapStore.getState().pendingFocus).toEqual({ kind: 'archetype', name: 'Swg.Shard.Unit' });
  });

  it('prompts rather than rendering an empty shell when no bar is focused', () => {
    renderPanel({ focusedBar: null });

    expect(screen.getByText(/Hover a bar/)).toBeTruthy();
  });
});
