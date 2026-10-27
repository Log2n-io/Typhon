/**
 * "Watch this bot" (bot-telemetry.md § 7): the dashboard's bots page names a traced bot, and the god camera goes to it and
 * follows it.
 *
 * <b>The game server takes no part.</b> The dashboard learns the bot's body from the bot's own report — its wire netId, its
 * realm and where it stands — and hands that to this page, first in the URL it opens (`?as=god&follow=<netId>&realm=…`),
 * then by `postMessage` to the same window. Everything after that is what a person at the god camera could do by hand:
 * pick the realm, fly to the spot, click the body, press follow. NetIds are global to the database
 * (`NetIdAllocator`), so the number the bot's session was told names the same body to this one.
 *
 * Pure, so the parsing, the origin rule and the step machine are asserted without a canvas (`watch-link.test.ts`).
 */

/** The `data.kind` of a follow message. */
export const WATCH_MESSAGE_KIND = 'swg-follow';

/** How far the god camera stands from the bot's last known position while it waits for the body to be served, metres. */
export const WATCH_APPROACH_DISTANCE_M = 120;

/** How long a watch waits for the realm and then the body before it gives up, milliseconds. */
export const WATCH_TIMEOUT_MS = 20_000;

/** A bot to watch, as the dashboard names it. */
export interface WatchTarget {
  /** The body's wire netId; never 0. */
  readonly netId: number;
  /** The realm it is in: 0 the planet. */
  readonly realm: number;
  /** Where it was last reported, in its realm's frame, or `null` when the dashboard did not say. */
  readonly x: number | null;
  readonly z: number | null;
  /** The bot's index in its fleet, for the label, or `null`. */
  readonly bot: number | null;
}

/**
 * The watch the page was opened with, or `null`.
 *
 * @param search `window.location.search`.
 */
export function watchFromUrl(search: string): WatchTarget | null {
  const p = new URLSearchParams(search);
  return targetOf(p.get('follow'), p.get('realm'), p.get('x'), p.get('z'), p.get('bot'));
}

/**
 * A follow message's target, or `null` for anything that is not one.
 *
 * @param data `MessageEvent.data`, which any page may send.
 */
export function watchFromMessage(data: unknown): WatchTarget | null {
  if (typeof data !== 'object' || data === null) {
    return null;
  }

  const m = data as Record<string, unknown>;
  if (m.kind !== WATCH_MESSAGE_KIND) {
    return null;
  }

  return targetOf(m.netId, m.realm, m.x, m.z, m.bot);
}

/**
 * Whether a follow message from `origin` is obeyed.
 *
 * @param origin `MessageEvent.origin`.
 * @param pageHostname `location.hostname` of this page.
 * @param watchFrom The `?watchFrom=` origin, or `null`.
 *
 * @remarks
 * <b>The same host on any port, because that is what the demo is.</b> The dashboard runs beside the game server on another
 * port of the same box, and is opened by the same name the client was. Anything else must be named in the URL.
 */
export function watchOriginAllowed(origin: string, pageHostname: string, watchFrom: string | null): boolean {
  const from = urlOf(origin);
  if (from === null) {
    return false;
  }

  if (from.hostname === pageHostname) {
    return true;
  }

  const allowed = watchFrom === null ? null : urlOf(watchFrom);
  return allowed !== null && allowed.origin === from.origin;
}

/** The `?watchFrom=` origin, or `null`. */
export function watchFromParam(search: string): string | null {
  return new URLSearchParams(search).get('watchFrom');
}

/** Where a watch is: getting into the bot's realm, getting the camera over it, waiting for its body, on it, or given up. */
export type WatchPhase = 'crossing' | 'approaching' | 'acquiring' | 'following' | 'lost';

export interface WatchState {
  readonly target: WatchTarget;
  readonly phase: WatchPhase;
  readonly startedMs: number;
  /** Whether the realm has been asked for, so it is asked once. */
  readonly asked: boolean;
}

/** What the app knows this frame. */
export interface WatchView {
  /** The realm on screen, or `null` before the first `REALM` block. */
  readonly realmId: number | null;
  readonly canViewRealm: boolean;
  /** Whether the target's netId is in the world the session holds. */
  readonly found: boolean;
}

/** What the app does for a step: one store action, or nothing. */
export type WatchAction =
  | { readonly kind: 'none' }
  | { readonly kind: 'viewRealm'; readonly realm: number }
  | { readonly kind: 'flyTo'; readonly x: number; readonly z: number }
  | { readonly kind: 'follow'; readonly netId: number };

