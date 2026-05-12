import { useEffect } from 'react';
import {
  applySelectionToStore,
  installSelectionUrlSync,
  parseSelectionFromSearch,
} from '@/stores/selectionUrlSync';

/**
 * One-shot bootstrap for the cross-panel selection state. Runs at app mount in two steps:
 *
 *   1. Apply URL → canonical stores (`useSelectionStore` + `useProfilerViewStore`).
 *      The per-store setters now inline the cross-store mirrors (selected resource → resource id,
 *      schema inspector ↔ component), so URL deep-links reach every legacy consumer through the
 *      inlined writes rather than a separate bridge layer.
 *   2. Install the canonical-stores → URL mirror. From here on, any stable-slot change writes
 *      back to `window.location.search` via `history.replaceState`.
 *
 * Pre-#345 had a step 0 that installed `selectionBridges` — Zustand subscriptions mirroring
 * legacy per-panel stores into the unified store. Those bridges are gone (see #345 Step 7); the
 * mirrors are inlined into the legacy stores' setters now.
 *
 * Call this from a single top-level component (Shell). Calling it elsewhere doubles the
 * subscriptions and produces redundant URL writes.
 */
export function useSelectionBootstrap(): void {
  useEffect(() => {
    applySelectionToStore(parseSelectionFromSearch(window.location.search));
    const stopUrlSync = installSelectionUrlSync();
    return () => {
      stopUrlSync();
    };
  }, []);
}
