import { ArchetypeStore, DEFAULT_MAX_RENDER_DELAY_MS, segmentHistoryFor } from './archetype-store.js';
import { validateSchema, type WorldSchema } from './schema.js';

/**
 * Sentinel returned by {@link WorldStore.locate} for an unknown netId. −1 rather than `0xFFFFFFFF`: a small integer
 * crosses a call as a tagged value, where a uint32 beyond the small-integer range is boxed into a heap number on every
 * miss. That range ends at 2^31 in Node and at 2^30 in browsers with pointer compression (Chrome); the Node tests observe
 * only the first.
 */
export const NOT_FOUND = -1;

const SLOT_BITS = 24;
const SLOT_MASK = (1 << SLOT_BITS) - 1;

export interface WorldStoreOptions {
  /**
   * Largest netId accepted. netIds are dense on the server, so the netId → slot map is a flat array of this many
   * entries at most (4 B each); a corrupt id beyond it throws instead of allocating. Default 2²² (16 MiB worst case).
   */
  readonly maxNetId?: number;
  /**
   * The largest render delay motion is evaluated at, in milliseconds; it sizes each slot's segment ring (see
   * `segmentHistoryFor`). Default 300, the `Clock`'s default `maxDelayMs`: a `Clock` built with a larger `maxDelayMs`
   * must pass the same value here, or render time can fall behind the ring. `FrameApplier` passes its clock's.
   */
  readonly maxRenderDelayMs?: number;
}

/**
 * The client's replica of what the server has shown it: one {@link ArchetypeStore} per archetype and a map from global
 * netId to its archetype and slot.
 *
 * Frames are applied strictly in order, one at a time (`05-sdks.md` § 1): {@link beginFrame}, enters and updates,
 * events, leaves, {@link endFrame}. Nothing asynchronous may reorder them.
 *
 * **netId reuse (`03-wire-protocol.md` § 10).** A frame never carries both an enter and a leave for one netId: the
 * server's quarantine separates them, and for a skipped session it sends the leave and defers the enter to the next
 * frame. So an enter for a netId already held is an anomaly — counted, and the enter replaces the holder. A leave
 * applies when leaves apply, to the netId's holder if it is of the leave's archetype; a leave for a netId nobody holds,
 * or held by another archetype, is an anomaly and changes nothing.
 */
export class WorldStore {
  readonly schema: WorldSchema;
  readonly archetypes: readonly ArchetypeStore[];

  /** Tick of the frame most recently begun; -1 before the first. */
  tick = -1;
  /** Frames applied so far. */
  frames = 0;
  /**
   * Protocol inconsistencies absorbed instead of thrown: unknown netIds, a tick that did not advance outside a `RESET`
   * frame.
   */
  anomalies = 0;
  /** Whether the current frame began with a reset: every slot-keyed state a consumer holds is void. */
  resetThisFrame = false;

  /** Largest netId accepted. */
  readonly maxNetId: number;
  /** netId → `archetype << 24 | slot` (as an int32), or {@link NOT_FOUND}. */
  private locations: Int32Array = new Int32Array(0);

  constructor(schema: WorldSchema, options: WorldStoreOptions = {}) {
    validateSchema(schema);
    const maxNetId = options.maxNetId ?? 1 << 22;
    if (!(Number.isInteger(maxNetId) && maxNetId >= 1 && maxNetId <= 0xffffffff)) {
      throw new Error(`maxNetId must be an integer in [1, 2^32 − 1], got ${maxNetId}`);
    }

    const maxRenderDelayMs = options.maxRenderDelayMs ?? DEFAULT_MAX_RENDER_DELAY_MS;
    if (!(maxRenderDelayMs > 0 && Number.isFinite(maxRenderDelayMs))) {
      throw new Error(`maxRenderDelayMs must be positive and finite, got ${maxRenderDelayMs}`);
    }

    this.schema = schema;
    this.maxNetId = maxNetId;
    const history = segmentHistoryFor(schema.tickPeriodUs, maxRenderDelayMs);
    this.archetypes = schema.archetypes.map((a) => new ArchetypeStore(a, history));
  }

