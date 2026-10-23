import { DEFAULT_ARCHETYPE_INFOS } from '../data/archetypes';
import { RealmPanel } from './RealmPanel';
import { CITIES, POIS } from '../data/world-data';
import { useSessionStore } from '../state/session-store';
import { useStats } from '../state/stats-store';
import { useUi, type Population } from '../state/ui-store';
import { sourceKindFrom } from './Viewport';

const POPULATIONS: Population[] = [1, 4, 16];

/**
 * Which source this page asked for, read once. A `URLSearchParams` per render is wasteful, and the answer cannot change
 * without a reload.
 */
const LIVE = sourceKindFrom(window.location.search) === 'live';

/**
 * World, view and layer controls.
 *
 * The layer and heatmap buttons come from the SOURCE's schema, published with the frame stats: a live catalog decides
 * which archetypes exist, in which order, and which of them the far tier counts. Before the first frame the client's own
 * known archetypes stand in, so the panel is never empty.
 */
/**
 * What the controls do in the mode the viewer is actually in.
 *
 * <b>One line described the god camera whatever was on screen.</b> In third person W/A/S/D walk the avatar and do not pan
 * anything, the wheel pulls the camera back rather than zooming a map, and a click names a target rather than opening an
 * inspector — so the panel was telling a player the opposite of what their hands were doing.
 */
function controlHint(playing: boolean, cameraMode: 'god' | 'eye'): string {
  if (cameraMode !== 'eye') {
    return 'Drag: pan · Right-drag: orbit · Wheel: zoom · WASD/QE/RF · Click: inspect';
  }

  return playing
    ? 'WASD: walk · Shift: run · Drag: look · Wheel: pull back · Click: target'
    : 'Drag: look · Wheel: pull back · Q/E: turn · R/F: tilt · Click: inspect';
}

/** What the camera button offers, which is a different thing for a player and for a god camera. */
function cameraButtonTitle(
  playing: boolean,
  cameraMode: 'god' | 'eye',
  selectedNetId: number,
  onPlanet: boolean,
): string {
  if (playing) {
    if (cameraMode === 'eye' && !onPlanet) {
      return 'The god camera is a view of a planet: it is not available here.';
    }

    return cameraMode === 'eye'
      ? 'Leave your avatar and fly over the whole planet, served as the god camera sees it. Your character stays where it is.'
      : 'Go back behind your own character. Drag to look, wheel to pull back over its shoulder.';
  }

  return selectedNetId === 0
    ? 'Select an entity first — eye view rides one.'
    : "Stand in the world, in the selected entity's eyes. Drag to look, wheel to pull back over its shoulder. You cannot drive it: this is spectating.";
}

