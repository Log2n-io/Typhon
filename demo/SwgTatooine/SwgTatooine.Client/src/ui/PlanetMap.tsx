import { useEffect, useRef, useState } from 'react';
import { missionTitle } from '../data/missions';
import { clampView, fitView, panBy, reliefPixels, toCanvas, toPlanet, zoomAt, type MapView } from '../data/planet-map';
import { CITIES, PLANET_HALF_EXTENT_M } from '../data/world-data';
import { useMap } from '../state/map-store';
import { useMissions } from '../state/mission-store';
import { useTerrain } from '../state/terrain-store';
import type { TerrainBaked } from '../terrain/terrain.worker';
import { formatDistance } from './NavPane';

/** The relief image's side, pixels: a quarter of the field's posts, sharp enough at the closest zoom the map allows over a city. */
const RELIEF_PX = 1024;

/** The relief of the planet's field, made once per field: a few tens of milliseconds, kept for every later opening. */
const reliefs = new WeakMap<TerrainBaked, HTMLCanvasElement>();

function reliefOf(baked: TerrainBaked): HTMLCanvasElement {
  let canvas = reliefs.get(baked);
  if (canvas === undefined) {
    canvas = document.createElement('canvas');
    canvas.width = RELIEF_PX;
    canvas.height = RELIEF_PX;
    const ctx = canvas.getContext('2d');
    if (ctx !== null) {
      ctx.putImageData(new ImageData(reliefPixels(baked, RELIEF_PX), RELIEF_PX, RELIEF_PX), 0, 0);
    }

    reliefs.set(baked, canvas);
  }

  return canvas;
}

/** The map's square side, CSS pixels: most of the smaller viewport side, and never too small to read. */
function mapSize(): number {
  return Math.max(320, Math.floor(Math.min(window.innerWidth * 0.8, window.innerHeight * 0.8 - 60)));
}

/**
 * The planet map (Loïc, 2026-11-02), toggled by the toolbar's Map button and by <kbd>M</kbd>, closed by Escape too: the planet's relief,
 * its cities by name, the missions held on their lairs, and the player where it stands, its arrow the way the camera looks. Over every
 * other window. The wheel zooms about the cursor, a drag pans; the buttons zoom about the centre, find the player, or show the whole
 * planet again.
 */
export function PlanetMap() {
  const open = useMap((s) => s.open);
  return open ? <PlanetMapWindow /> : null;
}

