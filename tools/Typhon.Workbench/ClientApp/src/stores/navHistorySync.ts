import { useSelectionStore } from './useSelectionStore';
import { useNavHistoryStore } from './useNavHistoryStore';

/**
 * Leaf types recorded in nav history as generic `bus-leaf` entries. Resource keeps its richer
 * `resource-selected` entry; the viewport-carrying types (span/tick + file-map page/chunk/cell/segment)
 * get their own viewport entries from their panels (Stage 3+), so they are excluded here to avoid a
 * double push.
 */
const PUSHED_LEAF_TYPES = new Set(['component', 'field', 'archetype', 'entity', 'system', 'query', 'index']);

/**
 * Installs the selection-bus → nav-history bridge (Stage 1, #373): every primary selection of a
 * recordable object type pushes a history entry, so Back/Forward replays the full drill path — not just
 * the legacy resource/profiler/dbmap selections (closes G9). Suppressed during a restore (back/forward)
 * so replaying an entry doesn't re-record it. Returns the unsubscribe handle.
 */
export function installNavHistorySync(): () => void {
  return useSelectionStore.subscribe((state, prev) => {
    const leaf = state.leaf;
    if (leaf === prev.leaf || leaf === null) return;
    if (!PUSHED_LEAF_TYPES.has(leaf.type)) return;
    if (useNavHistoryStore.getState().isRestoring) return;
    useNavHistoryStore.getState().push({ kind: 'bus-leaf', leaf, timestamp: Date.now() });
  });
}