export function Toolbar() {
  const ui = useUi();

  // A session that can play has a sphere to widen; one that cannot is a god camera with a region. Read as locals because
  // the View section tests both and the names are what make that section readable.
  const playing = ui.playing;
  const range = ui.playerRadiusRange;

  // Narrowly selected, like the stats below: the session store changes on a login and a kick and nothing else, but the
  // toolbar re-renders often enough that subscribing to the whole store would be a needless dependency.
  const accountName = useSessionStore((s) => s.grant?.name ?? null);
  const logOut = useSessionStore((s) => s.logOut);

  // Selected NARROWLY. Stats are published four times a second and the whole snapshot changes each time, so subscribing
  // to it re-rendered the toolbar — and the radius slider under the user's thumb — at that rate. These three change only
  // when a source is adopted.
  const layers = useStats((s) => s.stats?.layers);
  const heatChannels = useStats((s) => s.stats?.heatChannels) ?? [];
  const sourceName = useStats((s) => s.stats?.source.name);
  // Whether the server has actually sent a DEBUG block: a boolean, so this does not re-render with the numbers in it.
  const canDebug = useStats((s) => (s.stats?.replication ?? null) !== null);
  const canPause = useStats((s) => s.stats?.canPause) ?? false;
  const canViewRealm = useStats((s) => s.stats?.canViewRealm) ?? false;
  // The realm, narrowly: each field on its own, so the toolbar re-renders on a crossing and not four times a second.
  const realmId = useStats((s) => s.stats?.realm?.realmId) ?? null;
  const realmScene = useStats((s) => s.stats?.realm?.scene) ?? null;
  const realmSlot = useStats((s) => s.stats?.realm?.slot) ?? 0;
  // Narrowly selected: these two change only when the tolerance or the viewport does, not four times a second.
  const terrainFinestM = useStats((s) => s.stats?.terrainFinestM);
  const terrainNodes = useStats((s) => s.stats?.terrainNodes);
  const terrainTriangles = useStats((s) => s.stats?.terrainTriangles);
  const terrainCapped = useStats((s) => s.stats?.terrainCapped);
  const archetypes = layers === undefined ? DEFAULT_ARCHETYPE_INFOS : layers.map((l) => l.archetype);
  return (
    <div className="panel toolbar">
      <div className="toolbar-title">Typhon · Live World</div>

      {/* Who this tab is, and the way out (A8). Shown only for a logged-in session: the god camera is anonymous and has
          nothing to log out of, which is the one tool that has to work on a box nobody has an account on. */}
      {accountName !== null && (
        <section>
          <h3>Account</h3>
          <div className="session-row">
            <span className="session-name">{accountName}</span>
            <button type="button" onClick={logOut}>
              Log out
            </button>
          </div>
        </section>
      )}

      <section>
        <h3>{LIVE ? `World (${sourceName ?? 'connecting…'})` : 'World (mock server)'}</h3>
        {/* The coarsest control on the panel, so it sits above the rest of it. Hidden entirely against a source that
            cannot honour it — the mock, or a catalog without the command — for the reason Pause is: a control that
            cannot work is worse than none. */}
        {canViewRealm && ui.realms !== null && (
          <>
            <div className="row">
              <select
                className="realm-select"
                title="Ask the server to put this session in another realm. Every accepted ask is a whole-realm RESET."
                value={realmId ?? ''}
                onChange={(e) => {
                  ui.viewRealm(Number(e.target.value));
                }}
              >
                {ui.realms.planets.map((planet) => (
                  <option key={planet.id} value={planet.id}>
                    Planet {planet.id}
                  </option>
                ))}
                {ui.realms.space !== null && <option value={ui.realms.space.id}>Space</option>}
                {/* The realm we are in, when it is one the directory does not list — an interior entered by number.
                    Without this the select would show a planet while the world showed a room. */}
                {realmId !== null && realmScene === 'interior' && <option value={realmId}>Interior {realmSlot}</option>}
              </select>
            </div>
            {/* Interiors are a RANGE, not a list: the shipping map has about six hundred enterable buildings per
                planet, so a menu of them would be unusable and enormous. The server publishes the rule instead and
                this applies it to the planet currently on screen. */}
            {realmScene === 'planet' && ui.realms.interiors.perPlanet > 0 && (
              <div className="row">
                <label>
                  Interior #
                  <input
                    type="number"
                    min={0}
                    max={ui.realms.interiors.perPlanet - 1}
                    defaultValue={0}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' && ui.realms !== null) {
                        // Truncated, not just clamped: `valueAsNumber` returns 3.7 for a typed 3.7, and the clamp
                        // preserves it — so a fractional realm id reached a VarUInt field on the wire. `|| 0` already
                        // handles the empty box (NaN) and the clamp handles a negative.
                        const typed = Math.trunc(e.currentTarget.valueAsNumber || 0);
                        const portal = Math.max(0, Math.min(ui.realms.interiors.perPlanet - 1, typed));
                        const target = ui.realms.interiors.first + realmSlot * ui.realms.interiors.perPlanet + portal;

                        // Asking for the realm you are already in produces no REALM block, so nothing would ever end
                        // the fade — the viewer would sit behind an opaque screen until the safety valve fired. The
                        // `<select>` is guarded by the DOM, which fires no change for an unchanged value; this is not.
                        if (target !== realmId) {
                          ui.viewRealm(target);
                        }
                      }
                    }}
                  />
                </label>
                <span className="hint">of {ui.realms.interiors.perPlanet} — press Enter</span>
              </div>
            )}
          </>
        )}
        {/* Under the two controls that CHANGE the realm, because it is what those controls are acting on. Renders
            nothing at all against a source that reports no inventory. */}
        <RealmPanel />
        <div className="row">
          {!LIVE &&
            POPULATIONS.map((p) => (
              <button
                key={p}
                className={ui.population === p ? 'active' : ''}
                onClick={() => {
                  ui.setPopulation(p);
                }}
              >
                x{p}
              </button>
            ))}
          {!LIVE && (
            <button
              onClick={() => {
                ui.setSeed((ui.seed * 1103515245 + 12345) % 2147483647);
              }}
            >
              New seed
            </button>
          )}
          {/* Shown only when it does something: against a live server the catalog has to declare `SetPaused`, and
              pausing then stops the world for EVERY session, which the title says out loud. */}
          {canPause && (
            <button
              className={ui.paused ? 'active' : ''}
              title={LIVE ? 'Stops the server simulating, for every connected client' : 'Stops the mock world'}
              onClick={() => {
                ui.setPaused(!ui.paused);
              }}
            >
              {ui.paused ? 'Resume' : 'Pause'}
            </button>
          )}
        </div>
        {LIVE ? (
          <div className="hint">
            The engine over <code>/ws</code>. Add <code>?source=mock</code> for the browser-side mock, or{' '}
            <code>?as=god</code> for the anonymous god camera — both skip the login screen, which is the point of them.
            {ui.maxViewRadius !== null && ui.maxViewRadius > 0
              ? ` This server honours a radius up to ${ui.maxViewRadius} m.`
              : ''}
          </div>
        ) : (
          <label className="slider">
            Latency {ui.latencyMs} ms ± {ui.jitterMs}
            <input
              type="range"
              min={0}
              max={300}
              step={10}
              value={ui.latencyMs}
              onChange={(e) => {
                ui.setLatency(Number(e.target.value), ui.jitterMs);
              }}
            />
          </label>
        )}
      </section>

      <section>
        <h3>View</h3>
        {/* How far the SERVER serves this session, which is the thing that decides whether a building exists at all —
            distinct from the camera's far plane, which is 40 km and never the limit. A player's session is a sphere the
            server widens in place; a god camera's is a region. Only one of the two is ever in play, so only one slider
            is shown, and the player's takes precedence because a session that can play is being played. */}
        {playing && range !== null ? (
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
        ) : playing ? (
          <div className="hint">
            This server does not say what view radius a player may ask for, so the slider is off.
          </div>
        ) : ui.maxViewRadius === 0 ? (
          <div className="hint">
            This server serves its camera the whole world, so there is no view radius to set (it was started without{' '}
            <code>--god-region</code>).
          </div>
        ) : (
          <label className="slider">
            Radius {ui.viewRadius} m
            <input
              type="range"
              min={250}
              // The server's own ceiling when it told us (`/typhon/demo.json`), because a larger request is shrunk
              // without a word and the slider would go on doing nothing. 4000 while unknown, which is the mock's case.
              max={ui.maxViewRadius ?? 4000}
              step={250}
              value={ui.viewRadius}
              onChange={(e) => {
                ui.setViewRadius(Number(e.target.value));
              }}
            />
          </label>
        )}
        {/* Places to fly the god camera to, so shown only where they do something: in the god camera, on a planet. Behind
            an avatar or in an entity's eyes the camera is not the map's, and indoors the places are not in this realm. The
            mock has no realm and is always a planet. */}
        {ui.cameraMode === 'god' && (realmScene === 'planet' || realmScene === null) && (
          <>
            <div className="row wrap">
              {CITIES.map((c) => (
                <button
                  key={c.name}
                  onClick={() => {
                    ui.flyTo(c.x, c.z, 1400);
                  }}
                >
                  {c.name}
                </button>
              ))}
            </div>
            <div className="row wrap">
              {POIS.map((p) => (
                <button
                  key={p.name}
                  onClick={() => {
                    ui.flyTo(p.x, p.z, 900);
                  }}
                >
                  {p.name}
                </button>
              ))}
              <button
                onClick={() => {
                  ui.flyTo(0, 0, 21000);
                }}
              >
                Whole planet
              </button>
            </div>
          </>
        )}
      </section>

      <section>
        <h3>Terrain</h3>
        <label className="slider">
          Detail {ui.terrainPixelError.toFixed(1)} px error
          <input
            type="range"
            min={0.25}
            max={16}
            step={0.25}
            value={ui.terrainPixelError}
            onChange={(e) => {
              ui.setTerrainPixelError(Number(e.target.value));
            }}
          />
        </label>
        <div className="hint">
          How far the ground may depart from the real height field, measured in SCREEN PIXELS. A quadtree splits only
          where a region&rsquo;s own measured error would exceed it, so a flat basin stays coarse while a cliff
          underfoot does not: currently {terrainNodes ?? 0} nodes down to <code>{terrainFinestM ?? '—'} m</code>, over{' '}
          {(terrainTriangles ?? 0).toLocaleString()} triangles.
          {terrainCapped === true
            ? ' ⚠ The node budget is binding, not the tolerance — finer settings will not add detail.'
            : ''}
        </div>
      </section>

      <section>
        <h3>Heatmap (far tier)</h3>
        <div className="row wrap">
          {heatChannels.length === 0 ? (
            <div className="hint">This session has no aggregate grid (the server needs `--god-region`).</div>
          ) : (
            heatChannels.map((channel) => (
              <button
                key={channel.name}
                className={(ui.heatmap[channel.name] ?? false) ? 'active' : ''}
                onClick={() => {
                  ui.toggleHeatmap(channel.name);
                }}
              >
                {channel.label}
              </button>
            ))
          )}
        </div>
      </section>

      <section>
        <h3>Layers</h3>
        <div className="row wrap">
          {archetypes.map((info) => (
            <button
              key={info.name}
              className={(ui.layers[info.name] ?? true) ? 'active' : ''}
              onClick={() => {
                ui.toggleLayer(info.name);
              }}
            >
              {info.label}
            </button>
          ))}
          <button
            className={ui.showAttacks ? 'active' : ''}
            onClick={() => {
              ui.setShowAttacks(!ui.showAttacks);
            }}
          >
            Attacks
          </button>
          <button
            className={ui.showGrid ? 'active' : ''}
            onClick={() => {
              ui.setShowGrid(!ui.showGrid);
            }}
          >
            Grid
          </button>
          {/* The sun's shadow map: a GPU cost with a toggle, because a 2 048 map is re-rendered over every structure the
              session holds and an impostor cannot cast at all — so what it buys depends on how near something real is. */}
          <button
            className={ui.shadows ? 'active' : ''}
            onClick={() => {
              ui.setShadows(!ui.shadows);
            }}
          >
            Shadows
          </button>
          <button
            className={ui.showLabels ? 'active' : ''}
            onClick={() => {
              ui.setShowLabels(!ui.showLabels);
            }}
          >
            Labels
          </button>
          <button
            // Highlighted only for a god camera, where it is a MODE you are in — "eye view, on the selection". For a
            // player it is a switch that names where it takes you, and a switch that is also lit reads as a state you
            // cannot tell the direction of.
            className={!ui.playing && ui.cameraMode === 'eye' ? 'active' : ''}
            // A player's eye view rides its own avatar, so a selection is not what makes it available. Testing the
            // selection alone left a logged-in player with no way back to the god camera at all. A player's god camera
            // is a view of a planet, so off one it cannot be entered (the server would refuse it).
            disabled={
              (ui.selectedNetId === 0 && !ui.playing) ||
              (ui.playing && ui.cameraMode === 'eye' && realmScene !== 'planet')
            }
            title={cameraButtonTitle(ui.playing, ui.cameraMode, ui.selectedNetId, realmScene === 'planet')}
            onClick={() => {
              ui.setCameraMode(ui.cameraMode === 'eye' ? 'god' : 'eye');
            }}
          >
            {ui.playing ? (ui.cameraMode === 'eye' ? 'God camera' : 'Third person') : 'Eye view'}
          </button>
        </div>
      </section>

      {/* Views of what the game itself is doing, rather than of the world: none of them changes anything. */}
      <section>
        <h3>Debug</h3>
        <div className="row wrap">
          <button
            className={ui.showGizmos ? 'active' : ''}
            title="Portals (blue, the arrow crosses them), spawns (green), interactions (magenta), bot approach paths (yellow) and your collision body (white), drawn from the data the game uses and through walls."
            onClick={() => {
              ui.setShowGizmos(!ui.showGizmos);
            }}
          >
            Gizmos
          </button>
          <button
            className={ui.showColliders ? 'active' : ''}
            title="The colliders the server published and your prediction is pushed out of (orange; a top too steep to stand on is redder), with the sloped top the collision reads."
            onClick={() => {
              ui.setShowColliders(!ui.showColliders);
            }}
          >
            Colliders
          </button>
          {/*
            A14. Offered only to a session that HAS a predicted avatar: for a god camera there is no prediction to contrast
            with the authority, so the button would toggle nothing — the same rule the camera-mode button follows.
          */}
          {ui.playing ? (
            <button
              className={ui.showAuthoritative ? 'active' : ''}
              title="Also draw your avatar, translucent, where the server says it is, beside where this client predicted it. The gap is the latency."
              onClick={() => {
                ui.setShowAuthoritative(!ui.showAuthoritative);
              }}
            >
              Server pos
            </button>
          ) : null}
          <button
            className={ui.showDebug ? 'active' : ''}
            disabled={!canDebug}
            title={
              canDebug
                ? "The engine's own picture of this session: the replication grid, the cells it has delivered, and the shape it is serving."
                : 'This source sends no DEBUG block — the mock, or a session the server did not grant the cap.'
            }
            onClick={() => {
              ui.setShowDebug(!ui.showDebug);
            }}
          >
            Replication
          </button>
        </div>
      </section>

      <div className="hint">{controlHint(ui.playing, ui.cameraMode)}</div>
    </div>
  );
}
