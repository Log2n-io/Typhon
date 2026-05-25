import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';

/**
 * Last-viewed deep-inspector target per file (PC-1 / PC-9). Records the archetype + component the user
 * deliberately switched to (via the header switcher) for a given database, so re-opening the Archetype /
 * Component Inspector cold restores *that* choice instead of falling to the max-entity heuristic. Persisted
 * via the same test-safe localStorage wrapper as the other preference stores.
 */
export interface InspectorTargets {
  archetypeId?: string;
  componentType?: string;
}

interface InspectorTargetState {
  byKey: Record<string, InspectorTargets>;
  save: (key: string, patch: InspectorTargets) => void;
}


export const useInspectorTargetStore = create<InspectorTargetState>()(
  persist(
    (set) => ({
      byKey: {},
      save: (key, patch) =>
        set((s) => ({ byKey: { ...s.byKey, [key]: { ...s.byKey[key], ...patch } } })),
    }),
    { name: 'typhon-inspector-targets', storage: safeStorage },
  ),
);

/** PC-1 scope key: per file when known, else per session, else per session kind. */
export function inspectorTargetKey(filePath: string | null, sessionId: string | null, kind: string): string {
  return filePath ?? sessionId ?? kind;
}
