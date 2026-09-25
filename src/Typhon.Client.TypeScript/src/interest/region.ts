import type { CatalogCodec, CatalogCommand, CatalogPlan, MessagePlan } from '../protocol/catalog.js';
import { AckReason, BuiltInCommand } from '../protocol/constants.js';
import type { FieldValues } from '../protocol/field-codec.js';

/** The fewest vertices a flat world's region may carry: a triangle. */
export const REGION_MIN_VERTICES = 3;
/** The fewest vertices a deep world's region may carry: a tetrahedron. */
export const REGION_MIN_VERTICES_3D = 4;
/** The most: a horizon-clipped frustum needs 5–8, and 16 is headroom (W28). */
export const REGION_MAX_VERTICES = 16;
/** The built-in's rate, which is also the wire's ceiling for regions: 5 a second, burst 5. */
export const REGION_RATE = { perSec: 5, burst: 5 } as const;

/**
 * The built-in `ClientRegion` command for a world whose positions use `position`, matching what the engine builds
 * (`BuiltInCommands.CreateClientRegion`). Its vertices are quantized exactly like the archetypes' positions, so a
 * decoded region can never leave the world, and its fields are in canonical wire order (W11, W28). A `pos2` world's
 * region is a polygon of 3–16 points; a `pos3` world's a polyhedron of 4–16.
 */
export function createClientRegion(position: CatalogCodec): CatalogCommand {
  return {
    idx: BuiltInCommand.ClientRegionIdx,
    name: BuiltInCommand.ClientRegion,
    delivery: 'latest',
    rate: { perSec: REGION_RATE.perSec, burst: REGION_RATE.burst },
    // Canonical wire order: no field is packed, so they follow their names (W11).
    fields: [
      { name: BuiltInCommand.regionAltitudeField, codec: { t: 'f16' } },
      { name: BuiltInCommand.regionBudgetField, codec: { t: 'u16' } },
      {
        name: BuiltInCommand.regionVerticesField,
        codec: {
          t: 'list',
          of: position,
          minCount: position.t === 'pos3' ? REGION_MIN_VERTICES_3D : REGION_MIN_VERTICES,
          maxCount: REGION_MAX_VERTICES,
        },
      },
    ],
  };
}

export interface RegionOptions {
  readonly plan: CatalogPlan;
  /**
   * Where a region goes: `CommandQueue.enqueue` fits. Return its sequence number — or −1 when the queue refused it —
   * and {@link RegionSender.onAck} can tell this client's own regions from anything else in the `ACKS` block.
   */
  readonly send: (type: MessagePlan, values: FieldValues) => number | undefined;
  /**
   * The server refused a region: its hull had fewer than three points or no area, and it kept the previous one
   * (`ACKS` reason `REGION_INVALID`). The footprint is not resent — a client must not repeat a refused one — so a
   * consumer that wants a region has to offer a different shape.
   */
  readonly onRejected?: (seq: number) => void;
  readonly now?: () => number;
  /** The shortest interval between two regions. Default 200 ms — the built-in's 5 Hz. */
  readonly minIntervalMs?: number;
  /** A vertex must move this far, in world units, for a region to be worth sending. Default 4. */
  readonly moveThreshold?: number;
  /** Likewise for the viewpoint altitude, in metres. Default 4. */
  readonly altitudeThreshold?: number;
}

/**
 * Sends the client's viewpoint footprint as the built-in `ClientRegion` command (W28): a convex polygon of 3–16 points —
 * a quad at minimum, a horizon-clipped frustum exactly — with the viewpoint's altitude and the client's byte budget.
 *
 * - **A threshold, then a rate.** A region whose vertices all moved less than `moveThreshold` is not worth its bytes and
 *   is dropped. One that is worth sending waits, if need be, until `minIntervalMs` has passed since the last: latest
 *   wins, so only the newest of a burst ever travels. {@link poll} sends what is waiting — call it once a rendered frame.
 * - **The server hulls it.** Vertices need not be convex or wound: the engine takes the convex hull and turns it into
 *   half-planes. A hull of fewer than 3 points or no area is answered with `ACKS` reason `REGION_INVALID` and the
 *   previous region is kept. Feed that block to {@link onAck}: the refusal is surfaced through
 *   {@link RegionOptions.onRejected}, and the same footprint is never sent again — the SDK does not retry for you.
 */
export class RegionSender {
  private readonly options: RegionOptions;
  private readonly now: () => number;
  private readonly minIntervalMs: number;
  private readonly moveThreshold: number;
  private readonly altitudeThreshold: number;
  private readonly command: MessagePlan;
  /** 2 for a flat world's polygon, 3 for a deep world's polyhedron: the vertex codec's axes. */
  private readonly dims: number;

  /** The region last handed to {@link setRegion}, flattened `x, y` pairs. */
  private desired: Float64Array = new Float64Array(0);
  private desiredAltitude = 0;
  private desiredBudget = 0;
  /** The region last sent, to compare against. */
  private sentVertices: Float64Array = new Float64Array(0);
  private sentAltitude = Number.NaN;
  private sentBudget = -1;
  private lastSentMs = Number.NEGATIVE_INFINITY;
  private waiting = false;
  private sent = 0;
  private suppressed = 0;
  private rejected = 0;
  /** The sequence number of the region in flight, when the send path returned one. */
  private pendingSeq = -1;

