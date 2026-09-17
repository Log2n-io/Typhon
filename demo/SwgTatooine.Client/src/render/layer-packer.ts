import { evaluateSlot, MAX_MOTION_STRIDE, type ArchetypeStore, type FieldArray } from '@typhondb/client';
import { Archetype } from '../data/swg-schema';
import { SELECTED_SPRITE_SCALE, type LayerStyle } from './styles';
import { Band, chooseBand, packState, sphereInFrustum } from './view-math';

/** What every layer needs to know about the frame being drawn. */
export interface FrameView {
  readonly renderTick: number;
  readonly renderFrac: number;
  readonly originX: number;
  readonly originZ: number;
  /** Camera position in render space. */
  readonly eyeX: number;
  readonly eyeY: number;
  readonly eyeZ: number;
  /** Frustum planes in render space (see `view-math.ts`). */
  readonly planes: Float64Array;
  /** CSS pixels per metre at one metre of distance: viewport height / (2 tan(fov / 2)). */
  readonly pixelsPerMetre: number;
  /** Viewport size in CSS pixels: LOD thresholds, sprite and line sizes and picking all count CSS pixels. */
  readonly viewportWidth: number;
  readonly viewportHeight: number;
  readonly selectedNetId: number;
}

interface FieldReaders {
  readonly style: FieldArray | null;
  /** The style field is a flag (a lair's mission id): any non-zero value selects style 1. */
  readonly styleIsFlag: boolean;
  readonly mode: FieldArray | null;
}

const NO_FIELDS: FieldReaders = { style: null, styleIsFlag: false, mode: null };
const INITIAL_CAPACITY = 1024;

/** The SWG fields that pick an archetype's style and mode. */
function readersFor(archetype: number, store: ArchetypeStore): FieldReaders {
  switch (archetype) {
    case Archetype.WorldObject:
      return { style: store.field('kind'), styleIsFlag: false, mode: null };
    case Archetype.CreatureLair:
      return { style: store.field('missionId'), styleIsFlag: true, mode: null };
    case Archetype.Creature:
      return { style: store.field('template'), styleIsFlag: false, mode: store.field('mode') };
    case Archetype.CityNpc:
      return { style: null, styleIsFlag: false, mode: store.field('mode') };
    default:
      return { style: null, styleIsFlag: false, mode: store.field('activity') };
  }
}

/**
 * The CPU half of a layer, apart from Babylon so it can be tested: every live entity of one store evaluated at render time,
 * culled, banded and packed into the near and far instance arrays (`x, z, yaw, packed` at stride 4) with the netId of each
 * instance beside it.
 */
export class LayerPacker {
  readonly archetype: number;
  readonly style: LayerStyle;

  nearData = new Float32Array(0);
  farData = new Float32Array(0);
  /** Entity packed at each instance, captured at pack time for picking. */
  nearNetIds = new Uint32Array(0);
  farNetIds = new Uint32Array(0);
  nearCount = 0;
  farCount = 0;
  /** Instances each output array holds; bumped with {@link arraysVersion} when the arrays are replaced. */
  capacity = 0;
  arraysVersion = 0;

  private store: ArchetypeStore | null = null;
  private storeVersion = -1;
  private fields: FieldReaders = NO_FIELDS;
  private readonly motion = new Float64Array(MAX_MOTION_STRIDE);
  /** Per slot: the entity whose state the slot arrays describe, its LOD band and its last heading. */
  private seenNetId = new Uint32Array(0);
  private band = new Uint8Array(0);
  private yaw = new Float32Array(0);

  constructor(archetype: number, style: LayerStyle) {
    this.archetype = archetype;
    this.style = style;
    this.ensureCapacity(INITIAL_CAPACITY);
  }

  get bound(): ArchetypeStore | null {
    return this.store;
  }

  /** Binds to a store: per-slot state starts over. */
  bind(store: ArchetypeStore | null): void {
    this.store = store;
    this.storeVersion = -1;
    this.fields = NO_FIELDS;
    this.seenNetId.fill(0);
    this.nearCount = 0;
    this.farCount = 0;
  }

