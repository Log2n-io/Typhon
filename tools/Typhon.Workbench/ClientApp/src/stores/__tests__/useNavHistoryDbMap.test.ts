import { beforeEach, describe, expect, it } from 'vitest';
import { useNavHistoryStore } from '../useNavHistoryStore';
import { registerDbMapCameraRestore } from '@/shell/commands/openDbMap';
import type { Camera } from '@/libs/dbmap/camera';

// Covers the Database File Map nav-history integration (Module 15, A4 — §13 A4 AC2): a `dbmap-navigated`
// entry round-trips through back / forward and restores the recorded camera.

describe('useNavHistoryStore — dbmap-navigated', () => {
  beforeEach(() => useNavHistoryStore.getState().clear());

  it('back / forward restores the recorded map camera', () => {
    const restored: Camera[] = [];
    registerDbMapCameraRestore((c) => restored.push(c));

    const camA: Camera = { scale: 1, x: 0, y: 0 };
    const camB: Camera = { scale: 8, x: -100, y: -50 };
    const push = useNavHistoryStore.getState().push;
    push({ kind: 'dbmap-navigated', camera: camA, label: 'A', timestamp: 1 });
    push({ kind: 'dbmap-navigated', camera: camB, label: 'B', timestamp: 2 });

    useNavHistoryStore.getState().back();
    expect(restored.at(-1)).toEqual(camA);

    useNavHistoryStore.getState().forward();
    expect(restored.at(-1)).toEqual(camB);

    registerDbMapCameraRestore(null);
  });

  it('a reveal fly coalesces into the just-opened File Map entry (one Back stop, not two)', () => {
    const nav = useNavHistoryStore.getState();
    // A reveal: open the File Map (panel-opened) then fly to the target — must be ONE Back stop.
    nav.recordViewTransition('dbmap', { type: 'component', ref: 'CompA', touchedAt: 1 });
    expect(useNavHistoryStore.getState().entries).toHaveLength(1);
    nav.recordDbMapNav({ scale: 4, x: -10, y: -20 }, 'Component CompA', 'dbmap');
    const entries = useNavHistoryStore.getState().entries;
    expect(entries).toHaveLength(1); // replaced the panel-opened in place, not appended
    expect(entries[0]).toMatchObject({ kind: 'dbmap-navigated', panelId: 'dbmap', label: 'Component CompA' });
  });

  it('a fly while already in the File Map appends (preserves the map’s retraceable camera history)', () => {
    const nav = useNavHistoryStore.getState();
    nav.recordDbMapNav({ scale: 1, x: 0, y: 0 }, 'A', 'dbmap'); // no panel-opened on top → append
    nav.recordDbMapNav({ scale: 8, x: -5, y: -5 }, 'B', 'dbmap'); // top is dbmap-navigated → append
    expect(useNavHistoryStore.getState().entries).toHaveLength(2);
  });
});