  constructor(options: RegionOptions) {
    const command = options.plan.clientRegion;
    if (command === null) {
      throw new Error('this catalog has no ClientRegion command: the server did not enable it');
    }

    this.options = options;
    this.command = command;
    const vertices = command.body.fields.find((f) => f.name === BuiltInCommand.regionVerticesField);
    this.dims = vertices?.components === 3 ? 3 : 2;
    this.now = options.now ?? Date.now;
    const rate = options.plan.catalog.commands.find((c) => c.idx === BuiltInCommand.ClientRegionIdx)?.rate;
    this.minIntervalMs = options.minIntervalMs ?? (rate === undefined ? 200 : 1000 / rate.perSec);
    this.moveThreshold = options.moveThreshold ?? 4;
    this.altitudeThreshold = options.altitudeThreshold ?? 4;
  }

  /** Regions sent. */
  get sentCount(): number {
    return this.sent;
  }

  /** Regions dropped because they barely differed from the one sent. */
  get suppressedCount(): number {
    return this.suppressed;
  }

  /** Regions the server refused (`REGION_INVALID`). */
  get rejectedCount(): number {
    return this.rejected;
  }

  /** Whether a region is waiting for the rate to allow it. */
  get isPending(): boolean {
    return this.waiting;
  }

  /**
   * One `ACKS` record. A `REGION_INVALID` for the region in flight is reported through
   * {@link RegionOptions.onRejected}; the refused footprint stays the one this sender compares against, so it is not
   * sent again. Returns whether the record was this sender's.
   */
  onAck(seq: number, reason: number): boolean {
    if (seq !== this.pendingSeq || this.pendingSeq < 0) {
      return false;
    }

    this.pendingSeq = -1;
    if (reason !== AckReason.RegionInvalid) {
      return true;
    }

    this.rejected++;
    this.options.onRejected?.(seq);
    return true;
  }

  /**
   * Offers a new region: `vertices` is flattened `x, y` pairs (3–16) in a flat world, `x, y, z` triples (4–16) in a
   * deep one, in the grid's units. Returns whether it went out now; otherwise it is either dropped as too similar, or
   * kept for {@link poll}.
   */
  setRegion(vertices: ArrayLike<number>, altitudeM: number, budgetKiBps: number): boolean {
    const count = vertices.length / this.dims;
    const min = this.dims === 3 ? REGION_MIN_VERTICES_3D : REGION_MIN_VERTICES;
    if (!Number.isInteger(count) || count < min || count > REGION_MAX_VERTICES) {
      throw new RangeError(
        `a region needs ${min}–${REGION_MAX_VERTICES} vertices of ${this.dims} axes, got ${vertices.length} values`,
      );
    }

    for (let i = 0; i < vertices.length; i++) {
      if (!Number.isFinite(vertices[i]!)) {
        throw new RangeError(`region vertex ${Math.floor(i / this.dims)} is not finite`);
      }
    }

    if (this.desired.length !== vertices.length) {
      this.desired = new Float64Array(vertices.length);
    }

    for (let i = 0; i < vertices.length; i++) {
      this.desired[i] = vertices[i]!;
    }

    this.desiredAltitude = altitudeM;
    this.desiredBudget = budgetKiBps;
    if (!this.differs()) {
      this.suppressed++;
      this.waiting = false;
      return false;
    }

    this.waiting = true;
    return this.poll();
  }

  /** Sends the waiting region once the rate allows it. Returns whether one went out. */
  poll(nowMs: number = this.now()): boolean {
    if (!this.waiting || nowMs - this.lastSentMs < this.minIntervalMs) {
      return false;
    }

    const vertices: number[] = [];
    for (let i = 0; i < this.desired.length; i++) {
      vertices.push(this.desired[i]!);
    }

    const values: FieldValues = {
      [BuiltInCommand.regionVerticesField]: vertices,
      [BuiltInCommand.regionAltitudeField]: this.desiredAltitude,
      [BuiltInCommand.regionBudgetField]: this.desiredBudget,
    };
    const seq = this.options.send(this.command, values);
    this.pendingSeq = seq ?? -1;
    if (this.sentVertices.length !== this.desired.length) {
      this.sentVertices = new Float64Array(this.desired.length);
    }

    this.sentVertices.set(this.desired);
    this.sentAltitude = this.desiredAltitude;
    this.sentBudget = this.desiredBudget;
    this.lastSentMs = nowMs;
    this.waiting = false;
    this.sent++;
    return true;
  }

  /** Whether the desired region is far enough from the one sent to be worth its bytes. */
  private differs(): boolean {
    if (this.sentVertices.length !== this.desired.length || this.desiredBudget !== this.sentBudget) {
      return true;
    }

    if (!(Math.abs(this.desiredAltitude - this.sentAltitude) < this.altitudeThreshold)) {
      return true;
    }

    for (let i = 0; i < this.desired.length; i++) {
      if (Math.abs(this.desired[i]! - this.sentVertices[i]!) >= this.moveThreshold) {
        return true;
      }
    }

    return false;
  }
}
