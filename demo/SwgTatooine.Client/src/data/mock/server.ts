import { AGG_GRID, Archetype, ARCHETYPE_COUNT, SWG_SCHEMA } from '../swg-schema';
import { SpatialBins } from './bins';
import { DEFAULT_INTEREST, InterestManager, type InterestOptions } from './interest';
import { MotionTracker } from './motion-rule';
import {
  ENTER_HEADER,
  EVENT_RECORD,
  SEGMENT_RECORD,
  STATE_HEADER,
  TickFlags,
  type AggBlock,
  type ArchetypeBlock,
  type TickMessage,
} from './protocol';
import { EventKind, MockSimulation, SIM_EVENT_STRIDE, TICK_HZ } from './sim';
import { buildWorld, type MockWorld } from './world';

/**
 * The mock server: a simulation plus the engine's replication track for one session — interest, per-entity projection
 * with value comparison (SUB-10), measured motion, events routed to the entities the session knows, and the far-tier
 * aggregate — producing one {@link TickMessage} per tick. Independent of any worker, so tests drive it directly.
 *
 * Tick order, as the engine's: simulation (reading bins that describe the start of the tick) → bins rebuilt for what
 * moved → interest (enters, leaves) → projection → events → aggregate.
 */

/** 64 m cells: fine enough for the simulation's 24 m aggro and 75 m weapon queries, and 4 × 4 of them make an AGG cell. */
const BIN_M = 64;
const AGG_EVERY_TICKS = TICK_HZ;
/** WebSocket + TLS 1.3 + TCP/IPv4 per message (`04-protocol.md` § 7). */
const TRANSPORT_OVERHEAD_B = 76;

class RecordBuffer {
  data = new Float64Array(1024);
  count = 0;
  used = 0;

  /** Reserves one record of `stride` values and returns its offset. Read {@link data} after, never before: it may grow. */
  reserve(stride: number): number {
    if (this.used + stride > this.data.length) {
      const next = new Float64Array(Math.max(this.data.length * 2, this.used + stride));
      next.set(this.data);
      this.data = next;
    }

    const at = this.used;
    this.used += stride;
    this.count++;
    return at;
  }

  reset(): void {
    this.count = 0;
    this.used = 0;
  }

  /** An exact-size copy, safe to transfer. */
  take(): Float64Array {
    return this.data.slice(0, this.used);
  }
}

export class MockServer {
  readonly world: MockWorld;
  readonly sim: MockSimulation;
  readonly interest: InterestManager;
  tick = 0;

  private readonly bins: SpatialBins[];
  private readonly trackers: MotionTracker[];
  private readonly fieldCounts: number[];
  private readonly fieldGroups: Uint8Array[];
  private readonly lastSent: Float64Array[];
  private readonly enteredAt: Uint32Array[];
  private readonly enters: RecordBuffer[];
  private readonly segments: RecordBuffer[];
  private readonly states: RecordBuffer[];
  private readonly leaves: RecordBuffer[];
  private readonly events = new RecordBuffer();
  private readonly fieldScratch = new Float64Array(16);
  private readonly aggLast: Uint32Array;
  private readonly aggCounts: Uint32Array;
  private aggSent = false;
  private binsBuilt = false;

  constructor(seed: number, population: number, interest: InterestOptions = DEFAULT_INTEREST) {
    this.world = buildWorld(seed, population);
    this.sim = new MockSimulation(this.world, seed);
    this.interest = new InterestManager(this.world.sets, interest);
    this.bins = this.world.sets.map(() => new SpatialBins(BIN_M));
    this.trackers = this.world.sets.map((set) => new MotionTracker(set.count));
    this.fieldCounts = SWG_SCHEMA.archetypes.map((a) => a.fields.length);
    this.fieldGroups = SWG_SCHEMA.archetypes.map((a) => Uint8Array.from(a.fields, (f) => f.group));
    this.lastSent = this.world.sets.map((set, a) => new Float64Array(set.count * this.fieldCounts[a]));
    this.enteredAt = this.world.sets.map((set) => new Uint32Array(set.count));
    this.enters = this.world.sets.map(() => new RecordBuffer());
    this.segments = this.world.sets.map(() => new RecordBuffer());
    this.states = this.world.sets.map(() => new RecordBuffer());
    this.leaves = this.world.sets.map(() => new RecordBuffer());
    const aggSize = AGG_GRID.dimsX * AGG_GRID.dimsZ * AGG_GRID.archetypes.length;
    this.aggLast = new Uint32Array(aggSize);
    this.aggCounts = new Uint32Array(aggSize);
  }