  /** Evaluates, culls, bands and packs every live entity of the bound store. */
  pack(view: FrameView): void {
    const store = this.store;
    if (store === null) {
      this.nearCount = 0;
      this.farCount = 0;
      return;
    }

    if (store.version !== this.storeVersion) {
      this.resizeSlotState(store);
      this.fields = readersFor(this.archetype, store);
    }

    const count = store.liveCount;
    this.ensureCapacity(count);

    const fields = this.fields;
    const style = this.style;
    const bounds = style.bounds;
    const spriteLift = style.spriteLift;
    const byVelocity = style.heading === 'velocity';
    // A sprite's world radius at distance d: its CSS pixel radius (the selected size, the largest) × d / pixels-per-metre.
    const spriteMetresPerMetre = (style.spritePixels * SELECTED_SPRITE_SCALE * 0.5) / view.pixelsPerMetre;
    const motion = this.motion;
    const live = store.live;
    const netIds = store.netIds;
    const seenNetId = this.seenNetId;
    const bands = this.band;
    const yaws = this.yaw;
    const nearData = this.nearData;
    const farData = this.farData;
    const nearNetIds = this.nearNetIds;
    const farNetIds = this.farNetIds;
    const tick = view.renderTick;
    const frac = view.renderFrac;
    let near = 0;
    let far = 0;

    for (let i = 0; i < count; i++) {
      const slot = live[i];
      evaluateSlot(store, slot, tick, frac, motion, 0);
      const rx = motion[0] - view.originX;
      const rz = motion[1] - view.originZ;

      const raw = fields.style === null ? 0 : fields.style[slot];
      const styleIndex = fields.styleIsFlag ? (raw > 0 ? 1 : 0) : raw;
      const bound = styleIndex >= 0 && styleIndex < bounds.radius.length ? styleIndex : 0;
      const cy = bounds.centerY[bound];
      const dx = rx - view.eyeX;
      const dy = cy - view.eyeY;
      const dz = rz - view.eyeZ;
      const distance = Math.sqrt(dx * dx + dy * dy + dz * dz) + 1e-3;
      // The sphere holds the mesh, and the sprite too: drawn at its own lift, which may sit off the mesh's centre.
      const spriteRadius = distance * spriteMetresPerMetre + Math.abs(cy - spriteLift);
      const radius = Math.max(bounds.radius[bound], spriteRadius);
      if (!sphereInFrustum(view.planes, rx, cy, rz, radius)) {
        continue;
      }

      const netId = netIds[slot];
      if (seenNetId[slot] !== netId) {
        seenNetId[slot] = netId;
        bands[slot] = Band.Unset;
        yaws[slot] = byVelocity ? (netId * 2.399963) % (Math.PI * 2) : gridYaw(motion[0], motion[1]);
      }

      const pixels = (bounds.extent[bound] * view.pixelsPerMetre) / distance;
      const band = chooseBand(bands[slot], pixels);
      bands[slot] = band;
      const packed = packState(styleIndex, fields.mode === null ? 0 : fields.mode[slot], netId === view.selectedNetId);

      if (band === Band.Near) {
        // Heading only matters to a mesh. A still entity keeps the last heading it had as one.
        if (byVelocity && (motion[2] !== 0 || motion[3] !== 0)) {
          yaws[slot] = Math.atan2(motion[2], motion[3]);
        }

        const b = near * 4;
        nearData[b] = rx;
        nearData[b + 1] = rz;
        nearData[b + 2] = yaws[slot];
        nearData[b + 3] = packed;
        nearNetIds[near++] = netId;
      } else {
        const b = far * 4;
        farData[b] = rx;
        farData[b + 1] = rz;
        farData[b + 2] = 0;
        farData[b + 3] = packed;
        farNetIds[far++] = netId;
      }
    }

    this.nearCount = near;
    this.farCount = far;
  }

  private resizeSlotState(store: ArchetypeStore): void {
    const capacity = store.capacity;
    if (this.seenNetId.length !== capacity) {
      const seen = new Uint32Array(capacity);
      const band = new Uint8Array(capacity);
      const yaw = new Float32Array(capacity);
      const kept = Math.min(capacity, this.seenNetId.length);
      seen.set(this.seenNetId.subarray(0, kept));
      band.set(this.band.subarray(0, kept));
      yaw.set(this.yaw.subarray(0, kept));
      this.seenNetId = seen;
      this.band = band;
      this.yaw = yaw;
    }

    this.storeVersion = store.version;
  }

  private ensureCapacity(count: number): void {
    if (count <= this.capacity) {
      return;
    }

    let capacity = Math.max(INITIAL_CAPACITY, this.capacity);
    while (capacity < count) {
      capacity *= 2;
    }

    this.capacity = capacity;
    this.nearData = new Float32Array(capacity * 4);
    this.farData = new Float32Array(capacity * 4);
    this.nearNetIds = new Uint32Array(capacity);
    this.farNetIds = new Uint32Array(capacity);
    this.arraysVersion++;
  }
}

/** A stable heading for things laid out on streets: a multiple of 90° hashed from the position. */
function gridYaw(x: number, z: number): number {
  const h = Math.sin(x * 12.9898 + z * 78.233) * 43758.5453;
  return Math.floor((h - Math.floor(h)) * 4) * (Math.PI / 2);
}