  get entityCount(): number {
    let n = 0;
    for (const a of this.archetypes) {
      n += a.liveCount;
    }

    return n;
  }

  /**
   * Begins a frame. A tick at or below the previous frame's is an anomaly, unless the frame is a `RESET` (`reset`): a
   * restarted server begins again at a low tick, and the client reconnects with the same store (§ 10).
   */
  beginFrame(tick: number, reset = false): void {
    if (!reset && tick <= this.tick) {
      this.anomalies++;
    }

    this.tick = tick;
    this.resetThisFrame = false;
    const archetypes = this.archetypes;
    for (let i = 0; i < archetypes.length; i++) {
      archetypes[i]!.beginFrame();
    }
  }

  endFrame(): void {
    this.frames++;
  }

  /** Adds an entity entering the view and returns its slot. The caller writes its fields and initial motion. */
  enter(archetype: number, netId: number): number {
    if (!(netId > 0 && netId <= this.maxNetId) || !Number.isInteger(netId)) {
      throw new Error(`netId ${netId} is out of range (1..${this.maxNetId})`);
    }

    const store = this.archetypeStore(archetype);
    const previous = this.locate(netId);
    if (previous !== NOT_FOUND) {
      // An enter for a held netId never happens in a well-formed stream (§ 10): count it, and let the enter replace.
      this.anomalies++;
      this.archetypeStore(previous >>> SLOT_BITS).release(previous & SLOT_MASK);
    }

    const slot = store.allocate(netId);
    this.ensureLocations(netId);
    this.locations[netId] = (archetype << SLOT_BITS) | slot;
    return slot;
  }

  /**
   * Removes the holder of `netId`. With `archetype`, only a holder of that archetype: a leave belongs to its block's
   * archetype, as a segment or a state record does. Returns false, and counts an anomaly, when there is no such holder.
   */
  leave(netId: number, archetype = -1): boolean {
    const location = this.locate(netId);
    if (location === NOT_FOUND || (archetype >= 0 && location >>> SLOT_BITS !== archetype)) {
      this.anomalies++;
      return false;
    }

    this.archetypeStore(location >>> SLOT_BITS).release(location & SLOT_MASK);
    this.locations[netId] = NOT_FOUND;
    return true;
  }

  /**
   * `archetype << 24 | slot` of a held netId, or {@link NOT_FOUND}. Decode with {@link archetypeOf} / {@link slotOf}.
   */
  locate(netId: number): number {
    // `>>> 0` keeps only an integer in [0, 2^32): a fraction or a negative id is nobody's.
    return netId >>> 0 === netId && netId < this.locations.length ? this.locations[netId]! : NOT_FOUND;
  }

  /**
   * Drops every entity (a `RESET` frame). Slots are reusable at once, so the refill that follows does not grow the
   * store. The dropped entities are not listed as left: {@link resetThisFrame} voids every slot and netId a consumer
   * knows.
   */
  reset(): void {
    this.resetThisFrame = true;
    for (const store of this.archetypes) {
      for (let i = 0; i < store.liveCount; i++) {
        this.locations[store.netIds[store.live[i]!]!] = NOT_FOUND;
      }

      store.clear();
    }
  }

  archetypeStore(archetype: number): ArchetypeStore {
    const store = this.archetypes[archetype];
    if (store === undefined) {
      throw new Error(`Unknown archetype ${archetype}`);
    }

    return store;
  }

  private ensureLocations(netId: number): void {
    if (netId < this.locations.length) {
      return;
    }

    let length = Math.max(1024, this.locations.length);
    while (length <= netId) {
      length *= 2;
    }

    const next = new Int32Array(Math.min(length, this.maxNetId + 1)).fill(NOT_FOUND);
    next.set(this.locations);
    this.locations = next;
  }
}

export function archetypeOf(location: number): number {
  return location >>> SLOT_BITS;
}

export function slotOf(location: number): number {
  return location & SLOT_MASK;
}
