import { defaultDistanceFor, type RealmView } from '../data/realm-view';
import { cameraBoundsFor, type CameraBounds } from '../render/scene-profile';
import { useChat } from '../state/chat-store';
import { useUi } from '../state/ui-store';

/**
 * The parts of the app a realm switch has to reach that are not stores.
 *
 * Declared as an interface for the reason `frame-policy.ts` exists: `ClientApp` needs a canvas, a WebGL context, a
 * `ResizeObserver`, `matchMedia` and two Workers, so nothing that lives inside it can be asserted. The decision of
 * WHAT a switch invalidates is the part worth pinning, and this is what it takes to state it without a GPU.
 */
export interface RealmSwitchTargets {
  /** Takes the camera's limits from the realm, and pulls it inside them. Applied BEFORE the jump. */
  setCameraBounds(bounds: CameraBounds): void;
  /** Puts the map camera at a point, without smoothing. */
  jumpCamera(x: number, z: number, distanceM: number): void;
  /**
   * Drops the app's own per-selection caches — the selection label's key — and marks the eye camera's subject as
   * REBASED rather than gone: the netIds changed under it, the body did not.
   */
  forgetSelectionCaches(): void;
  /**
   * Discards where the viewer is looking, so the eye camera re-aims at its subject's own facing.
   *
   * Only a crossing does this. Inside one realm the aim stays valid through any number of resets.
   */
  forgetEyeAim(): void;
  /** Drops the app's record of the region it last sent, which is in the old realm's coordinate frame. */
  forgetRegion(): void;
  /** Drops the app's record of the view radius it last asked for, which the new session was never told. */
  forgetViewRadius(): void;
}

/**
 * Everything that stops being true when the store is emptied under the app, whatever emptied it.
 *
 * <b>A `RESET` empties the store and nothing else.</b> The SDK clears the netId map and every archetype and relays the
 * aggregate grids; every piece of state the app keeps ALONGSIDE the store survives, still describing entities that no
 * longer exist. `ClientApp.adopt` faces the identical hazard when the store OBJECT is replaced, and its comment states
 * it exactly — netIds are dense and reused, so one held across the swap very often resolves in the new store to a
 * different entity. A `RESET` keeps the same store object and empties it, so `adopt` never runs, and every hazard it
 * guards against is live.
 *
 * <b>A realm change is only one of the ways it arrives.</b> A profile switch — which is what spectating an entity is —
 * resets the store in the realm the session is already in, so `onRealmChanged` never fires and the crossing path never
 * runs. That is the same defect one door further along, which is why this is the whole of what a reset invalidates and
 * {@link resetForRealm} is it plus the camera.
 *
 * <b>What is kept is as considered as what is dropped.</b> The view radius, the layer and heatmap toggles, the grid,
 * label and debug switches, the terrain tolerance and the pause are the viewer's preferences, not facts about a
 * world. Re-deriving them at every door would make the client feel as though it had crashed and restarted.
 */
export function resetForSession(targets: RealmSwitchTargets): void {
  // netIds are dense and reused, so the id that named a creature before the reset names an unrelated one after it with
  // near-certainty rather than by chance. `select(0)` also drops `follow` and returns the camera to god mode.
  useUi.getState().select(0);

  // The app's own half of the selection, which `select(0)` cannot reach. Left set, the next entry into eye mode reads
  // as "same subject" and GLIDES from wherever the old entity happened to be.
  targets.forgetSelectionCaches();

  // The SDK's `RegionSender` forgets its half on `realmChanged`; this half was left holding "already sent this",
  // which suppresses the first region after the reset until the camera happens to move.
  targets.forgetRegion();

  // The same hazard for the radius, and it had the same bug: `sendViewRadius` sends only on CHANGE, so a slider left at
  // 1 280 m from the previous session matched what the app believed it had already asked for, and nothing was sent — while
  // the new session was being served its profile's own 192 m. The slider then read 1 280 and the HUD read 192, which is
  // #1075's failure (a control that lies about what the server is doing) reached by a different route.
  //
  // Not the slider's VALUE: that is the person's standing wish and survives deliberately (`setPlayerRadiusRange` keeps it).
  // What cannot survive is the app's memory of having asked.
  targets.forgetViewRadius();

  // Chat is fifty metres of earshot, routed within one realm. Carrying a shop's conversation onto another planet is
  // the visible form of this whole class of bug.
  useChat.getState().clear();
}

/**
 * A reset that also moved the session to another realm: {@link resetForSession}, and then the camera.
 *
 * The camera is the half that is specific to a crossing. A reset inside one realm leaves every coordinate it holds
 * valid, so moving it would be a jump the viewer did not ask for; a crossing invalidates all of them at once.
 *
 * @param next The realm arrived in, or `null` when the session was left in none.
 */
export function resetForRealm(next: RealmView | null, targets: RealmSwitchTargets): void {
  resetForSession(targets);

  if (next === null) {
    return;
  }

  // The aim as well as the coordinates: arriving somewhere else facing the way you faced in the last realm is the same
  // class of nonsense as arriving at the old realm's coordinates.
  targets.forgetEyeAim();

  // Bounds BEFORE the jump, and the order is load-bearing: the jump's own clamp is the new realm's only if the new
  // realm's limits are already in place. The other way round, arriving in an interior would clamp the destination
  // against the planet — which admits it — and then the first pan would snap the camera somewhere else entirely.
  targets.setCameraBounds(cameraBoundsFor(next));

  // A JUMP, never a glide. The camera holds the old realm's coordinates: eight kilometres of planet inside a 64 m
  // room, or the middle of a room inside a planet. A glide would fly the camera across the distance between two
  // coordinate systems that have no relationship to each other, which is a long trip through nothing.
  targets.jumpCamera(next.centreX, next.centreZ, defaultDistanceFor(next));
}
