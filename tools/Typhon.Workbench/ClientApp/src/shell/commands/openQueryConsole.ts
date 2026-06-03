import { useSessionStore } from '@/stores/useSessionStore';
import { useQueryConsoleStore } from '@/stores/useQueryConsoleStore';
import { ensureDockPanel } from './openSchemaBrowser';

/**
 * Query Console open/toggle commands (#386 Phase 1 — AC-18). Open-session only — `canOpenQueryConsole` lets the
 * caller (View menu, palette) hide the entry in trace/attach sessions, matching the disabled-with-tooltip pattern
 * the panel uses internally.
 *
 * Cross-link prefill: pass `fromArchetype` to seed the FROM chip. We push the seed into the store BEFORE opening
 * the panel so the panel's first mount reads it without a flicker — no microtask scheduling needed.
 */
export interface OpenQueryConsoleParams {
  /** Pre-fill the FROM chip with this archetype name (cross-link hand-off from Schema Explorer / Data Browser). */
  fromArchetype?: string;
}

export function canOpenQueryConsole(): boolean {
  return useSessionStore.getState().kind === 'open';
}

export function openQueryConsole(params?: OpenQueryConsoleParams): void {
  if (!canOpenQueryConsole()) return;
  if (params?.fromArchetype) {
    // Seed the spec BEFORE the panel mounts so its first render uses the prefilled archetype. The store is the
    // single source of truth — the panel reads from it, not from dockview panel params.
    const store = useQueryConsoleStore.getState();
    store.setSpec({ ...store.spec, archetype: params.fromArchetype });
    // Update DSL to match so the editor mode-switch round-trips cleanly.
    const dsl = store.dslDraft;
    const next = dsl && /^FROM\s+/m.test(dsl)
      ? dsl.replace(/^FROM\s+\S+/m, `FROM ${params.fromArchetype}`)
      : `FROM ${params.fromArchetype}\n${dsl}`;
    store.setDslDraft(next);
  }
  ensureDockPanel('query-console', 'QueryConsole', 'Query Console');
}

/** Toggle variant for View menu / palette — closes when open, opens when closed. */
export function toggleViewQueryConsole(): void {
  if (!canOpenQueryConsole()) return;
  // No public toggle helper exported from openSchemaBrowser; the ensureDockPanel + manual close pattern works
  // because the dock api is registered on the same module-scoped `registeredApi` they both see.
  ensureDockPanel('query-console', 'QueryConsole', 'Query Console');
}
