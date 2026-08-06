// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest';
import { sessionHasCapability, useSessionStore } from '@/stores/useSessionStore';
import { renderHook } from '@testing-library/react';
import { useDatabasePaused } from '@/stores/useSessionStore';
import type { SessionDto } from '@/api/generated/model';

/**
 * The paused-session fields (#621) and the capability selectors every ex-`kind === 'trace'` gate now routes through.
 *
 * Tested at the store rather than only through panels because the four migrated surfaces (Query Analyzer, Inspector
 * detail, Top Spans, call-tree scoping) all ask the same two questions — so the answers are worth pinning once,
 * directly, instead of inferring them from four render trees.
 */
const base: SessionDto = {
  sessionId: 'sess-1',
  kind: 'Open',
  state: 'Ready',
  filePath: 'C:/db/world.typhon',
};

beforeEach(() => {
  useSessionStore.getState().clearSession();
});

describe('useSessionStore — paused fields', () => {
  it('records isPaused and the holder reason from the DTO', () => {
    useSessionStore.getState().setSession({
      ...base,
      isPaused: true,
      lifecycle: 'Paused',
      reason: 'Database released to PID 4242 on BUILDBOX (since 2026-08-03 09:00:00Z).',
      capabilities: ['profiler'],
    });

    const s = useSessionStore.getState();
    expect(s.isPaused).toBe(true);
    expect(s.pausedReason).toContain('PID 4242');
  });

  it('clears the reason when the session is not paused, so a stale banner can never render', () => {
    useSessionStore.getState().setSession({ ...base, isPaused: true, reason: 'held by PID 1' });
    expect(useSessionStore.getState().pausedReason).not.toBeNull();

    useSessionStore.getState().setSession({ ...base, isPaused: false, reason: 'held by PID 1', capabilities: ['database'] });
    const s = useSessionStore.getState();
    expect(s.isPaused).toBe(false);
    expect(s.pausedReason).toBeNull();
  });

  it('treats a missing isPaused as not paused — an older server must not read as paused', () => {
    useSessionStore.getState().setSession({ ...base, capabilities: ['database'] });
    expect(useSessionStore.getState().isPaused).toBe(false);
  });

  it('clearSession resets the paused fields', () => {
    useSessionStore.getState().setSession({ ...base, isPaused: true, reason: 'x' });
    useSessionStore.getState().clearSession();

    const s = useSessionStore.getState();
    expect(s.isPaused).toBe(false);
    expect(s.pausedReason).toBeNull();
  });
});

describe('capability selectors — the replacement for kind checks', () => {
  it('an OPEN database with a capture attached can profile', () => {
    // The exact session the old `kind === 'trace' || 'attach'` test got wrong.
    useSessionStore.getState().setSession({ ...base, capabilities: ['database', 'profiler'] });

    const s = useSessionStore.getState();
    expect(s.kind).toBe('open');
    expect(sessionHasCapability(s, 'profiler')).toBe(true);
    expect(sessionHasCapability(s, 'database')).toBe(true);
  });

  it('a paused session keeps profiler and loses database', () => {
    useSessionStore.getState().setSession({ ...base, isPaused: true, capabilities: ['profiler'] });

    const s = useSessionStore.getState();
    expect(sessionHasCapability(s, 'profiler')).toBe(true);
    expect(sessionHasCapability(s, 'database')).toBe(false);
  });

  it('a plain database cannot profile', () => {
    useSessionStore.getState().setSession({ ...base, capabilities: ['database'] });
    expect(sessionHasCapability(useSessionStore.getState(), 'profiler')).toBe(false);
  });
});

/**
 * The condition database-backed panels use to show a paused state instead of an error (the Data Browser is the first
 * consumer). Both flags, not either alone — see the selector's own remarks.
 */
describe('useDatabasePaused', () => {
  const paused = () => renderHook(() => useDatabasePaused()).result.current;

  it('is true for a released database', () => {
    useSessionStore.getState().setSession({ ...base, isPaused: true, capabilities: ['profiler'] });
    expect(paused()).toBe(true);
  });

  it('is false for a live database', () => {
    useSessionStore.getState().setSession({ ...base, capabilities: ['database'] });
    expect(paused()).toBe(false);
  });

  it('is false for an Attach session, which has no database to come back', () => {
    // `!hasDatabase` alone would be true here, and an attach session would render a paused state forever.
    useSessionStore.getState().setSession({ ...base, kind: 'Attach', capabilities: ['profiler'] });
    expect(paused()).toBe(false);
  });

  it('is false the moment the database capability returns, even before isPaused is cleared', () => {
    // Guards the resume instant: `isPaused` alone would hold panels in a paused state after the data was already back.
    useSessionStore.setState({ isPaused: true, capabilities: ['database', 'profiler'] });
    expect(paused()).toBe(false);
  });
});
