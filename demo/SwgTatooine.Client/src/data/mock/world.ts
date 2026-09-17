import { AiMode, Archetype, PlayerActivity, StructureKind } from '../swg-schema';
import {
  BASELINE_PLAYER_STRUCTURES,
  BASELINE_PLAYERS,
  CITIES,
  CreatureTemplate,
  LAIR_SPAWN_RADIUS_M,
  PLANET_HALF_EXTENT_M,
  PLAYER_RUN_SPEED_MPS,
  POIS,
  SPAWN_REGIONS,
  TEMPLATE_HEALTH,
  TEMPLATE_SPAWN_LIMIT,
  TEMPLATE_SPEED_MPS,
} from '../world-data';
import { Rng } from './rng';

/**
 * The mock server's world: every entity of the planet as structure-of-arrays, built the way
 * `demo/SwgTatooine/World/WorldBuilder.cs` builds it (same geography, same counts per population factor, same placement
 * rules), so the client sees the same densities it will see from the real server.
 */

/** Minimal view every archetype offers to interest management and motion measurement. */
export interface EntitySet {
  readonly archetype: number;
  readonly count: number;
  readonly x: Float64Array;
  readonly z: Float64Array;
}

export interface Statics extends EntitySet {
  readonly kind: Uint8Array;
}

export interface Lairs extends EntitySet {
  readonly template: Uint8Array;
  readonly missionId: Uint8Array;
  readonly hp: Float32Array;
  readonly maxHp: Float32Array;
  /** 1 for destroy-mission pool lairs, which relocate when a mission is issued. */
  readonly pool: Uint8Array;
  readonly missionTicks: Int32Array;
}

export interface Creatures extends EntitySet {
  readonly vx: Float64Array;
  readonly vz: Float64Array;
  readonly speed: Float32Array;
  readonly destX: Float64Array;
  readonly destZ: Float64Array;
  readonly homeX: Float64Array;
  readonly homeZ: Float64Array;
  readonly mode: Uint8Array;
  readonly hp: Float32Array;
  readonly maxHp: Float32Array;
  readonly template: Uint8Array;
  readonly lair: Int32Array;
  readonly think: Int32Array;
  readonly deadTicks: Int32Array;
  /** Index of the player being chased or fought, or -1. */
  readonly target: Int32Array;
  readonly attackCooldown: Int32Array;
}

export interface Npcs extends EntitySet {
  readonly vx: Float64Array;
  readonly vz: Float64Array;
  readonly destX: Float64Array;
  readonly destZ: Float64Array;
  readonly homeX: Float64Array;
  readonly homeZ: Float64Array;
  readonly mode: Uint8Array;
  readonly think: Int32Array;
}

export interface Players extends EntitySet {
  readonly vx: Float64Array;
  readonly vz: Float64Array;
  readonly speed: Float32Array;
  readonly destX: Float64Array;
  readonly destZ: Float64Array;
  readonly activity: Uint8Array;
  readonly activityTicks: Int32Array;
  readonly hp: Float32Array;
  readonly maxHp: Float32Array;
  readonly homeCity: Uint8Array;
  readonly shuttleDest: Uint8Array;
  /** Index of the creature being fought, or -1. */
  readonly target: Int32Array;
  /** Index of the mission lair being attacked when no creature is in range, or -1. */
  readonly targetLair: Int32Array;
  readonly attackCooldown: Int32Array;
}

export interface MockWorld {
  readonly population: number;
  readonly statics: Statics;
  readonly lairs: Lairs;
  readonly creatures: Creatures;
  readonly npcs: Npcs;
  readonly players: Players;
  /** Indexed by archetype. */
  readonly sets: readonly EntitySet[];
  /** Shuttleport position per city, `[x, z]` pairs. */
  readonly shuttleports: Float64Array;
}

const MISSION_POOL_BASE = BASELINE_PLAYERS / 4;
const PLAYER_CITY_SITES = 12;
const PLAYER_CITY_SEPARATION_M = 1000;
const PLAYER_CITY_RADII = [150, 300, 450];

function scale(baseline: number, population: number): number {
  return Math.round(baseline * population);
}

