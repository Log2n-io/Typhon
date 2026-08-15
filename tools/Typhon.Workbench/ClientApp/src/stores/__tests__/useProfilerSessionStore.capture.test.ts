import { afterEach, describe, expect, it } from 'vitest';
import { useProfilerSessionStore, type CaptureState } from '@/stores/useProfilerSessionStore';

// On-demand tick capture (#805) — the store's handling of `captureStateChanged`.

function state(overrides: Partial<CaptureState> = {}): CaptureState {
  return {
    state: 'Idle',
    remaining: 0,
    recordedTicks: 0,
    mode: 'CherryPick',
    tickNumbersAbsolute: true,
    tickNumberingSuspect: false,
    bytesReceived: 0,
    bytesRetained: 0,
    ...overrides,
  };
}

describe('useProfilerSessionStore — capture state', () => {
  afterEach(() => {
    useProfilerSessionStore.setState({ captureState: null });
  });

  it('starts null so the control can stay hidden until the server has spoken', () => {
    expect(useProfilerSessionStore.getState().captureState).toBeNull();
  });

  it('applies a captureStateChanged frame', () => {
    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'captureStateChanged', captureState: state({ state: 'Recording', remaining: 12 }) },
    ]);
    expect(useProfilerSessionStore.getState().captureState?.state).toBe('Recording');
    expect(useProfilerSessionStore.getState().captureState?.remaining).toBe(12);
  });

  it('takes the LAST frame in a batch, not the first', () => {
    // The state is a snapshot rather than a delta, and the stream is coalesced into rAF batches — so an intermediate
    // frame in the same batch is already stale by the time it is applied. Applying it last-wins is what stops the
    // badge counting backwards on a busy engine.
    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'captureStateChanged', captureState: state({ state: 'Recording', remaining: 9 }) },
      { kind: 'captureStateChanged', captureState: state({ state: 'Recording', remaining: 8 }) },
      { kind: 'captureStateChanged', captureState: state({ state: 'Idle', remaining: 0, recordedTicks: 10 }) },
    ]);
    const captured = useProfilerSessionStore.getState().captureState;
    expect(captured?.state).toBe('Idle');
    expect(captured?.recordedTicks).toBe(10);
  });

  it('survives a batch that also carries other delta kinds', () => {
    useProfilerSessionStore.getState().applyLiveBatch([
      { kind: 'captureStateChanged', captureState: state({ state: 'Recording', remaining: 3 }) },
      { kind: 'heartbeat', status: 'connected' },
    ]);
    expect(useProfilerSessionStore.getState().captureState?.remaining).toBe(3);
    expect(useProfilerSessionStore.getState().connectionStatus).toBe('connected');
  });
});
