import {
  AggregateGrid,
  archetypeOf,
  evaluateSlot,
  MAX_MOTION_STRIDE,
  NOT_FOUND,
  slotOf,
  WorldStore,
} from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { applyTick } from '../src/data/mock/apply';
import { DEFAULT_INTEREST } from '../src/data/mock/interest';
import { MockServer } from '../src/data/mock/server';
import { buildWorld } from '../src/data/mock/world';
import type { EventSink } from '../src/data/source';
import { AGG_GRID, POSITION_QUANTUM_M, SWG_SCHEMA } from '../src/data/swg-schema';
import { CITIES, POIS } from '../src/data/world-data';

describe('mock world', () => {
  it('matches the benchmark census at x1: 17 724 entities', () => {
    const w = buildWorld(42, 1);
    expect(w.statics.count).toBe(3576);
    expect(w.npcs.count).toBe(1155);
    expect(w.players.count).toBe(320);
    expect(w.statics.count + w.lairs.count + w.creatures.count + w.npcs.count + w.players.count).toBe(17_724);
  });

  it('is deterministic for a seed', () => {
    const a = buildWorld(7, 1);
    const b = buildWorld(7, 1);
    expect(a.creatures.x).toEqual(b.creatures.x);
    expect(a.players.z).toEqual(b.players.z);
  });
});

interface Recorded {
  readonly attacker: number;
  readonly target: number;
  readonly attackerHeld: boolean;
  readonly targetHeld: boolean;
}

interface OracleResult {
  readonly compared: number;
  readonly worst: number;
  readonly events: readonly Recorded[];
  readonly world: WorldStore;
  readonly grid: AggregateGrid;
}

/**
 * A small differential oracle (the shape of v2 AC-8): after every applied frame, the client store must equal what the
 * server watches — membership, every replicated field, and every position within the motion tolerance.
 */
function runOracle(server: MockServer, ticks: number, move: (t: number) => void): OracleResult {
  const world = new WorldStore(SWG_SCHEMA);
  const grid = new AggregateGrid(AGG_GRID);
  const events: Recorded[] = [];
  const sink: EventSink = {
    onAttack: (_tick, attacker, target) => {
      events.push({
        attacker,
        target,
        attackerHeld: world.locate(attacker) !== NOT_FOUND,
        targetHeld: world.locate(target) !== NOT_FOUND,
      });
    },
  };
  const position = new Float64Array(MAX_MOTION_STRIDE);
  const fields = new Float64Array(16);
  let worst = 0;
  let compared = 0;

  for (let t = 1; t <= ticks; t++) {
    move(t);
    applyTick(
      world,
      grid,
      server.step(() => 0),
      sink,
    );

    let watched = 0;
    for (let a = 0; a < server.interest.archetypes.length; a++) {
      const interest = server.interest.archetypes[a];
      const set = server.world.sets[a];
      const store = world.archetypes[a];
      watched += interest.watchedCount;
      for (let k = 0; k < interest.watchedCount; k++) {
        const i = interest.watched[k];
        const location = world.locate(interest.netIds[i]);
        expect(location).not.toBe(NOT_FOUND);
        expect(archetypeOf(location)).toBe(a);
        const slot = slotOf(location);
        evaluateSlot(store, slot, t, 0, position, 0);
        worst = Math.max(worst, Math.hypot(position[0] - set.x[i], position[1] - set.z[i]));

        server.projectFields(a, i, fields);
        for (let f = 0; f < store.schema.fields.length; f++) {
          const field = store.schema.fields[f];
          const held = store.fieldAt(f)[slot];
          const expected = field.kind === 'f32' ? Math.fround(fields[f]) : fields[f];
          if (held !== expected) {
            throw new Error(
              `tick ${t}: ${store.schema.name}.${field.name} of netId ${interest.netIds[i]} is ${held}, server says ${expected}`,
            );
          }
        }

        compared++;
      }
    }

    expect(world.entityCount).toBe(watched);
  }

  return { compared, worst, events, world, grid };
}

describe('mock server → SDK store (a small differential oracle)', () => {
  it('keeps the replica equal to the watched set, field for field, positions within tolerance', () => {
    const server = new MockServer(1234, 1);
    server.setRegion(CITIES[0].x, CITIES[0].z, 1500);
    const { compared, worst, events, world } = runOracle(server, 400, (t) => {
      if (t === 150) {
        // Move the camera to a busy landmark: a burst of leaves and enters, and fights to watch.
        server.setRegion(POIS[0].x, POIS[0].z, 1500);
      }
    });

    expect(compared).toBeGreaterThan(100_000);
    expect(worst).toBeLessThanOrEqual(0.05 + POSITION_QUANTUM_M);
    expect(world.anomalies).toBe(0);
    expect(events.length).toBeGreaterThan(0);
    // Every end the server named is held by the client when the event is applied: leaves come after events.
    for (const e of events) {
      expect(e.attacker === 0 || e.attackerHeld).toBe(true);
      expect(e.target === 0 || e.targetHeld).toBe(true);
    }
  });

  it('survives a camera jump that drops thousands of entities of one archetype in one frame', () => {
    const server = new MockServer(99, 1, { ...DEFAULT_INTEREST, enterBudget: 20_000 });
    server.setRegion(0, 0, 4096);
    const { world } = runOracle(server, 30, (t) => {
      if (t === 20) {
        server.setRegion(-7000, -7000, 500);
      }
    });

    expect(world.anomalies).toBe(0);
  });

  it('sends per-cell counts that equal the world, cell for cell', () => {
    const server = new MockServer(5, 1);
    server.setRegion(0, 0, 500);
    const { grid } = runOracle(server, 30, () => undefined);
    const dims = AGG_GRID.dims[0];
    const nArch = AGG_GRID.archetypes.length;
    const expected = new Uint32Array(dims * dims * nArch);
    AGG_GRID.archetypes.forEach((archetype, slot) => {
      const set = server.world.sets[archetype];
      for (let i = 0; i < set.count; i++) {
        const cx = Math.min(dims - 1, Math.max(0, Math.floor((set.x[i] - AGG_GRID.origin[0]) / AGG_GRID.cell)));
        const cz = Math.min(dims - 1, Math.max(0, Math.floor((set.z[i] - AGG_GRID.origin[1]) / AGG_GRID.cell)));
        expected[(cz * dims + cx) * nArch + slot]++;
      }
    });

    // The last aggregate went out at tick 30, and the world has not moved since.
    expect(Array.from(grid.counts)).toEqual(Array.from(expected));
  });

  it('keeps ticking while paused, and the world stops', () => {
    const server = new MockServer(3, 1);
    server.setRegion(CITIES[0].x, CITIES[0].z, 800);
    server.step(() => 0);
    const before = Float64Array.from(server.world.players.x);
    const message = server.step(() => 0, true);
    expect(message.tick).toBe(2);
    expect(Array.from(server.world.players.x)).toEqual(Array.from(before));
  });
});
