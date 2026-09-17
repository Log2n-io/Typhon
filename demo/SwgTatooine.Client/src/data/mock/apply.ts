import { archetypeOf, NOT_FOUND, slotOf, type AggregateGrid, type WorldStore } from '@typhondb/client';
import type { EventSink } from '../source';
import { ENTER_HEADER, EVENT_RECORD, SEGMENT_RECORD, STATE_HEADER, TickFlags, type TickMessage } from './protocol';

/**
 * Applies one mock frame to the SDK store, in the order a `typhon.2` decoder applies a `TICK`
 * (`03-wire-protocol.md` § 5): enters and updates of every archetype, then events, then leaves, then aggregates.
 */
export function applyTick(world: WorldStore, grid: AggregateGrid, message: TickMessage, sink: EventSink): void {
  world.beginFrame(message.tick);
  grid.beginFrame();
  if ((message.flags & TickFlags.Reset) !== 0) {
    world.reset();
    grid.reset();
  }

  for (const block of message.blocks) {
    const store = world.archetypeStore(block.archetype);
    const fieldCount = store.schema.fields.length;
    const groups = store.schema.fields.map((f) => f.group);
    // An enter may grow the store, which replaces every array: re-read them whenever the version moves.
    let version = store.version;
    let fields = store.schema.fields.map((_, f) => store.fieldAt(f));

    const enters = block.enters;
    const enterStride = ENTER_HEADER + fieldCount;
    for (let r = 0; r < block.enterCount; r++) {
      const o = r * enterStride;
      const slot = world.enter(block.archetype, enters[o]);
      if (store.version !== version) {
        version = store.version;
        fields = store.schema.fields.map((_, f) => store.fieldAt(f));
      }

      if (store.hasPosition) {
        store.resetMotion(
          slot,
          enters[o + 1],
          enters[o + 2],
          enters[o + 3],
          enters[o + 4],
          enters[o + 5],
          enters[o + 6],
        );
      }

      for (let f = 0; f < fieldCount; f++) {
        fields[f][slot] = enters[o + ENTER_HEADER + f]!;
      }
    }

    const segments = block.segments;
    for (let r = 0; r < block.segmentCount; r++) {
      const o = r * SEGMENT_RECORD;
      const location = world.locate(segments[o]);
      if (location === NOT_FOUND || archetypeOf(location) !== block.archetype) {
        world.anomalies++;
        continue;
      }

      store.pushSegment(
        slotOf(location),
        segments[o + 1],
        segments[o + 2],
        segments[o + 3],
        segments[o + 4],
        segments[o + 5],
        segments[o + 6],
      );
    }

    const states = block.states;
    const stateStride = STATE_HEADER + fieldCount;
    for (let r = 0; r < block.stateCount; r++) {
      const o = r * stateStride;
      const location = world.locate(states[o]);
      if (location === NOT_FOUND || archetypeOf(location) !== block.archetype) {
        world.anomalies++;
        continue;
      }

      const slot = slotOf(location);
      const mask = states[o + 1];
      for (let f = 0; f < fieldCount; f++) {
        if (((mask >> groups[f]) & 1) === 1) {
          fields[f][slot] = states[o + STATE_HEADER + f]!;
        }
      }

      store.markUpdated(slot, mask);
    }
  }

  const events = message.events;
  for (let e = 0; e < message.eventCount; e++) {
    const o = e * EVENT_RECORD;
    sink.onAttack(message.tick, events[o + 1], events[o + 2], events[o + 3], events[o + 4]);
  }

  for (const block of message.blocks) {
    for (let r = 0; r < block.leaveCount; r++) {
      world.leave(block.leaves[r]);
    }
  }

  const agg = message.agg;
  if (agg !== null) {
    if (agg.reset) {
      grid.reset();
    }

    for (let k = 0; k < agg.cellCount; k++) {
      grid.setCell(agg.cells[k], agg.counts, k * grid.archetypeCount);
    }
  }

  world.endFrame();
}
