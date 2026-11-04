import { TERRAIN_MAX_PX, TERRAIN_TARGET_FPS } from '../app/terrain-detail';
import { useSessionStore } from '../state/session-store';
import { useStats } from '../state/stats-store';
import { useUi } from '../state/ui-store';

/**
 * The settings a player has as well as the debug toolbar (client.md C36): the account and its way out, how far the server serves the player, and
 * the terrain's detail. Shown by the Options window and by the world toolbar, which is the debug layer's.
 */

/** Who this tab is logged in as, and the way out (A8). Nothing for the god camera, which is anonymous. */
export function AccountSection() {
  // Narrowly selected: the session store changes on a login and a kick and nothing else.
  const accountName = useSessionStore((s) => s.grant?.name ?? null);
  const logOut = useSessionStore((s) => s.logOut);
  if (accountName === null) {
    return null;
  }

  return (
    <section>
      <h3>Account</h3>
      <div className="session-row">
        <span className="session-name">{accountName}</span>
        <button type="button" onClick={logOut}>
          Log out
        </button>
      </div>
    </section>
  );
}

/** A player's served radius: the server's sphere round the avatar, widened in place (client.md C15). */
export function PlayerRadius() {
  const ui = useUi();
  const range = ui.playerRadiusRange;
  return range !== null ? (
    <label className="slider">
      Served radius {ui.playerRadius ?? range.def} m
      <input
        type="range"
        // Both ends are the server's (`/typhon/demo.json`): its profile's own radius, and the ceiling the
        // replication grid's window bound allows. Neither is a figure this client invents.
        min={range.min}
        max={range.max}
        // A step the range chooses rather than a constant: the ceiling is five replication cells, so it moved from
        // 320 m to 1 280 m when the demo matched its replication cell to its spatial one, and 16 m steps over a
        // 1 km range is 68 drag positions of pointless precision. A 64th of the range, rounded to 8 m.
        step={Math.max(8, Math.round((range.max - range.min) / 64 / 8) * 8)}
        value={ui.playerRadius ?? range.def}
        onChange={(e) => {
          ui.setPlayerRadius(Number(e.target.value));
        }}
      />
    </label>
  ) : (
    <div className="hint">This server does not say what view radius a player may ask for, so the slider is off.</div>
  );
}

/** The terrain's detail: the tolerance it is drawn at, and the viewer's floor under the frame-rate governor (client.md C26). */
export function TerrainSection() {
  const ui = useUi();
  // Narrowly selected: these change only when the tolerance or the viewport does, not four times a second.
  const terrainFinestM = useStats((s) => s.stats?.terrainFinestM);
  const terrainNodes = useStats((s) => s.stats?.terrainNodes);
  const terrainTriangles = useStats((s) => s.stats?.terrainTriangles);
  const terrainCapped = useStats((s) => s.stats?.terrainCapped);
  const terrainAutoPx = useStats((s) => s.stats?.terrainAutoPx ?? null);
  const terrainCappedFps = useStats((s) => s.stats?.terrainCappedFps ?? null);
  return (
    <section>
      <h3>Terrain</h3>
      <label className="slider">
        {/* The slider shows the tolerance the terrain is drawn at — the governor's when it has coarsened above the
              viewer's setting — and dragging it sets the viewer's setting, which resets the governor to it. */}
        Detail {(terrainAutoPx ?? ui.terrainPixelError).toFixed(1)} px error
        {terrainAutoPx !== null
          ? ` (auto: under ${String(TERRAIN_TARGET_FPS)} fps; your setting ${ui.terrainPixelError.toFixed(1)})`
          : terrainCappedFps !== null
            ? ` (kept: the frame rate is capped at ${terrainCappedFps.toFixed(0)} fps, which coarser ground does not raise)`
            : ''}
        <input
          type="range"
          min={0.25}
          max={TERRAIN_MAX_PX}
          step={0.25}
          value={terrainAutoPx ?? ui.terrainPixelError}
          onChange={(e) => {
            ui.setTerrainPixelError(Number(e.target.value));
          }}
        />
      </label>
      <div className="hint">
        currently {terrainNodes ?? 0} nodes down to <code>{terrainFinestM ?? '—'} m</code>, over{' '}
        {(terrainTriangles ?? 0).toLocaleString()} tri
        {terrainCapped === true ? ' ⚠ node budget binding — finer settings add no detail' : ''}
      </div>
    </section>
  );
}
