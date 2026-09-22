import { AiMode, Archetype, PlayerActivity } from '../swg-schema';
import {
  AGGRO_RADIUS_M,
  CITIES,
  LAIR_SPAWN_RADIUS_M,
  LEASH_RADIUS_M,
  MAX_CHASE_RANGE_M,
  PLAYER_MOUNT_SPEED_MPS,
  PLAYER_RUN_SPEED_MPS,
  POIS,
  TEMPLATE_AGGRESSIVE,
  TEMPLATE_DAMAGE,
  WEAPON_RANGE_M,
} from '../world-data';
import type { SpatialBins } from './bins';
import { Rng } from './rng';
import { clampToPlanet, randomWildernessPoint, weightedCity, type MockWorld } from './world';

/**
 * The mock simulation: the behaviour of `demo/SwgTatooine/Sim/SimBridge.*.cs`, with the fixes the live-demo design
 * schedules before any client (`03-server.md` § 10, `06-gameplay.md` § 3), so god mode shows causality:
 *
 * - no snap-to-lair: a creature returns to its lair only when revived (S0-1); spawns clamped to the planet (S0-2);
 *   constants in seconds (S0-3);
 * - target-aware two-way combat: players at a landmark pick a target and shoot it, creatures chase and hit back, players
 *   are cloned at the nearest city when they fall (S1);
 * - a closed mission loop: a mission lair relocates near a fighting player and takes its defenders along, that player
 *   travels to it, and the lair goes dormant when destroyed (G1, G2, G10).
 *
 * Every hit is recorded as an event for the attack-line overlay. Spatial queries read the server's bins, which describe
 * positions at the start of the tick.
 */

export const TICK_HZ = 10;
const DT = 1 / TICK_HZ;
const CREATURE_ATTACK_RANGE_M = 8;
const PLAYER_ATTACK_DAMAGE = 95;
const CREATURE_RESPAWN_TICKS = 180 * TICK_HZ;
const MISSION_OFFER_INTERVAL_TICKS = 5 * TICK_HZ;

export const EventKind = { Attack: 1 } as const;

/** Hits recorded during a tick: attacker archetype and index, target archetype and index, damage, target health after. */
export const SIM_EVENT_STRIDE = 6;

export class SimEvents {
  data = new Float64Array(SIM_EVENT_STRIDE * 256);
  count = 0;

  push(
    attackerArch: number,
    attacker: number,
    targetArch: number,
    target: number,
    damage: number,
    hpAfter: number,
  ): void {
    const o = this.count * SIM_EVENT_STRIDE;
    if (o + SIM_EVENT_STRIDE > this.data.length) {
      const next = new Float64Array(this.data.length * 2);
      next.set(this.data);
      this.data = next;
    }

    this.data[o] = attackerArch;
    this.data[o + 1] = attacker;
    this.data[o + 2] = targetArch;
    this.data[o + 3] = target;
    this.data[o + 4] = damage;
    this.data[o + 5] = hpAfter;
    this.count++;
  }

  clear(): void {
    this.count = 0;
  }
}

export class MockSimulation {
  readonly world: MockWorld;
  readonly events = new SimEvents();
  /** Set when a lair moved this tick: its bins need a rebuild. */
  lairsMoved = false;

  private readonly rng: Rng;
  private readonly p = new Float64Array(2);
  private bins: readonly SpatialBins[] = [];
  // Query predicates, allocated once rather than per query.
  private readonly livingCreature: (i: number) => boolean;
  private readonly livingPlayer: (i: number) => boolean;
  private readonly activeMissionLair: (i: number) => boolean;

  constructor(world: MockWorld, seed: number) {
    this.world = world;
    this.rng = new Rng(seed ^ 0x5bd1e995);
    const cr = world.creatures;
    const pl = world.players;
    const lairs = world.lairs;
    this.livingCreature = (i) => cr.mode[i] !== AiMode.Dead;
    this.livingPlayer = (i) => pl.hp[i] > 0;
    this.activeMissionLair = (i) => lairs.missionId[i] !== 0;
  }

