import { useEffect, useRef } from 'react';
import { ClientApp, type SourceFactory } from '../app/client-app';
import { MockSource } from '../data/mock/mock-source';

/** The mock server today; a Subscriptions v2 connection at M1 (`05-client.md` § 11, C4). */
const createSource: SourceFactory = (context, ui) =>
  new MockSource({
    ...context,
    seed: ui.seed,
    population: ui.population,
    latencyMs: ui.latencyMs,
    jitterMs: ui.jitterMs,
  });

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

    const app = new ClientApp(canvas, overlay, createSource);
    app.start();
    return () => {
      app.dispose();
    };
  }, []);

  return (
    <div className="viewport">
      <canvas ref={canvasRef} className="viewport-canvas" />
      <div ref={overlayRef} className="viewport-overlay" />
    </div>
  );
}
