import { useEffect, useRef } from 'react';
import { ClientApp, type SourceFactory } from '../app/client-app';
import { MockSource } from '../data/mock/mock-source';
import { startInventoryPoll } from '../app/inventory-poll';
import { fetchRealmInventory } from '../data/realm-inventory';
import { fetchServerConfig } from '../data/server-config';
import { RealmFade } from './RealmFade';
import { refused } from '../app/realm-transition';
import { TyphonSource } from '../data/source';
import { useRealms } from '../state/realm-store';
import { useUi } from '../state/ui-store';

/**
 * Which world the client shows (`05-client.md` § 11, C4).
 *
 * <b>Live by default.</b> The page is normally served by `SwgTatooine --serve`, from the same origin as `/ws`, so the
 * engine is one relative URL away and showing the browser-side mock instead would be showing a simulation of the thing
 * the client exists to prove. `?source=mock` asks for the mock, which is still the only way to drive the client without
 * a server and the only source with a latency slider.
 */
export type SourceKind = 'live' | 'mock';

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

function factoryFor(kind: SourceKind): SourceFactory {
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
      kind: 'god',
      altitudeM: ui.viewRadius,
      onRealmRefused: () => {
        // The one thing that will ever say the crossing is not coming: the realm never changes, so no frame ends the
        // fade. Without this the viewer waits out the whole hold limit for an answer the server already gave.
        const state = useUi.getState();
        state.setTransition(refused(state.transition, performance.now()));
      },
      onSpectateRefused: () => {
        // A refused ride leaves nothing behind on its own: no frame arrives, so nothing takes the camera out of the
        // eye mode the ask put it in optimistically, and the viewer is left at head height on an entity the session
        // was never anchored to. The rate-limited case says nothing at all without this.
        useUi.getState().setCameraMode('god');
      },
    });
}

/** Mounts the client on a canvas. The render loop runs outside React; React only starts and stops it. */
export function Viewport() {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const overlayRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    const overlay = overlayRef.current;
    if (canvas === null || overlay === null) {
      return;
    }

    const kind = sourceKindFrom(window.location.search);
    const app = new ClientApp(canvas, overlay, factoryFor(kind));
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
          useUi.getState().setRealms(config.realms);
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
  }, []);

  return (
    <div className="viewport">
      <canvas ref={canvasRef} className="viewport-canvas" />
      <div ref={overlayRef} className="viewport-overlay" />
      <RealmFade />
    </div>
  );
}
