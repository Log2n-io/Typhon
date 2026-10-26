import { Fragment } from 'react';
import { SpeedClass } from '../data/source';
import { gaitLabel } from '../app/gait';
import { realmLabel } from '../data/realm-view';
import { useStats } from '../state/stats-store';
import { useTerrain } from '../state/terrain-store';
import { useUi } from '../state/ui-store';

const fmt = (v: number, digits = 1) => (Number.isFinite(v) ? v.toFixed(digits) : '—');
const kb = (bytes: number) => `${(bytes / 1024).toFixed(1)} KB/s`;

/** Frame, clock, source and per-archetype counts, refreshed four times a second. */
export function Hud() {
  const stats = useStats((s) => s.stats);
  // Whether this server told us the radius it honours; without it the row cannot claim to be the granted one.
  const radiusKnown = useUi((s) => s.maxViewRadius) !== null;
  // How the planet was obtained. Two selectors rather than one returning an object: zustand compares with Object.is, so
  // a fresh object every render would re-render forever. Read before the early return, because a hook may not be called
  // conditionally.
  const terrainCached = useTerrain((s) => s.fromCache);
  const terrainLoadMs = useTerrain((s) => s.loadMs);
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
          {stats.feet === null ? null : (
            <tr>
              {/* The gap between the bottom of the drawn body and the surface the collision rule says is under it.
                  0.00 means the feet are on the ground. Anything else is the body in the air, and the three figures
                  beside it say which of the four numbers involved is the one that disagrees. */}
              <td>Feet</td>
              <td style={{ color: Math.abs(stats.feet.gapM) > 0.05 ? '#ff6b6b' : undefined }}>
                {`${fmt(stats.feet.gapM, 2)} m gap · body ${fmt(stats.feet.bottomY, 2)} · surface `}
                {`${fmt(stats.feet.surfaceY, 2)} · terrain ${fmt(stats.feet.terrainY, 2)} · server ${fmt(stats.feet.serverY, 2)}`}
              </td>
            </tr>
          )}
          <tr>
            {/* What Shift gives (or the swoop, on the 1 key), the boost Shift+1..4 latched, and what the SERVER says that is
                worth — the figure is replicated, so a dash here means the owner block has not arrived and the predictor is
                in its no-prediction fallback, which is worth being able to see. */}
            <td>Gait</td>
            <td>{`${gaitLabel(stats.gait)}${stats.gait === SpeedClass.swoop ? ' (1)' : ''} ×${stats.boost} (⇧${stats.boost}) · ${fmt(stats.gaitMps, 1)} m/s`}</td>
          </tr>
          <tr>
            {/* The terrain under the camera's target, and where it came from. The world no longer starts before the field
                does, so there is no 'baking' state to report here any more — what is worth reporting instead is whether
                this session paid the bake or read it back, because that is the difference between ~2 s and a fraction of
                it, and it is the one number that says the local database is doing its job. */}
            <td>Ground</td>
            <td>
              {`${fmt(stats.groundM, 1)} m `}
              {terrainCached
                ? `(cached, read in ${fmt(terrainLoadMs, 0)} ms)`
                : `(baked in ${fmt(stats.terrainBakeMs, 0)} ms)`}
            </td>
          </tr>
        </tbody>
      </table>
      <div className="hud-title">Server ({stats.source.name})</div>
      <table>
        <tbody>
          <tr>
            {/* Where this session IS, above what it holds — because every count below it is a count within this realm
                and nowhere else (SUB-28), and a viewer who cannot see which realm cannot read any of them. */}
            <td title="The realm this session is in. Everything below is counted within it and nothing outside it exists for this session.">
              Realm
            </td>
            <td>
              {realmLabel(stats.realm)}
              {stats.realm === null ? '' : ` (#${stats.realm.realmId})`}
            </td>
          </tr>
          {stats.ridingNetId !== 0 && (
            <tr>
              {/* Immediately under the realm, because it is the reason the realm is what it is: a spectating session's
                  realm is its subject's, and it changes when the subject walks through a door. */}
              <td title="The session is anchored on this entity: replication is centred on it and follows it between realms.">
                Riding
              </td>
              <td>#{stats.ridingNetId}</td>
            </tr>
          )}
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
          {/* A14. Shown only for a session that predicts: the lead is the removed latency as a distance, and the counters are
              how a prediction fault announces itself. Any of them climbing is a bug, not a network condition. */}
          {stats.prediction === null ? null : (
            <tr>
              <td title="How far ahead of the server's own figure your body is drawn, and the prediction's three fault counters: snaps, stat fallbacks, unreconciled frames. All three should sit at their warm-up value.">
                Predict lead
              </td>
              <td>
                {fmt(stats.prediction.leadM, 2)} m
                {stats.prediction.snaps + stats.prediction.fallbacks > 0
                  ? ` · ${stats.prediction.snaps} snap / ${stats.prediction.fallbacks} fb`
                  : ''}
              </td>
            </tr>
          )}
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
            <Fragment key={layer.archetype.name}>
              <tr>
                <td>{layer.archetype.label}</td>
                <td>{layer.held.toLocaleString()}</td>
                <td>{layer.near.toLocaleString()}</td>
                <td>{layer.far.toLocaleString()}</td>
              </tr>
              {layer.drivers === null ? null : (
                <>
                  <tr className="hud-sub" title="Players the server simulates: no account, no name">
                    <td>· crowd</td>
                    <td>{layer.drivers.crowd.toLocaleString()}</td>
                    <td />
                    <td />
                  </tr>
                  <tr className="hud-sub" title="Players of the bot fleet: their account says so">
                    <td>· bots</td>
                    <td>{layer.drivers.bots.toLocaleString()}</td>
                    <td />
                    <td />
                  </tr>
                  <tr className="hud-sub" title="Players a person drives in a browser — you included">
                    <td>· people</td>
                    <td>{layer.drivers.people.toLocaleString()}</td>
                    <td />
                    <td />
                  </tr>
                </>
              )}
            </Fragment>
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
