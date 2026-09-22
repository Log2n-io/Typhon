/**
 * The planet's geography and creature templates, mirrored from `demo/SwgTatooine/World/TatooineData.cs`.
 *
 * Coordinates, radii and statistics are the server's numbers (facts about a 2003 game, via SWGEmu Core3 and community
 * wikis). **Display names are original**: every public artefact avoids the franchise's names
 * (`claude/design/SwgTatooine/06-gameplay.md` § 7), so each entry carries the name shown on screen, not the source name.
 */

export const PLANET_HALF_EXTENT_M = 8192;
export const PLANET_EDGE_M = 16384;

export interface CityDef {
  readonly name: string;
  readonly x: number;
  readonly z: number;
  readonly radius: number;
  readonly buildings: number;
  readonly npcs: number;
  readonly playerWeight: number;
}

export interface PoiDef {
  readonly name: string;
  readonly x: number;
  readonly z: number;
  readonly radius: number;
  readonly props: number;
  readonly lairs: number;
}

export interface SpawnRegionDef {
  readonly name: string;
  readonly x: number;
  readonly z: number;
  readonly radius: number;
  readonly lairsPerSqKm: number;
  readonly template: number;
}

export const CITIES: readonly CityDef[] = [
  { name: 'Port Sable', x: 3460, z: -4768, radius: 456, buildings: 190, npcs: 340, playerWeight: 0.34 },
  { name: 'Whitestone', x: -1370, z: -3639, radius: 336, buildings: 140, npcs: 240, playerWeight: 0.18 },
  { name: 'Dry Wells', x: -2890, z: 2198, radius: 380, buildings: 135, npcs: 220, playerWeight: 0.16 },
  { name: 'Red Mesa', x: 1530, z: 3175, radius: 330, buildings: 105, npcs: 170, playerWeight: 0.13 },
  { name: "Tern's Rest", x: 59, z: -5372, radius: 125, buildings: 55, npcs: 80, playerWeight: 0.08 },
  { name: 'Tallow', x: 3795, z: 2388, radius: 180, buildings: 38, npcs: 55, playerWeight: 0.06 },
  { name: 'Far Reach', x: -5166, z: -6620, radius: 180, buildings: 35, npcs: 50, playerWeight: 0.05 },
];

export const POIS: readonly PoiDef[] = [
  { name: 'Bone Fields', x: 7450, z: 4531, radius: 520, props: 85, lairs: 14 },
  { name: 'The Citadel', x: -5962, z: -6259, radius: 190, props: 45, lairs: 6 },
  { name: 'The Sink', x: -6169, z: -3387, radius: 150, props: 22, lairs: 4 },
  { name: 'Raider Fort', x: -3980, z: 6311, radius: 230, props: 60, lairs: 12 },
  { name: 'Scrap Hold', x: -6141, z: 1854, radius: 170, props: 38, lairs: 7 },
  { name: "Hermit's Hut", x: -4512, z: -2270, radius: 90, props: 12, lairs: 2 },
  { name: 'Old Farm', x: -2579, z: -5500, radius: 110, props: 16, lairs: 2 },
];

export const CreatureTemplate = {
  DuneRat: 0,
  Rockhopper: 1,
  ShagGrazer: 2,
  Raider: 3,
  PackBeast: 4,
  CampGuard: 5,
} as const;

export const CREATURE_TEMPLATE_NAMES: readonly string[] = [
  'Dune rat',
  'Rockhopper',
  'Shag grazer',
  'Raider',
  'Pack beast',
  'Camp guard',
];

export const SPAWN_REGIONS: readonly SpawnRegionDef[] = [
  { name: 'Northern Dunes', x: -1200, z: 5600, radius: 2600, lairsPerSqKm: 18, template: CreatureTemplate.ShagGrazer },
  { name: 'Western Dunes', x: -6200, z: 4200, radius: 2100, lairsPerSqKm: 15, template: CreatureTemplate.Raider },
  { name: 'Broken Wastes', x: -3600, z: -1400, radius: 2400, lairsPerSqKm: 22, template: CreatureTemplate.Rockhopper },
  { name: 'Rat Draw', x: 5400, z: -1500, radius: 1900, lairsPerSqKm: 20, template: CreatureTemplate.DuneRat },
  { name: 'Eastern Wastes', x: 6100, z: 3000, radius: 2200, lairsPerSqKm: 14, template: CreatureTemplate.PackBeast },
  { name: 'Southern Wastes', x: 800, z: -6800, radius: 2300, lairsPerSqKm: 17, template: CreatureTemplate.DuneRat },
  { name: 'Wells Approach', x: -3400, z: 600, radius: 1500, lairsPerSqKm: 21, template: CreatureTemplate.Rockhopper },
  { name: 'Arid Flats', x: 2200, z: 400, radius: 2000, lairsPerSqKm: 13, template: CreatureTemplate.ShagGrazer },
];

/** Per template: creatures a lair keeps alive, hit points, damage, speed (m/s), aggression, render size (m). */
export const TEMPLATE_SPAWN_LIMIT: readonly number[] = [6, 5, 4, 5, 3, 8];
export const TEMPLATE_HEALTH: readonly number[] = [180, 260, 900, 640, 1100, 420];
export const TEMPLATE_DAMAGE: readonly number[] = [12, 18, 45, 38, 30, 26];
export const TEMPLATE_SPEED_MPS: readonly number[] = [3.4, 3.0, 2.2, 4.2, 2.0, 3.6];
export const TEMPLATE_AGGRESSIVE: readonly boolean[] = [true, true, false, true, false, true];
export const TEMPLATE_SIZE_M: readonly number[] = [1.0, 1.2, 3.2, 1.8, 3.8, 1.9];

export const LAIR_SPAWN_RADIUS_M = 25;
export const AGGRO_RADIUS_M = 24;
export const MAX_CHASE_RANGE_M = 75;
export const LEASH_RADIUS_M = 192;
export const AWARENESS_RADIUS_M = 192;
export const WEAPON_RANGE_M = 75;
export const PLAYER_RUN_SPEED_MPS = 5;
export const PLAYER_MOUNT_SPEED_MPS = 12;

export const BASELINE_PLAYERS = 320;
export const BASELINE_PLAYER_STRUCTURES = 2600;
