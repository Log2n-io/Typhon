import { ARCHETYPE_LABELS } from '../data/swg-schema';
import { useStats } from '../state/stats-store';

const fmt = (v: number, digits = 1) => (Number.isFinite(v) ? v.toFixed(digits) : '—');
const kb = (bytes: number) => `${(bytes / 1024).toFixed(1)} KB/s`;

/** Frame, clock, source and per-archetype counts, refreshed four times a second. */
export function Hud() {
  const stats = useStats((s) => s.stats);
  if (stats === null) {
    return <div className="panel hud">Starting…</div>;
  }

  const server = stats.source.server;
  return (
    <div className="panel hud">
      <div className="hud-title">Client</div>
      <table>
        <tbody>
          <tr>
            <td>FPS</td>
            <td>{fmt(stats.fps, 0)}</td>
          </tr>
          <tr>
            <td>Frame JS (ours)</td>
            <td>
              {fmt(stats.frameJsMs, 2)} ms · p95 {fmt(stats.frameJsP95Ms, 2)}
            </td>
          </tr>
          <tr>
            <td>Babylon frame</td>
            <td>{fmt(stats.frameTotalMs, 2)} ms</td>
          </tr>
          <tr>
            <td>Draw calls</td>
            <td>{stats.drawCalls}</td>
          </tr>
          <tr>
            <td>Apply / tick</td>
            <td>{stats.source.applyMs < 0.1 ? '< 0.1' : fmt(stats.source.applyMs, 2)} ms</td>
          </tr>
          <tr>
            <td>Render delay</td>
            <td>{fmt(stats.renderDelayMs, 0)} ms</td>
          </tr>
          <tr>
            <td>Tick (render / latest)</td>
            <td>
              {fmt(stats.renderTime, 1)} / {stats.latestTick}
            </td>
          </tr>
          <tr>
            <td>Altitude</td>
            <td>{fmt(stats.altitude, 0)} m</td>
          </tr>
        </tbody>
      </table>
      <div className="hud-title">Server ({stats.source.name})</div>
      <table>
        <tbody>
          <tr>
            <td>World</td>
            <td>{server?.worldEntities.toLocaleString() ?? '—'} entities</td>
          </tr>
          <tr>
            <td>Watched / held</td>
            <td>
              {server?.watched.toLocaleString() ?? '—'} / {stats.held.toLocaleString()}
              {server !== null && server.watched !== stats.held ? ' ⚠' : ''}
            </td>
          </tr>
          <tr>
            <td>Near radius</td>
            <td>
              {fmt(stats.nearRadius, 0)} m{stats.source.viewComplete ? '' : ' (filling)'}
            </td>
          </tr>
          <tr>
            <td>Sim / replication</td>
            <td>
              {fmt(server?.simMs ?? Number.NaN, 2)} / {fmt(server?.replicationMs ?? Number.NaN, 2)} ms
            </td>
          </tr>
          <tr>
            <td>Wire (est.)</td>
            <td>{kb(stats.source.wireBytesPerSec)}</td>
          </tr>
          <tr>
            <td>Anomalies</td>
            <td>{stats.anomalies}</td>
          </tr>
        </tbody>
      </table>
      <table className="hud-layers">
        <thead>
          <tr>
            <th />
            <th>held</th>
            <th>mesh</th>
            <th>dot</th>
          </tr>
        </thead>
        <tbody>
          {stats.layers.map((layer, archetype) => (
            <tr key={archetype}>
              <td>{ARCHETYPE_LABELS[archetype]}</td>
              <td>{layer.held.toLocaleString()}</td>
              <td>{layer.near.toLocaleString()}</td>
              <td>{layer.far.toLocaleString()}</td>
            </tr>
          ))}
          <tr>
            <td>Attack lines</td>
            <td />
            <td>{stats.attackLines}</td>
            <td />
          </tr>
        </tbody>
      </table>
    </div>
  );
}
