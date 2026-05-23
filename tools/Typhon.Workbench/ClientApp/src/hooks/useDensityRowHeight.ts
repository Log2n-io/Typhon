import { useDensityStore, DENSITY_ROW_HEIGHT } from '@/stores/useDensityStore';

/**
 * Current list/tree row height (px) for the active density (DS-1). Virtualized lists + trees pass this
 * to `estimateSize` / `rowHeight` so a density change re-measures them (conformance suite H).
 */
export function useDensityRowHeight(): number {
  return DENSITY_ROW_HEIGHT[useDensityStore((s) => s.mode)];
}
