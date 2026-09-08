// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { SpatialTickTelemetry, TickData } from '@/libs/profiler/model/traceModel';
import { GaugeId } from '@/libs/profiler/model/types';
import SpatialMaintenancePanel from '../SpatialMaintenancePanel';
import { useSessionStore } from '@/stores/useSessionStore';

// The panel's only data source. Stubbed rather than driven through the chunk cache: this fixture is about what the panel
// SHOWS for a given set of counters, and building a real decoded trace to reach three numbers would test the decoder.
const live = vi.hoisted(() => ({ data: null as unknown }));
vi.mock('@/hooks/profiler/useLiveGaugeData', () => ({
  useLiveGaugeData: () => live.data,
}));

const NO_PROPS = {} as IDockviewPanelProps;
const ARCH = 2;

function row(over: Partial<SpatialTickTelemetry> = {}): SpatialTickTelemetry {
  return {
    archetypeId: ARCH,
    migrations: 0, hysteresisAbsorbed: 0, migrationCpuMs: 0,
    driftersDetected: 0, relocationsAdmitted: 0, relocationsThrottled: 0, relocationsSuperseded: 0,
    driftersUnplaced: 0, driftersUnplacedNoCandidate: 0, driftersSpilled: 0, pinsRejected: 0, crossingsQueued: 0,
    repairUnits: 0, repairUnitsRefused: 0, repairQueueDepth: 0,
    budgetUsedMs: 0,
    tightnessSamples: 0, extentRatio: 0, packingBound: 0,
    activeClusters: 0, cellTreePromotions: 0, cellTreeDemotions: 0,
    ...over,
  };
}

function tick(tickNumber: number, rows: SpatialTickTelemetry[]): TickData {
  return {
    tickNumber,
    startUs: tickNumber * 1000,
    endUs: tickNumber * 1000 + 1000,
    spatialByArchetype: new Map(rows.map((r) => [r.archetypeId, r])),
  } as unknown as TickData;
}

function setLive(ticks: TickData[], gauges: Array<[GaugeId, number]> = []) {
  live.data = {
    windowedTicks: ticks,
    gaugeData: {
      gaugeSeries: new Map(gauges.map(([id, value]) => [id, { id, samples: [{ tickNumber: 1, timestampUs: 0, value }] }])),
      gaugeCapacities: new Map(),
      memoryAllocEvents: [], gcEvents: [], gcSuspensions: [], offCpuBySlot: new Map(),
    },
    windowStartUs: 0,
    windowEndUs: 1000,
    hasData: ticks.length > 0,
  };
}

beforeEach(() => {
  useSessionStore.setState({ kind: 'attach', sessionId: 'sess-A', filePath: 'localhost:9100' });
  setLive([]);
});
afterEach(() => cleanup());

