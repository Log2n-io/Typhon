import { useEffect, useRef, useState } from 'react';
import { missionTitle } from '../data/missions';
import { clampView, fitView, panBy, reliefPixels, toCanvas, toPlanet, zoomAt, type MapView } from '../data/planet-map';
import { CITIES, PLANET_HALF_EXTENT_M } from '../data/world-data';
import { useMap } from '../state/map-store';
import { useMissions } from '../state/mission-store';
import { useTerrain } from '../state/terrain-store';
import type { TerrainBaked } from '../terrain/terrain.worker';
import { formatDistance } from './NavPane';
import { WindowFrame } from './WindowFrame';
import { useWaypoints } from '../state/waypoints-store';
import { useHorde } from '../state/horde-store';
import { RALLY_COLOR } from '../data/horde';
import { colorOf, cssColor, GROUP_COLORS, NAMED_COLORS, WAYPOINT_NAME_MAX_BYTES } from '../data/waypoints';

/** How long a press on the map is held before it offers a waypoint there, ms (client.md C25: a long press opens what something offers). */
const LONG_PRESS_MS = 500;

/** How far a press may move and still be a press rather than a drag, CSS pixels. */
const PRESS_SLOP_PX = 6;

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

/** The map's square side on a first opening, CSS pixels: most of the smaller viewport side, and never too small to read. */
function mapSize(vw: number, vh: number): number {
  return Math.max(320, Math.floor(Math.min(vw * 0.8, vh * 0.8 - 60)));
}

/** What the window adds round the map: its padding across, its title bar and foot down. */
const MAP_CHROME_W = 18;
const MAP_CHROME_H = 76;

/**
 * The planet map (Loïc, 2026-11-02), toggled by the toolbar's Map button and by <kbd>M</kbd>, closed by Escape too: the planet's relief,
 * its cities by name, the missions held on their lairs, and the player where it stands, its arrow the way the camera looks. A game window
 * (client.md C36): the map is the largest square its body holds, so resizing the window resizes the map. The wheel zooms about the cursor,
 * a drag pans; the buttons zoom about the centre, find the player, or show the whole planet again.
 */
export function PlanetMap() {
  const open = useMap((s) => s.open);
  return open ? <PlanetMapWindow /> : null;
}

