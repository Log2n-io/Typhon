import { beforeEach, describe, expect, it } from 'vitest';
import { useNavHistoryStore, type NavEntry } from '../useNavHistoryStore';
import { useSelectedResourceStore, type SelectedResource } from '../useSelectedResourceStore';
import { useProfilerViewStore } from '../useProfilerViewStore';
import { registerAnimateViewport } from '@/shell/commands/profilerCommands';
import type { ResourceNodeDto } from '@/api/generated/model/resourceNodeDto';

const makeRaw = (id: string): ResourceNodeDto => ({
  id,
  name: id,
  type: 'Segment',
  entityCount: null,
  children: [],
});

const makeSelected = (resourceId: string): SelectedResource => ({
  resourceId,
  kind: 'Segment',
  name: resourceId,
  path: [resourceId],
  raw: makeRaw(resourceId),
});

const resourceEntry = (id: string): NavEntry => ({
  kind: 'resource-selected',
  resourceId: id,
  selected: makeSelected(id),
  timestamp: Date.now(),
});

const panelEntry = (id: string): NavEntry => ({
  kind: 'panel-opened',
  panelId: id,
  leaf: null,
  timestamp: Date.now(),
});

beforeEach(() => {
  useNavHistoryStore.getState().clear();
  useSelectedResourceStore.getState().clear();
});

describe('useNavHistoryStore — basic navigation', () => {
  it('starts empty, cannot back or forward', () => {
    const s = useNavHistoryStore.getState();
    expect(s.pointer).toBe(-1);
    expect(s.canBack).toBe(false);
    expect(s.canForward).toBe(false);
  });

  it('push adds entry, cannot back from first entry', () => {
    useNavHistoryStore.getState().push(panelEntry('panel'));
    const s = useNavHistoryStore.getState();
    expect(s.entries).toHaveLength(1);
    expect(s.pointer).toBe(0);
    expect(s.canBack).toBe(false);
    expect(s.canForward).toBe(false);
  });

  it('push two entries: canBack=true from second', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().push(panelEntry('b'));
    const s = useNavHistoryStore.getState();
    expect(s.canBack).toBe(true);
    expect(s.canForward).toBe(false);
  });

  it('back then forward restores pointer', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().push(panelEntry('b'));
    useNavHistoryStore.getState().back();
    expect(useNavHistoryStore.getState().pointer).toBe(0);
    expect(useNavHistoryStore.getState().canForward).toBe(true);
    useNavHistoryStore.getState().forward();
    expect(useNavHistoryStore.getState().pointer).toBe(1);
    expect(useNavHistoryStore.getState().canForward).toBe(false);
  });

  it('back does nothing when canBack=false', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().back();
    expect(useNavHistoryStore.getState().pointer).toBe(0);
  });

  it('forward does nothing when canForward=false', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().forward();
    expect(useNavHistoryStore.getState().pointer).toBe(0);
  });

  it('push from mid-history discards forward stack', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    useNavHistoryStore.getState().push(panelEntry('b'));
    useNavHistoryStore.getState().push(panelEntry('c'));
    useNavHistoryStore.getState().back();
    useNavHistoryStore.getState().back();
    useNavHistoryStore.getState().push(panelEntry('d'));
    const s = useNavHistoryStore.getState();
    expect(s.entries).toHaveLength(2);
    expect(s.pointer).toBe(1);
    expect(s.canForward).toBe(false);
  });
});

describe('useNavHistoryStore — ring buffer overflow', () => {
  it('caps at 100 entries on overflow', () => {
    for (let i = 0; i < 105; i++) {
      useNavHistoryStore.getState().push(panelEntry(`e${i}`));
    }
    const s = useNavHistoryStore.getState();
    expect(s.entries).toHaveLength(100);
    expect(s.pointer).toBe(99);
    expect(s.entries[0].kind === 'panel-opened' && s.entries[0].panelId).toBe('e5');
  });
});