export function buildWorld(seed: number, population: number): MockWorld {
  const rng = new Rng(seed);
  const p = new Float64Array(2);

  // ── Counts first, so every array is allocated once ────────────────────────────────────────────────────────────
  const lairPlan: { x: number; z: number; radius: number; count: number; template: number; pool: boolean }[] = [];
  for (const poi of POIS) {
    lairPlan.push({
      x: poi.x,
      z: poi.z,
      radius: poi.radius,
      count: scale(poi.lairs, population),
      template: CreatureTemplate.Raider,
      pool: false,
    });
  }

  for (const region of SPAWN_REGIONS) {
    const areaSqKm = (Math.PI * region.radius * region.radius) / 1_000_000;
    lairPlan.push({
      x: region.x,
      z: region.z,
      radius: region.radius,
      count: scale(Math.round(areaSqKm * region.lairsPerSqKm), population),
      template: region.template,
      pool: false,
    });
  }

  lairPlan.push({
    x: 0,
    z: 0,
    radius: 0,
    count: scale(MISSION_POOL_BASE, population),
    template: CreatureTemplate.CampGuard,
    pool: true,
  });

  let staticCount = scale(BASELINE_PLAYER_STRUCTURES, population);
  for (const c of CITIES) {
    staticCount += c.buildings;
  }

  for (const poi of POIS) {
    staticCount += poi.props;
  }

  let lairCount = 0;
  let creatureCount = 0;
  for (const plan of lairPlan) {
    lairCount += plan.count;
    creatureCount += plan.count * TEMPLATE_SPAWN_LIMIT[plan.template];
  }

  let npcCount = 0;
  for (const c of CITIES) {
    npcCount += scale(c.npcs, population);
  }

  const playerCount = scale(BASELINE_PLAYERS, population);

  const statics = allocStatics(staticCount);
  const lairs = allocLairs(lairCount);
  const creatures = allocCreatures(creatureCount);
  const npcs = allocNpcs(npcCount);
  const players = allocPlayers(playerCount);
  const shuttleports = new Float64Array(CITIES.length * 2);

  // ── Cities: buildings (the first is the shuttleport) and NPCs ─────────────────────────────────────────────────
  let s = 0;
  let n = 0;
  CITIES.forEach((city, ci) => {
    for (let i = 0; i < city.buildings; i++) {
      rng.pointInDisc(city.x, city.z, city.radius, p);
      statics.x[s] = p[0]!;
      statics.z[s] = p[1]!;
      statics.kind[s] =
        i === 0 ? StructureKind.Shuttleport : i % 9 === 0 ? StructureKind.Terminal : StructureKind.Building;
      if (i === 0) {
        shuttleports[ci * 2] = p[0]!;
        shuttleports[ci * 2 + 1] = p[1]!;
      }

      s++;
    }

    const cityNpcs = scale(city.npcs, population);
    for (let i = 0; i < cityNpcs; i++) {
      rng.pointInDisc(city.x, city.z, city.radius, p);
      npcs.x[n] = npcs.homeX[n] = npcs.destX[n] = p[0]!;
      npcs.z[n] = npcs.homeZ[n] = npcs.destZ[n] = p[1]!;
      npcs.mode[n] = rng.next() < 0.12 ? AiMode.Wander : AiMode.Idle;
      npcs.think[n] = rng.int(1, 40);
      n++;
    }
  });

  // ── Points of interest: props ─────────────────────────────────────────────────────────────────────────────────
  for (const poi of POIS) {
    for (let i = 0; i < poi.props; i++) {
      rng.pointInDisc(poi.x, poi.z, poi.radius, p);
      statics.x[s] = p[0]!;
      statics.z[s] = p[1]!;
      statics.kind[s] = StructureKind.PoiProp;
      s++;
    }
  }

  // ── Lairs and their creatures ─────────────────────────────────────────────────────────────────────────────────
  let l = 0;
  let c = 0;
  for (const plan of lairPlan) {
    const perLair = TEMPLATE_SPAWN_LIMIT[plan.template];
    for (let i = 0; i < plan.count; i++) {
      if (plan.pool) {
        randomWildernessPoint(rng, p);
      } else {
        rng.pointInDisc(plan.x, plan.z, plan.radius, p);
      }

      const lx = clampToPlanet(p[0], 4);
      const lz = clampToPlanet(p[1], 4);
      lairs.x[l] = lx;
      lairs.z[l] = lz;
      lairs.template[l] = plan.template;
      lairs.pool[l] = plan.pool ? 1 : 0;
      lairs.maxHp[l] = plan.pool ? 4500 : 2400;
      lairs.hp[l] = lairs.maxHp[l]!;

      for (let k = 0; k < perLair; k++) {
        rng.pointInDisc(lx, lz, LAIR_SPAWN_RADIUS_M, p);
        creatures.x[c] = creatures.destX[c] = clampToPlanet(p[0], 1.5);
        creatures.z[c] = creatures.destZ[c] = clampToPlanet(p[1], 1.5);
        creatures.homeX[c] = lx;
        creatures.homeZ[c] = lz;
        creatures.speed[c] = TEMPLATE_SPEED_MPS[plan.template]!;
        creatures.mode[c] = AiMode.Wander;
        creatures.maxHp[c] = TEMPLATE_HEALTH[plan.template]!;
        creatures.hp[c] = creatures.maxHp[c]!;
        creatures.template[c] = plan.template;
        creatures.lair[c] = l;
        creatures.think[c] = rng.int(1, 10);
        creatures.target[c] = -1;
        creatures.attackCooldown[c] = rng.int(0, 20);
        c++;
      }

      l++;
    }
  }

  // ── Player structures: two thirds in player-city knots, the rest scattered ────────────────────────────────────
  const sites = buildPlayerCitySites(rng, p);
  const structureTotal = scale(BASELINE_PLAYER_STRUCTURES, population);
  const inSites = Math.floor(structureTotal * 0.66);
  for (let i = 0; i < structureTotal; i++) {
    if (i < inSites && sites.length > 0) {
      const site = sites[rng.int(0, sites.length)];
      rng.pointInDisc(site.x, site.z, site.radius, p);
    } else {
      randomWildernessPoint(rng, p);
    }

    const roll = rng.next();
    statics.x[s] = clampToPlanet(p[0], 14);
    statics.z[s] = clampToPlanet(p[1], 14);
    statics.kind[s] =
      roll < 0.55 ? StructureKind.PlayerHouse : roll < 0.78 ? StructureKind.Harvester : StructureKind.Factory;
    s++;
  }

  // ── Players, born mid-activity ────────────────────────────────────────────────────────────────────────────────
  for (let i = 0; i < playerCount; i++) {
    const cityIndex = weightedCity(rng);
    const city = CITIES[cityIndex];
    const roll = rng.next();
    let activity: number;
    if (roll < 0.4) {
      activity = PlayerActivity.Idle;
      rng.pointInDisc(city.x, city.z, city.radius, p);
    } else if (roll < 0.6) {
      activity = PlayerActivity.Travelling;
      const other = CITIES[rng.int(0, CITIES.length)];
      const f = rng.next();
      p[0] = city.x + (other.x - city.x) * f;
      p[1] = city.z + (other.z - city.z) * f;
    } else if (roll < 0.82) {
      activity = PlayerActivity.Combat;
      const poi = POIS[rng.int(0, POIS.length)];
      rng.pointInDisc(poi.x, poi.z, poi.radius, p);
    } else {
      activity = PlayerActivity.Roaming;
      rng.pointInDisc(city.x, city.z, city.radius * 6, p);
    }

    players.x[i] = players.destX[i] = clampToPlanet(p[0], 1);
    players.z[i] = players.destZ[i] = clampToPlanet(p[1], 1);
    players.activity[i] = activity;
    players.activityTicks[i] = activity === PlayerActivity.Idle ? rng.int(1, 400) : 0;
    players.speed[i] = PLAYER_RUN_SPEED_MPS;
    players.maxHp[i] = 1400;
    players.hp[i] = 1400;
    players.homeCity[i] = cityIndex;
    players.target[i] = -1;
    players.targetLair[i] = -1;
    players.attackCooldown[i] = rng.int(0, 20);
  }

  return {
    population,
    statics,
    lairs,
    creatures,
    npcs,
    players,
    sets: [statics, lairs, creatures, npcs, players],
    shuttleports,
  };
}

