import type { QueryDefinitionDto } from '@/api/generated/model/queryDefinitionDto';
import { toNumber } from '@/panels/QueryAnalyzer/numeric';

/**
 * Count distinct query definitions owned by each system. Pure function — no React, no DOM —
 * unit-testable in isolation. P8 of umbrella #342 (issue #341).
 *
 * <para>One definition can list multiple owner-system IDs (when the same query is issued by
 * several systems via shared infrastructure). Each unique <c>(definition, system)</c> pair counts
 * once toward the system's tally — a definition that lists the same system twice in
 * <c>ownerSystemIds</c> still counts as one for that system. This matches the Query Catalog's
 * filter semantics: filtering by system S returns the rows whose <c>ownerSystemIds</c> includes
 * S, regardless of multiplicity.</para>
 *
 * @returns a Map keyed by system name (resolved via {@link systemIndexToName}). System IDs not
 *   in the lookup are dropped — they're either invalid or come from a metadata-stale trace, and
 *   we have no name to display for them.
 */
export function buildQueryCountsBySystem(
  definitions: readonly QueryDefinitionDto[],
  systemIndexToName: Map<number, string>,
): Map<string, number> {
  const out = new Map<string, number>();
  for (const def of definitions) {
    const seen = new Set<number>();
    for (const rawId of def.ownerSystemIds ?? []) {
      const id = toNumber(rawId);
      if (id < 0 || seen.has(id)) continue;
      seen.add(id);
      const name = systemIndexToName.get(id);
      if (!name) continue;
      out.set(name, (out.get(name) ?? 0) + 1);
    }
  }
  return out;
}

/**
 * Resolves the single owning definition for each system name when (and only when) the system owns
 * exactly one distinct query. Drives the "click badge → also expand the relevant row" affordance
 * in the Catalog: with two owned queries the user wants the filter, not an arbitrary pick. Pairs
 * with {@link buildQueryCountsBySystem} — same iteration shape, different summarization.
 */
export function buildSingleOwnedDefBySystem(
  definitions: readonly QueryDefinitionDto[],
  systemIndexToName: Map<number, string>,
): Map<string, { kind: number; localId: number }> {
  // Two-pass: first count owners per system (so we know what 'exactly one' means); second pass
  // records the lone definition.
  const counts = buildQueryCountsBySystem(definitions, systemIndexToName);
  const out = new Map<string, { kind: number; localId: number }>();
  for (const def of definitions) {
    const seen = new Set<number>();
    for (const rawId of def.ownerSystemIds ?? []) {
      const id = toNumber(rawId);
      if (id < 0 || seen.has(id)) continue;
      seen.add(id);
      const name = systemIndexToName.get(id);
      if (!name || counts.get(name) !== 1) continue;
      out.set(name, { kind: toNumber(def.instanceId.kind), localId: toNumber(def.instanceId.localId) });
    }
  }
  return out;
}
