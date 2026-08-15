import { useMemo } from 'react';
import { useComponentList } from '@/hooks/schema/useComponentList';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import { useComponentSchema } from '@/hooks/schema/useComponentSchema';
import { useSessionCapability } from '@/stores/useSessionStore';
import { traceIdentityOf } from '@/libs/schema/componentIdentity';

/**
 * What the **database** says about the thing a recorded query was scanning (#618, design §4.3).
 *
 * The capture knows a query's cost — selectivity, P99, execution count — and nothing about why. The database knows
 * which fields carry an index and how many entities are actually in there, and nothing about cost. Neither half can
 * produce the diagnosis the design asks for:
 *
 * > *"This query scans `Unit.Position`, 2% selectivity, P99 4 ms, 12k executions"* → *"`Position` has no index;
 * > `Unit` currently holds 340k entities."*
 *
 * **Present tense on purpose.** The index set and the entity count are today's, not the capture's — the same honesty
 * caveat §4.2 puts on the storage lens. The wording in the UI has to say "currently", because a index added since the
 * capture would otherwise read as an explanation for a slow run it postdates.
 */
export interface QueryTargetReality {
  /** Fields of the target that carry an index in the database right now. Empty when the target is an archetype. */
  indexedFields: Set<string>;
  /** Entities the target holds right now, or null when the database does not know the target. */
  entityCount: number | null;
  /** False when the target does not resolve in this database — the bridge is then absent rather than zeroed. */
  resolved: boolean;
}

const NOT_RESOLVED: QueryTargetReality = { indexedFields: new Set(), entityCount: null, resolved: false };

/**
 * Joins a query's target to the open database, if there is one.
 *
 * @param targetName The target's name as the capture records it — a CLR full name for a component target, the
 *   archetype's name for an archetype target. Both come pre-resolved from `useProfilerNameMaps`.
 * @param targetIsComponent Whether the target is a ComponentType (vs an Archetype), which decides which database
 *   table answers.
 */
export function useQueryTargetReality(targetName: string | null, targetIsComponent: boolean): QueryTargetReality {
  // A standalone trace session has no database to ask. Everything below no-ops, and the Query Analyzer renders exactly
  // as it did before this bridge existed.
  const hasDatabase = useSessionCapability('database');

  const { list: components } = useComponentList();
  const { list: archetypes } = useArchetypeList();

  // Join on the CLR full name, for the reason set out in componentIdentity.ts: it is the only identifier the capture
  // and the database both hold. The display name differs on each side and matching it silently finds nothing.
  const component = useMemo(() => {
    if (!hasDatabase || !targetIsComponent || !targetName) return null;
    return components.find((c) => traceIdentityOf(c) === targetName) ?? null;
  }, [hasDatabase, targetIsComponent, targetName, components]);

  const archetype = useMemo(() => {
    if (!hasDatabase || targetIsComponent || !targetName) return null;
    return archetypes.find((a) => a.name === targetName) ?? null;
  }, [hasDatabase, targetIsComponent, targetName, archetypes]);

  // Keyed by the DATABASE's own typeName — the schema name, which is what its routes take.
  //
  // The FIELD LIST is the source of truth for "is this field indexed?", not `components/{t}/indexes`. That endpoint
  // reports each index's offset within the whole record (payload + the per-component overhead) while a field's own
  // offset is payload-relative, so its name lookup misses and it emits a synthetic `@12` placeholder — observed live
  // on an indexed `Faction.Value` at payload offset 0 with a 12-byte overhead. Matching evaluator names against those
  // would report "no index on Value" for an indexed field: a confident wrong answer, which §5.7 forbids outright.
  // `FieldDto.isIndexed` is keyed by name and needs no offset arithmetic. (The placeholder is a real pre-existing bug
  // in that endpoint — the Indexes tab shows `@12` too — but fixing it is not this feature's business; see §1.5.)
  const { schema } = useComponentSchema(component?.typeName ?? null);

  return useMemo(() => {
    // Not until the field list has landed. An empty index set during the load window renders as "no index on X" and
    // then corrects itself a moment later — a wrong answer stated confidently is worse than a beat of nothing.
    if (component && schema) {
      const indexed = new Set<string>();
      for (const f of schema.fields ?? []) {
        if (f.isIndexed && f.name) indexed.add(f.name);
      }
      return { indexedFields: indexed, entityCount: component.entityCount, resolved: true };
    }
    if (archetype) {
      // An archetype target has no index list of its own — its indexes live on its components. The entity count is
      // still the useful half, and it is the number the design's example actually quotes.
      return { indexedFields: new Set(), entityCount: archetype.entityCount, resolved: true };
    }
    return NOT_RESOLVED;
  }, [component, archetype, schema]);
}
