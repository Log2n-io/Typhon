/**
 * Connects to a running `SwgTatooine --serve` as a god session, asks for a region, and prints what the store holds.
 *
 * A headless version of what the browser shows, for when the two disagree: it uses the same SDK the client does, so a
 * difference between this and the page is the renderer's, and a difference between this and the server is the wire's.
 *
 *   node --experimental-strip-types --no-warnings demo/SwgTatooine.Client/tools/probe.ts [url] [x] [z] [radius]
 *
 * It runs the SDK's BUILD, where the browser runs its sources through a Vite alias — Node's type stripping does not remap
 * the `.js` specifiers the SDK's sources import each other by, so `dist/` is the only thing importable here. That is a
 * difference this tool exists to rule out, not to introduce, so it refuses to run against a `dist` older than the sources
 * rather than quietly disagreeing with the page for a reason that has nothing to do with whatever is being chased.
 */
import { readdirSync, statSync } from 'node:fs';
import {
  Capabilities,
  Clock,
  CommandQueue,
  DebugGrid,
  DebugSubType,
  evaluateSlot,
  FrameApplier,
  MAX_MOTION_STRIDE,
  PingScheduler,
  PushGeometry,
  PushShape,
  ReconnectingClient,
  RegionSender,
  WireReader,
} from '../../../src/Typhon.Client.TypeScript/dist/index.js';

/** Refuses a build older than the sources it was built from. */
function requireFreshSdk(): void {
  const sdk = new URL('../../../src/Typhon.Client.TypeScript/', import.meta.url);
  const builtAt = statSync(new URL('dist/index.js', sdk)).mtimeMs;
  let newest = 0;
  const walk = (dir: URL): void => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const child = new URL(entry.name + (entry.isDirectory() ? '/' : ''), dir);
      if (entry.isDirectory()) {
        walk(child);
      } else if (entry.name.endsWith('.ts')) {
        newest = Math.max(newest, statSync(child).mtimeMs);
      }
    }
  };
  walk(new URL('src/', sdk));
  if (newest > builtAt) {
    console.error(
      'The SDK build is older than its sources. Run `npm run build` in src/Typhon.Client.TypeScript first.',
    );
    process.exit(2);
  }
}

requireFreshSdk();

const url = process.argv[2] ?? 'ws://127.0.0.1:8099/ws';
const x = Number(process.argv[3] ?? -2000);
const z = Number(process.argv[4] ?? 1500);
const radius = Number(process.argv[5] ?? 1500);
const seconds = Number(process.argv[6] ?? 6);

/**
 * A seventh argument naming an archetype re-aims the region onto a live entity of it, once the store has filled, and
 * keeps re-aiming every two seconds.
 *
 * Why it exists: a region session hears a 50 m disc at its hull's CENTROID, and a town square chosen by eye can hold
 * three buildings and no one who speaks — which is what a camera parked on Port Sable measured. Chasing an entity makes
 * the earshot count non-zero by construction, so a run that still hears nothing is evidence about the routing rather
 * than about where the camera happened to point.
 *
 *   node --experimental-strip-types tools/probe.ts <url> <x> <z> <radius> <seconds> CityNpc
 */
const chase = process.argv[7] ?? '';

const clock = new Clock({ tickPeriodMs: 100 });
const events = new Map<string, number>();
const samples: string[] = [];
let applier: FrameApplier | null = null;
let commands: CommandQueue | null = null;
let region: RegionSender | null = null;
/**
 * The `PING` loop, which this tool went without and must not.
 *
 * A session that stops acknowledging is closed with **4001** after a bounded run of silence, and a probe with no
 * `PingScheduler` is silent by construction — so every run reconnected every few seconds, restarted its store, and
 * reported a nearly empty world. That reads as "the region takes tens of seconds to settle" or "the world is quiet",
 * which is exactly the wrong conclusion and cost real investigation time during SWG-09's chat hunt. The browser client
 * has always had one; this tool is supposed to be its headless twin.
 */
