// Filter-to-dim for the Database File Map (Module 15, §4.6 / §13 A4 AC4).
//
// The filter dims cells that do not match a predicate rather than hiding them, so structure stays legible
// (after SpaceSniffer). It composes with the active encoding and lens. v1 filters by page type — the coarse,
// always-available dimension; the mask is a pure function of the StructuralMap the client already holds.

import type { DbMapData } from './types';

/** A filter-to-dim predicate. `null` (no filter object) dims nothing; an empty `pageTypes` likewise. */
export interface DbMapFilter {
  /** Page-type ordinals (`DbPageType`) kept bright — a cell whose type is absent is dimmed. */
  pageTypes: number[];
}

/**
 * Builds the per-cell filter mask: 1 = the cell passes (stays bright), 0 = it is dimmed. Returns `null` when
 * the filter is null or selects no page type — there is then nothing to dim, and the renderer skips the layer.
 */
export function buildFilterMask(data: DbMapData, filter: DbMapFilter | null): Uint8Array | null {
  if (!filter || filter.pageTypes.length === 0) {
    return null;
  }
  const keep = new Set(filter.pageTypes);
  const mask = new Uint8Array(data.pageCount);
  for (let p = 0; p < data.pageCount; p++) {
    mask[p] = keep.has(data.pageType[p]) ? 1 : 0;
  }
  return mask;
}
