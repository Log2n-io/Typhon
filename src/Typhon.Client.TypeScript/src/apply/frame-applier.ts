import { AggregateGrid } from '../aggregates/aggregate-grid.js';
import type { Clock } from '../clock/clock.js';
import {
  ValueKind,
  type ArchetypePlan,
  type CatalogPlan,
  type FieldPlan,
  type GridPlan,
  type MessagePlan,
  type MetricPlan,
} from '../protocol/catalog.js';
import { TickFlags } from '../protocol/constants.js';
import type { RealmFrame } from '../protocol/realm-frame.js';
import { catalogHashToHex } from '../protocol/messages.js';
import {
  BlockMask,
  TickReader,
  type EntitiesTarget,
  type GeneratedDecoders,
  type TickSink,
} from '../protocol/tick-reader.js';
import type { ArchetypeStore } from '../store/archetype-store.js';
import type { FieldArray, NumericFieldKind } from '../store/schema.js';
import { archetypeOf, NOT_FOUND, slotOf, WorldStore } from '../store/world-store.js';
import { AckList, EventRecord, retainBytes, SelfState, SourceList, StatsState } from './frame-state.js';
import { gridSchemaFromCatalog, worldSchemaFromCatalog } from './schema-from-catalog.js';

export interface FrameApplierOptions {
  /** Largest netId the store accepts; an enter beyond it is counted as an anomaly. Default 2²². */
  readonly maxNetId?: number;
  /**
   * The largest render delay motion is evaluated at, sizing the store's segment rings. Default: the `clock`'s
   * `maxDelayMs` when a clock is given, else 300 ms (the `Clock`'s default). Pass it only to size the rings for another
   * delay than the clock's.
   */
  readonly maxRenderDelayMs?: number;
  /** Receives `PERIOD` changes, and each frame's receive time when {@link FrameApplier.apply} is given one. */
  readonly clock?: Clock;
  /**
   * Called once per event, after every other block of its frame has applied and before its leaves, with a record reused
   * for every event of its type.
   *
   * **A throw propagates** out of {@link FrameApplier.apply}. The store then holds a partial frame (the frame's leaves,
   * among others, have not applied), so the session must be closed — the same contract as a `WireFormatError`. Catch
   * inside the handler to survive a handler bug.
   */
  readonly onEvent?: (event: EventRecord) => void;
  /** Called per `DEBUG` sub-block with a view of the message, valid only during the call. */
  readonly onDebug?: (subType: number, data: Uint8Array, offset: number, length: number) => void;
  /** Called per `EXT` block with a view of the message, valid only during the call. */
  readonly onExt?: (appTypeId: number, data: Uint8Array, offset: number, length: number) => void;
  /**
   * Generated `ENTITIES` decoders (`typhon-codegen`, 05-sdks § 2): each archetype's records are decoded straight into
   * the store, without the interpreter's per-field dispatch. Requires {@link catalogHash}; the applier refuses decoders
   * generated from another catalog. Every other block, and an archetype the module leaves `null`, uses the interpreter.
   */
  readonly decoders?: GeneratedDecoders;
  /** The session's catalog hash (`SessionInfo.catalogHash`), which {@link decoders} must have been generated from. */
  readonly catalogHash?: Uint8Array;
  /**
   * Called when a `REALM` block changes the session's realm (`typhon.3`): the previous frame (or `null`) and the new one
   * (or `null` for `REALM(NONE)`). Called once, after the `RESET` that carried it cleared the store and before any of the
   * frame's records apply — the moment to load the new realm's scene, by its {@link RealmFrame.appTag}.
   */
  readonly onRealmChanged?: (previous: RealmFrame | null, current: RealmFrame | null) => void;
  /**
   * The realm the session is already in, for an applier that starts mid-stream — a recording replayed from a later
   * frame, a test — whose first frame will not carry the `REALM` that placed it. Normally absent: a session's first
   * frame is a `RESET` that carries its `REALM`.
   */
  readonly initialRealm?: RealmFrame;
}