  /** Advances one tick. `bins` index by archetype and reflect positions at the start of the tick. */
  step(tick: number, bins: readonly SpatialBins[]): void {
    this.bins = bins;
    this.events.clear();
    this.lairsMoved = false;
    this.stepPlayers();
    this.stepCreatures();
    this.stepNpcs();
    this.stepMissions(tick);
  }

  // ── Players ─────────────────────────────────────────────────────────────────────────────────────────────────────

  private stepPlayers(): void {
    const pl = this.world.players;
    const rng = this.rng;
    for (let i = 0; i < pl.count; i++) {
      if (pl.hp[i] <= 0) {
        this.clonePlayer(i);
        continue;
      }

      const activity = pl.activity[i];
      if (pl.activityTicks[i] > 0) {
        pl.activityTicks[i]--;
        if (activity === PlayerActivity.AwaitingShuttle) {
          if (pl.activityTicks[i] === 0) {
            // The shuttle lands: the waiting passenger reappears at the destination port.
            const dest = pl.shuttleDest[i];
            this.teleportPlayer(i, this.world.shuttleports[dest * 2], this.world.shuttleports[dest * 2 + 1], 10);
            pl.activity[i] = PlayerActivity.Idle;
            pl.activityTicks[i] = rng.int(20, 120) * TICK_HZ;
          }
        } else if (activity !== PlayerActivity.Idle) {
          const arrived = distSq(pl.x[i], pl.z[i], pl.destX[i], pl.destZ[i]) < 25;
          if (arrived) {
            pl.vx[i] = 0;
            pl.vz[i] = 0;
            if (activity === PlayerActivity.ToShuttle) {
              pl.activity[i] = PlayerActivity.AwaitingShuttle;
              pl.activityTicks[i] = rng.int(5, 40) * TICK_HZ;
            } else if (activity !== PlayerActivity.Combat) {
              pl.activityTicks[i] = 0;
            }
          } else {
            steer(pl.vx, pl.vz, i, pl.speed[i], pl.x[i], pl.z[i], pl.destX[i], pl.destZ[i]);
          }
        }
      } else {
        this.choosePlayerActivity(i);
      }

      if (pl.activity[i] === PlayerActivity.Combat && pl.vx[i] === 0 && pl.vz[i] === 0) {
        this.playerFight(i);
      } else {
        pl.target[i] = -1;
        pl.targetLair[i] = -1;
      }

      pl.x[i] = clampToPlanet(pl.x[i] + pl.vx[i], 1);
      pl.z[i] = clampToPlanet(pl.z[i] + pl.vz[i], 1);
    }
  }

  private choosePlayerActivity(i: number): void {
    const pl = this.world.players;
    const rng = this.rng;
    const roll = rng.next();
    const p = this.p;
    if (roll < 0.4) {
      pl.activity[i] = PlayerActivity.Idle;
      pl.activityTicks[i] = rng.int(20, 120) * TICK_HZ;
      pl.vx[i] = 0;
      pl.vz[i] = 0;
      return;
    }

    if (roll < 0.6) {
      const from = nearestCity(pl.x[i], pl.z[i]);
      if (rng.next() < 0.25) {
        pl.activity[i] = PlayerActivity.ToShuttle;
        pl.destX[i] = this.world.shuttleports[from * 2];
        pl.destZ[i] = this.world.shuttleports[from * 2 + 1];
        pl.shuttleDest[i] = (from + rng.int(1, CITIES.length)) % CITIES.length;
        pl.speed[i] = PLAYER_RUN_SPEED_MPS;
      } else {
        const city = CITIES[weightedCity(rng)];
        rng.pointInDisc(city.x, city.z, city.radius, p);
        pl.activity[i] = PlayerActivity.Travelling;
        pl.destX[i] = p[0];
        pl.destZ[i] = p[1];
        pl.speed[i] = rng.next() < 0.5 ? PLAYER_RUN_SPEED_MPS : PLAYER_MOUNT_SPEED_MPS;
      }

      pl.activityTicks[i] = 3600 * TICK_HZ;
    } else if (roll < 0.82) {
      const poi = POIS[rng.int(0, POIS.length)];
      rng.pointInDisc(poi.x, poi.z, poi.radius, p);
      this.sendToFight(i, p[0], p[1]);
    } else {
      const a = rng.next() * Math.PI * 2;
      const r = rng.range(200, 1000);
      pl.activity[i] = PlayerActivity.Roaming;
      pl.destX[i] = clampToPlanet(pl.x[i] + Math.cos(a) * r, 16);
      pl.destZ[i] = clampToPlanet(pl.z[i] + Math.sin(a) * r, 16);
      pl.speed[i] = PLAYER_RUN_SPEED_MPS;
      pl.activityTicks[i] = 300 * TICK_HZ;
    }

    steer(pl.vx, pl.vz, i, pl.speed[i], pl.x[i], pl.z[i], pl.destX[i], pl.destZ[i]);
  }