function PlanetMapWindow() {
  const close = useMap((s) => s.close);
  const player = useMap((s) => s.player);
  const journal = useMissions((s) => s.journal);
  const baked = useTerrain((s) => s.baked);
  const half = PLANET_HALF_EXTENT_M;
  const [size, setSize] = useState(mapSize);
  const [view, setView] = useState<MapView>(() => fitView(mapSize(), half));
  const [cursor, setCursor] = useState<{ readonly x: number; readonly z: number } | null>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const drag = useRef<{ readonly x: number; readonly y: number } | null>(null);

  // The viewport resized: the same planet point stays centred, the zoom never past the whole planet.
  useEffect(() => {
    const onResize = (): void => {
      const next = mapSize();
      setSize(next);
      setView((v) => clampView({ ...v, pxPerM: Math.max(v.pxPerM, fitView(next, half).pxPerM) }, next, half));
    };
    window.addEventListener('resize', onResize);
    return () => {
      window.removeEventListener('resize', onResize);
    };
  }, [half]);

  // Escape closes it, as it closes a context menu.
  useEffect(() => {
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') {
        e.preventDefault();
        close();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
    };
  }, [close]);

  // The wheel, natively and not passive: a passive listener cannot stop the page (and the 3D view under it) from taking the wheel too.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (canvas === null) {
      return undefined;
    }

    const onWheel = (e: WheelEvent): void => {
      e.preventDefault();
      const r = canvas.getBoundingClientRect();
      const factor = Math.exp(-e.deltaY * 0.0015);
      setView((v) => zoomAt(v, size, half, e.clientX - r.left, e.clientY - r.top, factor));
    };
    canvas.addEventListener('wheel', onWheel, { passive: false });
    return () => {
      canvas.removeEventListener('wheel', onWheel);
    };
  }, [size, half]);

  // Drawn whenever what it shows changes: the view, the player (a few times a second while it moves), the missions, the relief.
  useEffect(() => {
    const canvas = canvasRef.current;
    const ctx = canvas?.getContext('2d') ?? null;
    if (canvas === null || ctx === null) {
      return;
    }

    const dpr = window.devicePixelRatio || 1;
    const px = Math.round(size * dpr);
    if (canvas.width !== px || canvas.height !== px) {
      canvas.width = px;
      canvas.height = px;
    }

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = '#1b1610';
    ctx.fillRect(0, 0, size, size);

    const corner = toCanvas(view, size, -half, half);
    const side = 2 * half * view.pxPerM;
    if (baked !== null) {
      ctx.imageSmoothingEnabled = true;
      ctx.drawImage(reliefOf(baked), corner.px, corner.py, side, side);
    }

    ctx.strokeStyle = 'rgba(0, 0, 0, 0.6)';
    ctx.lineWidth = 1;
    ctx.strokeRect(corner.px, corner.py, side, side);
    grid(ctx, view, size, half);

    ctx.textAlign = 'center';
    ctx.textBaseline = 'bottom';
    for (const city of CITIES) {
      const at = toCanvas(view, size, city.x, city.z);
      const r = Math.max(4, city.radius * view.pxPerM);
      ctx.beginPath();
      ctx.arc(at.px, at.py, r, 0, Math.PI * 2);
      ctx.fillStyle = 'rgba(240, 184, 74, 0.25)';
      ctx.fill();
      ctx.strokeStyle = 'rgba(240, 184, 74, 0.9)';
      ctx.lineWidth = 1.5;
      ctx.stroke();
      label(ctx, city.name, at.px, at.py - r - 3, '#ffe2a8', 'bold 13px system-ui, sans-serif');
    }

    for (const mission of journal) {
      if (mission.lair === null) {
        continue;
      }

      const at = toCanvas(view, size, mission.lair.x, mission.lair.z);
      ctx.beginPath();
      ctx.moveTo(at.px, at.py - 7);
      ctx.lineTo(at.px + 7, at.py);
      ctx.lineTo(at.px, at.py + 7);
      ctx.lineTo(at.px - 7, at.py);
      ctx.closePath();
      ctx.fillStyle = '#ff5a4f';
      ctx.fill();
      ctx.strokeStyle = '#2a0b08';
      ctx.lineWidth = 1.5;
      ctx.stroke();
      label(ctx, missionTitle(mission.name), at.px, at.py - 10, '#ffb1aa', '12px system-ui, sans-serif');
    }

    if (player !== null) {
      const at = toCanvas(view, size, player.x, player.z);
      ctx.save();
      ctx.translate(at.px, at.py);
      ctx.rotate(player.yaw);
      ctx.beginPath();
      ctx.moveTo(0, -11);
      ctx.lineTo(7, 8);
      ctx.lineTo(0, 4);
      ctx.lineTo(-7, 8);
      ctx.closePath();
      ctx.fillStyle = '#5aaeff';
      ctx.fill();
      ctx.strokeStyle = '#06203a';
      ctx.lineWidth = 2;
      ctx.stroke();
      ctx.restore();
    }
  }, [view, size, half, baked, journal, player]);

  const centreOnPlayer = (): void => {
    if (player !== null) {
      setView((v) => clampView({ cx: player.x, cz: player.z, pxPerM: Math.max(v.pxPerM, 0.08) }, size, half));
    }
  };

  const zoomCentre = (factor: number): void => {
    setView((v) => zoomAt(v, size, half, size / 2, size / 2, factor));
  };

  const scaleM = scaleBar(view.pxPerM);
  return (
    <div className="panel planet-map" role="dialog" aria-label="Planet map">
      <div className="planet-map-head">
        <span className="planet-map-title">Tatooine</span>
        <span className="planet-map-tools">
          <button
            type="button"
            onClick={() => {
              zoomCentre(1.6);
            }}
            title="Zoom in (wheel: about the cursor)"
          >
            +
          </button>
          <button
            type="button"
            onClick={() => {
              zoomCentre(1 / 1.6);
            }}
            title="Zoom out"
          >
            −
          </button>
          <button type="button" onClick={centreOnPlayer} disabled={player === null} title="Centre the map on you">
            Me
          </button>
          <button
            type="button"
            onClick={() => {
              setView(fitView(size, half));
            }}
            title="The whole planet"
          >
            Planet
          </button>
          <button type="button" onClick={close} title="Close (M, Escape)">
            ✕
          </button>
        </span>
      </div>
      <canvas
        ref={canvasRef}
        className="planet-map-canvas"
        style={{ width: size, height: size }}
        onPointerDown={(e) => {
          e.currentTarget.setPointerCapture(e.pointerId);
          drag.current = { x: e.clientX, y: e.clientY };
        }}
        onPointerMove={(e) => {
          const r = e.currentTarget.getBoundingClientRect();
          setCursor(toPlanet(view, size, e.clientX - r.left, e.clientY - r.top));
          const from = drag.current;
          if (from !== null && e.buttons === 0) {
            drag.current = null;
          } else if (from !== null) {
            drag.current = { x: e.clientX, y: e.clientY };
            setView((v) => panBy(v, size, half, e.clientX - from.x, e.clientY - from.y));
          }
        }}
        onPointerUp={() => {
          drag.current = null;
        }}
        onPointerCancel={() => {
          drag.current = null;
        }}
        onLostPointerCapture={() => {
          drag.current = null;
        }}
        onPointerLeave={() => {
          setCursor(null);
        }}
      />
      <div className="planet-map-foot">
        <span>
          {cursor === null ? 'wheel: zoom · drag: pan' : `${Math.round(cursor.x)}, ${Math.round(cursor.z)}`}
          {player === null ? ' · you are not on the planet' : ''}
        </span>
        <span className="planet-map-scale">
          <span className="planet-map-scale-bar" style={{ width: scaleM * view.pxPerM }} />
          {formatDistance(scaleM)}
        </span>
      </div>
    </div>
  );
}