/** A numeric column's element type, as {@link storeColumn} dispatches on it. */
const COLUMN_KIND: Readonly<Record<NumericFieldKind, number>> = {
  u8: 0,
  i8: 1,
  u16: 2,
  i16: 3,
  u32: 4,
  i32: 5,
  f32: 6,
  f64: 7,
};

/** What the field values that follow a record call belong to. */
const Target = { None: 0, Entity: 1, Owner: 2 } as const;
type Target = (typeof Target)[keyof typeof Target];

/**
 * Decodes `TICK` messages straight into a {@link WorldStore}: one copy, from the wire into the store's typed arrays,
 * with no allocation per record (05-sdks § 1).
 *
 * **Apply order (`03-wire-protocol.md` § 10):** every block but `EVENTS` → `EVENTS` → leaves, whatever order the
 * blocks travel in. A first pass applies everything but events and collects leaves; a second pass reads only the
 * `EVENTS` blocks (the others are skipped by their length); then the leaves apply. An event handler therefore sees the
 * whole frame — its enters and updates, `SOURCES`, `SELF`, `ACKS`, `AGG` and `STATS` — and still resolves every
 * entity that leaves in it.
 *
 * **Per frame**, after {@link apply}: the world's change lists; {@link selfState}, {@link acks}, {@link sources} and
 * {@link stats} for this frame; {@link tick}, {@link flags} and {@link periodUs}. A `RESET` frame clears the world, the
 * aggregates and the owner state before anything in it applies; metrics are not entity state and survive it.
 *
 * **Inconsistencies are absorbed and counted** in the world's `anomalies` (§ 10): a segment, state record or leave for a
 * netId its block's archetype does not hold (it changes nothing), an enter for a held netId (it replaces the holder), an
 * enter beyond `maxNetId`. A malformed message throws `WireFormatError`; the store may then hold part of the frame, and
 * the connection must be closed with the error's code.
 */
export class FrameApplier implements TickSink, EntitiesTarget {
  readonly plan: CatalogPlan;
  readonly world: WorldStore;
  /** Per catalog grid, by index; each allocates its counts on its first `AGG`. */
  readonly grids: readonly AggregateGrid[];
  /** The controlled entity's owner state (`SELF`). */
  readonly selfState = new SelfState();
  readonly acks = new AckList();
  readonly sources = new SourceList();
  readonly stats: StatsState;

  /** The current frame's tick; −1 before the first. */
  tick = -1;
  flags = 0;
  /** The elapsed interval's period in microseconds when the frame carried `PERIOD`, otherwise 0. */
  periodUs = 0;

  private readonly options: FrameApplierOptions;
  private readonly reader: TickReader;
  private readonly eventPass: EventPass;
  /** Per archetype: the store field index of each public `FieldPlan.index`, or −1 when nothing is stored for it. */
  private readonly storeFieldOf: readonly Int32Array[];
  /** Per archetype: the column type of each public `FieldPlan.index` (see {@link storeColumn}), or −1. */
  private readonly storeKindOf: readonly Int8Array[];

  private target: Target = Target.None;
  private archetype = 0;
  private store: ArchetypeStore | null = null;
  private storeFields: Int32Array = new Int32Array(0);
  private storeKinds: Int8Array = new Int8Array(0);
  private slot = 0;
  private grid: AggregateGrid | null = null;
  /** This frame's leaves, applied last: netId and the archetype of the block that carried it. */
  private leaves = new Uint32Array(256);
  private leaveArchetypes = new Uint8Array(256);
  private leaveCount = 0;
  /** The realm the last `REALM` block set, for {@link FrameApplierOptions.onRealmChanged}. */
  private heldRealm: RealmFrame | null = null;