describe('Spatial Maintenance panel (#911 O3)', () => {
  it('renders an explained cold state outside an attach session', () => {
    // Not a preference: every counter here is per-tick and reset by the fence, so it exists only while an engine ticks.
    // An open session would render a whole panel of zeros meaning "there is no engine".
    useSessionStore.setState({ kind: 'open', sessionId: 'sess-T', filePath: '/db.typhon' });
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const cold = screen.getByTestId('spatial-maintenance-cold');
    expect(cold.textContent).toContain('Attach');
    expect(screen.queryByTestId('spatial-maintenance-header')).toBeNull();
  });

  it('says so when the window carries no spatial activity, rather than showing zeros', () => {
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-maintenance-cold').textContent).toContain('No spatial activity');
  });

  it('names the tick its per-tick figures came from', () => {
    // Two clocks: these are per-tick values, so a panel polling at UI rate is showing one arbitrary tick out of hundreds.
    // Showing the number without the tick is not a reading.
    setLive([tick(41, [row({ migrations: 3 })]), tick(42, [row({ migrations: 12 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-maintenance-tick').textContent).toContain('42');
  });

  it('groups the counters by mechanism', () => {
    setLive([tick(1, [row({ migrations: 5 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    for (const id of ['crossing', 'relocation', 'repair', 'budget', 'structure', 'grid']) {
      expect(screen.getByTestId(`spatial-group-${id}`)).toBeTruthy();
    }
  });

  it('shows the drifter identity as a verdict, over the one-tick lag', () => {
    setLive([
      tick(1, [row({ driftersDetected: 6 })]),
      tick(2, [row({ relocationsAdmitted: 2, relocationsThrottled: 3, driftersUnplaced: 1 })]),
    ]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-reading-identity-badge').textContent).toBe('ok');
    expect(screen.getByTestId('spatial-reading-identity-detail').textContent).toContain('tick 1');
    expect(screen.getByTestId('spatial-reading-identity-detail').textContent).toContain('tick 2');
  });

  it('reads tightness against the bound, and says "nothing was written" rather than implying points', () => {
    setLive([tick(1, [row({ tightnessSamples: 0 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const detail = screen.getByTestId('spatial-reading-tightness-detail').textContent ?? '';
    expect(detail).toContain('No cluster was written');
    expect(detail).not.toContain('0.00×');
  });

  it('reports the ratio to the bound when clusters were sampled', () => {
    setLive([tick(1, [row({ tightnessSamples: 10, extentRatio: 0.9, packingBound: 0.5 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-reading-tightness-detail').textContent).toContain('1.80×');
  });

  it('labels the migration cost as CPU-ms, never as a duration', () => {
    // W workers busy for 1 ms report W. Rendering it as a duration is the error that made an 8 ms budget buy one unit.
    setLive([tick(1, [row({ migrationCpuMs: 4.5 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-group-budget').textContent).toContain('CPU-ms');
  });

  it('shows the fence span beside the CPU-ms, with the parallelism their ratio implies', () => {
    // The pairing IS the point: summed CPU cannot be compared to a frame budget on its own, and the span is what the
    // host actually waited. 9000 us = 9 ms span against 36 CPU-ms is 4x.
    setLive([tick(1, [row({ migrationCpuMs: 36 })])], [[GaugeId.ClusterFenceSpanUs, 9000]]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const budget = screen.getByTestId('spatial-group-budget');
    expect(budget.textContent).toContain('9.000 ms');
    expect(budget.textContent).toContain('CPU-ms');
    expect(screen.queryByTestId('spatial-fence-span-absent')).toBeNull();
  });

  it('says the fence ran serially rather than printing a zero duration', () => {
    // A host driving WriteTickFence itself never runs the phase-exec systems that time the span, so the series is absent.
    // Rendering that as "0.000 ms" would claim a free fence, which is the one reading the engine cannot support.
    setLive([tick(1, [row({ migrationCpuMs: 36, budgetUsedMs: 2 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const absent = screen.getByTestId('spatial-fence-span-absent');
    expect(absent.textContent).toContain('serial fence');
    // Scoped to the span's own row: the group legitimately prints durations for the other stats, so asserting over the
    // whole group would pass or fail on those instead of on the thing under test.
    expect(absent.textContent).not.toMatch(/\d\s*ms/);
  });

  it('flags a repair unit count pinned at one while units are refused', () => {
    setLive(Array.from({ length: 6 }, (_, i) => tick(i + 1, [row({ repairUnits: 1, repairUnitsRefused: 2 })])));
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-reading-repair-pin-badge').textContent).toBe('check');
  });

  it('says an engine with no grid emits no occupancy series, rather than drawing zeros', () => {
    setLive([tick(1, [row()])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-group-grid').textContent).toContain('no spatial grid');
  });

  it('shows grid occupancy when the gauge series is present', () => {
    setLive([tick(1, [row()])], [
      [GaugeId.SpatialGridBlockCount, 12],
      [GaugeId.SpatialGridOccupiedCells, 3400],
      [GaugeId.SpatialGridIntraBlockFill, 4250],
      [GaugeId.SpatialGridResidentBytes, 262_144],
      [GaugeId.SpatialGridDenseEquivalentBytes, 134_217_728],
    ]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const text = screen.getByTestId('spatial-group-grid').textContent ?? '';
    // The gauge carries hundredths of a percent — 4250 is 42.5 %, and rendering it as 4,250 % is the mistake to catch.
    expect(text).toContain('42.5 %');
    expect(text).not.toContain('no spatial grid');
  });
});
