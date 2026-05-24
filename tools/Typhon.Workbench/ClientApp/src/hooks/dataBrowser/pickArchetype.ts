import type { ArchetypeInfo } from '@/hooks/schema/types';

/**
 * Type-first "Open in → Data Browser" resolution (AC2.7). A component belongs to many archetypes (M:N), so
 * opening the Data Browser *from a component* has to choose one to scope to. We auto-pick the archetype with
 * the most entities — the one most likely to be what the user wants to inspect — and the Data Browser's own
 * archetype picker is the change control for the rest.
 *
 * Returns `null` when no archetype has any entities, so callers render no "Open in Data Browser" verb at all
 * rather than a dead one (PC-6) — there is nothing to browse.
 */
export function pickPrimaryArchetype(archetypes: ArchetypeInfo[]): ArchetypeInfo | null {
  let best: ArchetypeInfo | null = null;
  for (const a of archetypes) {
    if (a.entityCount <= 0) continue;
    if (best === null || a.entityCount > best.entityCount) {
      best = a;
    }
  }
  return best;
}