  constructor(plan: CatalogPlan, options: FrameApplierOptions = {}) {
    this.plan = plan;
    this.options = options;
    // The ring follows the clock that sets render time, unless told otherwise.
    const maxRenderDelayMs = options.maxRenderDelayMs ?? options.clock?.maxDelayMs;
    this.world = new WorldStore(worldSchemaFromCatalog(plan), {
      ...(options.maxNetId === undefined ? {} : { maxNetId: options.maxNetId }),
      ...(maxRenderDelayMs === undefined ? {} : { maxRenderDelayMs }),
    });
    this.storeFieldOf = plan.archetypes.map((a) => {
      const store = this.world.archetypeStore(a.idx);
      return Int32Array.from(a.fields, (f) => store.fieldIndex(f.name));
    });
    this.storeKindOf = plan.archetypes.map((a) => {
      const fields = this.world.archetypeStore(a.idx).schema.fields;
      return Int8Array.from(this.storeFieldOf[a.idx]!, (index) => {
        const kind = index < 0 ? undefined : fields[index]!.kind;
        return kind === undefined || kind === 'text' || kind === 'bytes' ? -1 : COLUMN_KIND[kind];
      });
    });
    // Placeholders until the first REALM lays each grid over its realm (typhon.3): an AGG before one is refused.
    this.grids = plan.grids.map((g) => new AggregateGrid(gridSchemaFromCatalog(g.grid, null)));
    this.stats = new StatsState(plan);
    const decoders = options.decoders;
    if (decoders !== undefined) {
      checkDecoders(plan, decoders, options.catalogHash, this.storeFieldOf);
    }

    this.reader = decoders === undefined ? new TickReader(plan) : new TickReader(plan, { decoders, target: this });
    this.eventPass = new EventPass(plan, options.onEvent);
    if (options.initialRealm !== undefined) {
      this.reader.realm = options.initialRealm;
      this.heldRealm = options.initialRealm;
      for (let i = 0; i < this.grids.length; i++) {
        this.grids[i]!.reframe(gridSchemaFromCatalog(plan.grids[i]!.grid, options.initialRealm));
      }
    }
  }

  /**
   * Decodes and applies one `TICK` message, type byte included. `recvMs`, the local time it was received, is recorded
   * in the clock when one is configured.
   */
  apply(message: Uint8Array, recvMs?: number): void {
    this.reader.read(message, this, BlockMask.All & ~BlockMask.Events);
    this.eventPass.tick = this.tick;
    this.reader.read(message, this.eventPass, BlockMask.Events);

    const world = this.world;
    for (let i = 0; i < this.leaveCount; i++) {
      world.leave(this.leaves[i]!, this.leaveArchetypes[i]);
    }

    this.leaveCount = 0;
    world.endFrame();

    const clock = this.options.clock;
    if (clock !== undefined) {
      // A RESET frame going back in time is a restarted server: the clock's timeline and render time belong to the old
      // one, and would otherwise hold render time at the old tick until the new one caught up.
      if ((this.flags & TickFlags.Reset) !== 0 && this.tick < clock.latestTick) {
        clock.reset();
      }

      if (this.periodUs !== 0) {
        // PERIOD in frame N + 1 is the duration of [N, N + 1] (§ 3).
        clock.onPeriodChange(this.tick - 1, this.periodUs / 1000);
      }

      if (recvMs !== undefined) {
        clock.onFrame(this.tick, recvMs);
      }
    }
  }

  /**
   * The session's realm (`typhon.3`): the frame every position the store holds was decoded over, or `null` before the
   * first `REALM` and after a `REALM(NONE)`.
   */
  get realmFrame(): RealmFrame | null {
    return this.reader.realm;
  }

  /** The name of {@link realmFrame}'s kind, from the catalog's `realmKinds`; `null` in no realm. */
  get realmKind(): string | null {
    const frame = this.reader.realm;
    return frame === null ? null : (this.plan.realmKinds[frame.kindIdx] ?? null);
  }