let ping: PingScheduler | null = null;
let ticks = 0;

// The DEBUG block (CLI3D-03): the engine's own account of what this session is being served, which is the half of the
// browser's replication overlay that does not depend on a GPU.
const debugGrid = new DebugGrid();
const debugGeometry = new PushGeometry();
const debugReader = new WireReader();
let sawGrid = false;
let sawGeometry = false;
let geometryUpdates = 0;

/**
 * A region's hull and its vertex centroid.
 *
 * The centroid is not decoration: it is the point a region session's own near-routed events are measured from
 * (`RouteNear` against a `ClientRegion` observer), so a `Say` within 50 m of THIS point is what the session hears — not
 * one within 50 m of anything the camera is looking at.
 */
function hullText(g: PushGeometry): string {
  if (g.vertexCount === 0) {
    return 'none yet';
  }

  let cx = 0;
  let cz = 0;
  const parts: string[] = [];
  for (let i = 0; i < g.vertexCount; i++) {
    const x = g.vertices[i * g.dims];
    const z = g.vertices[i * g.dims + 1];
    cx += x;
    cz += z;
    parts.push(`(${x.toFixed(0)}, ${z.toFixed(0)})`);
  }

  return `${parts.join(' ')} — centroid (${(cx / g.vertexCount).toFixed(1)}, ${(cz / g.vertexCount).toFixed(1)})`;
}

/** SWG's `/say` range, and the radius `RouteNear` carries for `Chat`. */
const SAY_RANGE_M = 50;

/**
 * How many entities the session HOLDS within earshot of the point it hears from.
 *
 * This is the number that decides whether a silent chat panel is a bug or arithmetic. A region session hears a 50 m disc
 * at its hull's centroid; a town's NPCs are spread over hundreds of metres, so that disc can honestly be empty. Counting
 * what is actually in it separates "nothing was routed" from "nobody was there", which no amount of staring at the
 * routing code can.
 */
function withinEarshot(a: FrameApplier): string {
  const g = debugGeometry;
  if (!sawGeometry || g.vertexCount === 0) {
    return 'earshot: no hull, so no point to measure from';
  }

  let cx = 0;
  let cz = 0;
  for (let i = 0; i < g.vertexCount; i++) {
    cx += g.vertices[i * g.dims];
    cz += g.vertices[i * g.dims + 1];
  }

  cx /= g.vertexCount;
  cz /= g.vertexCount;

  const scratch = new Float64Array(MAX_MOTION_STRIDE);
  const counts: string[] = [];
  let total = 0;
  for (const plan of a.plan.archetypes) {
    const store = a.world.archetypeStore(plan.idx);
    if (!store.hasPosition) {
      continue;
    }

    let near = 0;
    for (let slot = 0; slot < store.capacity; slot++) {
      if (!store.isLive(slot)) {
        continue;
      }

      evaluateSlot(store, slot, a.tick, 0, scratch, 0);
      const dx = scratch[0] - cx;
      const dz = scratch[1] - cz;
      if (dx * dx + dz * dz <= SAY_RANGE_M * SAY_RANGE_M) {
        near++;
      }
    }

    total += near;
    if (near > 0) {
      counts.push(`${plan.name} ${near}`);
    }
  }

  return `earshot: ${total} held within ${SAY_RANGE_M} m of (${cx.toFixed(0)}, ${cz.toFixed(0)})${counts.length > 0 ? ` — ${counts.join(', ')}` : ''}`;
}