export function clampToPlanet(v: number, halfExtent: number): number {
  const limit = PLANET_HALF_EXTENT_M - halfExtent;
  return v < -limit ? -limit : v > limit ? limit : v;
}

/** A point on the planet outside every city's keep-out disc (1.5 × its radius). */
export function randomWildernessPoint(rng: Rng, out: Float64Array): void {
  const half = PLANET_HALF_EXTENT_M * 0.97;
  for (let attempt = 0; attempt < 24; attempt++) {
    const x = rng.range(-half, half);
    const z = rng.range(-half, half);
    let clear = true;
    for (const city of CITIES) {
      const dx = city.x - x;
      const dz = city.z - z;
      const keepOut = city.radius * 1.5;
      if (dx * dx + dz * dz < keepOut * keepOut) {
        clear = false;
        break;
      }
    }

    if (clear) {
      out[0] = x;
      out[1] = z;
      return;
    }
  }

  out[0] = rng.range(-half, half);
  out[1] = rng.range(-half, half);
}

export function weightedCity(rng: Rng): number {
  const roll = rng.next();
  let acc = 0;
  for (let i = 0; i < CITIES.length; i++) {
    acc += CITIES[i].playerWeight;
    if (roll <= acc) {
      return i;
    }
  }

  return CITIES.length - 1;
}

