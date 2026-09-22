import { ARCHETYPE_LABELS } from '../data/swg-schema';
import { CITIES, POIS } from '../data/world-data';
import { useUi, type Population } from '../state/ui-store';

const HEAT_LABELS = ['Lairs', 'Creatures', 'City NPCs', 'Players'];
const POPULATIONS: Population[] = [1, 4, 16];

/** World, view and layer controls. */
export function Toolbar() {
  const ui = useUi();
  return (
    <div className="panel toolbar">
      <div className="toolbar-title">Typhon · Live World</div>

      <section>
        <h3>World (mock server)</h3>
        <div className="row">
          {POPULATIONS.map((p) => (
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
          <button
            onClick={() => {
              ui.setSeed((ui.seed * 1103515245 + 12345) % 2147483647);
            }}
          >
            New seed
          </button>
          <button
            className={ui.paused ? 'active' : ''}
            onClick={() => {
              ui.setPaused(!ui.paused);
            }}
          >
            {ui.paused ? 'Resume' : 'Pause'}
          </button>
        </div>
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
      </section>

      <section>
        <h3>View</h3>
        <label className="slider">
          Near radius {ui.viewRadius} m
          <input
            type="range"
            min={250}
            max={4000}
            step={250}
            value={ui.viewRadius}
            onChange={(e) => {
              ui.setViewRadius(Number(e.target.value));
            }}
          />
        </label>
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
        <h3>Heatmap (far tier)</h3>
        <div className="row wrap">
          {HEAT_LABELS.map((label, i) => (
            <button
              key={label}
              className={ui.heatmap[i] ? 'active' : ''}
              onClick={() => {
                ui.toggleHeatmap(i);
              }}
            >
              {label}
            </button>
          ))}
        </div>
      </section>

      <section>
        <h3>Layers</h3>
        <div className="row wrap">
          {ARCHETYPE_LABELS.map((label, i) => (
            <button
              key={label}
              className={ui.layers[i] ? 'active' : ''}
              onClick={() => {
                ui.toggleLayer(i);
              }}
            >
              {label}
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
        </div>
      </section>

      <div className="hint">Drag: pan · Right-drag: orbit · Wheel: zoom · WASD/QE/RF · Click: inspect</div>
    </div>
  );
}
