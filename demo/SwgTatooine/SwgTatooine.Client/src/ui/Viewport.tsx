import { useEffect, useRef } from 'react';
import { ClientApp, type SourceFactory } from '../app/client-app';
import { MockSource } from '../data/mock/mock-source';
import { startInventoryPoll } from '../app/inventory-poll';
import { fetchRealmInventory } from '../data/realm-inventory';
import { fetchServerConfig } from '../data/server-config';
import { RealmFade } from './RealmFade';
import { TerrainLoading } from './TerrainLoading';
import { refused } from '../app/realm-transition';
import { TyphonSource } from '../data/source';
import { useRealms } from '../state/realm-store';
import { useSessionStore } from '../state/session-store';
import { useTerrain } from '../state/terrain-store';
import { useUi } from '../state/ui-store';

/**
 * Which world the client shows (`05-client.md` § 11, C4).
 *
 * <b>Live by default.</b> The page is normally served by `SwgTatooine.Server --serve`, from the same origin as `/ws`, so the
 * engine is one relative URL away and showing the browser-side mock instead would be showing a simulation of the thing
 * the client exists to prove. `?source=mock` asks for the mock, which is still the only way to drive the client without
 * a server and the only source with a latency slider.
 */
export type SourceKind = 'live' | 'mock';

/**
 * Whether this run needs an account at all.
 *
 * @param search `window.location.search`.
 * @returns Whether the login screen is the only way in.
 *
 * @remarks
 * <b>Two runs legitimately have no account, and A8's gate shut both out.</b> The mock source never contacts a server, so requiring a
 * login to reach it means the one path that works with nothing running needs something running. The god camera is the diagnostic
 * tool: it is the thing you point at a box to find out what is wrong with it, which is exactly when nobody has signed up yet, and the
 * server admits it anonymously on purpose (A3). Both are explicit in the URL, so neither is a way to slip past the gate by accident
 * — a player session still has to log in, because the server refuses a `player` with no valid token.
 */
export function needsAccount(search: string): boolean {
  if (sourceKindFrom(search) === 'mock') {
    return false;
  }

  return !godRequested(search);
}

/**
 * Whether the URL asks for the anonymous god camera.
 *
 * @param search `window.location.search`.
 * @returns Whether to connect with no token at all.
 *
 * @remarks
 * <b>It has to suppress the stored grant, not merely skip the login screen.</b> The session's KIND is decided by whether a
 * token is presented, so `?as=god` in a tab that already had one connected as a `player` carrying it — and after a server
 * restart that token is signed with a key that no longer exists, so the session was refused and nothing streamed at all.
 * The one tool that has to work on a box nobody has an account on was the one asking for an account.
 */
export function godRequested(search: string): boolean {
  return new URLSearchParams(search).get('as') === 'god';
}

/** The mock's ceiling: it clamps nothing, so this is only how far the slider goes. */
const MOCK_MAX_VIEW_RADIUS_M = 4000;

/**
 * How often the realm inventory is re-fetched, milliseconds.
 *
 * The server recounts populations about once a second, so polling faster would re-read the same numbers; and the thing
 * being watched — a room falling asleep some seconds after the last person leaves — happens on that scale. It is a
 * separate request from the frame stream on purpose: this is operator data about every realm, not per-session state.
 */
const INVENTORY_POLL_MS = 1000;

export function sourceKindFrom(search: string): SourceKind {
  return new URLSearchParams(search).get('source') === 'mock' ? 'mock' : 'live';
}

/** The server's WebSocket, on the origin that served the page. Vite's dev server proxies `/ws` to it. */
export function websocketUrl(location: { protocol: string; host: string }): string {
  return `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws`;
}

/**
 * Builds the source for a run.
 *
 * @param kind `mock` or `live`.
 * @param token The bearer token A2 minted, or `null` to connect as the god camera.
 * @returns The factory the app drives.
 *
 * @remarks
 * <b>The token is what decides the session KIND, and that is the whole of A8's wire.</b> With one, the session is a
 * `player`: it is admitted against the account the token names, is given an avatar, and may send the movement intents. The
 * god camera stays reachable with no token at all — it is the one tool that has to work on a box nobody has an account on
 * — which is why this takes a nullable token rather than requiring one.
 */
function factoryFor(
  kind: SourceKind,
  token: string | null,
  onClosed: (code: number, reason: string) => void,
): SourceFactory {
  if (kind === 'mock') {
    return (context, ui) =>
      new MockSource({
        ...context,
        seed: ui.seed,
        population: ui.population,
        latencyMs: ui.latencyMs,
        jitterMs: ui.jitterMs,
      });
  }

  return (context, ui) =>
    new TyphonSource({
      url: websocketUrl(window.location),
      clock: context.clock,
      events: context.events,
      // A player when this tab is logged in, the god camera otherwise. The server refuses a `player` with no valid token
      // (A3), so these two have to move together — hence one expression rather than two options set in two places.
      kind: token === null ? 'god' : 'player',
      // Spread rather than `token: token ?? undefined`: under exactOptionalPropertyTypes an optional property may be
      // ABSENT or a string, and never present-and-undefined, so the god camera must omit the key instead of nulling it.
      ...(token === null ? {} : { token }),
      onClosed,
      altitudeM: ui.viewRadius,
      onRealmRefused: () => {
        // The one thing that will ever say the crossing is not coming: the realm never changes, so no frame ends the
        // fade. Without this the viewer waits out the whole hold limit for an answer the server already gave.
        const state = useUi.getState();
        state.setTransition(refused(state.transition, performance.now()));
      },
      onShuttleRefused: (reason) => {
        // The only thing that will ever say the flight is not happening. An accepted boarding shows up in the avatar's
        // activity and the flight in its position, but a refusal produces no frame of any kind, so without this the
        // panel would show an ask in flight until the session ended.
        useUi.getState().shuttleRefused(reason);
      },
      onSpectateRefused: () => {
        // A refused ride leaves nothing behind on its own: no frame arrives, so nothing takes the camera out of the
        // eye mode the ask put it in optimistically, and the viewer is left at head height on an entity the session
        // was never anchored to. The rate-limited case says nothing at all without this.
        useUi.getState().setCameraMode('god');
      },
      onOverviewRefused: () => {
        // Refused off a planet, or with the spectator places full: the session is still served the player's own view,
        // so a camera left up in the sky would look down on one lit disc. Back behind the avatar.
        useUi.getState().setCameraMode('eye');
      },
    });
}

