import type { AssetSet } from '../data/server-config';

/**
 * Which published asset a structure is, and which way it faces — both read from the server, neither derived here.
 *
 * <b>This used to be a table of kind to asset, and that was wrong as soon as collision existed.</b> The server picks
 * which asset stands where and pushes bodies out of its walls; a client with its own copy of the rule would eventually
 * draw a building it does not collide with. The two could not even agree by accident, because the choice between
 * equivalent assets (a city's three terminals) is drawn per entity, and a client numbers entities by netId while the
 * world builds them by EntityId. So the server replicates the CHOICE — an index into a table it publishes in
 * `/typhon/demo.json` — and this resolves it.
 */

/**
 * The asset id a structure's replicated index names, or `null` for a structure that has none.
 *
 * @param set The announced asset set, carrying the server's table.
 * @param assetIndex The structure's replicated `asset`.
 * @returns A catalogue asset id, or `null`.
 */
export function assetForStructure(set: AssetSet, assetIndex: number): string | null {
  return assetIndex >= 0 && assetIndex < set.structures.length ? set.structures[assetIndex] : null;
}

/**
 * The yaw a structure is drawn at, from the quarter turn the server replicated.
 *
 * <b>The world's, not a hash of the position.</b> A building with a door and a collision shape has an orientation that
 * the server pushes bodies out of, so a renderer that invented its own would draw the door in a different wall from the
 * one you cannot walk through. `layer-packer.ts` reads the same field for the same reason, and falls back to its own
 * `gridYaw` only for the archetypes that replicate none.
 *
 * @param quarters The replicated `yaw`, in quarter turns.
 * @returns Radians about +Y.
 */
export function structureYaw(quarters: number): number {
  return quarters * (Math.PI / 2);
}

/**
 * A lair's quarter turn, from where it stands: stable for as long as it stands, and spread across the four.
 *
 * @param x Planet metres, east.
 * @param z Planet metres, north.
 * @returns 0 to 3.
 */
export function lairQuarter(x: number, z: number): number {
  const h = Math.imul(Math.floor(x * 4) | 0, 0x9e3779b1) ^ Math.imul(Math.floor(z * 4) | 0, 0x85ebca77);
  return ((h >>> 13) ^ h) & 3;
}
