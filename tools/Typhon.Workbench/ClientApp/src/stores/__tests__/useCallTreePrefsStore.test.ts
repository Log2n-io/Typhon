// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useCallTreePrefsStore } from '@/stores/useCallTreePrefsStore';

// AC3.16 / PC-1 — Call Tree lenses (off-CPU split + fold direction + group-by-category) are user-of-the-tool
// preferences that survive reload AND session change. The Call Tree's *scope* (span/system/window) stays
// session-scoped via useCallTreeScopeStore — only the lenses persist.

const STORE_KEY = 'workbench-call-tree-prefs';

beforeEach(() => {
  localStorage.clear();
  // Reset to defaults — zustand keeps state across tests.
  useCallTreePrefsStore.setState({ viewMode: 'wall-clock', direction: 'top-down', groupByCategory: false });
});
afterEach(() => {
  localStorage.clear();
});

describe('useCallTreePrefsStore', () => {
  it('starts at wall-clock / top-down / no grouping', () => {
    const s = useCallTreePrefsStore.getState();
    expect(s.viewMode).toBe('wall-clock');
    expect(s.direction).toBe('top-down');
    expect(s.groupByCategory).toBe(false);
  });

  it('setters update each lens independently', () => {
    useCallTreePrefsStore.getState().setViewMode('on-cpu');
    useCallTreePrefsStore.getState().setDirection('sandwich');
    useCallTreePrefsStore.getState().setGroupByCategory(true);
    const s = useCallTreePrefsStore.getState();
    expect(s.viewMode).toBe('on-cpu');
    expect(s.direction).toBe('sandwich');
    expect(s.groupByCategory).toBe(true);
  });
});

describe('useCallTreePrefsStore — persistence (AC3.16)', () => {
  it('persists all three lenses to localStorage', () => {
    useCallTreePrefsStore.getState().setViewMode('on-cpu');
    useCallTreePrefsStore.getState().setDirection('bottom-up');
    useCallTreePrefsStore.getState().setGroupByCategory(true);

    const raw = localStorage.getItem(STORE_KEY);
    expect(raw).toBeTruthy();
    const persisted = JSON.parse(raw!) as { state: Record<string, unknown>; version: number };
    expect(persisted.version).toBe(1);
    expect(persisted.state.viewMode).toBe('on-cpu');
    expect(persisted.state.direction).toBe('bottom-up');
    expect(persisted.state.groupByCategory).toBe(true);
  });

  it('rehydrates from a seeded blob', async () => {
    localStorage.setItem(STORE_KEY, JSON.stringify({
      state: { viewMode: 'on-cpu', direction: 'sandwich', groupByCategory: true },
      version: 1,
    }));
    await useCallTreePrefsStore.persist.rehydrate();
    const s = useCallTreePrefsStore.getState();
    expect(s.viewMode).toBe('on-cpu');
    expect(s.direction).toBe('sandwich');
    expect(s.groupByCategory).toBe(true);
  });
});