  private sendToFight(i: number, x: number, z: number): void {
    const pl = this.world.players;
    pl.activity[i] = PlayerActivity.Combat;
    pl.destX[i] = clampToPlanet(x, 1);
    pl.destZ[i] = clampToPlanet(z, 1);
    pl.speed[i] = PLAYER_MOUNT_SPEED_MPS;
    pl.activityTicks[i] = 600 * TICK_HZ;
    steer(pl.vx, pl.vz, i, pl.speed[i], pl.x[i], pl.z[i], pl.destX[i], pl.destZ[i]);
  }

  private playerFight(i: number): void {
    const pl = this.world.players;
    const cr = this.world.creatures;
    const lairs = this.world.lairs;
    const range2 = WEAPON_RANGE_M * WEAPON_RANGE_M;
    let t = pl.target[i];
    if (t >= 0 && (cr.mode[t] === AiMode.Dead || distSq(pl.x[i], pl.z[i], cr.x[t], cr.z[t]) > range2)) {
      t = -1;
    }

    if (t < 0) {
      t = this.bins[Archetype.Creature].nearest(cr, pl.x[i], pl.z[i], WEAPON_RANGE_M, this.livingCreature);
    }

    pl.target[i] = t;
    let lair = -1;
    if (t < 0) {
      lair = pl.targetLair[i];
      if (
        lair >= 0 &&
        (lairs.missionId[lair] === 0 || distSq(pl.x[i], pl.z[i], lairs.x[lair], lairs.z[lair]) > range2)
      ) {
        lair = -1;
      }

      if (lair < 0) {
        lair = this.bins[Archetype.CreatureLair].nearest(
          lairs,
          pl.x[i],
          pl.z[i],
          WEAPON_RANGE_M,
          this.activeMissionLair,
        );
      }
    }

    pl.targetLair[i] = lair;
    if ((t < 0 && lair < 0) || --pl.attackCooldown[i] > 0) {
      return;
    }

    pl.attackCooldown[i] = this.rng.int(10, 30);
    if (t >= 0) {
      cr.hp[t] = Math.max(0, cr.hp[t] - PLAYER_ATTACK_DAMAGE);
      this.events.push(Archetype.Player, i, Archetype.Creature, t, PLAYER_ATTACK_DAMAGE, cr.hp[t] / cr.maxHp[t]);
      if (cr.hp[t] === 0) {
        cr.mode[t] = AiMode.Dead;
        cr.vx[t] = 0;
        cr.vz[t] = 0;
        cr.target[t] = -1;
        cr.deadTicks[t] = CREATURE_RESPAWN_TICKS;
        pl.target[i] = -1;
      } else if (cr.target[t] < 0) {
        // A wounded creature turns on whoever is shooting it.
        cr.target[t] = i;
        cr.mode[t] = AiMode.Pursue;
        cr.think[t] = 0;
      }

      return;
    }

    lairs.hp[lair] = Math.max(0, lairs.hp[lair] - PLAYER_ATTACK_DAMAGE);
    this.events.push(
      Archetype.Player,
      i,
      Archetype.CreatureLair,
      lair,
      PLAYER_ATTACK_DAMAGE,
      lairs.hp[lair] / lairs.maxHp[lair],
    );
    if (lairs.hp[lair] === 0) {
      // Destroyed: the mission completes, the lair goes dormant where it stands and heals for its next mission.
      lairs.missionId[lair] = 0;
      lairs.missionTicks[lair] = 0;
      lairs.hp[lair] = lairs.maxHp[lair];
      pl.targetLair[i] = -1;
      pl.activityTicks[i] = 1;
    }
  }

