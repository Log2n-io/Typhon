import type { ArchetypeStore } from '@typhondb/client';
import { AI_MODE_NAMES, Archetype, CONTROLLER_NAMES, PLAYER_ACTIVITY_NAMES, STRUCTURE_KIND_NAMES } from './swg-schema';
import { CREATURE_TEMPLATE_NAMES } from './world-data';

/** Human-readable values of the SWG fields, for the inspector. */

export function describeFields(
  archetype: number,
  store: ArchetypeStore,
  slot: number,
): { name: string; value: string }[] {
  return store.schema.fields.map((field, index) => ({
    name: field.name,
    value: formatField(archetype, field.name, store.fieldAt(index)[slot]),
  }));
}

export function formatField(archetype: number, name: string, raw: number): string {
  switch (name) {
    case 'hp':
      return `${Math.round(raw * 100)} %`;
    case 'template':
      return CREATURE_TEMPLATE_NAMES[raw] ?? String(raw);
    case 'mode':
      return AI_MODE_NAMES[raw] ?? String(raw);
    case 'activity':
      return PLAYER_ACTIVITY_NAMES[raw] ?? String(raw);
    case 'controller':
      return CONTROLLER_NAMES[raw] ?? String(raw);
    case 'kind':
      return STRUCTURE_KIND_NAMES[raw] ?? String(raw);
    case 'target':
      return raw === 0 ? '—' : `#${raw}`;
    case 'missionId':
      return archetype === Archetype.CreatureLair && raw > 0 ? `mission, difficulty ${raw}` : 'dormant';
    default:
      return String(raw);
  }
}
