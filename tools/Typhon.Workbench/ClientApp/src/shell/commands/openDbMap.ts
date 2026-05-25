import type { Camera } from '@/libs/dbmap/camera';
import { useDagViewStore } from '@/panels/SystemDag/useDagViewStore';
import { useDbMapStore } from '@/stores/useDbMapStore';
import { useResourceGraphStore } from '@/stores/useResourceGraphStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { ensureDockPanel, ensureResourceTreeVisible, openComponentInspector } from './openSchemaBrowser';

// Cross-links between the Database File Map and the rest of the Workbench (Module 15, §7.3 / §13 A4 AC1).
// Every link identifies a component by its type name — the common handle Resource Explorer, Schema Inspector
// and the map all share. Admin Ops (Module 11) and WAL Events (Module 08) are not implemented; their links
// degrade to disabled actions in the panels rather than being wired here.

/** Resource Explorer / Schema Inspector → map: open the file map focused on a component type's segment. */
export function openDbMapForComponent(typeName: string): void {
  useDbMapStore.getState().requestFocusComponent(typeName);
  ensureDockPanel('dbmap', 'DbMap', 'Database File Map');
}

/**
 * Inspector System card → System DAG: highlight the system on the bus, request the canvas to centre + fit its node
 * (the `pendingFocusSystem` reveal signal, distinct from the plain bus highlight so only an explicit reveal recentres),
 * and surface the DAG panel. The panel auto-enables "show engine tracks" if the target is an engine-internal system
 * that would otherwise be hidden (3D, AC3.14 handoff).
 */
export function revealSystemInDag(name: string): void {
  useSelectionStore.getState().setSystem(name);
  useDagViewStore.getState().requestFocusSystem(name);
  ensureDockPanel('system-dag', 'SystemDag', 'System DAG');
}

/**
 * Map → Schema: select the component on the bus and open the Component Inspector (Stage 2 consolidation —
 * the old SchemaLayout panel was removed in GAP-02). The Inspector reads the bus leaf, so it re-targets.
 */
export function openComponentInSchema(typeName: string): void {
  useSelectionStore.getState().select('component', typeName);
  openComponentInspector();
}

/**
 * Map → Resource Explorer: scroll the Resource Tree to a component type's node and select it — the "reveal"
 * action. It surfaces and focuses the node *without filtering* (filtering would hide every other node). The
 * resource node for a component table is named `ComponentTable_{TypeName}`.
 */
export function revealComponentInResourceTree(typeName: string): void {
  ensureResourceTreeVisible();
  useResourceGraphStore.getState().requestReveal(`ComponentTable_${typeName}`);
}

// ── Nav-history camera restore (§13 A4 AC2) ─────────────────────────────────────────────────────────────────
// The map camera is a panel-local ref, not a store — so the nav-history store cannot restore it directly.
// The panel publishes a restore closure here (the same module-registration pattern as registerDockApi); the
// nav-history `dbmap-navigated` restore calls it. No-op until the File Map panel has mounted.

let cameraRestore: ((camera: Camera) => void) | null = null;

/** The File Map panel registers (and on unmount clears) its camera fly-to here. */
export function registerDbMapCameraRestore(fn: ((camera: Camera) => void) | null): void {
  cameraRestore = fn;
}

/** Flies the File Map camera to a recorded framing — invoked by an `Alt+←/→` nav-history restore. */
export function restoreDbMapCamera(camera: Camera): void {
  cameraRestore?.(camera);
}