  private clonePlayer(i: number): void {
    const pl = this.world.players;
    const city = CITIES[nearestCity(pl.x[i], pl.z[i])];
    this.teleportPlayer(i, city.x, city.z, city.radius * 0.5);
    pl.hp[i] = pl.maxHp[i];
    pl.activity[i] = PlayerActivity.Idle;
    pl.activityTicks[i] = this.rng.int(10, 60) * TICK_HZ;
    pl.target[i] = -1;
    pl.targetLair[i] = -1;
  }

  private teleportPlayer(i: number, x: number, z: number, jitter: number): void {
    const pl = this.world.players;
    this.rng.pointInDisc(x, z, jitter, this.p);
    pl.x[i] = pl.destX[i] = clampToPlanet(this.p[0], 1);
    pl.z[i] = pl.destZ[i] = clampToPlanet(this.p[1], 1);
    pl.vx[i] = 0;
    pl.vz[i] = 0;
  }

  // ── Creatures ───────────────────────────────────────────────────────────────────────────────────────────────────

  private stepCreatures(): void {
    const cr = this.world.creatures;
    const pl = this.world.players;
    const rng = this.rng;
    const playerBins = this.bins[Archetype.Player];
    const leash2 = LEASH_RADIUS_M * LEASH_RADIUS_M;
    const chase2 = MAX_CHASE_RANGE_M * MAX_CHASE_RANGE_M;
    const attack2 = CREATURE_ATTACK_RANGE_M * CREATURE_ATTACK_RANGE_M;
    for (let i = 0; i < cr.count; i++) {
      const mode = cr.mode[i];
      if (mode === AiMode.Dead) {
        if (--cr.deadTicks[i] <= 0) {
          // Revived by its lair, back home: the one jump a creature makes.
          rng.pointInDisc(cr.homeX[i], cr.homeZ[i], LAIR_SPAWN_RADIUS_M, this.p);
          cr.x[i] = cr.destX[i] = clampToPlanet(this.p[0], 1.5);
          cr.z[i] = cr.destZ[i] = clampToPlanet(this.p[1], 1.5);
          cr.hp[i] = cr.maxHp[i];
          cr.mode[i] = AiMode.Wander;
          cr.think[i] = rng.int(4, 11);
        }

        continue;
      }

      if (mode === AiMode.Fighting) {
        this.creatureFight(i);
      }

      if (cr.think[i] > 0) {
        cr.think[i]--;
      } else {
        cr.think[i] = rng.int(4, 11);
        const x = cr.x[i];
        const z = cr.z[i];
        const home2 = distSq(x, z, cr.homeX[i], cr.homeZ[i]);
        if (home2 > leash2) {
          cr.mode[i] = AiMode.Leashing;
          cr.target[i] = -1;
          steer(cr.vx, cr.vz, i, cr.speed[i], x, z, cr.homeX[i], cr.homeZ[i]);
        } else if (mode === AiMode.Leashing) {
          if (home2 < 16) {
            cr.mode[i] = AiMode.Wander;
            this.pickWanderDestination(i);
          } else {
            steer(cr.vx, cr.vz, i, cr.speed[i], x, z, cr.homeX[i], cr.homeZ[i]);
          }
        } else if (mode === AiMode.Pursue || mode === AiMode.Fighting) {
          const t = cr.target[i];
          if (t < 0 || pl.hp[t] <= 0 || distSq(x, z, pl.x[t], pl.z[t]) > chase2) {
            cr.mode[i] = AiMode.Wander;
            cr.target[i] = -1;
            this.pickWanderDestination(i);
          } else if (distSq(x, z, pl.x[t], pl.z[t]) <= attack2) {
            cr.mode[i] = AiMode.Fighting;
            cr.vx[i] = 0;
            cr.vz[i] = 0;
          } else {
            cr.mode[i] = AiMode.Pursue;
            cr.destX[i] = pl.x[t];
            cr.destZ[i] = pl.z[t];
            steer(cr.vx, cr.vz, i, cr.speed[i], x, z, pl.x[t], pl.z[t]);
          }
        } else {
          if (distSq(x, z, cr.destX[i], cr.destZ[i]) < 4) {
            this.pickWanderDestination(i);
          } else {
            steer(cr.vx, cr.vz, i, cr.speed[i], x, z, cr.destX[i], cr.destZ[i]);
          }

          if (TEMPLATE_AGGRESSIVE[cr.template[i]]) {
            const t = playerBins.nearest(pl, x, z, AGGRO_RADIUS_M, this.livingPlayer);
            if (t >= 0) {
              cr.mode[i] = AiMode.Pursue;
              cr.target[i] = t;
              steer(cr.vx, cr.vz, i, cr.speed[i], x, z, pl.x[t], pl.z[t]);
            }
          }
        }
      }

      cr.x[i] = clampToPlanet(cr.x[i] + cr.vx[i], 1.5);
      cr.z[i] = clampToPlanet(cr.z[i] + cr.vz[i], 1.5);
    }
  }