/** Mounts the client on a canvas. The render loop runs outside React; React only starts and stops it. */
export function Viewport() {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const overlayRef = useRef<HTMLDivElement>(null);

  // The world does not mount until the planet is in hand.
  //
  // <b>This is what makes the avatar appear in the right place on the first frame it is drawn.</b> The field is not
  // optional data that arrives late — the camera's ground clearance, every altitude query and the ground mesh itself all
  // read it, so a session that starts without it starts wrong and corrects itself visibly seconds later. The load began at
  // module scope in `main.tsx`, so by here it is usually already finished: on a warm cache it is a database read, and on a
  // cold one it has been running since before the login form was filled in. What is left is the remainder, if any.
  const terrain = useTerrain((s) => s.baked);

  // The token the session will present. In the dependency list below, so logging in as somebody else tears the session
  // down and builds a new one rather than leaving a socket authenticated as the previous account.
  // `?as=god` means NO token, whatever is in storage: see godRequested. Read from the URL this page was opened with, which
  // cannot change without a reload, so this is a constant for the life of the component.
  const anonymous = godRequested(window.location.search);
  const token = useSessionStore((s) => (anonymous ? null : (s.grant?.token ?? null)));
  const noteClosed = useSessionStore((s) => s.noteClosed);

  useEffect(() => {
    const canvas = canvasRef.current;
    const overlay = overlayRef.current;
    // Nothing is built before the ground exists; the effect re-runs when it does.
    if (canvas === null || overlay === null || terrain === null) {
      return;
    }

    const kind = sourceKindFrom(window.location.search);
    const app = new ClientApp(canvas, overlay, factoryFor(kind, token, noteClosed), terrain);
    app.start();

    // The mock honours whatever it is asked for, so its ceiling is simply the largest the slider offers — stated, not
    // left unknown, because "unknown" makes the HUD hedge with "(asked)" about a source that never clamps anything.
    let cancelled = false;
    let poll = 0;
    let onVisible: (() => void) | null = null;
    let stopPoll: (() => void) | null = null;
    if (kind === 'mock') {
      useUi.getState().setMaxViewRadius(MOCK_MAX_VIEW_RADIUS_M);
    } else {
      void fetchServerConfig().then((config) => {
        if (!cancelled && config !== null) {
          useUi.getState().setMaxViewRadius(config.maxViewRadiusM);
          // The player sphere's range, which is a different mechanism from the god region above. Null from a server that
          // does not publish it, which leaves the panel saying so rather than offering a slider nothing honours.
          useUi.getState().setPlayerRadiusRange(
            config.player === null
              ? null
              : {
                  min: config.player.radiusM,
                  max: config.player.maxRadiusM,
                  def: config.player.defaultRadiusM,
                },
          );
          useUi.getState().setRealms(config.realms);
          // The published asset set, when this server serves one. Mounted here rather than in the constructor because it
          // is the server that says whether there is anything to mount, and that answer arrives over HTTP.
          if (config.assets !== null) {
            void app.mountAssets(config.assets);
          }

          // How wide a body is, so prediction pushes out of a wall the way the server does.
          app.setBodyHalf(config.body.halfM, config.body.worldHalfM);
          // When each port's shuttle is down, so it is drawn landing when the server boards.
          app.setShuttleSchedule(config.shuttle);
        }
      });

      const inventory = startInventoryPoll({
        read: fetchRealmInventory,
        // Null is published too: a server that stopped serving it must make the panel say so rather than leave the
        // last document on screen looking live.
        publish: (document_) => {
          useRealms.getState().setInventory(document_);
        },
        visible: () => document.visibilityState !== 'hidden',
      });

      inventory.refresh();
      poll = window.setInterval(inventory.tick, INVENTORY_POLL_MS);
      stopPoll = inventory.stop;
      onVisible = () => {
        if (document.visibilityState === 'visible') {
          inventory.refresh();
        }
      };

      document.addEventListener('visibilitychange', onVisible);
    }

    return () => {
      cancelled = true;
      if (poll !== 0) {
        window.clearInterval(poll);
      }

      if (onVisible !== null) {
        document.removeEventListener('visibilitychange', onVisible);
      }

      stopPoll?.();

      app.dispose();
    };
    // `token` and `noteClosed`: a login, a logout or a login as somebody else must tear this session down and build a
    // new one. Leaving them out would keep a socket authenticated as the previous account after the UI had moved on.
  }, [token, noteClosed, terrain]);

  return (
    <div className="viewport">
      <canvas ref={canvasRef} className="viewport-canvas" />
      <div ref={overlayRef} className="viewport-overlay" />
      {terrain === null ? <TerrainLoading /> : null}
      <RealmFade />
    </div>
  );
}
