import { useEffect, useRef } from 'react';
import { ClientApp, type SourceFactory } from '../app/client-app';
import { MockSource } from '../data/mock/mock-source';
import { fetchServerConfig } from '../data/server-config';
import { RealmFade } from './RealmFade';
import { refused } from '../app/realm-transition';
import { TyphonSource } from '../data/source';
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
    if (kind === 'mock') {
      useUi.getState().setMaxViewRadius(MOCK_MAX_VIEW_RADIUS_M);
    } else {
      void fetchServerConfig().then((config) => {
        if (!cancelled && config !== null) {
          useUi.getState().setMaxViewRadius(config.maxViewRadiusM);
          useUi.getState().setRealms(config.realms);
        }
      });
    }

    return () => {
      cancelled = true;
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