describe('useNavHistoryStore — updateTopSelection', () => {
  it('no-ops when the stack is empty', () => {
    useNavHistoryStore.getState().updateTopSelection(null);
    const s = useNavHistoryStore.getState();
    expect(s.entries).toEqual([]);
    expect(s.pointer).toBe(-1);
  });

  it('no-ops when the top entry is not a profiler-selected kind', () => {
    useNavHistoryStore.getState().push(panelEntry('a'));
    const before = useNavHistoryStore.getState().entries;
    useNavHistoryStore.getState().updateTopSelection(null);
    // entries array reference stable — no synthetic push, no mutation.
    expect(useNavHistoryStore.getState().entries).toBe(before);
  });

  it('patches the top profiler-selected entry in place without moving the pointer', () => {
    const entry: NavEntry = {
      kind: 'profiler-selected',
      selection: null,
      viewRange: { startUs: 0, endUs: 1000 },
      timestamp: 0,
    };
    useNavHistoryStore.getState().push(entry);

    const newSelection = {
      kind: 'span',
      span: { kind: 10, name: 'x', threadSlot: 0, startUs: 0, endUs: 1, durationUs: 1 },
    } as never;
    useNavHistoryStore.getState().updateTopSelection(newSelection);

    const after = useNavHistoryStore.getState();
    expect(after.pointer).toBe(0);
    expect(after.entries).toHaveLength(1);
    const top = after.entries[0];
    if (top.kind !== 'profiler-selected') throw new Error('kind changed unexpectedly');
    expect(top.selection).toBe(newSelection);
  });

  it('no-ops during restore so a back/forward dispatch does not re-patch', () => {
    // Push two profiler-selected entries, back() to the first, then try updateTopSelection.
    // The back() dispatch temporarily sets isRestoring=true while it calls into the selection
    // store; a naive selection-store subscriber that called updateTopSelection would otherwise
    // churn the entry mid-restore. This test pins the isRestoring gate.
    const e1: NavEntry = {
      kind: 'profiler-selected', selection: null,
      viewRange: { startUs: 0, endUs: 100 }, timestamp: 0,
    };
    const e2: NavEntry = {
      kind: 'profiler-selected', selection: null,
      viewRange: { startUs: 100, endUs: 200 }, timestamp: 0,
    };
    useNavHistoryStore.getState().push(e1);
    useNavHistoryStore.getState().push(e2);

    // Manually flip isRestoring to simulate the window inside back(); updateTopSelection must be
    // a no-op during this window.
    useNavHistoryStore.setState({ isRestoring: true });
    const before = useNavHistoryStore.getState().entries;
    const newSel = { kind: 'span', span: {} as never } as never;
    useNavHistoryStore.getState().updateTopSelection(newSel);
    expect(useNavHistoryStore.getState().entries).toBe(before);
    useNavHistoryStore.setState({ isRestoring: false });
  });
});