const NONE: WatchAction = { kind: 'none' };

/** A new watch. */
export function beginWatch(target: WatchTarget, nowMs: number): WatchState {
  return { target, phase: 'crossing', startedMs: nowMs, asked: false };
}

/**
 * One frame of a watch.
 *
 * @remarks
 * <b>The body is selected only once it is in the world.</b> The app drops a selection whose netId it cannot find on the
 * very next frame, so selecting ahead of the server would be undone before the body arrived. Until then the camera is
 * flown to where the dashboard last saw the bot, which is what makes the server start serving that area at all.
 */
export function stepWatch(
  state: WatchState,
  view: WatchView,
  nowMs: number,
): { state: WatchState; action: WatchAction } {
  if (state.phase === 'following' || state.phase === 'lost') {
    return { state, action: NONE };
  }

  if (nowMs - state.startedMs > WATCH_TIMEOUT_MS) {
    return { state: { ...state, phase: 'lost' }, action: NONE };
  }

  const t = state.target;
  if (state.phase === 'crossing') {
    if (view.realmId === null) {
      return { state, action: NONE };
    }

    if (view.realmId !== t.realm) {
      if (state.asked) {
        return { state, action: NONE };
      }

      return view.canViewRealm
        ? { state: { ...state, asked: true }, action: { kind: 'viewRealm', realm: t.realm } }
        : { state: { ...state, phase: 'lost' }, action: NONE };
    }

    state = { ...state, phase: 'approaching' };
  }

  if (view.found) {
    return { state: { ...state, phase: 'following' }, action: { kind: 'follow', netId: t.netId } };
  }

  if (state.phase === 'approaching') {
    return {
      state: { ...state, phase: 'acquiring' },
      action: t.x === null || t.z === null ? NONE : { kind: 'flyTo', x: t.x, z: t.z },
    };
  }

  return { state, action: NONE };
}

/** The store actions a step drives: `useUi`'s, narrowed so a test can hand in the real store. */
export interface WatchActions {
  viewRealm(realmId: number): void;
  flyTo(x: number, z: number, distance: number): void;
  followWatched(netId: number): void;
}

/** Carries out a step's action. */
export function applyWatchAction(action: WatchAction, ui: WatchActions): void {
  switch (action.kind) {
    case 'viewRealm':
      ui.viewRealm(action.realm);
      break;
    case 'flyTo':
      ui.flyTo(action.x, action.z, WATCH_APPROACH_DISTANCE_M);
      break;
    case 'follow':
      ui.followWatched(action.netId);
      break;
    case 'none':
      break;
  }
}

/** The label on the god view, or `null` when there is nothing to say. */
export function watchLabel(state: WatchState | null, selectedNetId: number): string | null {
  if (state === null) {
    return null;
  }

  const t = state.target;
  const who = t.bot === null ? `#${t.netId}` : `bot #${t.bot} (netId ${t.netId})`;
  switch (state.phase) {
    case 'crossing':
      return `Watching ${who} — going to realm ${t.realm}…`;
    case 'approaching':
    case 'acquiring':
      return `Watching ${who} — looking for it…`;
    case 'following':
      // Once the viewer picks something else, or the body leaves the view, it is no longer being watched.
      return selectedNetId === t.netId ? `Watching ${who}` : null;
    case 'lost':
      return `Lost ${who}: not found in realm ${t.realm} — watch it again from the dashboard`;
  }
}

function targetOf(netId: unknown, realm: unknown, x: unknown, z: unknown, bot: unknown): WatchTarget | null {
  const id = integerOf(netId);
  if (id === null || id <= 0) {
    return null;
  }

  const r = integerOf(realm) ?? 0;
  const px = numberOf(x);
  const pz = numberOf(z);
  const index = integerOf(bot);
  return {
    netId: id,
    realm: r < 0 ? 0 : r,
    x: px === null || pz === null ? null : px,
    z: px === null || pz === null ? null : pz,
    bot: index === null || index < 0 ? null : index,
  };
}

/** A finite number from a message field or a URL parameter, or `null`. */
function numberOf(v: unknown): number | null {
  if (typeof v === 'number') {
    return Number.isFinite(v) ? v : null;
  }

  if (typeof v === 'string' && v.trim() !== '') {
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }

  return null;
}

function integerOf(v: unknown): number | null {
  const n = numberOf(v);
  return n !== null && Number.isSafeInteger(n) ? n : null;
}

function urlOf(s: string): URL | null {
  try {
    return new URL(s);
  } catch {
    return null;
  }
}