  realm(frame: RealmFrame | null): void {
    this.target = Target.None;
    const previous = this.heldRealm;
    this.heldRealm = frame;
    const grids = this.grids;
    for (let i = 0; i < grids.length; i++) {
      grids[i]!.reframe(gridSchemaFromCatalog(this.plan.grids[i]!.grid, frame));
    }

    if (frame === null ? previous !== null : !frame.equals(previous)) {
      this.options.onRealmChanged?.(previous, frame);
    }
  }

  beginTick(tick: number, flags: number, periodUs: number): void {
    this.tick = tick;
    this.flags = flags;
    this.periodUs = periodUs;
    this.target = Target.None;
    this.leaveCount = 0;
    this.acks.count = 0;
    this.sources.count = 0;
    this.stats.received = false;
    this.selfState.beginFrame();
    this.world.beginFrame(tick, (flags & TickFlags.Reset) !== 0);
    const grids = this.grids;
    for (let i = 0; i < grids.length; i++) {
      grids[i]!.beginFrame();
    }

    if ((flags & TickFlags.Reset) !== 0) {
      this.world.reset();
      for (const grid of this.grids) {
        grid.reset();
      }

      this.selfState.clear();
    }
  }

  beginEntities(archetype: ArchetypePlan): void {
    this.archetype = archetype.idx;
    this.store = this.world.archetypeStore(archetype.idx);
    this.storeFields = this.storeFieldOf[archetype.idx]!;
    this.storeKinds = this.storeKindOf[archetype.idx]!;
    this.target = Target.None;
  }

  enter(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): void {
    const slot = this.enterSlot(netId, position, velocity, t0, epoch);
    this.slot = slot;
    this.target = slot < 0 ? Target.None : Target.Entity;
  }

  segment(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): void {
    this.target = Target.None;
    this.segmentAt(netId, position, velocity, t0, epoch);
  }

  state(netId: number, groupMask: number): void {
    const slot = this.stateSlot(netId, groupMask);
    this.slot = slot;
    this.target = slot < 0 ? Target.None : Target.Entity;
  }

  // ── EntitiesTarget: what a generated decoder calls, only from inside `apply`, while its archetype's ENTITIES block is read. ──

  archetypeStore(idx: number): ArchetypeStore {
    return this.world.archetypeStore(idx);
  }

  enterSlot(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): number {
    const world = this.world;
    if (netId === 0 || netId > world.maxNetId) {
      world.anomalies++;
      return -1;
    }

    const store = this.store!;
    const slot = world.enter(this.archetype, netId);
    if (store.hasPosition) {
      store.resetMotionFrom(slot, position, store.linear ? velocity : null, t0, epoch);
    }

    return slot;
  }

  segmentAt(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): void {
    const location = this.world.locate(netId);
    if (location === NOT_FOUND || archetypeOf(location) !== this.archetype) {
      this.world.anomalies++;
      return;
    }

    const store = this.store!;
    store.pushSegmentFrom(slotOf(location), position, store.linear ? velocity : null, t0, epoch);
  }

  stateSlot(netId: number, groupMask: number): number {
    const location = this.world.locate(netId);
    if (location === NOT_FOUND || archetypeOf(location) !== this.archetype) {
      this.world.anomalies++;
      return -1;
    }

    const slot = slotOf(location);
    this.store!.markUpdated(slot, groupMask);
    return slot;
  }

  leave(netId: number): void {
    this.target = Target.None;
    if (this.leaveCount === this.leaves.length) {
      const next = new Uint32Array(this.leaves.length * 2);
      next.set(this.leaves);
      this.leaves = next;
      const archetypes = new Uint8Array(next.length);
      archetypes.set(this.leaveArchetypes);
      this.leaveArchetypes = archetypes;
    }

    this.leaveArchetypes[this.leaveCount] = this.archetype;
    this.leaves[this.leaveCount++] = netId;
  }

  event(): void {
    // Events are read by the second pass (EventPass); the first pass never selects their blocks.
  }

  self(archetype: ArchetypePlan | null, netId: number, lastSeq: number, ownerMask: number): void {
    this.selfState.receive(archetype, netId, lastSeq, ownerMask);
    this.target = Target.Owner;
  }

