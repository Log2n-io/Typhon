import { ProtocolConstants } from './constants.js';
import { malformed } from './errors.js';
import { quantStep, unsignedTop } from './math.js';
import type { WireReader } from './reader.js';
import type { WireWriter } from './writer.js';

/** `REALM` flags bit 0: the session is in no realm; nothing follows the flags. */
const FLAG_NONE = 1;
/** `REALM` flags bit 1: the realm is three-dimensional. */
const FLAG_DEEP = 2;
/** The realm id a `REALM(NONE)` carries. */
export const NO_REALM = 0xffff;

/**
 * The realm a session's positions are framed in (`typhon.3`, 12-realms § 5.2): what the `REALM` block carries, and what
 * every realm-framed codec — `pos2`, `pos3`, the `AGG` grid — is quantized over and decoded with (SUB-30). The C#
 * `RealmFrame`'s twin, validated identically.
 *
 * `REALM := u16 realmId | u16 generation | u8 flags [unless NONE: varu kindIdx | u32 appTag | u8 posBits | f64 cellM |
 * f64 min[3] | f64 max[3]]` — always three axes; a flat realm has {@link deep} false and a `pos2` uses axes 0 and 1.
 *
 * Immutable: a decoded frame may be cached by `(realmId, generation)` and shared.
 */
export class RealmFrame {
  readonly realmId: number;
  /** The realm catalog's generation of {@link realmId}: a reused id is a new realm. */
  readonly generation: number;
  /** The realm kind's index in the catalog's `realmKinds`. */
  readonly kindIdx: number;
  /** The application's tag for the realm, opaque to the engine: a client picks its scene by it. */
  readonly appTag: number;
  /** Bits per position axis: 16, 24 or 32. */
  readonly positionBits: number;
  /** The realm's replication cell side, in metres; an `AGG` tile is `tileCells` of them. */
  readonly cellM: number;
  readonly deep: boolean;
  /** The lower bounds, three axes. */
  readonly min: Float64Array;
  /** The upper bounds, three axes. */
  readonly max: Float64Array;
  /** The position quantum per axis at {@link positionBits}, exactly as `quantStep` computes it. */
  readonly step: Float64Array;
  /** The top code, `2^positionBits − 1`. */
  readonly top: number;
  private commands: RealmFrame | null = null;

  constructor(
    realmId: number,
    generation: number,
    kindIdx: number,
    appTag: number,
    positionBits: number,
    cellM: number,
    deep: boolean,
    min: ArrayLike<number>,
    max: ArrayLike<number>,
  ) {
    const problem = frameProblem(realmId, kindIdx, Number.MAX_SAFE_INTEGER, positionBits, cellM, min, max);
    if (problem !== null) {
      throw new RangeError(problem);
    }

    this.realmId = realmId;
    this.generation = generation;
    this.kindIdx = kindIdx;
    this.appTag = appTag >>> 0;
    this.positionBits = positionBits;
    this.cellM = cellM;
    this.deep = deep;
    this.min = Float64Array.from({ length: 3 }, (_, i) => min[i]!);
    this.max = Float64Array.from({ length: 3 }, (_, i) => max[i]!);
    this.step = Float64Array.from({ length: 3 }, (_, i) => quantStep(this.min[i]!, this.max[i]!, positionBits));
    this.top = unsignedTop(positionBits);
  }

  /**
   * This frame at {@link ProtocolConstants.commandPositionBits}: what a `COMMANDS` message's realm-framed fields travel
   * over — the realm's bounds, at a width the server's transport knows without reading the session's realm (SUB-05).
   */
  get forCommands(): RealmFrame {
    if (this.positionBits === ProtocolConstants.commandPositionBits) {
      return this;
    }

    this.commands ??= new RealmFrame(
      this.realmId,
      this.generation,
      this.kindIdx,
      this.appTag,
      ProtocolConstants.commandPositionBits,
      this.cellM,
      this.deep,
      this.min,
      this.max,
    );
    return this.commands;
  }

  /** An `AGG` grid's cell count on `axis`: `⌈(max − min) / (tileCells · cellM)⌉`, and 1 on axis 2 of a flat realm. */
  aggregateDim(axis: number, tileCells: number): number {
    if (axis === 2 && !this.deep) {
      return 1;
    }

    const dims = Math.ceil((this.max[axis]! - this.min[axis]!) / (tileCells * this.cellM));
    return dims < 1 ? 1 : Math.min(dims, 0x7fffffff);
  }

  /** An `AGG` grid's cell count over all three axes, or `Infinity` past 2^31. */
  aggregateCellCount(tileCells: number): number {
    let count = 1;
    for (let axis = 0; axis < 3; axis++) {
      count *= this.aggregateDim(axis, tileCells);
      if (count > 0x7fffffff) {
        return Infinity;
      }
    }

    return count;
  }

