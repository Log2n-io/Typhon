import { beforeEach, describe, expect, it } from 'vitest';
import { openDataBrowser, registerDockApi } from '@/shell/commands/openSchemaBrowser';
import { openComponentInSchema, openDbMapForComponent, revealArchetypeInInspector, revealComponentInResourceTree, revealSystemInDag } from '@/shell/commands/openDbMap';
import { jumpToTimeRange } from '@/shell/commands/profilerCommands';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useDataBrowserStore } from '@/stores/useDataBrowserStore';
import { useDbMapStore } from '@/stores/useDbMapStore';
import { useResourceGraphStore } from '@/stores/useResourceGraphStore';
import { useDagViewStore } from '@/panels/SystemDag/useDagViewStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';

// AC2.14 — Stage-2 cross-view handoff matrix (UC-XV). Each handoff command resolves end-to-end to the
// destination's *target state* — the bus leaf or the focus-request store the destination view reads — proven
// **dock-independent** (no dockview api registered): a handoff carries the user to the right object whether or
// not the panel is already mounted; mounting/focusing is the destination view's own concern. This is the one
// resolution mechanism every source affordance routes through, so the per-source affordance tests
// (ComponentInspector / ArchetypeInspector / Inspector) + these prove the full matrix.

describe('AC2.14 — handoff resolution matrix', () => {
  beforeEach(() => {
    registerDockApi(null); // no dock → only the destination *state* resolution is exercised
    useSelectionStore.getState().clear();
    useDataBrowserStore.getState().reset();
    useDbMapStore.getState().clearPendingFocus();
    useResourceGraphStore.getState().clearRevealRequest();
    useSelectionStore.getState().clearLeaf();
  });

  // Open in → Data Browser, from {Archetype, Component, Resource, Segment} — all route through openDataBrowser.
  it('Open in → Data Browser scopes the browser to the archetype + sets the bus leaf', () => {
    openDataBrowser('2002');
    expect(useDataBrowserStore.getState().archetypeId).toBe('2002');
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'archetype', ref: '2002' });
  });

  // File Map / Segment / Resource → Open in (Component Inspector / Schema) — routes through openComponentInSchema.
  it('Open in → Schema selects the component on the bus leaf', () => {
    openComponentInSchema('Position');
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'component', ref: 'Position' });
  });

  // Reveal in → File Map, from {Resource, Schema, Inspector, Storage Health} — routes through openDbMapForComponent.
  it('Reveal in → File Map requests focus on the component’s segment', () => {
    openDbMapForComponent('Position');
    expect(useDbMapStore.getState().pendingFocusType).toBe('Position');
  });

  // File Map / Segment → Reveal in Resource Tree — routes through revealComponentInResourceTree.
  it('Reveal in → Resource Tree requests the ComponentTable node reveal', () => {
    revealComponentInResourceTree('Position');
    expect(useResourceGraphStore.getState().revealRequest).toBe('ComponentTable_Position');
  });

  // Used in → Archetype (Component Inspector "Used in" row) resolves via the bus archetype leaf.
  it('Used in → Archetype sets the bus archetype leaf', () => {
    useSelectionStore.getState().select('archetype', '2002');
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'archetype', ref: '2002' });
  });
});

// AC3.14 (#376 4D-1) — Query Analyzer cross-pillar hand-offs, proven dock-independent (no dockview api): each
// resolves to the destination's bus leaf / focus-request / global-scope state. (Target→Component reuses
// `openComponentInSchema`, already covered above.)
describe('AC3.14 — Query Analyzer hand-off resolution', () => {
  beforeEach(() => {
    registerDockApi(null);
    useSelectionStore.getState().clear();
    useDagViewStore.getState().clearPendingFocusSystem();
    useProfilerViewStore.getState().commitViewRange({ startUs: 0, endUs: 0 });
  });

  it('owner → System DAG sets the bus system + requests DAG focus', () => {
    revealSystemInDag('Movement');
    expect(useSelectionStore.getState().system).toBe('Movement');
    expect(useDagViewStore.getState().pendingFocusSystem).toBe('Movement');
  });

  it('archetype target → Archetype Inspector sets the bus archetype leaf', () => {
    revealArchetypeInInspector('2002');
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'archetype', ref: '2002' });
  });

  it('execution → Jump to time sets the global time window', () => {
    jumpToTimeRange(120, 480);
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 120, endUs: 480 });
  });

  it('Jump to time ignores a degenerate (empty) window', () => {
    useProfilerViewStore.getState().commitViewRange({ startUs: 5, endUs: 9 });
    jumpToTimeRange(50, 50); // endUs not > startUs → no-op
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 5, endUs: 9 });
  });
});