function describeGeometry(): string {
  if (!sawGeometry) {
    return 'no PUSH_GEOMETRY (the session was not granted the DEBUG cap, or nothing changed)';
  }

  const g = debugGeometry;
  const w = g.window;
  const depth = g.deep ? w : 1;
  let delivered = 0;
  for (let lz = 0; lz < depth; lz++) {
    for (let ly = 0; ly < w; ly++) {
      for (let lx = 0; lx < w; lx++) {
        delivered += g.delivered(g.windowOrigin[0] + lx, g.windowOrigin[1] + ly, g.windowOrigin[2] + lz) ? 1 : 0;
      }
    }
  }

  const shape =
    g.shape === PushShape.Sphere
      ? `sphere R'=${g.radiusM.toFixed(0)} m h=${g.slackM.toFixed(0)} L${g.level} at (${g.anchor[0].toFixed(0)}, ${g.anchor[1].toFixed(0)})`
      : g.shape === PushShape.World
        ? `world cursor=${g.cursorHi.toString(16)}${g.cursorLo.toString(16).padStart(8, '0')}`
        : `region held ${g.held} of ${g.nearBudget}, hull ${hullText(g)}`;
  return `${shape}\n         ${delivered} of ${w * w * depth} window cells delivered, window ${w} from (${g.windowOrigin[0]}, ${g.windowOrigin[1]}, ${g.windowOrigin[2]})${g.viewComplete ? ', VIEW_COMPLETE' : ''}`;
}

const client = new ReconnectingClient({
  url,
  kind: 'god',
  token: '',
  caps: Capabilities.Stats | Capabilities.Debug,
  handlers: {
    onPong: (pong, recvMs) => {
      ping?.onPong(pong, recvMs);
    },
    onWelcome: (session, connection) => {
      const plan = session.plan;
      console.log(`WELCOME  archetypes=${plan.archetypes.map((a) => `${a.idx}:${a.name}`).join(' ')}`);
      console.log(
        `         events=${plan.catalog.events.map((e) => e.name).join(' ')}  grids=${plan.catalog.grids.length}`,
      );
      applier = new FrameApplier(plan, {
        clock,
        onEvent: (event) => {
          const name = event.type.name;
          events.set(name, (events.get(name) ?? 0) + 1);
          if (name === 'Chat' && samples.length < 5) {
            samples.push(`#${event.number('speaker')} ${event.text('text')}`);
          }
        },
        onDebug: (subType, data, offset, length) => {
          const r = debugReader.reset(data.subarray(offset, offset + length));
          if (subType === DebugSubType.Grid) {
            debugGrid.readFrom(r);
            sawGrid = true;
          } else if (subType === DebugSubType.PushGeometry) {
            debugGeometry.readFrom(r);
            sawGeometry = true;
            geometryUpdates++;
          }

          r.release();
        },
      });
      commands = new CommandQueue({ plan });
      region =
        plan.clientRegion === null
          ? null
          : new RegionSender({
              plan,
              send: (type, values) => commands?.enqueue(type, values),
              realm: () => applier?.realmFrame ?? null,
            });
      ping?.stop();
      ping = new PingScheduler({
        pingHz: plan.catalog.tick.pingHz,
        send: (message) => {
          connection.sendPing(message);
        },
        lastAppliedTick: () => applier?.tick ?? 0,
      });
      ping.start();
      if (region === null) {
        console.log('         no ClientRegion in this catalog (the server has no --god-region)');
      } else {
        const quad = [x - radius, z - radius, x + radius, z - radius, x + radius, z + radius, x - radius, z + radius];
        region.setRegion(quad, radius, 256);
      }
    },
    onTick: (message, recvMs) => {
      applier?.apply(message, recvMs);
      ticks++;
    },
    // A reconnecting probe silently restarts its store, so a run that ends with nothing held reads as an empty world
    // rather than as a session that never lived long enough to fill. Say so.
    onClose: (close) => {
      console.log(`CLOSE    code=${close.code} reason=${JSON.stringify(close.reason)} clean=${close.wasClean}`);
    },
  },
});

client.start();

setInterval(() => {
  const a = applier;
  if (a === null) {
    return;
  }

  region?.poll();
  const connection = client.connection;
  if (connection !== null) {
    commands?.flush(
      a.tick,
      (bytes) => {
        connection.send(bytes);
      },
      a.realmFrame,
    );
  }
}, 100);

