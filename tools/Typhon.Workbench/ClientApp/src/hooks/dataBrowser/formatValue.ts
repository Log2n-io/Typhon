import type { ComponentValue } from './types';

/**
 * Renders a decoded field value for display. Primitive scalars print directly; complex/unsupported fields (value === null)
 * fall back to a truncated hex dump of the raw bytes. Shared by the Component Cards detail and the entity-list preview columns.
 */
export function formatValue(field: ComponentValue): string {
  if (field.value === null || field.value === undefined) {
    if (!field.raw) return '—';
    return field.raw.length > 16 ? `0x${field.raw.slice(0, 16)}…` : `0x${field.raw}`;
  }
  if (typeof field.value === 'boolean') return field.value ? 'true' : 'false';
  return String(field.value);
}
