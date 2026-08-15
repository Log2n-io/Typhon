import { afterEach, describe, expect, it } from 'vitest';
import type { SessionDto } from '@/api/generated/model';
import { isLiveStreamSession, useSessionStore } from '@/stores/useSessionStore';

/**
 * P5 — the session fields that make a paused database watchable, and the predicate that decides whether anything
 * subscribes to the live stream.
 */
const BASE = {
  sessionId: 'sess-A',
  kind: 'Open',
  state: 'Ready',
  filePath: 'C:/db/world.typhon',
} as SessionDto;

afterEach(() => useSessionStore.getState().clearSession());

describe('setSession — watch fields', () => {
  it('carries the advertised endpoint and the watching flag', () => {
    useSessionStore.getState().setSession({
      ...BASE,
      isPaused: true,
      profilerEndpoint: 'localhost:9100',
      isWatchingLive: true,
    });

    const s = useSessionStore.getState();
    expect(s.profilerEndpoint).toBe('localhost:9100');
    expect(s.isWatchingLive).toBe(true);
  });

  /**
   * An older server, or any session with no holder, sends neither field. They must land as "not watchable" rather than
   * undefined — the banner tests `profilerEndpoint` for null, and `undefined` would render a button pointing nowhere.
   */
  it('defaults to not-watchable when the server sends neither field', () => {
    useSessionStore.getState().setSession(BASE);

    const s = useSessionStore.getState();
    expect(s.profilerEndpoint).toBeNull();
    expect(s.isWatchingLive).toBe(false);
  });

  it('clears both on clearSession', () => {
    useSessionStore.getState().setSession({ ...BASE, profilerEndpoint: 'localhost:9100', isWatchingLive: true });
    useSessionStore.getState().clearSession();

    const s = useSessionStore.getState();
    expect(s.profilerEndpoint).toBeNull();
    expect(s.isWatchingLive).toBe(false);
  });
});

describe('isLiveStreamSession', () => {
  it('is true for an attach session', () => {
    expect(isLiveStreamSession({ kind: 'attach', isWatchingLive: false })).toBe(true);
  });

  // The P5 case: an Open session watching its holder's engine has a live stream, and gating on kind is exactly what
  // left it without one — no capture state, therefore no Record control, with the whole server side already working.
  it('is true for an Open session that is watching', () => {
    expect(isLiveStreamSession({ kind: 'open', isWatchingLive: true })).toBe(true);
  });

  it('is false for an Open session that is not watching — that endpoint 409s with no runtime to serve', () => {
    expect(isLiveStreamSession({ kind: 'open', isWatchingLive: false })).toBe(false);
  });

  it('is false when there is no session at all', () => {
    expect(isLiveStreamSession({ kind: 'none', isWatchingLive: false })).toBe(false);
  });
});
