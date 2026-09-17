import { AiMode, Archetype, PlayerActivity, StructureKind } from '../data/swg-schema';
import { TEMPLATE_SIZE_M } from '../data/world-data';
import type { ShapeKind } from './meshes';
import { MODE_LIMIT, STYLE_LIMIT } from './view-math';

/**
 * How each archetype looks: shape, colour and size per style index, a tint per mode, and how big it is for LOD and culling.
 * Primitive art on purpose (`05-client.md` C5): the point is to read the simulation, not to dress it.
 */

export type HeadingSource = 'velocity' | 'grid';

/** Style indices per archetype and tints per mode: the sizes of the shaders' uniform arrays, fixed by the packing. */
export const STYLE_COUNT = STYLE_LIMIT;
export const TINT_COUNT = MODE_LIMIT;
/** A selected entity's mesh grows by this factor, its sprite by {@link SELECTED_SPRITE_SCALE}. */
export const SELECTED_MESH_SCALE = 1.4;
export const SELECTED_SPRITE_SCALE = 2.5;

/** Bounds per style index, derived from the sizes: what culling, LOD and picking use. */
export interface StyleBounds {
  /** Bounding-sphere radius in metres, for the shape grown by the selection scale. */
  readonly radius: Float64Array;
  /** Height of the bounding sphere's centre above the ground. */
  readonly centerY: Float64Array;
  /** Largest size component: the projected size that picks the LOD band. */
  readonly extent: Float64Array;
  /** Height of the drawn (not grown) mesh's centre: where a click aims. */
  readonly pickY: Float64Array;
}

export interface LayerStyle {
  readonly shape: ShapeKind;
  /** {@link STYLE_COUNT} RGB colours, by style index. */
  readonly colors: readonly (readonly [number, number, number])[];
  /** {@link STYLE_COUNT} sizes (width, height, length) in metres, by style index. Every shape fits its size's box. */
  readonly sizes: readonly (readonly [number, number, number])[];
  /** {@link TINT_COUNT} RGBA tints by mode: the colour is mixed toward rgb by a. */
  readonly tints: readonly (readonly [number, number, number, number])[];
  /** Mode whose shape is flattened (a corpse), or -1. */
  readonly flattenMode: number;
  readonly heading: HeadingSource;
  /** Sprite diameter in pixels in the far band. */
  readonly spritePixels: number;
  /** Height of the sprite above the ground. */
  readonly spriteLift: number;
  readonly bounds: StyleBounds;
}

type StyleInput = Omit<LayerStyle, 'bounds'>;

function withBounds(style: StyleInput): LayerStyle {
  const radius = new Float64Array(STYLE_COUNT);
  const centerY = new Float64Array(STYLE_COUNT);
  const extent = new Float64Array(STYLE_COUNT);
  const pickY = new Float64Array(STYLE_COUNT);
  for (let i = 0; i < STYLE_COUNT; i++) {
    const [w, h, l] = style.sizes[i] ?? style.sizes[0];
    // Every shape spans x and z in [−½, ½] and y in [0, 1] before scaling (`meshes.ts`), whatever its yaw. The sphere
    // around the grown shape also holds the plain one: its centre moves up by 0.2 h ≤ 0.4 r.
    radius[i] = SELECTED_MESH_SCALE * 0.5 * Math.hypot(w, h, l);
    centerY[i] = SELECTED_MESH_SCALE * 0.5 * h;
    extent[i] = Math.max(w, h, l);
    pickY[i] = 0.5 * h;
  }

  return { ...style, bounds: { radius, centerY, extent, pickY } };
}

const NO_TINT: [number, number, number, number] = [0, 0, 0, 0];

function fill<T>(values: readonly T[], length: number, filler: T): T[] {
  return Array.from({ length }, (_, i) => values[i] ?? filler);
}

const STRUCTURE_COLORS: [number, number, number][] = [];
STRUCTURE_COLORS[StructureKind.Building] = [0.8, 0.72, 0.58];
STRUCTURE_COLORS[StructureKind.Terminal] = [0.35, 0.75, 0.8];
STRUCTURE_COLORS[StructureKind.Shuttleport] = [0.7, 0.74, 0.8];
STRUCTURE_COLORS[StructureKind.PoiProp] = [0.55, 0.42, 0.32];
STRUCTURE_COLORS[StructureKind.PlayerHouse] = [0.86, 0.8, 0.66];
STRUCTURE_COLORS[StructureKind.Factory] = [0.62, 0.38, 0.26];
STRUCTURE_COLORS[StructureKind.Harvester] = [0.45, 0.48, 0.5];
STRUCTURE_COLORS[StructureKind.CampObject] = [0.66, 0.55, 0.4];