function buildPlayerCitySites(rng: Rng, p: Float64Array): { x: number; z: number; radius: number }[] {
  const sites: { x: number; z: number; radius: number }[] = [];
  for (let attempt = 0; sites.length < PLAYER_CITY_SITES && attempt < 4000; attempt++) {
    randomWildernessPoint(rng, p);
    const x = p[0];
    const z = p[1];
    const clear = sites.every((site) => {
      const dx = site.x - x;
      const dz = site.z - z;
      return dx * dx + dz * dz >= PLAYER_CITY_SEPARATION_M * PLAYER_CITY_SEPARATION_M;
    });
    if (clear) {
      sites.push({ x, z, radius: PLAYER_CITY_RADII[rng.int(0, PLAYER_CITY_RADII.length)] });
    }
  }

  return sites;
}

function allocStatics(count: number): Statics {
  return {
    archetype: Archetype.WorldObject,
    count,
    x: new Float64Array(count),
    z: new Float64Array(count),
    kind: new Uint8Array(count),
  };
}

function allocLairs(count: number): Lairs {
  return {
    archetype: Archetype.CreatureLair,
    count,
    x: new Float64Array(count),
    z: new Float64Array(count),
    template: new Uint8Array(count),
    missionId: new Uint8Array(count),
    hp: new Float32Array(count),
    maxHp: new Float32Array(count),
    pool: new Uint8Array(count),
    missionTicks: new Int32Array(count),
  };
}

function allocCreatures(count: number): Creatures {
  return {
    archetype: Archetype.Creature,
    count,
    x: new Float64Array(count),
    z: new Float64Array(count),
    vx: new Float64Array(count),
    vz: new Float64Array(count),
    speed: new Float32Array(count),
    destX: new Float64Array(count),
    destZ: new Float64Array(count),
    homeX: new Float64Array(count),
    homeZ: new Float64Array(count),
    mode: new Uint8Array(count),
    hp: new Float32Array(count),
    maxHp: new Float32Array(count),
    template: new Uint8Array(count),
    lair: new Int32Array(count),
    think: new Int32Array(count),
    deadTicks: new Int32Array(count),
    target: new Int32Array(count),
    attackCooldown: new Int32Array(count),
  };
}

function allocNpcs(count: number): Npcs {
  return {
    archetype: Archetype.CityNpc,
    count,
    x: new Float64Array(count),
    z: new Float64Array(count),
    vx: new Float64Array(count),
    vz: new Float64Array(count),
    destX: new Float64Array(count),
    destZ: new Float64Array(count),
    homeX: new Float64Array(count),
    homeZ: new Float64Array(count),
    mode: new Uint8Array(count),
    think: new Int32Array(count),
  };
}

function allocPlayers(count: number): Players {
  return {
    archetype: Archetype.Player,
    count,
    x: new Float64Array(count),
    z: new Float64Array(count),
    vx: new Float64Array(count),
    vz: new Float64Array(count),
    speed: new Float32Array(count),
    destX: new Float64Array(count),
    destZ: new Float64Array(count),
    activity: new Uint8Array(count),
    activityTicks: new Int32Array(count),
    hp: new Float32Array(count),
    maxHp: new Float32Array(count),
    homeCity: new Uint8Array(count),
    shuttleDest: new Uint8Array(count),
    target: new Int32Array(count),
    targetLair: new Int32Array(count),
    attackCooldown: new Int32Array(count),
  };
}