  ack(seq: number, reason: number): void {
    this.acks.push(seq, reason);
  }

  source(requestId: number, status: number, code: number): void {
    this.sources.push(requestId, status, code);
  }

  beginAggregate(grid: GridPlan, reset: boolean): void {
    this.target = Target.None;
    const counts = this.grids[grid.idx]!;
    if (reset) {
      counts.reset();
    }

    this.grid = counts;
  }

  aggregateCell(cell: number, counts: Uint32Array): void {
    this.grid!.setCell(cell, counts, 0);
  }

  metric(metric: MetricPlan, valueIndex: number, value: number): void {
    const stats = this.stats;
    stats.values[metric.offset + valueIndex] = value;
    stats.tick = this.tick;
    stats.received = true;
  }

  debug(subType: number, data: Uint8Array, offset: number, length: number): void {
    this.target = Target.None;
    this.options.onDebug?.(subType, data, offset, length);
  }

  ext(appTypeId: number, data: Uint8Array, offset: number, length: number): void {
    this.target = Target.None;
    this.options.onExt?.(appTypeId, data, offset, length);
  }

  unknownBlock(): void {
    this.target = Target.None;
  }

  endTick(): void {
    this.target = Target.None;
  }

  number(field: FieldPlan, values: Float64Array): void {
    if (this.target === Target.Entity) {
      const index = this.storeFields[field.index]!;
      if (index >= 0) {
        const components = field.components;
        storeColumn(
          this.storeKinds[field.index]!,
          this.store!.columns[index]!,
          this.slot * components,
          values,
          components,
        );
      }
    } else if (this.target === Target.Owner) {
      this.selfState.setNumber(field, values);
    }
  }

  text(field: FieldPlan, value: string): void {
    if (this.target === Target.Entity) {
      const index = this.storeFields[field.index]!;
      if (index >= 0) {
        this.store!.textAt(index)[this.slot] = value;
      }
    } else if (this.target === Target.Owner) {
      this.selfState.setText(field, value);
    }
  }

  bytes(field: FieldPlan, data: Uint8Array, offset: number, length: number): void {
    if (this.target === Target.Entity) {
      const index = this.storeFields[field.index]!;
      if (index >= 0) {
        const column = this.store!.bytesAt(index);
        column[this.slot] = retainBytes(data, offset, length, column[this.slot]);
      }
    } else if (this.target === Target.Owner) {
      this.selfState.setBytes(field, data, offset, length);
    }
  }

  list(): void {
    // Lists are event and command fields only: the catalog refuses them on archetypes and owner sections.
  }
}

/**
 * Refuses generated decoders the applier cannot trust: generated from another catalog than the session's, shaped for
 * another archetype count, or writing a field the store does not hold (every public field is stored, so this is a guard
 * against a store schema that changed under a generated module).
 */
function checkDecoders(
  plan: CatalogPlan,
  decoders: GeneratedDecoders,
  catalogHash: Uint8Array | undefined,
  storeFieldOf: readonly Int32Array[],
): void {
  if (catalogHash === undefined) {
    throw new Error('generated decoders need the session catalog hash (catalogHash) to be checked against');
  }

  const hash = catalogHashToHex(catalogHash);
  if (hash !== decoders.catalogHash) {
    throw new Error(
      `the generated decoders come from catalog ${decoders.catalogHash}, but the session's is ${hash}: regenerate them with typhon-codegen`,
    );
  }

  if (decoders.entities.length !== plan.archetypes.length) {
    throw new Error(
      `the generated decoders cover ${decoders.entities.length} archetype(s), the catalog ${plan.archetypes.length}`,
    );
  }

  for (const archetype of plan.archetypes) {
    const unstored = archetype.fields.some(
      (f) => f.valueKind !== ValueKind.Skipped && storeFieldOf[archetype.idx]![f.index]! < 0,
    );
    if (decoders.entities[archetype.idx] != null && unstored) {
      throw new Error(
        `archetype '${archetype.name}' has a field the store does not hold; the generated decoder cannot write it`,
      );
    }
  }
}

