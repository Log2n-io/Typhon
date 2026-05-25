import type { QueryDefinitionDto } from '@/api/generated/model';
import { toNumber } from './numeric';

/**
 * Pure filter predicate for the Query Catalog. Extracted from <c>QueryCatalogPanel</c> so it can be
 * unit-tested without a React render. Returns true when the definition should be visible under the
 * current filter set.
 *
 * Issue #338 (P5 of #342).
 */
export interface CatalogFilter {
  /** Lower-cased trimmed search needle. Empty string = no text filter. */
  search: string;
  /** System ID filter; null = no system filter. */
  systemFilter: number | null;
  /** Archetype (target component type) ID filter; null = no archetype filter. */
  archetypeFilter: number | null;
}

export interface NameLookup {
  archetypeName: (id: number) => string;
  systemName: (id: number) => string;
}

export function passesFilter(d: QueryDefinitionDto, f: CatalogFilter, names: NameLookup): boolean {
  if (f.archetypeFilter !== null && toNumber(d.targetComponentType) !== f.archetypeFilter) {
    return false;
  }
  if (f.systemFilter !== null) {
    const owners = (d.ownerSystemIds ?? []).map((id) => toNumber(id));
    if (!owners.includes(f.systemFilter)) return false;
  }
  if (f.search.length > 0) {
    const archetypeName = names.archetypeName(toNumber(d.targetComponentType));
    const evalText = (d.evaluators ?? [])
      .map((e) => `${e.fieldName ?? ''} ${e.opDisplay ?? ''}`)
      .join(' ');
    const ownerText = (d.ownerSystemIds ?? [])
      .map((id) => names.systemName(toNumber(id)))
      .join(' ');
    const hay = `${archetypeName} ${evalText} ${ownerText} ${d.userSource.method ?? ''}`.toLowerCase();
    if (!hay.includes(f.search)) return false;
  }
  return true;
}
