import { useStats } from '../state/stats-store';
import { useUi } from '../state/ui-store';

const fmt = (v: number, digits = 1) => (Number.isFinite(v) ? v.toFixed(digits) : '—');
const kb = (bytes: number) => `${(bytes / 1024).toFixed(1)} KB/s`;

/** Frame, clock, source and per-archetype counts, refreshed four times a second. */
export function Hud() {
  const stats = useStats((s) => s.stats);
  // Whether this server told us the radius it honours; without it the row cannot claim to be the granted one.
  const radiusKnown = useUi((s) => s.maxViewRadius) !== null;
  if (stats === null) {
    return <div className="panel hud">Starting…</div>;
  }

  const server = stats.source.server;
  const repl = stats.replication;
  const session = stats.source.session;
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
            <td>Terrain</td>
            <td>
              {stats.terrainTriangles.toLocaleString()} tris · {stats.terrainNodes} nodes @{' '}
              {fmt(stats.terrainFinestM, 0)} m{stats.terrainCapped ? ' ⚠ capped' : ''}
            </td>
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
          <tr>
            {/* The terrain under the camera's target, and — until the bake lands — that it has not landed. The bake time is
                worth a place here rather than a console line: it is a full second, and a session that looks flat for its
                first second should say why. */}
            <td>Ground</td>
            <td>
              {stats.terrainBakeMs === 0
                ? 'baking…'
                : `${fmt(stats.groundM, 1)} m (baked in ${fmt(stats.terrainBakeMs, 0)} ms)`}
            </td>
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
            {/* Named for what it can stand behind. The server shrinks a region past its profile's ceiling without a
                word, so this is only the granted radius once the slider has been capped by `/typhon/demo.json`. */}
            <td
              title={
                repl !== null
                  ? "The radius the ENGINE is serving, read from its own geometry: a sphere's R′, or the largest disc inside the hull it kept."
                  : radiusKnown
                    ? "The camera's radius, within what this server honours."
                    : 'What the camera asked for. This server did not say what it honours, so it may be serving less.'
              }
            >
              {repl !== null ? 'Radius (served)' : radiusKnown ? 'Radius' : 'Radius (asked)'}
            </td>
            <td>
              {fmt(stats.nearRadius, 0)} m{stats.source.viewComplete ? '' : ' (filling)'}
            </td>
          </tr>
          <tr>
            {/* Two different statistics, and replication is INSIDE the tick — its systems are in the same schedule. The
                labels say so, because "20 / 7 ms" reads as 27 ms of work and is not. */}
            <td title="The whole tick's median, and the replication track's 99th percentile. Replication runs inside the tick: it is part of the left number, not extra.">
              Tick p50 / repl p99
            </td>
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
          {repl === null ? null : (
            <>
              <tr>
                {/* Straight from the DEBUG block: the shape the ENGINE is serving, after any clamp, and the cells it has
                    actually pushed. When this disagrees with the Radius row above, the Radius row is the one that is a wish. */}
                <td title="The session's shape as the engine holds it, and how many of its window's cells have been delivered.">
                  Served / cells
                </td>
                <td>
                  {repl.shape}
                  {repl.radiusM > 0 ? ` ${fmt(repl.radiusM, 0)} m L${repl.level}` : ''} ·{' '}
                  {repl.deliveredCells.toLocaleString()} / {repl.windowCells.toLocaleString()} @ {fmt(repl.cellM, 0)} m
                  {repl.viewComplete ? '' : ' (filling)'}
                </td>
              </tr>
              {repl.nearBudget === 0 ? null : (
                <tr>
                  <td title="The near budget's own estimate of what this region holds, against the budget that shrinks it.">
                    Held / budget
                  </td>
                  <td>
                    {repl.held.toLocaleString()} / {repl.nearBudget.toLocaleString()}
                    {repl.held > repl.nearBudget ? ' ⚠' : ''}
                  </td>
                </tr>
              )}
            </>
          )}
        </tbody>
      </table>
      {session === null ? null : (
        <>
          <div className="hud-title">This session</div>
          <table>
            <tbody>
              <tr>
                <td title="Round trip measured by the PING loop.">RTT</td>
                <td>{session.rttMs > 0 ? `${fmt(session.rttMs, 0)} ms` : '—'}</td>
              </tr>
              <tr>
                {/* Two different measurements of the same thing, deliberately side by side: the server's own figure for
                    this session, and this client's estimate over arrival times. A gap between them is worth seeing. */}
                <td title="What the server says it is sending THIS session, against what this client measures arriving. They should track.">
                  Out (server / seen)
                </td>
                <td>
                  {kb(session.outBytesPerSec)} / {kb(stats.source.wireBytesPerSec)}
                </td>
              </tr>
              <tr>
                <td title="Regions this client sent, held back as not worth sending, and had refused by the server.">
                  Regions s/h/refused
                </td>
                <td>
                  {session.regionsSent} / {session.regionsSuppressed} / {session.regionsRejected}
                  {session.regionsRejected > 0 ? ' ⚠' : ''}
                </td>
              </tr>
              {session.skippedFrames === 0 && session.droppedCommands === 0 ? null : (
                <tr>
                  {/* Counters, not rates — they only rise. A HUD that showed them per second would be lying. */}
                  <td title="Frames the server chose not to send this session, and commands it refused (rate limiting included). Both are totals since the session opened.">
                    Skipped / dropped
                  </td>
                  <td>
                    {session.skippedFrames.toLocaleString()} / {session.droppedCommands.toLocaleString()}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </>
      )}
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
          {stats.layers.map((layer) => (
            <tr key={layer.archetype.name}>
              <td>{layer.archetype.label}</td>
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