if (chase !== '') {
  setInterval(() => {
    const a = applier;
    const plan = a?.plan.archetypes.find((p) => p.name === chase);
    if (a === null || plan === undefined || region === null) {
      return;
    }

    const store = a.world.archetypeStore(plan.idx);
    const scratch = new Float64Array(MAX_MOTION_STRIDE);
    for (let slot = 0; slot < store.capacity; slot++) {
      if (!store.isLive(slot)) {
        continue;
      }

      evaluateSlot(store, slot, a.tick, 0, scratch, 0);
      const [cx, cz] = [scratch[0], scratch[1]];
      region.setRegion(
        [cx - radius, cz - radius, cx + radius, cz - radius, cx + radius, cz + radius, cx - radius, cz + radius],
        radius,
        256,
      );
      return;
    }
  }, 2000);
}

setTimeout(() => {
  const a = applier;
  if (a === null) {
    console.log('no session');
    process.exit(1);
  }

  console.log(`\n${ticks} ticks, tick ${a.tick}, ${a.world.entityCount} entities held\n`);
  console.log(
    sawGrid
      ? `DEBUG    grid ${debugGrid.dims[0]}x${debugGrid.dims[1]}x${debugGrid.dims[2]} of ${debugGrid.cellM} m from (${debugGrid.origin[0]}, ${debugGrid.origin[1]}, ${debugGrid.origin[2]})`
      : 'DEBUG    no GRID: this session was not granted the DEBUG cap',
  );
  console.log(`         ${describeGeometry()}  (${geometryUpdates} updates)`);
  console.log(`         ${withinEarshot(a)}\n`);
  const scratch = new Float64Array(MAX_MOTION_STRIDE);
  for (const plan of a.plan.archetypes) {
    const store = a.world.archetypeStore(plan.idx);
    const samples: string[] = [];
    for (let slot = 0; slot < store.capacity && samples.length < 3; slot++) {
      if (!store.isLive(slot)) {
        continue;
      }

      evaluateSlot(store, slot, a.tick, 0, scratch, 0);
      const fields = store.schema.fields.map((f, i) => `${f.name}=${store.fieldAt(i)[slot]}`).join(' ');
      samples.push(`(${scratch[0].toFixed(1)}, ${scratch[1].toFixed(1)}) ${fields}`);
    }

    console.log(
      `${plan.name.padEnd(14)} live=${String(store.liveCount).padStart(5)} pos=${store.hasPosition ? (store.schema.position?.kind ?? '?') : 'none'}`,
    );
    for (const s of samples) {
      console.log(`               ${s}`);
    }
  }

  for (const grid of a.grids) {
    let cells = 0;
    let total = 0;
    for (let i = 0; i < grid.counts.length; i++) {
      if (grid.counts[i] !== 0) {
        cells++;
        total += grid.counts[i];
      }
    }

    console.log(
      `
aggregate grid ${grid.index}: cell ${grid.schema.cell} m, dims ${grid.schema.dims.join('x')}, ` +
        `archetypes [${grid.schema.archetypes.join(', ')}], version ${grid.version}, ${cells} non-zero cells, ${total} counted`,
    );
  }

  console.log(`
events: ${[...events].map(([n, c]) => `${n} x${c}`).join(', ') || 'none'}`);
  for (const line of samples) {
    console.log(`  ${line}`);
  }

  console.log(`
stats: last block at tick ${a.stats.tick} (received-this-frame ${a.stats.received})`);
  if (a.stats.tick >= 0) {
    for (const metric of a.plan.catalog.metrics) {
      const plan = a.plan.metricByName(metric.name);
      if (plan !== null) {
        console.log(`  ${metric.name.padEnd(36)} ${a.stats.valueOf(plan)}`);
      }
    }
  }

  process.exit(0);
}, seconds * 1000);
