import type { useSelectionStore } from '@/stores/useSelectionStore';
import type { InspectorTargets } from '@/stores/useInspectorTargetStore';

/**
 * Self-addressing deep-inspector target resolution (PC-9). Shared by the Archetype + Component inspectors:
 * given the loaded targets and the file's PC-1-recorded last target, decide what the inspector shows when
 * opened cold (no bus selection), and say whether that pick was a heuristic "auto" (→ surface the chip) or a
 * deliberate restore (→ no chip). Pure so the precedence is unit-tested without rendering.
 */

export interface TargetCandidate {
  id: string;
  entityCount: number;
}

export interface ResolvedTarget {
  id: string;
  /** true → heuristic / first pick (show the "(auto)" chip); false → restored the recorded choice. */
  auto: boolean;
}

/**
 * Resolve the cold-open target. Precedence (PC-9):
 *   1. the PC-1 recorded id, if still present  → not auto (a deliberate prior choice)
 *   2. the candidate with the most entities     → auto
 *   3. (all-zero / tie) the first candidate     → auto  [falls out of the strict-`>` max scan]
 * Returns null only when there are no candidates (→ the PC-2 Empty state).
 */
export function resolveInspectorTarget(candidates: TargetCandidate[], recordedId: string | null): ResolvedTarget | null {
  if (candidates.length === 0) return null;
  if (recordedId != null) {
    const found = candidates.find((c) => c.id === recordedId);
    if (found) return { id: found.id, auto: false };
  }
  let best = candidates[0];
  for (const c of candidates) {
    if (c.entityCount > best.entityCount) best = c;
  }
  return { id: best.id, auto: true };
}

type InspectorObjectType = 'archetype' | 'component';
type SelectFn = ReturnType<typeof useSelectionStore.getState>['select'];

/**
 * Commit a *deliberate* target switch (header switcher): publish the bus slot — the same path a handoff uses
 * — and record it as this file's last-viewed target (PC-1). Pure over its injected `select` / `savePref` so
 * the side-effects are unit-tested without the bus singleton.
 */
export function commitInspectorTarget(opts: {
  type: InspectorObjectType;
  id: string;
  prefKey: string;
  select: SelectFn;
  savePref: (key: string, patch: InspectorTargets) => void;
}): void {
  opts.select(opts.type, opts.id);
  opts.savePref(opts.prefKey, opts.type === 'archetype' ? { archetypeId: opts.id } : { componentType: opts.id });
}
