import { useSelectionStore, type SelectionObjectType } from './useSelectionStore';
import { useNavHistoryStore } from './useNavHistoryStore';
import { currentActivePanelId } from './navFocusBridge';

/**
 * Leaf types recorded in nav history as generic `bus-leaf` entries. Resource keeps its richer
 * `resource-selected` entry; the viewport-carrying types (span/tick + file-map page/chunk/cell/segment)
 * get their own viewport entries from their panels (Stage 3+), so they are excluded here to avoid a
 * double push.
 *
 * **Typed `SelectionObjectType`, not `string`, since #1083.** As a set of bare strings this was a second closed list
 * that the compiler could not relate to the object-type union, so adding `realm` to the union left realm selections
 * silently unrecorded — Back skipped straight past them. The annotation does not make the list exhaustive, but it does
 * make a typo a build error and puts this file in the compiler's answer to "what does a new object type touch?".
 */
const PUSHED_LEAF_TYPES = new Set<SelectionObjectType>([
  'component',
  'field',
  'archetype',
  'entity',
  'system',
  'query',
  'index',
  'realm',
]);

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
    // View-granular record: updates the current view's entry in place, or pushes a new one for a new view.
    // The active panel id stamps the origin so back-to-here restores focus, not just the leaf (IA §3.2/§5.3).
    useNavHistoryStore.getState().recordSelection(leaf, currentActivePanelId());
  });
}
