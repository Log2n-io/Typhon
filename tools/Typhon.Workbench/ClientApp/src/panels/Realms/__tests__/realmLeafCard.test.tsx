// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { TickData } from '@/libs/profiler/model/traceModel';
import type { Realm } from '@/hooks/realms/types';
import { RealmLeafCard } from '../RealmLeafCard';

/**
 * The realm Inspector body, and which sources answer it (#1083).
 *
 * **Reported live.** Selecting a realm on a live session answered "Realm 0 is not in this session's catalog. It may
 * belong to another database, or have been retired." The card was written against `useRealmList` alone — the catalog —
 * which a live session cannot read, because the running engine holds the file. So every realm read as absent, with a
 * guess at retirement on top, while the panel beside it was drawing that realm's telemetry.
 */

let telemetry: { ticks: TickData[]; hasTelemetry: boolean; isLive: boolean } = { ticks: [], hasTelemetry: false, isLive: false };
vi.mock('@/hooks/realms/useRealmTelemetry', () => ({ useRealmTelemetry: () => telemetry }));

let catalogList: Realm[] = [];
let hasCatalog = false;
vi.mock('@/hooks/realms/useRealmList', () => ({
  useRealmList: () => ({
    list: catalogList,
    catalog: { maxRealms: 8, realms: catalogList, liveState: false, liveStateReason: 'opened as a file', catalogued: catalogList.length },
    hasRealms: true,
    hasCatalog,
    isLoading: false,
    error: null,
  }),
}));

function tickWithRealm(realmId: number): TickData {
  const shape = {
    realmId,
    archetypeId: 1,
    runState: 1,
    divisor: 4,
    cellSize: 256,
    cellCount: 4096,
    gridDepth: 1,
    clusters: 459,
    clusterReach: 0,
    escapedClusters: 0,
    promotedCells: 0,
    blockedCells: 0,
    budgetConfiguredMs: 1,
    efficiencyTolerance: 0.1,
  };
  return { tickNumber: 824, spatialByRealm: new Map([[realmId * 65536 + 1, shape]]) } as unknown as TickData;
}

function catalogRealm(id: number): Realm {
  return {
    id,
    generation: 0,
    source: 'catalog',
    registered: true,
    lifecycle: 'live',
    grid: { minX: 0, minY: 0, minZ: 0, maxX: 64, maxY: 64, maxZ: 64, cellSize: 64, migrationHysteresisRatio: 0.05, deep: false },
    runState: '',
    divisor: null,
    sessions: null,
    kind: '',
  };
}

beforeEach(() => {
  telemetry = { ticks: [], hasTelemetry: false, isLive: false };
  catalogList = [];
  hasCatalog = false;
});

afterEach(cleanup);

describe('telemetry only — a live session with no readable file', () => {
  /** The regression: a realm the panel is drawing must not be reported as absent from a catalog this session has none of. */
  it('renders the live state, and never claims the realm is missing', () => {
    telemetry = { ticks: [tickWithRealm(0)], hasTelemetry: true, isLive: true };

    render(<RealmLeafCard id={0} />);

    expect(screen.getByText(/Realm 0 — primary/)).toBeTruthy();
    expect(screen.getByText('once every 4 ticks')).toBeTruthy();
    expect(screen.getByText('459')).toBeTruthy();
    expect(screen.queryByText(/not in this session/i)).toBeNull();
    expect(screen.queryByText(/have been retired/i)).toBeNull();
  });

  /**
   * A realm with no row is asleep, not missing. Saying "not found" would contradict the property the panel exists to
   * show — a dormant realm emits no telemetry at all, which is what makes it free.
   */
  it('says a silent realm is dormant rather than absent', () => {
    telemetry = { ticks: [tickWithRealm(0)], hasTelemetry: true, isLive: true };

    render(<RealmLeafCard id={7} />);

    expect(screen.getByText(/reported nothing on tick 824/)).toBeTruthy();
    expect(screen.getByText(/dormant realm emits no telemetry/)).toBeTruthy();
  });
});

describe('catalog only — a file with nothing running', () => {
  it('renders identity and geometry, and gives the reason policy is unknowable', () => {
    catalogList = [catalogRealm(3)];
    hasCatalog = true;

    render(<RealmLeafCard id={3} />);

    expect(screen.getByText('Realm 3')).toBeTruthy();
    expect(screen.getByText('1 (one cell)')).toBeTruthy();
    expect(screen.getByText('opened as a file')).toBeTruthy();
  });

  it('does not speculate about a realm the catalog simply lacks', () => {
    catalogList = [catalogRealm(3)];
    hasCatalog = true;

    render(<RealmLeafCard id={9} />);

    expect(screen.getByText(/not in this database’s realm catalog/)).toBeTruthy();
  });
});

describe('both sources — a file with a capture attached, or one whose holder is watched', () => {
  /**
   * **The case the old kind-based branch could not express at all.** Such a session is `kind === 'open'`, so it took
   * the catalog path and the live half was invisible — on the one session shape that has the complete realm.
   */
  it('renders identity from the file AND run state from the telemetry, in one card', () => {
    catalogList = [catalogRealm(0)];
    hasCatalog = true;
    telemetry = { ticks: [tickWithRealm(0)], hasTelemetry: true, isLive: false };

    render(<RealmLeafCard id={0} />);

    // From the catalog:
    expect(screen.getByText('Realm 0 — primary')).toBeTruthy();
    expect(screen.getByText('1 (one cell)')).toBeTruthy();
    // From the telemetry, on the named tick:
    expect(screen.getByText(/State on tick 824/)).toBeTruthy();
    expect(screen.getByText('once every 4 ticks')).toBeTruthy();
    // And the "policy is unknowable" note is gone, because here it IS knowable.
    expect(screen.queryByText('opened as a file')).toBeNull();
  });
});