  /** Whether `other` is the same frame, field for field. */
  equals(other: RealmFrame | null): boolean {
    if (other === this) {
      return true;
    }

    if (other === null) {
      return false;
    }

    for (let i = 0; i < 3; i++) {
      if (this.min[i] !== other.min[i] || this.max[i] !== other.max[i]) {
        return false;
      }
    }

    return (
      this.realmId === other.realmId &&
      this.generation === other.generation &&
      this.kindIdx === other.kindIdx &&
      this.appTag === other.appTag &&
      this.positionBits === other.positionBits &&
      this.cellM === other.cellM &&
      this.deep === other.deep
    );
  }

  /** Decodes a `REALM` block's content: the frame, or `null` for `REALM(NONE)`. A value § 5.2 refuses is 1007. */
  static read(r: WireReader, realmKindCount: number): RealmFrame | null {
    const realmId = r.u16();
    const generation = r.u16();
    const flags = r.u8();
    if ((flags & ~(FLAG_NONE | FLAG_DEEP)) !== 0) {
      throw malformed(`REALM flags 0x${flags.toString(16)} set a reserved bit`);
    }

    if ((flags & FLAG_NONE) !== 0) {
      if (flags !== FLAG_NONE) {
        throw malformed('REALM(NONE) sets another flag');
      }

      if (realmId !== NO_REALM || generation !== 0) {
        throw malformed(
          `REALM(NONE) names realm ${realmId} generation ${generation}; NONE is ${NO_REALM}, generation 0`,
        );
      }

      return null;
    }

    const kindIdx = r.varu();
    const appTag = r.u32();
    const bits = r.u8();
    const cellM = r.f64();
    const min = [r.f64(), r.f64(), r.f64()];
    const max = [r.f64(), r.f64(), r.f64()];
    const problem = frameProblem(realmId, kindIdx, realmKindCount, bits, cellM, min, max);
    if (problem !== null) {
      throw malformed(problem);
    }

    return new RealmFrame(realmId, generation, kindIdx, appTag, bits, cellM, (flags & FLAG_DEEP) !== 0, min, max);
  }

  /** Encodes this frame as a `REALM` block's content. */
  write(w: WireWriter): void {
    if (this.realmId >>> 16 !== 0 || this.generation >>> 16 !== 0) {
      throw new RangeError(`realm ${this.realmId} generation ${this.generation}: both are u16`);
    }

    w.u16(this.realmId);
    w.u16(this.generation);
    w.u8(this.deep ? FLAG_DEEP : 0);
    w.varu(this.kindIdx);
    w.u32(this.appTag);
    w.u8(this.positionBits);
    w.f64(this.cellM);
    for (let i = 0; i < 3; i++) {
      w.f64(this.min[i]!);
    }

    for (let i = 0; i < 3; i++) {
      w.f64(this.max[i]!);
    }
  }

  /** Encodes `REALM(NONE)`: the session is in no realm. */
  static writeNone(w: WireWriter): void {
    w.u16(NO_REALM);
    w.u16(0);
    w.u8(FLAG_NONE);
  }
}

function frameProblem(
  realmId: number,
  kindIdx: number,
  realmKindCount: number,
  bits: number,
  cellM: number,
  min: ArrayLike<number>,
  max: ArrayLike<number>,
): string | null {
  if (realmId === NO_REALM) {
    return `realm id ${NO_REALM} is REALM(NONE)'s`;
  }

  if (!(kindIdx >= 0 && kindIdx < realmKindCount)) {
    return `realm kind index ${kindIdx} is out of range (${realmKindCount} kind(s))`;
  }

  if (bits !== 16 && bits !== 24 && bits !== 32) {
    return `REALM posBits ${bits} is not 16, 24 or 32`;
  }

  if (!(Number.isFinite(cellM) && cellM > 0)) {
    return `REALM cellM ${cellM} is not a positive finite number`;
  }

  if (min.length < 3 || max.length < 3) {
    return 'REALM needs three bounds per side';
  }

  for (let i = 0; i < 3; i++) {
    const lo = min[i]!;
    const hi = max[i]!;
    if (!(Number.isFinite(lo) && Number.isFinite(hi) && lo < hi)) {
      return `REALM bounds on axis ${i} are not a finite min < max (${lo}, ${hi})`;
    }

    // The quantum must be a positive finite number: an extent past the f64 range makes it infinite, a subnormal one zero.
    const step = (hi - lo) / 2 ** bits;
    if (!(Number.isFinite(step) && step > 0)) {
      return `REALM bounds on axis ${i} give no usable ${bits}-bit quantum (${lo}, ${hi})`;
    }
  }

  return null;
}
