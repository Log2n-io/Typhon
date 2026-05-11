import type { QueryDefinitionDto } from '@/api/generated/model';
import { toNumber } from './numeric';

/**
 * Detect structurally-identical-but-distinct query definitions in the catalog.
 *
 * Two distinct `(Kind, LocalId)` instances that have the same target component type, same primary
 * index, same sort spec, and the same sorted list of `(FieldIdx, Op)` evaluators are considered
 * duplicates — usually a sign that two systems independently built equivalent Views that could
 * share one instance. The Workbench surfaces this as a small marker on each affected row so the
 * user can spot accidental duplication during refactor / code review.
 *
 * Design doc: §5.1 "Catalog row — duplicate-View marker".
 *
 * Issue #338 (P5 of #342).
 */
export function findDuplicateDefinitions(defs: readonly QueryDefinitionDto[]): Set<string> {
  if (defs.length < 2) return new Set();

  // Hash by structural shape — LocalId is intentionally excluded.
  const byShape = new Map<string, string[]>();
  for (const d of defs) {
    const shape = structuralShape(d);
    const rowId = `${toNumber(d.instanceId.kind)}:${toNumber(d.instanceId.localId)}`;
    const list = byShape.get(shape);
    if (list) list.push(rowId);
    else byShape.set(shape, [rowId]);
  }

  const duplicates = new Set<string>();
  for (const [, rowIds] of byShape) {
    if (rowIds.length >= 2) {
      for (const id of rowIds) duplicates.add(id);
    }
  }
  return duplicates;
}

/**
 * Compute a deterministic structural-shape string for a definition. Used as a hash key for
 * duplicate detection. Excludes `LocalId` (instance identity) and the source-location (which is
 * orthogonal to query structure). Includes:
 * - target component type
 * - primary index field
 * - sort field + direction
 * - sorted list of `(FieldIdx, Op)` evaluators
 */
function structuralShape(d: QueryDefinitionDto): string {
  const evals = (d.evaluators ?? [])
    .map((e) => `${toNumber(e.fieldIdx)}:${toNumber(e.op)}`)
    .sort()
    .join('|');
  return [
    toNumber(d.targetComponentType),
    toNumber(d.primaryIndexFieldIdx),
    toNumber(d.sortFieldIdx),
    d.sortDescending ? 'desc' : 'asc',
    evals,
  ].join('#');
}
