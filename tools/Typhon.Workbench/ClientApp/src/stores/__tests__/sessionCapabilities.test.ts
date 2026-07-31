import { beforeEach, describe, expect, it } from 'vitest';
import { sessionHasCapability, useSessionStore } from '@/stores/useSessionStore';
import { sessionCapabilitiesForKind } from '@/stores/sessionCapabilitiesForKind';
import { isViewVisible } from '@/shell/viewRegistry';

/**
 * #617 — panels follow what a session can DO, not what it is.
 *
 * The case that matters is the one no session-kind check could express: an open database is still `kind === 'open'`
 * after a capture is attached to it, and its profiler panels must nonetheless appear.
 */
describe('session capabilities (#617)', () => {
  beforeEach(() => {
    useSessionStore.setState({ kind: 'none', sessionId: null, capabilities: [], activeProfileId: null });
  });

  it('are read from the server, not derived from the session kind', () => {
    useSessionStore.getState().setSession({
      sessionId: 'sid',
      kind: 'Open',
      state: 'Ready',
      filePath: 'C:/data/world.typhon',
      capabilities: ['database', 'profiler'],
      activeProfileId: 'pid-1',
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const state = useSessionStore.getState();
    expect(state.kind).toBe('open');
    expect(state.capabilities).toEqual(['database', 'profiler']);
    expect(state.activeProfileId).toBe('pid-1');
  });

  it('default to empty when the server sends none', () => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    useSessionStore.getState().setSession({ sessionId: 'sid', kind: 'Open', state: 'Ready', filePath: 'x' } as any);
    expect(useSessionStore.getState().capabilities).toEqual([]);
  });

  it('clearSession drops them', () => {
    useSessionStore.setState({ capabilities: ['profiler'], activeProfileId: 'pid' });
    useSessionStore.getState().clearSession();
    expect(useSessionStore.getState().capabilities).toEqual([]);
    expect(useSessionStore.getState().activeProfileId).toBeNull();
  });

  it('an open database with a profile attached can show profiler views', () => {
    const withProfile = { kind: 'open' as const, capabilities: ['database', 'profiler'] };
    const withoutProfile = { kind: 'open' as const, capabilities: ['database'] };

    expect(isViewVisible('Profiler', withProfile)).toBe(true);
    expect(isViewVisible('Profiler', withoutProfile)).toBe(false);
    // …and the database views stay available either way — attaching a capture adds a capability, it does not swap one.
    expect(isViewVisible('DbMap', withProfile)).toBe(true);
  });

  it('the Profiles list itself is an open-session view — it is how you find a capture', () => {
    expect(isViewVisible('Profiles', { kind: 'open', capabilities: ['database'] })).toBe(true);
    expect(isViewVisible('Profiles', { kind: 'trace', capabilities: ['profiler'] })).toBe(false);
  });

  it('sessionHasCapability reads a plain state slice', () => {
    expect(sessionHasCapability({ capabilities: ['profiler'] }, 'profiler')).toBe(true);
    expect(sessionHasCapability({ capabilities: [] }, 'profiler')).toBe(false);
  });

  it('the test-only kind→capability table matches what the server reports for plain sessions', () => {
    expect(sessionCapabilitiesForKind('open')).toEqual(['database']);
    expect(sessionCapabilitiesForKind('trace')).toEqual(['profiler']);
    expect(sessionCapabilitiesForKind('attach')).toEqual(['profiler']);
    expect(sessionCapabilitiesForKind('none')).toEqual([]);
  });
});
