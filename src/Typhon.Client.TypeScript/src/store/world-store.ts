import { ArchetypeStore } from './archetype-store.js';
import { validateSchema, type WorldSchema } from './schema.js';

/** Sentinel returned by {@link WorldStore.locate} for an unknown netId. */
export const NOT_FOUND = 0xffffffff;

const SLOT_BITS = 24;
const SLOT_MASK = (1 << SLOT_BITS) - 1;

export interface WorldStoreOptions {
  /**
   * Largest netId accepted. netIds are dense on the server, so the netId → slot map is a flat array of this many entries
   * at most (4 B each); a corrupt id beyond it throws instead of allocating. Default 2²² (16 MiB worst case).
   */
  readonly maxNetId?: number;
}

/**
 * The client's replica of what the server has shown it: one {@link ArchetypeStore} per archetype and a map from global
 * netId to its archetype and slot.
 *
 * Frames are applied strictly in order, one at a time (`05-sdks.md` § 1): {@link beginFrame}, enters and updates, events,
 * leaves, {@link endFrame}. Nothing asynchronous may reorder them.
 *
 * **A netId reused inside one frame.** When the server releases an id and hands it to another entity within the same
 * tick, the frame carries the new entity's enter and — because leaves are applied last — the old entity's leave after it.
 * The store resolves that pair: the enter replaces the old entity, and the leave that follows in the same frame is
 * recognised as belonging to the replaced one and ignored.
 */
export class WorldStore {
  readonly schema: WorldSchema;
  readonly archetypes: readonly ArchetypeStore[];

  /** Tick of the frame most recently begun; -1 before the first. */
  tick = -1;
  /** Frames applied so far. */
  frames = 0;
  /** Protocol inconsistencies absorbed instead of thrown: unknown netIds, a tick that did not advance. */
  anomalies = 0;
  /** Whether the current frame began with a reset: every slot-keyed state a consumer holds is void. */
  resetThisFrame = false;

  private readonly maxNetId: number;
  /** netId → `archetype << 24 | slot`, or {@link NOT_FOUND}. */
  private locations: Uint32Array = new Uint32Array(0);
  /** netIds whose previous holder an enter replaced this frame; their leave, later in the frame, is the old one's. */
  private readonly replaced: number[] = [];

  constructor(schema: WorldSchema, options: WorldStoreOptions = {}) {
    validateSchema(schema);
    this.schema = schema;
    this.archetypes = schema.archetypes.map((a) => new ArchetypeStore(a));
    this.maxNetId = options.maxNetId ?? 1 << 22;
  }

  get entityCount(): number {
    let n = 0;
    for (const a of this.archetypes) {
      n += a.liveCount;
    }

    return n;
  }

  beginFrame(tick: number): void {
    if (tick <= this.tick) {
      this.anomalies++;
    }

    this.tick = tick;
    this.resetThisFrame = false;
    this.replaced.length = 0;
    for (const a of this.archetypes) {
      a.beginFrame();
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
      this.archetypeStore(previous >>> SLOT_BITS).release(previous & SLOT_MASK);
      this.replaced.push(netId);
    }

    const slot = store.allocate(netId);
    this.ensureLocations(netId);
    this.locations[netId] = ((archetype << SLOT_BITS) | slot) >>> 0;
    return slot;
  }

  /** Removes an entity leaving the view. Returns false when the netId was not held. */
  leave(netId: number): boolean {
    const replacedAt = this.replaced.indexOf(netId);
    if (replacedAt >= 0) {
      // The leave of the entity an enter already replaced this frame (see the class remarks).
      this.replaced.splice(replacedAt, 1);
      return true;
    }

    const location = this.locate(netId);
    if (location === NOT_FOUND) {
      this.anomalies++;
      return false;
    }

    this.archetypeStore(location >>> SLOT_BITS).release(location & SLOT_MASK);
    this.locations[netId] = NOT_FOUND;
    return true;
  }

  /** `archetype << 24 | slot` of a held netId, or {@link NOT_FOUND}. Decode with {@link archetypeOf} / {@link slotOf}. */
  locate(netId: number): number {
    return netId >= 0 && netId < this.locations.length ? this.locations[netId]! : NOT_FOUND;
  }

  /**
   * Drops every entity (a `RESET` frame). Each appears in its archetype's left list for this frame, and slots are reusable
   * at once, so the refill that follows does not grow the store; {@link resetThisFrame} tells consumers to drop slot state.
   */
  reset(): void {
    this.resetThisFrame = true;
    this.replaced.length = 0;
    for (const store of this.archetypes) {
      while (store.liveCount > 0) {
        const slot = store.live[store.liveCount - 1]!;
        this.locations[store.netIds[slot]!] = NOT_FOUND;
        store.release(slot, true);
      }
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

    const next = new Uint32Array(Math.min(length, this.maxNetId + 1)).fill(NOT_FOUND);
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
