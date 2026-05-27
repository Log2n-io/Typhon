import type { Camera } from '@/libs/dbmap/camera';
import { useDagViewStore } from '@/panels/SystemDag/useDagViewStore';
import { useDbMapStore } from '@/stores/useDbMapStore';
import { useResourceGraphStore } from '@/stores/useResourceGraphStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { ensureDockPanel, ensureResourceTreeVisible, openArchetypeInspector, openComponentInspector } from './openSchemaBrowser';

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
 * Set the unified bus's `system` leaf + projection in one call. Used by the Systems & Queries Navigator's
 * hover-reveal buttons so a user clicking a verb (Reveal in CP / Flow / Inspector) without having clicked the
 * row body first still ends up with the bus in a consistent state. Co-located with the other system reveals
 * for visibility; the leaf write and projection write are intentionally separate API surfaces on the bus
 * (leaf is the Inspector's primary signal, projection is the cluster panels' highlight target).
 */
function selectSystemOnBus(name: string): void {
  const store = useSelectionStore.getState();
  store.select('system', name);
  store.setSystem(name);
}

/**
 * Navigator → Critical Path: select the system on the bus and surface the CP panel. The panel reads the
 * projection and highlights the system's `cp-system-edge-${name}` band. Mirrors {@link revealSystemInDag}'s
 * shape but without a center-and-fit signal — CP has no node-positioning concept; the bar is wherever the
 * scheduler put it on the timeline.
 */
export function revealSystemInCriticalPath(name: string): void {
  selectSystemOnBus(name);
  ensureDockPanel('critical-path', 'CriticalPath', 'Critical Path');
}

/**
 * Navigator → Data Flow: select the system on the bus and surface the Data Flow panel. The panel's
 * AccessMatrix column header + DataFlow track row pick up the projection and outline (the same cross-panel
 * highlight `data-flow.spec.ts` exercises).
 */
export function revealSystemInDataFlow(name: string): void {
  selectSystemOnBus(name);
  ensureDockPanel('data-flow', 'DataFlow', 'Data Flow');
}

/**
 * Navigator → Inspector (Detail pane): select the system on the bus and surface the Detail panel. The
 * Detail panel reads the bus leaf and renders the system card with its DAG / phase / cross-link verbs.
 * `'detail'` is the canonical panel id; component key is `'Detail'` (see DockHost.tsx:183).
 */
export function revealSystemInInspector(name: string): void {
  selectSystemOnBus(name);
  ensureDockPanel('detail', 'Detail', 'Detail');
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
 * Query Analyzer → Archetype Inspector: select an archetype on the bus and open its inspector. The
 * archetype-target sibling of {@link openComponentInSchema} (a pull-mode query's `TargetComponentType`
 * is an ArchetypeId rather than a ComponentType id). The Inspector reads the bus leaf, so it re-targets.
 */
export function revealArchetypeInInspector(archetypeId: string): void {
  useSelectionStore.getState().select('archetype', archetypeId);
  openArchetypeInspector();
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
