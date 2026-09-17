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

/**
 * Change groups, shared by every archetype, in the catalog's canonical order (`03-wire-protocol.md` W14). Entering fields
 * carry no group (W15: `onEnter`), and motion travels as segments, not as a group.
 */
export const Group = { State: 0, Vitals: 1 } as const;
const GROUPS = ['state', 'vitals'];

/** Ground positions: 2D on the wire, x and z in the world. */
const MOVING = { kind: 'motion', dims: 2 } as const;

/** Server tick period (10 Hz, Core3-faithful). */
export const TICK_PERIOD_MS = 100;

export const SWG_SCHEMA: WorldSchema = {
  tickPeriodUs: TICK_PERIOD_MS * 1000,
  archetypes: [
    {
      index: Archetype.WorldObject,
      name: 'WorldObject',
      position: { kind: 'static', dims: 2 },
      groups: GROUPS,
      fields: [{ name: 'kind', kind: 'u8' }],
    },
    {
      index: Archetype.CreatureLair,
      name: 'CreatureLair',
      position: MOVING,
      groups: GROUPS,
      fields: [
        { name: 'template', kind: 'u8' },
        { name: 'missionId', kind: 'u8', group: Group.State },
        { name: 'hp', kind: 'f32', group: Group.Vitals },
      ],
    },
    {
      index: Archetype.Creature,
      name: 'Creature',
      position: MOVING,
      groups: GROUPS,
      fields: [
        { name: 'template', kind: 'u8' },
        { name: 'mode', kind: 'u8', group: Group.State },
        { name: 'target', kind: 'u32', group: Group.State },
        { name: 'hp', kind: 'f32', group: Group.Vitals },
      ],
    },
    {
      index: Archetype.CityNpc,
      name: 'CityNpc',
      position: MOVING,
      groups: GROUPS,
      fields: [{ name: 'mode', kind: 'u8', group: Group.State }],
    },
    {
      index: Archetype.Player,
      name: 'Player',
      position: MOVING,
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
  // Axis 0 is world x, axis 1 world z.
  origin: [-8192, -8192],
  cell: 256,
  dims: [64, 64],
  archetypes: [Archetype.CreatureLair, Archetype.Creature, Archetype.CityNpc, Archetype.Player],
};

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
