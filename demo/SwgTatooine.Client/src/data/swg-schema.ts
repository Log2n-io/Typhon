import type { GridSchema, WorldSchema } from '@typhondb/client';

/**
 * What the server's catalog will describe for this demo (`claude/design/SwgTatooine/04-protocol.md` § 2), in the shape the
 * SDK store needs. Until the live server exists the mock produces exactly this; afterwards the catalog parser does.
 */

export const Archetype = {
  WorldObject: 0,
  CreatureLair: 1,
  Creature: 2,
  CityNpc: 3,
  Player: 4,
} as const;

export type ArchetypeIndex = (typeof Archetype)[keyof typeof Archetype];

export const ARCHETYPE_COUNT = 5;

/** Change groups, shared by every archetype (`01-model.md` § 2 default groups plus `vitals`). */
export const Group = { Enter: 0, Motion: 1, State: 2, Vitals: 3 } as const;
const GROUPS = ['enter', 'motion', 'state', 'vitals'];

export const SWG_SCHEMA: WorldSchema = {
  archetypes: [
    {
      index: Archetype.WorldObject,
      name: 'WorldObject',
      position: 'static',
      groups: GROUPS,
      fields: [{ name: 'kind', kind: 'u8', group: Group.Enter }],
    },
    {
      index: Archetype.CreatureLair,
      name: 'CreatureLair',
      position: 'motion',
      groups: GROUPS,
      fields: [
        { name: 'template', kind: 'u8', group: Group.Enter },
        { name: 'missionId', kind: 'u8', group: Group.State },
        { name: 'hp', kind: 'f32', group: Group.Vitals },
      ],
    },
    {
      index: Archetype.Creature,
      name: 'Creature',
      position: 'motion',
      groups: GROUPS,
      fields: [
        { name: 'template', kind: 'u8', group: Group.Enter },
        { name: 'mode', kind: 'u8', group: Group.State },
        { name: 'target', kind: 'u32', group: Group.State },
        { name: 'hp', kind: 'f32', group: Group.Vitals },
      ],
    },
    {
      index: Archetype.CityNpc,
      name: 'CityNpc',
      position: 'motion',
      groups: GROUPS,
      fields: [{ name: 'mode', kind: 'u8', group: Group.State }],
    },
    {
      index: Archetype.Player,
      name: 'Player',
      position: 'motion',
      groups: GROUPS,
      fields: [
        { name: 'activity', kind: 'u8', group: Group.State },
        { name: 'controller', kind: 'u8', group: Group.State },
        { name: 'target', kind: 'u32', group: Group.State },
        { name: 'hp', kind: 'f32', group: Group.Vitals },
      ],
    },
  ],
};

/** The far tier: per-cell counts at 256 m over the whole planet. WorldObject is not counted (it never moves). */
export const AGG_GRID: GridSchema = {
  index: 0,
  originX: -8192,
  originZ: -8192,
  cellM: 256,
  dimsX: 64,
  dimsZ: 64,
  archetypes: [Archetype.CreatureLair, Archetype.Creature, Archetype.CityNpc, Archetype.Player],
};

/** Server tick period (10 Hz, Core3-faithful). */
export const TICK_PERIOD_MS = 100;

/** Position quantum: 24 bits over the 16 384 m planet (`03-wire-protocol.md` § 4). */
export const POSITION_QUANTUM_M = 16384 / (1 << 24);

/** Velocity quantum: 1/16 of a position quantum per tick. */
export const VELOCITY_QUANTUM_M = POSITION_QUANTUM_M / 16;

export const ARCHETYPE_LABELS: readonly string[] = ['Structure', 'Lair', 'Creature', 'City NPC', 'Player'];

export const AI_MODE_NAMES: readonly string[] = ['Idle', 'Wander', 'Pursue', 'Fighting', 'Leashing', 'Dead'];
export const AiMode = { Idle: 0, Wander: 1, Pursue: 2, Fighting: 3, Leashing: 4, Dead: 5 } as const;

export const PLAYER_ACTIVITY_NAMES: readonly string[] = [
  'Idle',
  'Travelling',
  'Combat',
  'Roaming',
  'To shuttle',
  'Awaiting shuttle',
];
export const PlayerActivity = {
  Idle: 0,
  Travelling: 1,
  Combat: 2,
  Roaming: 3,
  ToShuttle: 4,
  AwaitingShuttle: 5,
} as const;

export const CONTROLLER_NAMES: readonly string[] = ['Simulated', 'Bot', 'Human'];

export const STRUCTURE_KIND_NAMES: readonly string[] = [
  'Building',
  'Terminal',
  'Shuttleport',
  'Landmark prop',
  'House',
  'Factory',
  'Harvester',
  'Camp object',
];
export const StructureKind = {
  Building: 0,
  Terminal: 1,
  Shuttleport: 2,
  PoiProp: 3,
  PlayerHouse: 4,
  Factory: 5,
  Harvester: 6,
  CampObject: 7,
} as const;
