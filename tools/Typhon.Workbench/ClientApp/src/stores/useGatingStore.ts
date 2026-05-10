import { create } from 'zustand';
import type { SystemGatingInfo } from '@/lib/dag/gatingAnalysis';

/**
 * Cross-panel cache for gating analysis. The math is non-trivial and the inputs (systems, edges,
 * tick rows, range) are stable across the open panels — recomputing per-panel would duplicate the
 * work whenever both DataFlow and SystemDag are open.
 *
 * Producer pattern: a panel computes its analysis (using `computeGatingAnalysis` from
 * `@/lib/dag/gatingAnalysis`) and writes the result via {@link setGating} along with a
 * fingerprint that identifies the inputs. Consumers read {@link gatingByName} and check the
 * fingerprint to know whether the cached result matches their inputs.
 *
 * No persistence — gating is per-session and cheap enough to recompute on remount.
 */
export interface GatingState {
  /** Map keyed by system name. `null` until any producer panel populates it. */
  gatingByName: Map<string, SystemGatingInfo> | null;
  /**
   * Stable fingerprint of the inputs the cached map was computed from. Producers compose this
   * from session id + tick range + topology revision. Consumers compare to know whether the
   * cached result matches their own inputs (skip recompute) or is stale (recompute).
   */
  fingerprint: string | null;
  setGating: (gatingByName: Map<string, SystemGatingInfo>, fingerprint: string) => void;
  clear: () => void;
}

export const useGatingStore = create<GatingState>((set) => ({
  gatingByName: null,
  fingerprint: null,
  setGating: (gatingByName, fingerprint) => set({ gatingByName, fingerprint }),
  clear: () => set({ gatingByName: null, fingerprint: null }),
}));