function PlanetMapWindow() {
  const close = useMap((s) => s.close);
  const player = useMap((s) => s.player);
  const journal = useMissions((s) => s.journal);
  const waypoints = useWaypoints((s) => s.list);
  const horde = useHorde((s) => s.status);
  const radar = useHorde((s) => s.radar);
  const focus = useMap((s) => s.focus);
  // A long press on the map: the place a waypoint is offered at, and the form's name and colour (C38).
  const [pending, setPending] = useState<{ readonly x: number; readonly z: number } | null>(null);
  const [pendingName, setPendingName] = useState('');
  const [pendingColor, setPendingColor] = useState(0);
  const press = useRef<{
    readonly x: number;
    readonly y: number;
    readonly timer: ReturnType<typeof setTimeout>;
  } | null>(null);
  const baked = useTerrain((s) => s.baked);
  const half = PLANET_HALF_EXTENT_M;
  const [size, setSize] = useState(() => mapSize(window.innerWidth, window.innerHeight));
  const [view, setView] = useState<MapView>(() => fitView(mapSize(window.innerWidth, window.innerHeight), half));
  const [cursor, setCursor] = useState<{ readonly x: number; readonly z: number } | null>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const drag = useRef<{ readonly x: number; readonly y: number } | null>(null);
  const holder = useRef<HTMLDivElement>(null);

  // The window resized: the map is the largest square its body holds, the same planet point centred, the zoom never past the whole planet.
  useEffect(() => {
    const el = holder.current;
    if (el === null) {
      return undefined;
    }

    const observer = new ResizeObserver(() => {
      const next = Math.max(160, Math.floor(Math.min(el.clientWidth, el.clientHeight)));
      setSize((was) => {
        if (was !== next) {
          setView((v) => clampView({ ...v, pxPerM: Math.max(v.pxPerM, fitView(next, half).pxPerM) }, next, half));
        }

        return next;
      });
    });
    observer.observe(el);
    return () => {
      observer.disconnect();
    };
  }, [half]);

  // "Show on map": centred on the place asked for, at least close enough to read a city by — once an ask, adjusted while rendering (the ask is a
  // prop of sorts), so a resize keeps whatever the player panned to since.
  const [shownFocus, setShownFocus] = useState(0);
  if (focus !== null && focus.seq !== shownFocus) {
    setShownFocus(focus.seq);
    setView((v) => clampView({ cx: focus.x, cz: focus.z, pxPerM: Math.max(v.pxPerM, 0.08) }, size, half));
  }

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
      ctx.fillStyle = GROUP_COLORS.mission;
      ctx.fill();
      ctx.strokeStyle = '#2a0b08';
      ctx.lineWidth = 1.5;
      ctx.stroke();
      label(ctx, missionTitle(mission.name), at.px, at.py - 10, '#ffd2a8', '12px system-ui, sans-serif');
    }

    // A horde under way or announced (horde.md § 6): its fortress, and the rally point it marches on — for everyone.
    if (horde !== null && (horde.phase === 'incoming' || horde.phase === 'active')) {
      const fort = toCanvas(view, size, horde.fortX, horde.fortZ);
      ctx.fillStyle = cssColor(RALLY_COLOR);
      ctx.fillRect(fort.px - 6, fort.py - 6, 12, 12);
      ctx.strokeStyle = '#2a0b08';
      ctx.lineWidth = 1.5;
      ctx.strokeRect(fort.px - 6, fort.py - 6, 12, 12);
      label(ctx, 'Raider fortress', fort.px, fort.py - 9, '#ffb4a0', 'bold 12px system-ui, sans-serif');
      const rally = toCanvas(view, size, horde.x, horde.z);
      ctx.beginPath();
      ctx.arc(rally.px, rally.py, 8, 0, Math.PI * 2);
      ctx.strokeStyle = cssColor(RALLY_COLOR);
      ctx.lineWidth = 3;
      ctx.stroke();
      label(ctx, 'Rally point', rally.px, rally.py - 11, '#ffb4a0', 'bold 12px system-ui, sans-serif');

      // The town hall the raiders break (horde.md § 10), and the radar: where they are, a blob a cluster sized by its count.
      if ((horde.hallX ?? 0) !== 0 || (horde.hallZ ?? 0) !== 0) {
        const hall = toCanvas(view, size, horde.hallX ?? 0, horde.hallZ ?? 0);
        ctx.fillStyle = '#ffd54f';
        ctx.fillRect(hall.px - 5, hall.py - 5, 10, 10);
        label(ctx, 'Town hall', hall.px, hall.py - 8, '#ffe8a0', 'bold 12px system-ui, sans-serif');
      }

      if (radar !== null && radar.event === horde.event) {
        for (const b of radar.blips) {
          const at = toCanvas(view, size, b.x, b.z);
          ctx.beginPath();
          ctx.arc(at.px, at.py, 4 + Math.min(14, Math.sqrt(b.n) * 1.6), 0, Math.PI * 2);
          ctx.fillStyle = 'rgba(224, 64, 42, 0.45)';
          ctx.fill();
          label(ctx, String(b.n), at.px, at.py + 4, '#ffd0c4', '11px system-ui, sans-serif');
        }
      }
    }

    // The player's own waypoints (C38), each in its colour; the active one ringed.
    for (const w of waypoints ?? []) {
      const at = toCanvas(view, size, w.x, w.z);
      const colour = colorOf(w);
      ctx.beginPath();
      ctx.arc(at.px, at.py, 5, 0, Math.PI * 2);
      ctx.fillStyle = colour;
      ctx.fill();
      ctx.strokeStyle = 'rgba(0, 0, 0, 0.8)';
      ctx.lineWidth = 1.5;
      ctx.stroke();
      if (w.active) {
        ctx.beginPath();
        ctx.arc(at.px, at.py, 10, 0, Math.PI * 2);
        ctx.strokeStyle = colour;
        ctx.lineWidth = 2;
        ctx.stroke();
      }

      label(ctx, w.name, at.px, at.py - 8, colour, '12px system-ui, sans-serif');
    }

    if (pending !== null) {
      const at = toCanvas(view, size, pending.x, pending.z);
      ctx.beginPath();
      ctx.arc(at.px, at.py, 7, 0, Math.PI * 2);
      ctx.strokeStyle = '#ffffff';
      ctx.setLineDash([3, 3]);
      ctx.lineWidth = 2;
      ctx.stroke();
      ctx.setLineDash([]);
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
  }, [view, size, half, baked, journal, player, waypoints, horde, radar, pending]);

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
    <WindowFrame
      id="map"
      title="Tatooine"
      extra={
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
        </span>
      }
      onClose={close}
      ariaLabel="Planet map"
      className="planet-map"
      minW={240}
      minH={240}
      initial={(vw, vh) => {
        const side = mapSize(vw, vh);
        return {
          x: Math.round((vw - side - MAP_CHROME_W) / 2),
          y: Math.round((vh - side - MAP_CHROME_H) / 2),
          w: side + MAP_CHROME_W,
          h: side + MAP_CHROME_H,
        };
      }}
    >
      <div className="planet-map-holder" ref={holder}>
        <canvas
          ref={canvasRef}
          className="planet-map-canvas"
          style={{ width: size, height: size }}
          onPointerDown={(e) => {
            e.currentTarget.setPointerCapture(e.pointerId);
            drag.current = { x: e.clientX, y: e.clientY };
            // Held without moving: a waypoint offered there (C25, C38).
            const r = e.currentTarget.getBoundingClientRect();
            const at = toPlanet(view, size, e.clientX - r.left, e.clientY - r.top);
            if (press.current !== null) {
              clearTimeout(press.current.timer);
            }

            press.current = {
              x: e.clientX,
              y: e.clientY,
              timer: setTimeout(() => {
                press.current = null;
                drag.current = null;
                setPending(at);
                setPendingName('');
              }, LONG_PRESS_MS),
            };
          }}
          onPointerMove={(e) => {
            const r = e.currentTarget.getBoundingClientRect();
            setCursor(toPlanet(view, size, e.clientX - r.left, e.clientY - r.top));
            const held = press.current;
            if (held !== null && Math.hypot(e.clientX - held.x, e.clientY - held.y) > PRESS_SLOP_PX) {
              clearTimeout(held.timer);
              press.current = null;
            }

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
            if (press.current !== null) {
              clearTimeout(press.current.timer);
              press.current = null;
            }
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
      </div>
      {pending !== null ? (
        <form
          className="planet-map-way"
          onSubmit={(e) => {
            e.preventDefault();
            const name = pendingName.trim();
            if (name.length > 0 && useWaypoints.getState().create(name, pending.x, pending.z, pendingColor)) {
              setPending(null);
            }
          }}
        >
          <span>
            Waypoint at {Math.round(pending.x)}, {Math.round(pending.z)}
          </span>
          <input
            autoFocus
            value={pendingName}
            maxLength={WAYPOINT_NAME_MAX_BYTES}
            placeholder="Name"
            onChange={(e) => {
              setPendingName(e.target.value);
            }}
          />
          <select
            value={pendingColor}
            onChange={(e) => {
              setPendingColor(Number(e.target.value));
            }}
            style={{ color: pendingColor === 0 ? GROUP_COLORS.custom : cssColor(pendingColor) }}
          >
            <option value={0}>yellow (yours)</option>
            {Object.entries(NAMED_COLORS).map(([name, rgb]) => (
              <option key={name} value={rgb} style={{ color: cssColor(rgb) }}>
                {name}
              </option>
            ))}
          </select>
          <button type="submit" disabled={pendingName.trim().length === 0}>
            Make
          </button>
          <button
            type="button"
            onClick={() => {
              setPending(null);
            }}
          >
            Cancel
          </button>
        </form>
      ) : null}
      <div className="planet-map-foot">
        <span>
          {cursor === null
            ? 'wheel: zoom · drag: pan · hold: waypoint'
            : `${Math.round(cursor.x)}, ${Math.round(cursor.z)}`}
          {player === null ? ' · you are not on the planet' : ''}
        </span>
        <span className="planet-map-scale">
          <span className="planet-map-scale-bar" style={{ width: scaleM * view.pxPerM }} />
          {formatDistance(scaleM)}
        </span>
      </div>
    </WindowFrame>
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