  private creatureFight(i: number): void {
    const cr = this.world.creatures;
    const pl = this.world.players;
    const t = cr.target[i];
    if (t < 0 || pl.hp[t] <= 0 || --cr.attackCooldown[i] > 0) {
      return;
    }

    cr.attackCooldown[i] = this.rng.int(10, 30);
    const damage = TEMPLATE_DAMAGE[cr.template[i]];
    pl.hp[t] = Math.max(0, pl.hp[t] - damage);
    this.events.push(Archetype.Creature, i, Archetype.Player, t, damage, pl.hp[t] / pl.maxHp[t]);
  }

  private pickWanderDestination(i: number): void {
    const cr = this.world.creatures;
    const a = this.rng.next() * Math.PI * 2;
    const r = LEASH_RADIUS_M * 0.7 * Math.sqrt(this.rng.next());
    cr.destX[i] = clampToPlanet(cr.homeX[i] + Math.cos(a) * r, 1.5);
    cr.destZ[i] = clampToPlanet(cr.homeZ[i] + Math.sin(a) * r, 1.5);
    steer(cr.vx, cr.vz, i, cr.speed[i], cr.x[i], cr.z[i], cr.destX[i], cr.destZ[i]);
  }

  // ── City NPCs ───────────────────────────────────────────────────────────────────────────────────────────────────

  private stepNpcs(): void {
    const np = this.world.npcs;
    for (let i = 0; i < np.count; i++) {
      if (np.mode[i] !== AiMode.Wander) {
        continue;
      }

      if (np.think[i] > 0) {
        np.think[i]--;
      } else {
        np.think[i] = this.rng.int(20, 80);
        const a = this.rng.next() * Math.PI * 2;
        np.destX[i] = np.homeX[i] + Math.cos(a) * 8;
        np.destZ[i] = np.homeZ[i] + Math.sin(a) * 8;
      }

      steer(np.vx, np.vz, i, 1.2, np.x[i], np.z[i], np.destX[i], np.destZ[i]);
      np.x[i] = np.x[i] + np.vx[i];
      np.z[i] = np.z[i] + np.vz[i];
    }
  }

