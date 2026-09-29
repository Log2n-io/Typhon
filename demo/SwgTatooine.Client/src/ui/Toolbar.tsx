import { DEFAULT_ARCHETYPE_INFOS } from '../data/archetypes';
import { CITIES, POIS } from '../data/world-data';
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
export function Toolbar() {
  const ui = useUi();

  // Selected NARROWLY. Stats are published four times a second and the whole snapshot changes each time, so subscribing
  // to it re-rendered the toolbar — and the radius slider under the user's thumb — at that rate. These three change only
  // when a source is adopted.
  const layers = useStats((s) => s.stats?.layers);
  const heatChannels = useStats((s) => s.stats?.heatChannels) ?? [];
  const sourceName = useStats((s) => s.stats?.source.name);
  // Whether the server has actually sent a DEBUG block: a boolean, so this does not re-render with the numbers in it.
  const canDebug = useStats((s) => (s.stats?.replication ?? null) !== null);
  const canPause = useStats((s) => s.stats?.canPause) ?? false;
  // Narrowly selected: these two change only when the tolerance or the viewport does, not four times a second.
  const terrainFinestM = useStats((s) => s.stats?.terrainFinestM);
  const terrainNodes = useStats((s) => s.stats?.terrainNodes);
  const terrainTriangles = useStats((s) => s.stats?.terrainTriangles);
  const terrainCapped = useStats((s) => s.stats?.terrainCapped);
  const archetypes = layers === undefined ? DEFAULT_ARCHETYPE_INFOS : layers.map((l) => l.archetype);
  return (
    <div className="panel toolbar">
      <div className="toolbar-title">Typhon · Live World</div>

      <section>
        <h3>{LIVE ? `World (${sourceName ?? 'connecting…'})` : 'World (mock server)'}</h3>
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
            The engine over <code>/ws</code>. Add <code>?source=mock</code> to the URL for the browser-side mock.
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
        {ui.maxViewRadius === 0 ? (
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
          <button
            className={ui.showLabels ? 'active' : ''}
            onClick={() => {
              ui.setShowLabels(!ui.showLabels);
            }}
          >
            Labels
          </button>
          <button
            className={ui.cameraMode === 'eye' ? 'active' : ''}
            disabled={ui.selectedNetId === 0}
            title={
              ui.selectedNetId === 0
                ? 'Select an entity first — eye view rides one.'
                : "Stand in the world, in the selected entity's eyes. Drag to look, wheel to pull back over its shoulder. You cannot drive it: this is spectating."
            }
            onClick={() => {
              ui.setCameraMode(ui.cameraMode === 'eye' ? 'god' : 'eye');
            }}
          >
            Eye view
          </button>
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

      <div className="hint">Drag: pan · Right-drag: orbit · Wheel: zoom · WASD/QE/RF · Click: inspect</div>
    </div>
  );
}