const STRUCTURE_SIZES: [number, number, number][] = [];
STRUCTURE_SIZES[StructureKind.Building] = [18, 9, 18];
STRUCTURE_SIZES[StructureKind.Terminal] = [1.6, 2.6, 1.6];
STRUCTURE_SIZES[StructureKind.Shuttleport] = [30, 5, 30];
STRUCTURE_SIZES[StructureKind.PoiProp] = [5, 4, 5];
STRUCTURE_SIZES[StructureKind.PlayerHouse] = [14, 7, 14];
STRUCTURE_SIZES[StructureKind.Factory] = [22, 11, 22];
STRUCTURE_SIZES[StructureKind.Harvester] = [6, 14, 6];
STRUCTURE_SIZES[StructureKind.CampObject] = [4, 3, 4];

const CREATURE_COLORS: [number, number, number][] = [
  [0.58, 0.47, 0.36],
  [0.5, 0.52, 0.46],
  [0.36, 0.27, 0.2],
  [0.8, 0.66, 0.45],
  [0.5, 0.44, 0.38],
  [0.75, 0.25, 0.18],
];

const CREATURE_TINTS: [number, number, number, number][] = [];
CREATURE_TINTS[AiMode.Pursue] = [1, 0.55, 0.1, 0.5];
CREATURE_TINTS[AiMode.Fighting] = [1, 0.12, 0.08, 0.7];
CREATURE_TINTS[AiMode.Leashing] = [0.25, 0.5, 1, 0.4];
CREATURE_TINTS[AiMode.Dead] = [0.18, 0.17, 0.16, 0.85];

const PLAYER_TINTS: [number, number, number, number][] = [];
PLAYER_TINTS[PlayerActivity.Combat] = [1, 0.85, 0.1, 0.45];
PLAYER_TINTS[PlayerActivity.ToShuttle] = [0.2, 0.9, 1, 0.35];
PLAYER_TINTS[PlayerActivity.AwaitingShuttle] = [0.2, 0.9, 1, 0.6];

export const LAYER_STYLES: readonly LayerStyle[] = (() => {
  const styles: StyleInput[] = [];
  styles[Archetype.WorldObject] = {
    shape: 'box',
    colors: fill(STRUCTURE_COLORS, STYLE_COUNT, [0.7, 0.7, 0.7]),
    sizes: fill(STRUCTURE_SIZES, STYLE_COUNT, [4, 4, 4]),
    tints: fill([], TINT_COUNT, NO_TINT),
    flattenMode: -1,
    heading: 'grid',
    spritePixels: 3,
    spriteLift: 3,
  };
  styles[Archetype.CreatureLair] = {
    shape: 'mound',
    colors: fill(
      [
        [0.42, 0.33, 0.26],
        [0.85, 0.35, 0.15],
      ],
      STYLE_COUNT,
      [0.4, 0.3, 0.2],
    ),
    sizes: fill(
      [
        [10, 3, 10],
        [14, 5, 14],
      ],
      STYLE_COUNT,
      [10, 3, 10],
    ),
    tints: fill([], TINT_COUNT, NO_TINT),
    flattenMode: -1,
    heading: 'grid',
    spritePixels: 4,
    spriteLift: 2,
  };
  styles[Archetype.Creature] = {
    shape: 'arrow',
    colors: fill(CREATURE_COLORS, STYLE_COUNT, [0.5, 0.45, 0.4]),
    sizes: fill(
      TEMPLATE_SIZE_M.map((s): [number, number, number] => [s * 0.7, s * 0.8, s * 1.4]),
      STYLE_COUNT,
      [1, 1, 1.4],
    ),
    tints: fill(CREATURE_TINTS, TINT_COUNT, NO_TINT),
    flattenMode: AiMode.Dead,
    heading: 'velocity',
    spritePixels: 3,
    spriteLift: 1,
  };
  styles[Archetype.CityNpc] = {
    shape: 'prism',
    colors: fill([[0.4, 0.55, 0.78]], STYLE_COUNT, [0.4, 0.55, 0.78]),
    sizes: fill([[0.7, 1.8, 0.7]], STYLE_COUNT, [0.7, 1.8, 0.7]),
    tints: fill([], TINT_COUNT, NO_TINT),
    flattenMode: -1,
    heading: 'velocity',
    spritePixels: 2.5,
    spriteLift: 1,
  };
  styles[Archetype.Player] = {
    shape: 'prism',
    colors: fill([[0.25, 0.92, 0.45]], STYLE_COUNT, [0.25, 0.92, 0.45]),
    sizes: fill([[1.1, 2.2, 1.1]], STYLE_COUNT, [1.1, 2.2, 1.1]),
    tints: fill(PLAYER_TINTS, TINT_COUNT, NO_TINT),
    flattenMode: -1,
    heading: 'velocity',
    spritePixels: 5,
    spriteLift: 1.5,
  };
  return styles.map(withBounds);
})();