  // ── Destroy missions ────────────────────────────────────────────────────────────────────────────────────────────

  private stepMissions(tick: number): void {
    const lairs = this.world.lairs;
    for (let l = 0; l < lairs.count; l++) {
      if (lairs.missionId[l] !== 0 && --lairs.missionTicks[l] <= 0) {
        lairs.missionId[l] = 0;
      }
    }

    if (tick % MISSION_OFFER_INTERVAL_TICKS !== 0) {
      return;
    }

    const pl = this.world.players;
    const rng = this.rng;
    if (pl.count === 0 || lairs.count === 0) {
      return;
    }

    const player = rng.int(0, pl.count);
    if (pl.activity[player] !== PlayerActivity.Combat) {
      return;
    }

    const start = rng.int(0, lairs.count);
    for (let k = 0; k < lairs.count; k++) {
      const l = (start + k) % lairs.count;
      if (lairs.pool[l] !== 1 || lairs.missionId[l] !== 0) {
        continue;
      }

      const a = rng.next() * Math.PI * 2;
      const d = rng.range(1000, 2000);
      let x = clampToPlanet(pl.x[player] + Math.cos(a) * d, 4);
      let z = clampToPlanet(pl.z[player] + Math.sin(a) * d, 4);
      if (!Number.isFinite(x) || !Number.isFinite(z)) {
        randomWildernessPoint(rng, this.p);
        x = this.p[0];
        z = this.p[1];
      }

      lairs.x[l] = x;
      lairs.z[l] = z;
      lairs.missionId[l] = rng.int(1, 10);
      lairs.missionTicks[l] = rng.int(180, 420) * TICK_HZ;
      lairs.hp[l] = lairs.maxHp[l];
      this.lairsMoved = true;
      this.relocateDefenders(l, x, z);
      // The player who took the mission rides to it.
      rng.pointInDisc(x, z, 40, this.p);
      this.sendToFight(player, this.p[0], this.p[1]);
      return;
    }
  }

  private relocateDefenders(lair: number, x: number, z: number): void {
    const cr = this.world.creatures;
    for (let i = 0; i < cr.count; i++) {
      if (cr.lair[i] !== lair) {
        continue;
      }

      this.rng.pointInDisc(x, z, LAIR_SPAWN_RADIUS_M, this.p);
      cr.x[i] = cr.destX[i] = clampToPlanet(this.p[0], 1.5);
      cr.z[i] = cr.destZ[i] = clampToPlanet(this.p[1], 1.5);
      cr.homeX[i] = x;
      cr.homeZ[i] = z;
      cr.vx[i] = 0;
      cr.vz[i] = 0;
      cr.target[i] = -1;
      if (cr.mode[i] !== AiMode.Dead) {
        cr.mode[i] = AiMode.Wander;
      }
    }
  }
}

/** Points a velocity (metres per tick) at a destination, never overshooting it. */
export function steer(
  vx: Float64Array,
  vz: Float64Array,
  i: number,
  speedMps: number,
  x: number,
  z: number,
  destX: number,
  destZ: number,
): void {
  const dx = destX - x;
  const dz = destZ - z;
  const len = Math.sqrt(dx * dx + dz * dz);
  if (len < 0.001) {
    vx[i] = 0;
    vz[i] = 0;
    return;
  }

  const step = Math.min(speedMps * DT, len);
  vx[i] = (dx / len) * step;
  vz[i] = (dz / len) * step;
}

function distSq(ax: number, az: number, bx: number, bz: number): number {
  const dx = ax - bx;
  const dz = az - bz;
  return dx * dx + dz * dz;
}

function nearestCity(x: number, z: number): number {
  let best = 0;
  let bestD2 = Infinity;
  for (let i = 0; i < CITIES.length; i++) {
    const d2 = distSq(x, z, CITIES[i].x, CITIES[i].z);
    if (d2 < bestD2) {
      bestD2 = d2;
      best = i;
    }
  }

  return best;
}
