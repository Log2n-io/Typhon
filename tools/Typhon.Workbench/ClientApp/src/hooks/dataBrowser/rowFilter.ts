import type { ComponentValue, EntityRow } from './types';
import type { PreviewField } from './previewFields';

/**
 * Client-side "find" for the Data Browser (GAP-15, client part — graceful-degraded). True server-side
 * find/range over the index is engine-gated (Later); for now we filter the **loaded page** by a single
 * `field = value` predicate. Matching is a case-insensitive **contains** on the column's formatted value
 * (exact equality is useless for floats), keyed by the visible preview column name or the Entity Id.
 */
export interface RowFilter {
  field: string;
  value: string;
}

/** Parse a `field = value` find expression. Returns null for empty / unparseable input (→ no filter applied). */
export function parseRowFilter(input: string): RowFilter | null {
  const eq = input.indexOf('=');
  if (eq < 0) {
    return null;
  }
  const field = input.slice(0, eq).trim();
  const value = input.slice(eq + 1).trim();
  if (!field) {
    return null;
  }
  return { field, value };
}

/** Field names that address the Entity Id pseudo-column (it isn't a preview field, but users will filter on it). */
const ENTITY_ID_ALIASES = new Set(['id', 'entityid', 'entity id']);

/**
 * Apply a parsed filter to the loaded rows. Returns all rows when there's no filter. `fieldKnown` is false
 * when the named field is neither the Entity Id nor a visible preview column — the caller surfaces a hint
 * (you can only filter client-side on a loaded column) rather than silently showing zero rows.
 */
export function applyRowFilter(
  rows: EntityRow[],
  filter: RowFilter | null,
  columns: PreviewField[],
  fieldNameOf: (pf: PreviewField) => string,
  formatCell: (v: ComponentValue) => string,
): { rows: EntityRow[]; fieldKnown: boolean } {
  if (!filter) {
    return { rows, fieldKnown: true };
  }
  const needle = filter.value.toLowerCase();
  const fieldLc = filter.field.toLowerCase();

  if (ENTITY_ID_ALIASES.has(fieldLc)) {
    return { rows: rows.filter((r) => r.entityId.toLowerCase().includes(needle)), fieldKnown: true };
  }

  const colIdx = columns.findIndex((c) => fieldNameOf(c).toLowerCase() === fieldLc);
  if (colIdx < 0) {
    return { rows, fieldKnown: false };
  }
  return {
    rows: rows.filter((r) => {
      const cell = r.preview[colIdx];
      return cell != null && formatCell(cell).toLowerCase().includes(needle);
    }),
    fieldKnown: true,
  };
}