describe('useNavHistoryStore — restore dispatches to selection store', () => {
  it('back() on resource-selected entries updates useSelectedResourceStore', () => {
    useNavHistoryStore.getState().push(resourceEntry('r1'));
    useNavHistoryStore.getState().push(resourceEntry('r2'));
    expect(useSelectedResourceStore.getState().selected).toBeNull();

    useNavHistoryStore.getState().back();
    expect(useSelectedResourceStore.getState().selected?.resourceId).toBe('r1');

    useNavHistoryStore.getState().forward();
    expect(useSelectedResourceStore.getState().selected?.resourceId).toBe('r2');
  });

  it('restore does not push (no stack growth)', () => {
    useNavHistoryStore.getState().push(resourceEntry('r1'));
    useNavHistoryStore.getState().push(resourceEntry('r2'));
    const sizeBefore = useNavHistoryStore.getState().entries.length;

    // Simulate a downstream setSelected → push() attempt during back()
    useNavHistoryStore.getState().back();

    // Stack must not have grown: back() flipped the restore flag, so any hypothetical push
    // inside the dispatch chain would be a no-op.
    expect(useNavHistoryStore.getState().entries.length).toBe(sizeBefore);
    expect(useNavHistoryStore.getState().isRestoring).toBe(false);
  });

  // Width-aware restore (2026-05-26): the original regression-guard (#345) snapped EVERY
  // profiler-selected back/forward — fixed the visible slide between adjacent ticks, but ALSO
  // killed the legitimate ease-out when nav-back crosses a real zoom change (select-and-zoom →
  // back to overview). The current heuristic snaps when widths are similar (tick-to-tick) and
  // animates when widths differ ≥ 1.5× (zoom in/out).
  it('back()/forward() between SAME-width entries (adjacent ticks) snaps without animating', () => {
    let animateCalls = 0;
    registerAnimateViewport(() => { animateCalls++; });
    try {
      const e1: NavEntry = {
        kind: 'profiler-selected', selection: null,
        viewRange: { startUs: 0, endUs: 100 }, timestamp: 0,
      };
      const e2: NavEntry = {
        kind: 'profiler-selected', selection: null,
        viewRange: { startUs: 500, endUs: 600 }, timestamp: 0,
      };
      useNavHistoryStore.getState().push(e1);
      useNavHistoryStore.getState().push(e2);
      // After two pushes the committed viewRange should already match e2's range — but the test
      // bench's push() doesn't auto-commit, so seed it here so the back() sees a same-width
      // source viewport.
      useProfilerViewStore.getState().commitViewRange({ startUs: 500, endUs: 600 });

      useNavHistoryStore.getState().back();
      expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 0, endUs: 100 });

      useNavHistoryStore.getState().forward();
      expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 500, endUs: 600 });

      // No animation: widths are identical (both 100µs), ratio 1.0 — well below the 1.5× threshold.
      expect(animateCalls).toBe(0);
    } finally {
      registerAnimateViewport(null);
    }
  });

  it('back() from a NARROW viewport to a WIDE viewport (select-and-zoom → out) DOES animate', () => {
    let animateCalls = 0;
    let lastTarget: { startUs: number; endUs: number } | null = null;
    registerAnimateViewport((target) => { animateCalls++; lastTarget = target; });
    try {
      // e1: the wide overview the user came from (1000 µs).
      const e1: NavEntry = {
        kind: 'profiler-selected', selection: null,
        viewRange: { startUs: 0, endUs: 1000 }, timestamp: 0,
      };
      // e2: the narrow drag-to-zoom (100 µs — 10× narrower).
      const e2: NavEntry = {
        kind: 'profiler-selected', selection: null,
        viewRange: { startUs: 400, endUs: 500 }, timestamp: 0,
      };
      useNavHistoryStore.getState().push(e1);
      useNavHistoryStore.getState().push(e2);
      // Seed the committed viewport so the back() sees the narrow zoom as its origin.
      useProfilerViewStore.getState().commitViewRange({ startUs: 400, endUs: 500 });

      useNavHistoryStore.getState().back();
      // The animator was called with e1's range — the snap path was skipped.
      expect(animateCalls).toBe(1);
      expect(lastTarget).toEqual({ startUs: 0, endUs: 1000 });
    } finally {
      registerAnimateViewport(null);
    }
  });

  it('forward() from a WIDE viewport to a NARROW viewport (re-enter the zoom) DOES animate', () => {
    let animateCalls = 0;
    registerAnimateViewport(() => { animateCalls++; });
    try {
      const wide: NavEntry = {
        kind: 'profiler-selected', selection: null,
        viewRange: { startUs: 0, endUs: 1000 }, timestamp: 0,
      };
      const narrow: NavEntry = {
        kind: 'profiler-selected', selection: null,
        viewRange: { startUs: 400, endUs: 500 }, timestamp: 0,
      };
      useNavHistoryStore.getState().push(wide);
      useNavHistoryStore.getState().push(narrow);
      // back(): wide → animate (10× wider than current 100µs). We're not asserting back here —
      // we only care that forward() across the same boundary also animates.
      useProfilerViewStore.getState().commitViewRange({ startUs: 400, endUs: 500 });
      useNavHistoryStore.getState().back();
      // After back(), the animator was called once. Forward should call it again.
      const callsAfterBack = animateCalls;
      // The fallback when no animator is registered would have committed; either way curWidth
      // is now wide enough that the next forward() crosses the threshold.
      useProfilerViewStore.getState().commitViewRange({ startUs: 0, endUs: 1000 });

      useNavHistoryStore.getState().forward();
      expect(animateCalls).toBeGreaterThan(callsAfterBack);
    } finally {
      registerAnimateViewport(null);
    }
  });

  it('degenerate source viewport (curWidth ≤ 0) falls back to snap — defensive', () => {
    let animateCalls = 0;
    registerAnimateViewport(() => { animateCalls++; });
    try {
      // Source viewport is the `{0,0}` sentinel (no selection yet).
      useProfilerViewStore.getState().commitViewRange({ startUs: 0, endUs: 0 });
      const entry: NavEntry = {
        kind: 'profiler-selected', selection: null,
        viewRange: { startUs: 100, endUs: 200 }, timestamp: 0,
      };
      useNavHistoryStore.getState().push(entry);

      // Push a placeholder so back() has somewhere to land. We're asserting the path itself
      // doesn't crash / animate against a degenerate source.
      const trailing: NavEntry = {
        kind: 'profiler-selected', selection: null,
        viewRange: { startUs: 100, endUs: 200 }, timestamp: 0,
      };
      useNavHistoryStore.getState().push(trailing);
      useNavHistoryStore.getState().back();
      expect(animateCalls).toBe(0); // ratio fallback = 1, below the threshold → snap.
    } finally {
      registerAnimateViewport(null);
    }
  });
});