/** The second pass: reads only `EVENTS` blocks, fills each type's reused record and dispatches it once complete. */
class EventPass implements TickSink {
  tick = 0;

  /** The first pass applied the REALM; the reader already holds it for this pass's positions. */
  realm(): void {}

  private readonly records: readonly (EventRecord | undefined)[];
  private readonly handler: ((event: EventRecord) => void) | undefined;
  private pending: EventRecord | null = null;

  constructor(plan: CatalogPlan, handler: ((event: EventRecord) => void) | undefined) {
    const records: (EventRecord | undefined)[] = [];
    for (const e of plan.catalog.events) {
      records[e.idx] = new EventRecord(plan.event(e.idx));
    }

    this.records = records;
    this.handler = handler;
  }

  event(type: MessagePlan): void {
    this.flush();
    const record = this.records[type.idx]!;
    record.tick = this.tick;
    this.pending = record;
  }

  endTick(): void {
    this.flush();
  }

  number(field: FieldPlan, values: Float64Array): void {
    this.pending?.setNumber(field, values);
  }

  text(field: FieldPlan, value: string): void {
    this.pending?.setText(field, value);
  }

  bytes(field: FieldPlan, data: Uint8Array, offset: number, length: number): void {
    this.pending?.setBytes(field, data, offset, length);
  }

  list(field: FieldPlan, count: number, values: Float64Array): void {
    this.pending?.setList(field, count, values);
  }

  beginTick(): void {
    this.pending = null;
  }

  beginEntities(): void {}
  enter(): void {}
  segment(): void {}
  state(): void {}
  leave(): void {}
  self(): void {}
  ack(): void {}
  source(): void {}
  beginAggregate(): void {}
  aggregateCell(): void {}
  metric(): void {}
  debug(): void {}
  ext(): void {}
  unknownBlock(): void {}

  private flush(): void {
    const record = this.pending;
    this.pending = null;
    if (record !== null && this.handler !== undefined) {
      this.handler(record);
    }
  }
}

/**
 * Writes `count` values into a numeric column, through one store site per element type. A single `column[i] = v` over
 * eight typed-array types is megamorphic, and V8 boxes every fractional or wide number it hands to a megamorphic store:
 * a heap number per value per record (AC-6). Each case below sees one array type, so its store is monomorphic.
 */
function storeColumn(kind: number, column: FieldArray, base: number, values: Float64Array, count: number): void {
  switch (kind) {
    case 0: {
      const c = column as Uint8Array;
      for (let i = 0; i < count; i++) {
        c[base + i] = values[i]!;
      }

      break;
    }
    case 1: {
      const c = column as Int8Array;
      for (let i = 0; i < count; i++) {
        c[base + i] = values[i]!;
      }

      break;
    }
    case 2: {
      const c = column as Uint16Array;
      for (let i = 0; i < count; i++) {
        c[base + i] = values[i]!;
      }

      break;
    }
    case 3: {
      const c = column as Int16Array;
      for (let i = 0; i < count; i++) {
        c[base + i] = values[i]!;
      }

      break;
    }
    case 4: {
      const c = column as Uint32Array;
      for (let i = 0; i < count; i++) {
        c[base + i] = values[i]!;
      }

      break;
    }
    case 5: {
      const c = column as Int32Array;
      for (let i = 0; i < count; i++) {
        c[base + i] = values[i]!;
      }

      break;
    }
    case 6: {
      const c = column as Float32Array;
      for (let i = 0; i < count; i++) {
        c[base + i] = values[i]!;
      }

      break;
    }
    case 7: {
      const c = column as Float64Array;
      for (let i = 0; i < count; i++) {
        c[base + i] = values[i]!;
      }

      break;
    }
    default:
      break;
  }
}
