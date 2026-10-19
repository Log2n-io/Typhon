import type { ArchetypeStore } from '@typhondb/client';
import { AI_MODE_NAMES, CONTROLLER_NAMES, PLAYER_ACTIVITY_NAMES, STRUCTURE_KIND_NAMES } from './swg-schema';
import { CREATURE_TEMPLATE_NAMES } from './world-data';

/** Human-readable values of the SWG fields, for the inspector. */

/**
 * Every field the STORE declares, in its order, formatted. Which fields exist is the catalog's to say: the live catalog
 * gives a creature `aggro`, `mode` and `hp` and no `template`, where the mock gave it a template. Nothing here assumes a
 * field is present, and an archetype is identified by its name.
 */
export function describeFields(store: ArchetypeStore, slot: number): { name: string; value: string }[] {
  return store.schema.fields.map((field, index) => ({
    name: field.name,
    value: formatField(store.schema.name, field.name, store.fieldAt(index)[slot]),
  }));
}

export function formatField(archetype: string, name: string, raw: number): string {
  switch (name) {
    case 'hp':
      return `${Math.round(raw * 100)} %`;
    case 'aggro':
      return `${raw.toFixed(0)} m`;
    case 'region':
      // Sent as region + 1, with 0 for a structure in the wild: the field is a group set's key and a sparse index never files key 0.
      return raw === 0 ? 'wilderness' : `region ${raw - 1}`;
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
      return archetype === 'CreatureLair' && raw > 0 ? `mission, difficulty ${raw}` : 'dormant';
    default:
      return String(raw);
  }
}
