// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * P5 — *Watch live* on the paused banner.
 *
 * A paused session is the moment the user learns their application took the database. When that application is a
 * Typhon app with a live port, watching it is possible right there — and reaching it as an Open session (rather than
 * through Connect → Attach) is what keeps Profiles, the correlation bridges and every database view available.
 *
 * The offer is keyed on `profilerEndpoint`: the holder advertises it in `db.lock`, so non-null means "there is a live
 * engine to watch". An editor or a backup holding the database reports null, and offering to watch that would only
 * produce a connection refused.
 */
const spies = vi.hoisted(() => ({
  getSession: vi.fn(),
  useState: vi.fn(),
}));

vi.mock('@/api/generated/sessions/sessions', () => ({
  getApiSessionsId: spies.getSession,
  useGetApiSessionsIdState: spies.useState,
}));
vi.mock('@/api/bootstrapToken', () => ({
  applyWorkbenchAuthHeaders: (h: Headers) => h,
}));

import PausedBanner from '../PausedBanner';

const PAUSED_DTO = {
  sessionId: 'sess-A',
  kind: 'Open',
  state: 'Ready',
  filePath: 'C:/db/world.typhon',
  isPaused: true,
  reason: 'Database released to pid 1234.',
};

function setPaused(over: { profilerEndpoint?: string | null; isWatchingLive?: boolean } = {}) {
  useSessionStore.setState({
    kind: 'open',
    sessionId: 'sess-A',
    isPaused: true,
    pausedReason: 'Database released to pid 1234.',
    profilerEndpoint: over.profilerEndpoint ?? null,
    isWatchingLive: over.isWatchingLive ?? false,
  });
}

function stubFetch(ok: boolean): ReturnType<typeof vi.fn> {
  const f = vi.fn().mockResolvedValue({ ok, status: ok ? 200 : 500 } as Response);
  globalThis.fetch = f as unknown as typeof fetch;
  return f;
}

beforeEach(() => {
  // The banner polls /state; keep it agreeing with the store so the poll never triggers a re-read of its own.
  spies.useState.mockReturnValue({ data: { data: { isPaused: true } } });
  spies.getSession.mockResolvedValue({ data: { ...PAUSED_DTO, isWatchingLive: true, profilerEndpoint: 'localhost:9100' } });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  useSessionStore.getState().clearSession();
});

describe('PausedBanner — Watch live', () => {
  it('offers nothing to watch when the holder advertises no endpoint', () => {
    setPaused({ profilerEndpoint: null });
    render(<PausedBanner />);

    expect(screen.getByTestId('paused-banner')).toBeTruthy();
    expect(screen.queryByTestId('paused-banner-watch')).toBeNull();
  });

  it('offers Watch live when the holder advertises an endpoint', () => {
    setPaused({ profilerEndpoint: 'localhost:9100' });
    render(<PausedBanner />);

    const btn = screen.getByTestId('paused-banner-watch');
    expect(btn.textContent).toMatch(/Watch live/i);
  });

  it('renders nothing at all when the session is not paused, endpoint or not', () => {
    setPaused({ profilerEndpoint: 'localhost:9100' });
    useSessionStore.setState({ isPaused: false });
    const { container } = render(<PausedBanner />);

    expect(container.firstChild).toBeNull();
  });

  it('POSTs the watch request in cherry-pick mode and re-reads the session', async () => {
    const f = stubFetch(true);
    setPaused({ profilerEndpoint: 'localhost:9100' });
    render(<PausedBanner />);

    fireEvent.click(screen.getByTestId('paused-banner-watch'));

    await waitFor(() => expect(f).toHaveBeenCalled());
    const [url, init] = f.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/sessions/sess-A/profiler/watch');
    expect(init.method).toBe('POST');
    // Recording every tick of a session opened to observe someone else's run is the volume problem #805 removes.
    expect(JSON.parse(init.body as string)).toEqual({ cherryPick: true });

    // The session must be re-read: watching flips server-side CAPABILITIES, and every profiler panel gates on those.
    await waitFor(() => expect(spies.getSession).toHaveBeenCalledWith('sess-A'));
    await waitFor(() => expect(useSessionStore.getState().isWatchingLive).toBe(true));
  });

  it('offers Stop watching while watching, and DELETEs on click', async () => {
    const f = stubFetch(true);
    setPaused({ profilerEndpoint: 'localhost:9100', isWatchingLive: true });
    render(<PausedBanner />);

    expect(screen.getByTestId('paused-banner-watching').textContent).toMatch(/localhost:9100/);
    const btn = screen.getByTestId('paused-banner-watch');
    expect(btn.textContent).toMatch(/Stop watching/i);

    fireEvent.click(btn);
    await waitFor(() => expect(f).toHaveBeenCalled());
    const [url, init] = f.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/sessions/sess-A/profiler/watch');
    expect(init.method).toBe('DELETE');
    expect(init.body).toBeUndefined();
  });

  /**
   * The button must come back. Clearing the busy flag only on success would strand it on "Working…" for ever — nothing
   * unmounts this banner on failure, because the database is still paused.
   */
  it('surfaces a failure and restores the button', async () => {
    stubFetch(false);
    setPaused({ profilerEndpoint: 'localhost:9100' });
    render(<PausedBanner />);

    fireEvent.click(screen.getByTestId('paused-banner-watch'));

    await waitFor(() => {
      expect(screen.getByTestId('paused-banner-watch-error').textContent).toMatch(/localhost:9100/);
    });
    const btn = screen.getByTestId('paused-banner-watch') as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
    expect(btn.textContent).toMatch(/Watch live/i);
    expect(spies.getSession).not.toHaveBeenCalled();
  });
});
