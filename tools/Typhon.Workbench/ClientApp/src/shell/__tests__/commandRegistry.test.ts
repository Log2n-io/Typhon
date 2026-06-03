// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { buildBaseCommands } from '../commands/baseCommands';
import { useSessionStore, type SessionKind } from '@/stores/useSessionStore';

// IA §5.1 — the command palette shows a view-toggle only in the session kind that can open it, mirroring the View
// menu (both derive from viewRegistry.VIEW_SESSION_SCOPE). A view-toggle the current session can't open is ABSENT
// (no broken affordance), not present-but-dead. Non-view (shell) commands have no bound view → always present.

function idsFor(kind: SessionKind): Set<string> {
  useSessionStore.setState({ kind, sessionId: kind === 'none' ? null : 'sid' });
  return new Set(buildBaseCommands().map((c) => c.id));
}

afterEach(() => useSessionStore.setState({ kind: 'none', sessionId: null }));

// Open-session (.typhon) view-toggle command ids.
const OPEN_VIEW_CMDS = [
  'toggle-view-schema-explorer',
  'data-browser',
  'toggle-view-dbmap',
  'toggle-view-storage-health',
  'open-query-console',
  'toggle-view-query-console',
  'toggle-view-resource-tree',
];

// Profiler-session (trace/attach) view-toggle command ids — incl. the Profiler-view interaction commands.
const PROFILER_VIEW_CMDS = [
  'toggle-view-profiler',
  'toggle-view-top-spans',
  'toggle-view-call-tree',
  'toggle-view-source-preview',
  'show-source-current-span',
  'toggle-view-system-dag',
  'toggle-view-critical-path',
  'toggle-view-data-flow',
  'toggle-view-query-analyzer',
  'toggle-view-engine-health',
  'toggle-view-systems-queries-nav',
  'profiler-toggle-gauges',
  'profiler-toggle-systems',
  'profiler-zoom-full',
  'profiler-pan-left',
  'profiler-pan-right',
];

// Session-independent commands (no bound view, or scope 'any') — present in every kind.
const ALWAYS_CMDS = [
  'open-file',
  'attach',
  'open-trace',
  'close-session',
  'refresh-graph',
  'toggle-view-dev-fixture',
  'toggle-view-detail',
  'toggle-view-logs',
  'toggle-view-options',
  'save-layout-as-default',
  'reset-layout',
  'toggle-theme',
  'reload',
  'profiler-save-replay',
  'toggle-legends',
];

// Removed / deactivated surfaces — must never appear in any kind (their toggle fns no longer exist).
const REMOVED_CMDS = ['toggle-view-component-browser', 'toggle-view-schema-archetypes', 'about'];

describe('command palette — session-kind view gating (IA §5.1)', () => {
  it('open session: open view-toggles present, profiler ones absent', () => {
    const ids = idsFor('open');
    for (const id of OPEN_VIEW_CMDS) expect(ids.has(id), `open: "${id}" should be present`).toBe(true);
    for (const id of PROFILER_VIEW_CMDS) expect(ids.has(id), `open: "${id}" should be absent`).toBe(false);
  });

  it('trace/attach session: profiler view-toggles present, open ones absent', () => {
    const ids = idsFor('attach');
    for (const id of PROFILER_VIEW_CMDS) expect(ids.has(id), `attach: "${id}" should be present`).toBe(true);
    for (const id of OPEN_VIEW_CMDS) expect(ids.has(id), `attach: "${id}" should be absent`).toBe(false);
  });

  it('no session: no session-scoped view-toggles, only the always-on chrome', () => {
    const ids = idsFor('none');
    for (const id of [...OPEN_VIEW_CMDS, ...PROFILER_VIEW_CMDS]) expect(ids.has(id), `none: "${id}" should be absent`).toBe(false);
    for (const id of ALWAYS_CMDS) expect(ids.has(id), `none: "${id}" should be present`).toBe(true);
  });

  it('session-independent commands are present in every kind', () => {
    for (const kind of ['open', 'attach', 'none'] as SessionKind[]) {
      const ids = idsFor(kind);
      for (const id of ALWAYS_CMDS) expect(ids.has(id), `${kind}: "${id}" should be present`).toBe(true);
    }
  });

  it('removed / deactivated surfaces never appear (in any kind)', () => {
    for (const kind of ['open', 'attach', 'none'] as SessionKind[]) {
      const ids = idsFor(kind);
      for (const id of REMOVED_CMDS) expect(ids.has(id), `${kind}: "${id}" must stay removed`).toBe(false);
    }
  });
});