  get worldEntities(): number {
    let n = 0;
    for (const set of this.world.sets) {
      n += set.count;
    }

    return n;
  }

  setRegion(x: number, z: number, radius: number): void {
    this.interest.setRegion(x, z, radius);
  }

  /**
   * One tick. `paused` freezes the simulation but not the server: ticks keep advancing, as a real server's would, so the
   * client's clock keeps a steady timeline and moving entities come to rest through ordinary segments.
   */
  step(now: () => number = () => performance.now(), paused = false): TickMessage {
    const tick = ++this.tick;
    if (!this.binsBuilt) {
      for (let a = 0; a < ARCHETYPE_COUNT; a++) {
        this.bins[a].build(this.world.sets[a]);
      }

      this.binsBuilt = true;
    }

    const t0 = now();
    if (!paused) {
      this.sim.step(tick, this.bins);
    } else {
      this.sim.events.clear();
    }

    const t1 = now();
    if (!paused) {
      this.bins[Archetype.Creature].build(this.world.creatures);
      this.bins[Archetype.CityNpc].build(this.world.npcs);
      this.bins[Archetype.Player].build(this.world.players);
      if (this.sim.lairsMoved) {
        this.bins[Archetype.CreatureLair].build(this.world.lairs);
      }
    }

    this.interest.update(this.bins, tick);

    let wireBytes = 6;
    const blocks: ArchetypeBlock[] = [];
    for (let a = 0; a < ARCHETYPE_COUNT; a++) {
      const block = this.project(a, tick);
      if (block !== null) {
        blocks.push(block);
        wireBytes += this.estimateBlockBytes(a, block);
      }
    }

    const eventCount = this.routeEvents();
    if (eventCount > 0) {
      wireBytes += 4 + eventCount * 8;
    }

    const agg = tick % AGG_EVERY_TICKS === 0 ? this.aggregate() : null;
    if (agg !== null && agg.cellCount > 0) {
      wireBytes += 7 + agg.cellCount * (1.5 + AGG_GRID.archetypes.length * 1.5);
    }

    wireBytes += TRANSPORT_OVERHEAD_B;
    const flags = (this.interest.viewComplete ? TickFlags.ViewComplete : 0) | (tick === 1 ? TickFlags.Reset : 0);
    const t2 = now();

    return {
      type: 'tick',
      tick,
      flags,
      blocks,
      events: this.events.take(),
      eventCount,
      agg,
      stats: {
        simMs: t1 - t0,
        replicationMs: t2 - t1,
        watched: this.interest.watchedTotal,
        effectiveRadius: this.interest.effectiveRadius,
        wireBytes: Math.round(wireBytes),
        worldEntities: this.worldEntities,
      },
    };
  }

  /** Transferable buffers of a message, for `postMessage`. */
  static transferables(message: TickMessage): ArrayBuffer[] {
    const list: ArrayBuffer[] = [message.events.buffer as ArrayBuffer];
    for (const b of message.blocks) {
      list.push(
        b.enters.buffer as ArrayBuffer,
        b.segments.buffer as ArrayBuffer,
        b.states.buffer as ArrayBuffer,
        b.leaves.buffer as ArrayBuffer,
      );
    }

    if (message.agg !== null) {
      list.push(message.agg.cells.buffer as ArrayBuffer, message.agg.counts.buffer as ArrayBuffer);
    }

    return list;
  }

