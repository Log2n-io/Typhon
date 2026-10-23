import type { WorldSchema } from '@typhondb/client';

/**
 * Which archetype is which, resolved from the schema the source actually has.
 *
 * <b>Why this exists.</b> Until C4 the client held its own `Archetype` constant and indexed styles, labels, layer
 * toggles and the inspector by it. The server's catalog numbers archetypes in its own order — alphabetically, as it
 * happens, so `WorldObject` is 4 there and 0 here — and nothing would have told us: every entity would have been drawn,
 * in the wrong shape, reading the wrong fields. Identity is the archetype's NAME, which both the mock and the catalog
 * agree on; an index is only ever a position in one particular schema.
 */

/** The archetypes this client has art and field knowledge for. Anything else a catalog declares still draws, plainly. */
export const KNOWN_ARCHETYPES = ['WorldObject', 'CreatureLair', 'Creature', 'CityNpc', 'Player'] as const;

export type KnownArchetype = (typeof KNOWN_ARCHETYPES)[number];

const LABELS: Readonly<Record<string, string>> = {
  WorldObject: 'Structure',
  CreatureLair: 'Lair',
  Creature: 'Creature',
  CityNpc: 'City NPC',
  Player: 'Player',
};

/** An archetype as the UI names it: the catalog's name, and what to print. */
export interface ArchetypeInfo {
  readonly name: string;
  readonly label: string;
}

/** A schema's archetypes in its own index order, plus the lookup the renderer needs. */
export interface ArchetypeView {
  readonly count: number;
  /** By index, in the schema's order. */
  readonly infos: readonly ArchetypeInfo[];
  /** The index this schema gives a name, or −1 when it declares no such archetype. */
  indexOf(name: string): number;
  label(index: number): string;
  name(index: number): string;
}

/** What a name should be printed as: a known archetype's label, or the name itself for one this client does not know. */
export function labelOf(name: string): string {
  return LABELS[name] ?? name;
}

export function resolveArchetypes(schema: WorldSchema): ArchetypeView {
  const infos = schema.archetypes.map((a): ArchetypeInfo => ({ name: a.name, label: labelOf(a.name) }));
  const byName = new Map<string, number>();
  schema.archetypes.forEach((a, index) => {
    byName.set(a.name, index);
  });
  return {
    count: infos.length,
    infos,
    indexOf: (name) => byName.get(name) ?? -1,
    label: (index) => infos[index]?.label ?? '?',
    name: (index) => infos[index]?.name ?? '',
  };
}

/** What the UI shows before a source has a schema: the known archetypes, in this client's own order. */
export const DEFAULT_ARCHETYPE_INFOS: readonly ArchetypeInfo[] = KNOWN_ARCHETYPES.map((name) => ({
  name,
  label: labelOf(name),
}));
