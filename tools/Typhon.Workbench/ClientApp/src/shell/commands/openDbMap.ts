import type { Camera } from '@/libs/dbmap/camera';
import { useDbMapStore } from '@/stores/useDbMapStore';
import { useResourceGraphStore } from '@/stores/useResourceGraphStore';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { ensureDockPanel, ensureResourceTreeVisible } from './openSchemaBrowser';

// Cross-links between the Database File Map and the rest of the Workbench (Module 15, §7.3 / §13 A4 AC1).
// Every link identifies a component by its type name — the common handle Resource Explorer, Schema Inspector
// and the map all share. Admin Ops (Module 11) and WAL Events (Module 08) are not implemented; their links
// degrade to disabled actions in the panels rather than being wired here.

/** Resource Explorer / Schema Inspector → map: open the file map focused on a component type's segment. */
export function openDbMapForComponent(typeName: string): void {
  useDbMapStore.getState().requestFocusComponent(typeName);
  ensureDockPanel('dbmap', 'DbMap', 'Database File Map');
}

/** Map → Schema Inspector: open the Component Layout panel focused on a component type. */
export function openComponentInSchema(typeName: string): void {
  useSchemaInspectorStore.getState().selectComponent(typeName);
  ensureDockPanel('schema-layout', 'SchemaLayout', 'Component Layout');
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
