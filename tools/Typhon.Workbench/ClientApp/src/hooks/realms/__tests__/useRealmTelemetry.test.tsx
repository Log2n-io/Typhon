// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { TickData } from '@/libs/profiler/model/traceModel';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useRealmTelemetry } from '../useRealmTelemetry';

/**
 * Which ticks the Realms panel reads, and whether this session has any (#1083).
 *
 * Two defects this pins, both reported live:
 *
 *  - **"in live mode I only see the latest value"** — the panel read the head of the window and ignored the timeline,
 *    so a past tick was unreachable even though every tick's realm records are on disk.
 *  - **the panel chose its body by `kind === 'attach'`** — so a file with a capture attached, and a file whose holder
 *    is being watched, both `kind === 'open'` and both carrying per-realm records, never showed the live half at all.
 */

let cacheTicks: TickData[] = [];
const acquired: { sessionId: string | null; isLive: boolean }[] = [];
vi.mock('@/hooks/profiler/useProfilerCache', () => ({
  useProfilerCache: (sessionId: string | null, isLive: boolean) => {
    acquired.push({ sessionId, isLive });
    return { ticks: sessionId == null ? [] : cacheTicks };
  },
}));

function tick(n: number, startUs: number, endUs: number): TickData {
  return { tickNumber: n, startUs, endUs } as unknown as TickData;
}

function Probe() {
  const { ticks, hasTelemetry, isLive } = useRealmTelemetry();
  return (
    <span data-testid="probe">{`${hasTelemetry}|${isLive}|${ticks.map((t) => t.tickNumber).join(',')}`}</span>
  );
}

function read(): string {
  return screen.getByTestId('probe').textContent ?? '';
}

beforeEach(() => {
  acquired.length = 0;
  cacheTicks = [tick(1, 0, 100), tick(2, 100, 200), tick(3, 200, 300)];
  useSessionStore.setState({ sessionId: 's1', kind: 'open', isWatchingLive: false, capabilities: [], activeProfileId: null } as never);
  useProfilerViewStore.setState({ viewRange: { startUs: 0, endUs: 0 }, scopeLinked: true, pinnedRange: null } as never);
});

afterEach(cleanup);

describe('whether the session has per-realm telemetry', () => {
  it('a plain file has none, and acquires no profiler cache', () => {
    render(<Probe />);

    expect(read()).toBe('false|false|');
    expect(acquired.at(-1)?.sessionId).toBeNull();
  });

  it('a live attach has it', () => {
    useSessionStore.setState({ kind: 'attach' } as never);

    render(<Probe />);

    expect(read()).toBe('true|true|1,2,3');
  });

  /** `kind === 'open'` with a capture attached — the shape the kind test hid. */
  it('a FILE with a capture attached has it, and is not live', () => {
    useSessionStore.setState({ kind: 'open', capabilities: ['database', 'profiler'], activeProfileId: 'p1' } as never);

    render(<Probe />);

    expect(read()).toBe('true|false|1,2,3');
    // A replay must not claim the live-tail prefetch (#289).
    expect(acquired.at(-1)?.isLive).toBe(false);
  });

  /** `kind === 'open'`, paused, watching its holder's engine — live, and also hidden by the kind test. */
  it('a FILE watching its holder is live', () => {
    useSessionStore.setState({ kind: 'open', isWatchingLive: true, capabilities: ['database', 'profiler'] } as never);

    render(<Probe />);

    expect(read()).toBe('true|true|1,2,3');
  });
});

describe('a LIVE session follows the head while linked', () => {
  beforeEach(() => {
    useSessionStore.setState({ kind: 'attach' } as never);
  });

  /**
   * **Measured, not feared.** A live session's view range starts at the beginning of the capture and does not chase the
   * stream, so honouring it unconditionally pinned the panel to tick 1 for the rest of the session — seen against the
   * SWG demo, where the Realms panel read "tick 1" while the Spatial panel beside it read "tick 284".
   */
  it('ignores a stale range while linked, so a live panel tracks the stream', () => {
    useProfilerViewStore.setState({ viewRange: { startUs: 0, endUs: 50 } } as never);

    render(<Probe />);

    expect(read()).toBe('true|true|1,2,3');
  });

  /** Unlinking is the app's own freeze affordance (GAP-11), and it is the gesture that holds a live moment still. */
  it('honours the pinned range once unlinked', () => {
    useProfilerViewStore.setState({
      viewRange: { startUs: 0, endUs: 300 },
      scopeLinked: false,
      pinnedRange: { startUs: 0, endUs: 150 },
    } as never);

    render(<Probe />);

    expect(read()).toBe('true|true|1,2');
  });
});

describe('a REPLAY scrubs with the range, because it has no head to follow', () => {
  beforeEach(() => {
    // A file with a capture attached: telemetry, not live.
    useSessionStore.setState({
      kind: 'open',
      isWatchingLive: false,
      capabilities: ['database', 'profiler'],
      activeProfileId: 'p1',
    } as never);
  });

  /** An untouched timeline means "everything", so a live session shows the head of the stream from the first frame. */
  it('an unset scope takes every tick', () => {
    useProfilerViewStore.setState({ viewRange: { startUs: 0, endUs: 0 } } as never);

    render(<Probe />);

    expect(read()).toBe('true|false|1,2,3');
  });

  /** The reported defect: selecting a range must move the panel off the head of the stream. */
  it('a committed range narrows to the ticks inside it', () => {
    useProfilerViewStore.setState({ viewRange: { startUs: 0, endUs: 150 } } as never);

    render(<Probe />);

    // Tick 3 (200–300) starts after the range ends, so it drops — which is the whole point: the panel is no longer
    // pinned to the head of the stream.
    expect(read()).toBe('true|false|1,2');
  });

  /** A tick straddling an edge is in scope: a range is an overlap test, not a containment test. */
  it('includes a tick that only partly overlaps the range', () => {
    useProfilerViewStore.setState({ viewRange: { startUs: 190, endUs: 210 } } as never);

    render(<Probe />);

    expect(read()).toBe('true|false|2,3');
  });

  it('a range over one tick selects exactly that tick', () => {
    useProfilerViewStore.setState({ viewRange: { startUs: 110, endUs: 190 } } as never);

    render(<Probe />);

    expect(read()).toBe('true|false|2');
  });

  /** Unlinking freezes the scope, exactly as the scheduling panels do (GAP-11). */
  it('honours the unlink toggle by reading the pinned range', () => {
    useProfilerViewStore.setState({
      viewRange: { startUs: 0, endUs: 300 },
      scopeLinked: false,
      pinnedRange: { startUs: 210, endUs: 290 },
    } as never);

    render(<Probe />);

    expect(read()).toBe('true|false|3');
  });

  /**
   * A scope that falls between two ticks must not read as "no realms" — that looks like a dormant engine, which is a
   * claim this panel makes for real elsewhere and must not make by accident.
   */
  it('falls back to every tick when the scope selects none', () => {
    useProfilerViewStore.setState({ viewRange: { startUs: 5000, endUs: 6000 } } as never);

    render(<Probe />);

    expect(read()).toBe('true|false|1,2,3');
  });
});
