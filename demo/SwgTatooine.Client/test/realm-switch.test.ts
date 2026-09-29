import { beforeEach, describe, expect, it } from 'vitest';
import { RealmFrame } from '@typhondb/client';
import { MapCamera } from '../src/camera/map-camera';
import { defaultDistanceFor, realmViewOf, type RealmView } from '../src/data/realm-view';
import { resetForRealm, resetForSession, type RealmSwitchTargets } from '../src/app/realm-switch';
import type { CameraBounds } from '../src/render/scene-profile';
import { useChat } from '../src/state/chat-store';
import { useUi } from '../src/state/ui-store';

/** The planet, as `TatooineSim` registers it. */
const PLANET: RealmView = realmViewOf(
  new RealmFrame(0, 1, 0, 0x00000000, 24, 64, false, [-8192, -8192, 0], [8192, 8192, 64]),
)!;

/** An interior: `(0,0)..(64,64)`, and deliberately not centred on the origin. */
const INTERIOR: RealmView = realmViewOf(
  new RealmFrame(5, 1, 1, 0x10002005, 24, 64, false, [0, 0, 0], [64, 64, 64]),
)!;

interface Spy extends RealmSwitchTargets {
  jumps: { x: number; z: number; distanceM: number }[];
  bounds: CameraBounds[];
  /** The order the calls arrived in, so a test can assert that bounds precede the jump. */
  order: string[];
  selectionCaches: number;
  regions: number;
}

function spy(): Spy {
  const calls: Spy = {
    jumps: [],
    bounds: [],
    order: [],
    selectionCaches: 0,
    regions: 0,
    setCameraBounds(bounds) {
      calls.bounds.push(bounds);
      calls.order.push('bounds');
    },
    jumpCamera(x, z, distanceM) {
      calls.jumps.push({ x, z, distanceM });
      calls.order.push('jump');
    },
    forgetSelectionCaches() {
      calls.selectionCaches++;
    },
    forgetRegion() {
      calls.regions++;
    },
  };
  return calls;
}

/** The state a session accumulates in a realm, so each case starts from a viewer who was doing something. */
function settleInARealm(): void {
  useUi.setState({ selectedNetId: 4242, follow: true, cameraMode: 'eye' });
  useChat.getState().say({ tick: 1, speaker: 77, text: 'in the cantina', atMs: 0 });
}

beforeEach(() => {
  useUi.setState({ selectedNetId: 0, follow: false, cameraMode: 'god' });
  useChat.getState().clear();
});

describe('resetForRealm', () => {
  it('drops the selection, because netIds are dense per realm and would name a stranger', () => {
    settleInARealm();
    resetForRealm(INTERIOR, spy());
    expect(useUi.getState().selectedNetId).toBe(0);
  });

  it('stops following and leaves eye mode, so the camera is not left riding a stranger', () => {
    // Asserted apart from the selection: they come from one `select(0)` today, and the day that changes these are
    // the two symptoms a viewer would actually see.
    settleInARealm();
    resetForRealm(INTERIOR, spy());
    expect(useUi.getState().follow).toBe(false);
    expect(useUi.getState().cameraMode).toBe('god');
  });

  it("clears the chat, because earshot does not reach across a realm", () => {
    settleInARealm();
    expect(useChat.getState().lines.length).toBeGreaterThan(0);
    resetForRealm(INTERIOR, spy());
    expect(useChat.getState().lines).toHaveLength(0);
  });

  it("forgets the app's own selection caches and the region it last sent", () => {
    const calls = spy();
    resetForRealm(INTERIOR, calls);
    expect(calls.selectionCaches).toBe(1);
    expect(calls.regions).toBe(1);
  });

  it('jumps the camera to the new realm — to its CENTRE, which an interior does not share with the origin', () => {
    // The assertion the whole `centre + half` shape exists for. A client that jumped to (0, 0) would put the camera
    // on the corner of the room, outside everything in it.
    const calls = spy();
    resetForRealm(INTERIOR, calls);
    expect(calls.jumps).toHaveLength(1);
    expect(calls.jumps[0].x).toBe(32);
    expect(calls.jumps[0].z).toBe(32);
  });

  it('pulls back by the realm it arrived in, not by a fixed distance', () => {
    const toPlanet = spy();
    const toInterior = spy();
    resetForRealm(PLANET, toPlanet);
    resetForRealm(INTERIOR, toInterior);
    expect(toPlanet.jumps[0].distanceM).toBeGreaterThan(toInterior.jumps[0].distanceM);
    expect(toInterior.jumps[0].distanceM).toBe(defaultDistanceFor(INTERIOR));
  });

  it('takes the camera bounds from the realm, so a 64 m room is not a 16 km box', () => {
    const calls = spy();
    resetForRealm(INTERIOR, calls);
    expect(calls.bounds).toHaveLength(1);
    expect([calls.bounds[0].minX, calls.bounds[0].maxX]).toEqual([0, 64]);
  });

  it('sets the bounds BEFORE it jumps, or the jump clamps against the realm being left', () => {
    // Order, not merely presence. The jump's own clamp is the new realm's only once the new realm's limits are in
    // place; the other way round, arriving in an interior clamps the destination against the planet — which admits it
    // — and the camera is left describing a world it is not in.
    const calls = spy();
    resetForRealm(INTERIOR, calls);
    expect(calls.order).toEqual(['bounds', 'jump']);
  });

  it('still clears everything when the session was left in NO realm, and moves no camera', () => {
    settleInARealm();
    const calls = spy();
    resetForRealm(null, calls);
    expect(useUi.getState().selectedNetId).toBe(0);
    expect(useChat.getState().lines).toHaveLength(0);
    expect(calls.regions).toBe(1);
    expect(calls.jumps).toHaveLength(0);
  });
});

