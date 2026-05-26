// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import AnomalyLog from '../AnomalyLog';
import type { Anomaly } from '../anomalies';

function tickAnomaly(n: number, magnitude = 5): Anomaly {
  return {
    kind: 'tick-duration',
    tickNumber: n,
    startUs: n * 1_000,
    endUs: n * 1_000 + 500,
    durationUs: 500,
    magnitude,
    details: `${magnitude.toFixed(1)}× p95 baseline`,
  };
}

function gcAnomaly(n: number, totalPauseUs = 20_000): Anomaly {
  return {
    kind: 'gc-pause',
    tickNumber: n,
    startUs: n * 1_000,
    endUs: n * 1_000 + 500,
    totalPauseUs,
    magnitude: totalPauseUs / 16_000,
    eventCount: 1,
    details: `${(totalPauseUs / 1000).toFixed(1)} ms across 1 GC event`,
  };
}

beforeEach(() => {
  useProfilerSessionStore.setState({ anomalies: [] });
});
afterEach(() => cleanup());

describe('UC-OBS-03a — anomaly log empty state (PC-2 / suite D / AC4.6)', () => {
  it('renders the empty-state explanation when no anomalies are present', () => {
    render(<AnomalyLog />);
    expect(screen.getByTestId('engine-live-health-anomalies-empty')).toBeTruthy();
    // PC-6 — no broken affordance: Jump-to-last is disabled, not hidden.
    const btn = screen.getByTestId('engine-live-health-jump-last') as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
  });
});

describe('UC-OBS-03 — anomaly jump narrows the timeline scope (AC4.6, GAP-21 jump)', () => {
  it('renders one row per anomaly, sorted descending by tickNumber', () => {
    useProfilerSessionStore.setState({
      anomalies: [tickAnomaly(5), tickAnomaly(50), gcAnomaly(20)],
    });
    render(<AnomalyLog />);
    // Most-recent-first: 50, 20, 5
    const rows = screen.getAllByTestId(/^engine-live-health-anomaly-/);
    expect(rows.map((r) => r.getAttribute('data-testid'))).toEqual([
      'engine-live-health-anomaly-50-tick-duration',
      'engine-live-health-anomaly-20-gc-pause',
      'engine-live-health-anomaly-5-tick-duration',
    ]);
  });

  it('tags each row with kind + tone data attributes (severity bands)', () => {
    useProfilerSessionStore.setState({
      anomalies: [tickAnomaly(1, 1.5), tickAnomaly(2, 3), tickAnomaly(3, 6)],
    });
    render(<AnomalyLog />);
    expect(screen.getByTestId('engine-live-health-anomaly-3-tick-duration').getAttribute('data-tone')).toBe('bad');
    expect(screen.getByTestId('engine-live-health-anomaly-2-tick-duration').getAttribute('data-tone')).toBe('warn');
    expect(screen.getByTestId('engine-live-health-anomaly-1-tick-duration').getAttribute('data-tone')).toBe('normal');
  });

  it('Jump button calls commitViewRange with a window around the anomaly tick', () => {
    const spy = vi.fn();
    useProfilerViewStore.setState({ commitViewRange: spy });
    useProfilerSessionStore.setState({ anomalies: [tickAnomaly(42)] });
    render(<AnomalyLog />);
    fireEvent.click(screen.getByTestId('anomaly-jump-42'));
    expect(spy).toHaveBeenCalledOnce();
    const arg = spy.mock.calls[0][0];
    expect(arg).toHaveProperty('startUs');
    expect(arg).toHaveProperty('endUs');
    expect(arg.endUs).toBeGreaterThan(arg.startUs);
    // The window should *contain* the anomaly's tick range (startUs..endUs of the tick).
    expect(arg.startUs).toBeLessThanOrEqual(42 * 1_000);
    expect(arg.endUs).toBeGreaterThanOrEqual(42 * 1_000 + 500);
  });

  it('Jump to last targets the highest-tickNumber anomaly', () => {
    const spy = vi.fn();
    useProfilerViewStore.setState({ commitViewRange: spy });
    useProfilerSessionStore.setState({
      anomalies: [tickAnomaly(5), tickAnomaly(50), gcAnomaly(20)],
    });
    render(<AnomalyLog />);
    fireEvent.click(screen.getByTestId('engine-live-health-jump-last'));
    expect(spy).toHaveBeenCalledOnce();
    const arg = spy.mock.calls[0][0];
    expect(arg.endUs).toBeGreaterThanOrEqual(50 * 1_000 + 500);
  });
});
