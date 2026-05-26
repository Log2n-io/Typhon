// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import EngineLiveHealthPanel from '../EngineLiveHealthPanel';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';

// Spy on the disconnect API — hoisted so the vi.mock factory below can see it (vi.mock is hoisted above
// imports). Cleared per-test in the beforeEach.
const spies = vi.hoisted(() => ({
  disconnect: vi.fn().mockResolvedValue({}),
}));
vi.mock('@/api/generated/profiler/profiler', () => ({
  postApiSessionsSessionIdProfilerDisconnect: spies.disconnect,
}));

const NO_PROPS = {} as IDockviewPanelProps;

function setAttach() {
  useSessionStore.setState({ kind: 'attach', sessionId: 'sess-A', filePath: 'localhost:9100' });
}
function setTrace() {
  useSessionStore.setState({ kind: 'trace', sessionId: 'sess-T', filePath: '/path/to.typhon-trace' });
}
function setNone() {
  useSessionStore.setState({ kind: 'none', sessionId: null, filePath: null });
}

beforeEach(() => {
  spies.disconnect.mockClear();
  useProfilerSessionStore.setState({ connectionStatus: null, latestTickNumber: 0 });
});
afterEach(() => cleanup());

describe('UC-OBS-01 — engine-live-health panel mounts in attach, cold-state in non-attach (AC4.1, suite D / PC-2)', () => {
  it('shows the explained cold message in non-attach sessions (trace)', () => {
    setTrace();
    render(<EngineLiveHealthPanel {...NO_PROPS} />);
    const cold = screen.getByTestId('engine-live-health-cold');
    expect(cold.textContent).toContain('Engine Health is available');
    expect(cold.textContent).toContain('Attach');
    // The live header / Disconnect must not render in the cold branch (suite E — no half-built affordances).
    expect(screen.queryByTestId('engine-live-health-header')).toBeNull();
    expect(screen.queryByTestId('engine-live-health-disconnect')).toBeNull();
  });

  it('shows the cold message in none-session (no live data)', () => {
    setNone();
    render(<EngineLiveHealthPanel {...NO_PROPS} />);
    expect(screen.getByTestId('engine-live-health-cold')).toBeTruthy();
  });
});

describe('UC-OBS-01b — live header surfaces connection state, endpoint, uptime, tick count (AC4.2)', () => {
  it('renders the header with status, endpoint, uptime, and tick count', () => {
    setAttach();
    useProfilerSessionStore.setState({ connectionStatus: 'connected', latestTickNumber: 12345 });
    render(<EngineLiveHealthPanel {...NO_PROPS} />);

    expect(screen.getByTestId('engine-live-health-header')).toBeTruthy();
    expect(screen.getByTestId('engine-live-health-status').textContent).toBe('Connected');
    expect(screen.getByTestId('engine-live-health-status-dot').getAttribute('data-status')).toBe('connected');
    expect(screen.getByTestId('engine-live-health-endpoint').textContent).toBe('localhost:9100');
    expect(screen.getByTestId('engine-live-health-tick').textContent).toMatch(/12.?345/); // tolerate locale-grouped thousands
    expect(screen.getByTestId('engine-live-health-uptime').textContent).toMatch(/^up \d+s$/);
  });

  it('status dot + label reflect each ConnectionStatus value', () => {
    setAttach();
    const cases: Array<{ status: 'connecting' | 'connected' | 'reconnecting' | 'disconnected'; label: string }> = [
      { status: 'connecting', label: 'Connecting…' },
      { status: 'connected', label: 'Connected' },
      { status: 'reconnecting', label: 'Reconnecting…' },
      { status: 'disconnected', label: 'Disconnected' },
    ];
    for (const { status, label } of cases) {
      useProfilerSessionStore.setState({ connectionStatus: status });
      render(<EngineLiveHealthPanel {...NO_PROPS} />);
      expect(screen.getByTestId('engine-live-health-status-dot').getAttribute('data-status')).toBe(status);
      expect(screen.getByTestId('engine-live-health-status').textContent).toBe(label);
      cleanup();
    }
  });

  it('P2 + P3 surfaces mount: scalar tiles + anomaly empty state (gauges canvas removed post-P5 — lives in Profiler timeline)', () => {
    setAttach();
    useProfilerSessionStore.setState({ connectionStatus: 'connected', latestTickNumber: 0, anomalies: [] });
    render(<EngineLiveHealthPanel {...NO_PROPS} />);
    // P2 — engine-runtime tiles (DOM, no canvas). Five tiles always present (PC-2).
    expect(screen.getByTestId('engine-live-health-scalars')).toBeTruthy();
    expect(screen.getByTestId('tile-tick-rate')).toBeTruthy();
    expect(screen.getByTestId('tile-p95-duration')).toBeTruthy();
    expect(screen.getByTestId('tile-max-duration')).toBeTruthy();
    expect(screen.getByTestId('tile-gc-pauses')).toBeTruthy();
    expect(screen.getByTestId('tile-total-ticks')).toBeTruthy();
    // Gauges section is intentionally absent — the engine-data gauges live in the Profiler timeline
    // (TimeArea), which is the canonical place for them. The panel stays focused on at-a-glance health.
    expect(screen.queryByTestId('engine-live-health-gauges')).toBeNull();
    expect(screen.queryByTestId('engine-live-health-gauge-canvas-wrapper')).toBeNull();
    // P3 — anomaly empty state explains the detector + Jump-to-last is disabled (no broken affordance / PC-6).
    expect(screen.getByTestId('engine-live-health-anomalies-empty')).toBeTruthy();
    expect((screen.getByTestId('engine-live-health-jump-last') as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('UC-OBS-04 — Disconnect freezes the live stream, session stays open (AC4.3, suite E)', () => {
  it('clicking Disconnect calls postApiSessionsSessionIdProfilerDisconnect with the current sessionId', async () => {
    setAttach();
    useProfilerSessionStore.setState({ connectionStatus: 'connected', latestTickNumber: 100 });
    render(<EngineLiveHealthPanel {...NO_PROPS} />);

    const btn = screen.getByTestId('engine-live-health-disconnect') as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
    fireEvent.click(btn);
    await waitFor(() => {
      expect(spies.disconnect).toHaveBeenCalledWith('sess-A');
    });
  });

  it('Disconnect is disabled when not connected (suite E — no broken affordance)', () => {
    setAttach();
    useProfilerSessionStore.setState({ connectionStatus: 'disconnected' });
    render(<EngineLiveHealthPanel {...NO_PROPS} />);
    const btn = screen.getByTestId('engine-live-health-disconnect') as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
    expect(spies.disconnect).not.toHaveBeenCalled();
  });
});