describe('the camera actually lands inside the realm', () => {
  it('leaves a real MapCamera within the interior, not eight kilometres away in the old realm', () => {
    // The end-to-end form of the same claim, against the real camera rather than a spy: a switch must not leave the
    // eye holding coordinates from a world that is no longer on screen. `MapCamera` is pure float64, so this needs
    // no canvas.
    const camera = new MapCamera();
    camera.jumpTo(-6000, 7000, 18000);

    resetForRealm(INTERIOR, {
      setCameraBounds: (bounds) => {
        camera.setBounds(bounds);
      },
      jumpCamera: (x, z, distanceM) => {
        camera.jumpTo(x, z, distanceM);
      },
      forgetSelectionCaches: () => {},
      forgetRegion: () => {},
    });

    // STRICTLY inside, not merely within the bounds. `(0, 0)` is a corner of this realm and satisfies a
    // greater-than-or-equal check, so the loose form passes for a client that ignored the realm's origin entirely —
    // which is the exact bug the un-centred interior exists to catch.
    expect(camera.targetX).toBeGreaterThan(INTERIOR.minX);
    expect(camera.targetX).toBeLessThan(INTERIOR.maxX);
    expect(camera.targetZ).toBeGreaterThan(INTERIOR.minZ);
    expect(camera.targetZ).toBeLessThan(INTERIOR.maxZ);
  });
});

describe('resetForSession — a reset that did not move the session', () => {
  it('drops everything a crossing drops, because netId reuse does not need a realm change to bite', () => {
    // Spectating an entity switches the session's PROFILE. The store is emptied and refilled in the realm it was
    // already in, so `onRealmChanged` never fires — and the held netId names a stranger just as surely as it would
    // through a door.
    settleInARealm();
    const calls = spy();
    resetForSession(calls);

    const ui = useUi.getState();
    expect([ui.selectedNetId, ui.follow, ui.cameraMode]).toEqual([0, false, 'god']);
    expect([calls.selectionCaches, calls.regions, useChat.getState().lines.length]).toEqual([1, 1, 0]);
  });

  it('leaves the camera exactly where it was, because nothing it holds became invalid', () => {
    // The one thing a crossing does that this must NOT: every coordinate the camera holds is still in the realm it
    // is still in. A jump here would throw the viewer across the planet for a reason they could not see.
    settleInARealm();
    const calls = spy();
    resetForSession(calls);
    expect([calls.jumps, calls.bounds, calls.order]).toEqual([[], [], []]);
  });

  it('is idempotent, which is what lets a crossing run it twice — once for the reset, once for the realm', () => {
    settleInARealm();
    const calls = spy();
    resetForSession(calls);
    resetForRealm(PLANET, calls);

    const ui = useUi.getState();
    expect([ui.selectedNetId, useChat.getState().lines.length]).toEqual([0, 0]);
    // The second pass still did the camera work, which is the half only a crossing has.
    expect(calls.jumps).toHaveLength(1);
    expect(calls.order).toEqual(['bounds', 'jump']);
  });
});
