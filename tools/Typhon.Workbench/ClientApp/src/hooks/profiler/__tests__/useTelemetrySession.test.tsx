// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useTelemetrySession } from '../useTelemetrySession';

/**
 * Which session a profiler panel reads telemetry from.
 *
 * **The mistake this replaces was made at three of the four call sites of `useLiveGaugeData`:**
 * `sessionKind === 'attach' ? sessionId : null`. That asks how you connected; the question is what you have. A
 * captured profile and a database whose holder is being watched both carry per-tick records and are both
 * `kind === 'open'`, so the Spatial, Subscriptions and Realms panels showed nothing at all over any capture ever
 * taken — and their cold states said the data "exists only against a live engine", which is not true of a recording.
 */

function Probe() {
  const { sessionId, hasTelemetry, isLive } = useTelemetrySession();
  return <span data-testid="probe">{`${sessionId ?? '-'}|${hasTelemetry}|${isLive}`}</span>;
}

function read(): string {
  return screen.getByTestId('probe').textContent ?? '';
}

function session(over: Record<string, unknown>) {
  useSessionStore.setState({
    sessionId: 's1',
    kind: 'open',
    isWatchingLive: false,
    capabilities: [],
    activeProfileId: null,
    ...over,
  } as never);
}

beforeEach(() => session({}));
afterEach(cleanup);

describe('which sessions carry telemetry', () => {
  it('a plain file carries none, and offers no session to read from', () => {
    render(<Probe />);

    expect(read()).toBe('-|false|false');
  });

  it('a live attach carries it, live', () => {
    session({ kind: 'attach', capabilities: ['profiler'] });

    render(<Probe />);

    expect(read()).toBe('s1|true|true');
  });

  /** `kind === 'open'` — the shape the kind test hid, and the one that holds every capture you have ever taken. */
  it('a FILE with a capture attached carries it, recorded', () => {
    session({ capabilities: ['database', 'profiler'], activeProfileId: 'p1' });

    render(<Probe />);

    expect(read()).toBe('s1|true|false');
  });

  /** `kind === 'open'`, paused, watching its holder's engine (#621 P5) — live, and also hidden by the kind test. */
  it('a FILE watching its holder carries it, live', () => {
    session({ capabilities: ['database', 'profiler'], isWatchingLive: true });

    render(<Probe />);

    expect(read()).toBe('s1|true|true');
  });

  /**
   * The `sessionId` is withheld rather than the caller being trusted to check `hasTelemetry`, so the common call —
   * `useLiveGaugeData(sessionId)` — is correct by construction and cannot acquire a profiler cache for a session with
   * nothing in it.
   */
  it('withholds the session id when there is nothing to read', () => {
    session({ capabilities: ['database'] });

    render(<Probe />);

    expect(read()).toBe('-|false|false');
  });
});