/** A label readable on any ground: a dark halo under the text. */
function label(ctx: CanvasRenderingContext2D, text: string, x: number, y: number, colour: string, font: string): void {
  ctx.font = font;
  ctx.lineWidth = 3;
  ctx.strokeStyle = 'rgba(0, 0, 0, 0.75)';
  ctx.strokeText(text, x, y);
  ctx.fillStyle = colour;
  ctx.fillText(text, x, y);
}

/** Faint lines every 2 km, or every 500 m once zoomed in enough for them to be useful. */
function grid(ctx: CanvasRenderingContext2D, view: MapView, size: number, half: number): void {
  const stepM = view.pxPerM > 0.15 ? 500 : 2000;
  ctx.strokeStyle = 'rgba(0, 0, 0, 0.12)';
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let m = -half; m <= half; m += stepM) {
    const a = toCanvas(view, size, m, -half);
    const b = toCanvas(view, size, m, half);
    ctx.moveTo(a.px, a.py);
    ctx.lineTo(b.px, b.py);
    const c = toCanvas(view, size, -half, m);
    const d = toCanvas(view, size, half, m);
    ctx.moveTo(c.px, c.py);
    ctx.lineTo(d.px, d.py);
  }

  ctx.stroke();
}

/** A round length the scale bar shows: the largest of 1-2-5 × 10ⁿ metres under 120 pixels. */
function scaleBar(pxPerM: number): number {
  const maxM = 120 / pxPerM;
  const p = 10 ** Math.floor(Math.log10(maxM));
  for (const k of [5, 2, 1]) {
    if (k * p <= maxM) {
      return k * p;
    }
  }

  return p;
}