  /** The quantized projection of one entity, in schema field order: what the server replicates for it now. */
  projectFields(a: number, i: number, out: Float64Array): void {
    const w = this.world;
    const interest = this.interest.archetypes;
    switch (a) {
      case Archetype.WorldObject:
        out[0] = w.statics.kind[i];
        break;
      case Archetype.CreatureLair:
        out[0] = w.lairs.template[i];
        out[1] = w.lairs.missionId[i];
        out[2] = unorm8(w.lairs.hp[i], w.lairs.maxHp[i]);
        break;
      case Archetype.Creature: {
        const target = w.creatures.target[i];
        out[0] = w.creatures.template[i];
        out[1] = w.creatures.mode[i];
        out[2] = target >= 0 ? interest[Archetype.Player].netIds[target] : 0;
        out[3] = unorm8(w.creatures.hp[i], w.creatures.maxHp[i]);
        break;
      }
      case Archetype.CityNpc:
        out[0] = w.npcs.mode[i];
        break;
      case Archetype.Player: {
        const creature = w.players.target[i];
        const lair = w.players.targetLair[i];
        out[0] = w.players.activity[i];
        out[1] = 0;
        out[2] =
          creature >= 0
            ? interest[Archetype.Creature].netIds[creature]
            : lair >= 0
              ? interest[Archetype.CreatureLair].netIds[lair]
              : 0;
        out[3] = unorm8(w.players.hp[i], w.players.maxHp[i]);
        break;
      }
    }
  }

  /** S1 for one archetype: leaves, enters with full records, then value-compared updates of the other watched entities. */
  private project(a: number, tick: number): ArchetypeBlock | null {
    const set = this.world.sets[a];
    const xs = set.x;
    const zs = set.z;
    const interest = this.interest.archetypes[a];
    const netIds = interest.netIds;
    const tracker = this.trackers[a];
    const nFields = this.fieldCounts[a];
    const groups = this.fieldGroups[a];
    const lastSent = this.lastSent[a];
    const enteredAt = this.enteredAt[a];
    const enters = this.enters[a];
    const segments = this.segments[a];
    const states = this.states[a];
    const leaves = this.leaves[a];
    const scratch = this.fieldScratch;
    enters.reset();
    segments.reset();
    states.reset();
    leaves.reset();

    for (let k = 0; k < interest.leaveCount; k++) {
      const o = leaves.reserve(1);
      leaves.data[o] = interest.leaveNetIds[k];
    }

    for (let k = 0; k < interest.enterCount; k++) {
      const i = interest.enters[k];
      enteredAt[i] = tick;
      tracker.begin(i, xs[i], zs[i], tick);
      const o = enters.reserve(ENTER_HEADER + nFields);
      const d = enters.data;
      d[o] = netIds[i];
      d[o + 1] = tracker.p0x[i];
      d[o + 2] = tracker.p0z[i];
      d[o + 3] = 0;
      d[o + 4] = 0;
      d[o + 5] = tick;
      d[o + 6] = tracker.epoch[i];
      this.projectFields(a, i, scratch);
      for (let f = 0; f < nFields; f++) {
        d[o + ENTER_HEADER + f] = scratch[f];
        lastSent[i * nFields + f] = scratch[f];
      }
    }

    if (a !== Archetype.WorldObject) {
      const watched = interest.watched;
      for (let k = 0; k < interest.watchedCount; k++) {
        const i = watched[k];
        if (enteredAt[i] === tick) {
          continue;
        }

        if (tracker.step(i, xs[i], zs[i], tick)) {
          const o = segments.reserve(SEGMENT_RECORD);
          const d = segments.data;
          d[o] = netIds[i];
          d[o + 1] = tracker.p0x[i];
          d[o + 2] = tracker.p0z[i];
          d[o + 3] = tracker.vx[i];
          d[o + 4] = tracker.vz[i];
          d[o + 5] = tracker.t0[i];
          d[o + 6] = tracker.epoch[i];
        }

        this.projectFields(a, i, scratch);
        let mask = 0;
        for (let f = 0; f < nFields; f++) {
          if (scratch[f] !== lastSent[i * nFields + f]) {
            mask |= 1 << groups[f];
          }
        }

        if (mask !== 0) {
          const o = states.reserve(STATE_HEADER + nFields);
          const d = states.data;
          d[o] = netIds[i];
          d[o + 1] = mask;
          for (let f = 0; f < nFields; f++) {
            d[o + STATE_HEADER + f] = scratch[f];
            if (((mask >> groups[f]) & 1) === 1) {
              lastSent[i * nFields + f] = scratch[f];
            }
          }
        }
      }
    }

    if (enters.count + segments.count + states.count + leaves.count === 0) {
      return null;
    }

    return {
      archetype: a,
      enters: enters.take(),
      enterCount: enters.count,
      segments: segments.take(),
      segmentCount: segments.count,
      states: states.take(),
      stateCount: states.count,
      leaves: leaves.take(),
      leaveCount: leaves.count,
    };
  }

