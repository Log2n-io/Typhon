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
});
