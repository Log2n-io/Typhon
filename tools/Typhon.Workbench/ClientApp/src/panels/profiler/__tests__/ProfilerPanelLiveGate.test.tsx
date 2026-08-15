// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';

/**
 * The Profiler panel used to ask `kind === 'attach'` to mean "the data is live".
 *
 * That was wrong the moment a paused Open session could start watching its holder's engine (P5): such a session is
 * `kind === 'open'`, it has a live runtime, a connected stream and ticks arriving — and the panel showed the cold
 * state, or, past that gate, a header with no Record control. Worse, the capability is acquired **while the panel is
 * already open**, so the failure appeared only to someone who opened Profiler first and then clicked *Watch live*.
 *
 * This is the same defect the Query Analyzer had (`QueryAnalyzerCapabilityGate.test.tsx`) and for the same reason: a
 * panel gating on the session's KIND rather than on what the session can currently DO. These tests are therefore
 * written from the live-data question, with the watching database session as a first-class case.
 */
const stubs = vi.hoisted(() => ({
  cache: { ticks: [], gaugeData: { threadNames: [] }, threadInfos: [], pendingRangesUs: [] },
}));

vi.mock('@/hooks/profiler/useProfilerMetadata', () => ({ useProfilerMetadata: vi.fn() }));
vi.mock('@/hooks/profiler/useProfilerBuildProgress', () => ({ useProfilerBuildProgress: () => null }));
vi.mock('@/hooks/profiler/useProfilerLiveStream', () => ({ useProfilerLiveStream: vi.fn() }));
vi.mock('@/hooks/profiler/useProfilerSourceLocations', () => ({ useProfilerSourceLocations: vi.fn() }));
vi.mock('@/hooks/profiler/useProfilerStatsWriter', () => ({ useProfilerStatsWriter: vi.fn() }));
vi.mock('@/hooks/profiler/useProfilerTraceStatus', () => ({ useProfilerTraceStatus: () => false }));
vi.mock('@/hooks/profiler/useProfilerCache', () => ({ useProfilerCache: () => stubs.cache }));
vi.mock('@/hooks/usePanelHotkeys', () => ({ usePanelHotkeys: vi.fn() }));
// Canvas-backed children: jsdom has no 2D context and they are not what these tests are about.
vi.mock('../sections/TickOverview', () => ({ default: () => <div data-testid="tick-overview" /> }));
vi.mock('../sections/TimeArea', () => ({ default: () => <div data-testid="time-area" /> }));
vi.mock('../sections/OverloadStrip', () => ({ default: () => <div data-testid="overload-strip" /> }));
vi.mock('@/panels/profiler/components/CaptureControl', () => ({
  default: () => <div data-testid="capture-control" />,
}));
vi.mock('@/api/generated/profiles/profiles', () => ({
  useDeleteApiSessionsSessionIdProfileProfileId: () => ({ mutateAsync: vi.fn() }),
  usePostApiSessionsSessionIdProfile: () => ({ mutateAsync: vi.fn() }),
}));
vi.mock('@/api/generated/sessions/sessions', () => ({ getApiSessionsId: vi.fn() }));

import ProfilerPanel from '../ProfilerPanel';

const COLD = /Open a trace file or attach to a live engine/i;

// The panel takes dockview panel props; nothing under test reads them.
const PANEL_PROPS = {} as unknown as React.ComponentProps<typeof ProfilerPanel>;

function renderPanel() {
  return render(<ProfilerPanel {...PANEL_PROPS} />);
}

beforeEach(() => {
  useProfilerSessionStore.setState({ metadata: null, connectionStatus: 'connected' });
});

afterEach(() => {
  cleanup();
  useSessionStore.getState().clearSession();
  useProfilerSessionStore.getState().reset();
});

describe('ProfilerPanel — live gating follows the runtime, not the session kind', () => {
  it('shows the cold state for an Open session with nothing to profile', () => {
    useSessionStore.setState({ kind: 'open', sessionId: 's1', capabilities: ['database'], isWatchingLive: false });
    renderPanel();
    expect(screen.getByText(COLD)).toBeTruthy();
  });

  /**
   * The reported bug: Profiler was already open, *Watch live* was clicked, and the panel kept showing the cold state
   * with no way to record — the capability arrived after mount.
   */
  it('leaves the cold state when an already-open panel starts watching', () => {
    useSessionStore.setState({ kind: 'open', sessionId: 's1', capabilities: ['database'], isWatchingLive: false });
    const { rerender } = renderPanel();
    expect(screen.getByText(COLD)).toBeTruthy();

    // Watch live → the server reports the profiler capability and the session re-reads.
    useSessionStore.setState({ capabilities: ['database', 'profiler'], isWatchingLive: true });
    rerender(<ProfilerPanel {...PANEL_PROPS} />);

    expect(screen.queryByText(COLD)).toBeNull();
  });

  it('renders the Record control for a watching database session', () => {
    useSessionStore.setState({
      kind: 'open', sessionId: 's1', capabilities: ['database', 'profiler'], isWatchingLive: true,
    });
    useProfilerSessionStore.setState({ metadata: { tickSummaries: [] } as never });
    renderPanel();

    expect(screen.queryByText(COLD)).toBeNull();
    expect(screen.getByTestId('capture-control')).toBeTruthy();
  });

  it('still renders the Record control for an Attach session', () => {
    useSessionStore.setState({ kind: 'attach', sessionId: 's1', capabilities: ['profiler'], isWatchingLive: false });
    useProfilerSessionStore.setState({ metadata: { tickSummaries: [] } as never });
    renderPanel();

    expect(screen.queryByText(COLD)).toBeNull();
    expect(screen.getByTestId('capture-control')).toBeTruthy();
  });
});