  /**
   * Events go to the session when it knows the attacker or the target (`RouteToKnown`). An entity that left this tick is
   * still known to its events — the client applies leaves after events — and an end the session does not know travels as 0.
   */
  private routeEvents(): number {
    const sim = this.sim.events;
    const data = sim.data;
    const out = this.events;
    out.reset();
    for (let e = 0; e < sim.count; e++) {
      const base = e * SIM_EVENT_STRIDE;
      const attacker = this.interest.archetypes[data[base]].netIdOf(data[base + 1]);
      const target = this.interest.archetypes[data[base + 2]].netIdOf(data[base + 3]);
      if (attacker === 0 && target === 0) {
        continue;
      }

      const o = out.reserve(EVENT_RECORD);
      const d = out.data;
      d[o] = EventKind.Attack;
      d[o + 1] = attacker;
      d[o + 2] = target;
      d[o + 3] = data[base + 4];
      d[o + 4] = unorm8(data[base + 5], 1);
    }

    return out.count;
  }

  /**
   * Per-cell counts per archetype, read off the bins in O(cells) as the engine reads its per-cell counters — no entity
   * visited, no game state consulted. Only changed cells are sent; all non-empty ones the first time.
   */
  private aggregate(): AggBlock {
    const dims = AGG_GRID.dimsX;
    const nArch = AGG_GRID.archetypes.length;
    const counts = this.aggCounts;
    const span = AGG_GRID.cellM / BIN_M;
    for (let slot = 0; slot < nArch; slot++) {
      const bins = this.bins[AGG_GRID.archetypes[slot]];
      for (let cz = 0; cz < dims; cz++) {
        for (let cx = 0; cx < dims; cx++) {
          counts[(cz * dims + cx) * nArch + slot] = bins.countRect(
            cx * span,
            cz * span,
            (cx + 1) * span - 1,
            (cz + 1) * span - 1,
          );
        }
      }
    }

    const reset = !this.aggSent;
    let changedCount = 0;
    const cells = new Uint32Array(dims * dims);
    for (let cell = 0; cell < dims * dims; cell++) {
      let changed = false;
      let nonEmpty = false;
      for (let s = 0; s < nArch; s++) {
        const v = counts[cell * nArch + s];
        nonEmpty ||= v !== 0;
        changed ||= v !== this.aggLast[cell * nArch + s];
      }

      if (reset ? nonEmpty : changed) {
        cells[changedCount++] = cell;
      }
    }

    this.aggLast.set(counts);
    this.aggSent = true;
    const out = new Uint32Array(changedCount * nArch);
    for (let k = 0; k < changedCount; k++) {
      const cell = cells[k];
      out.set(counts.subarray(cell * nArch, cell * nArch + nArch), k * nArch);
    }

    return { reset, cellCount: changedCount, cells: cells.slice(0, changedCount), counts: out };
  }

  /** `typhon.2` record costs (`03-wire-protocol.md` § 11): gap-coded ids, 24-bit positions, 16-bit velocities. */
  private estimateBlockBytes(a: number, block: ArchetypeBlock): number {
    const fieldBytes = a === Archetype.Creature || a === Archetype.Player ? 5 : a === Archetype.CreatureLair ? 3 : 1;
    const enterBytes = a === Archetype.WorldObject ? 1.5 + 6 + fieldBytes : 14.5 + fieldBytes;
    return (
      8 +
      block.enterCount * enterBytes +
      block.segmentCount * 14.5 +
      block.stateCount * (2.5 + Math.min(2, fieldBytes)) +
      block.leaveCount * 1.5
    );
  }
}

function unorm8(value: number, max: number): number {
  if (max <= 0) {
    return 0;
  }

  const v = Math.round((value / max) * 255);
  return (v < 0 ? 0 : v > 255 ? 255 : v) / 255;
}
