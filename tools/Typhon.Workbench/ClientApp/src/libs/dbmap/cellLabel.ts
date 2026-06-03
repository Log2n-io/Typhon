import type { DbContentCell } from './types';

/**
 * Friendly display label for a decoded entity/component cell. The server decode (L4Decoder) labels component
 * rows with the registered — often namespace-qualified — component name: a `componentHeader` cell's label IS the
 * component name, and a `field` cell's label is `<componentName>.<fieldName>`. This swaps the component portion
 * for the smart short label, leaving the field part intact, so the cluster-entity decode reads the same friendly
 * names as the Schema Explorer / File Map:
 *   - componentHeader: `Typhon.ARPG.Combat.StatusEffects`        → `StatusEffects`
 *   - field:           `Typhon.ARPG.Combat.StatusEffects.Stacks` → `StatusEffects.Stacks`
 * Other cell kinds (entityPk, slot/meta rows, …) are returned unchanged. `shorten` is the session labeller
 * (`useComponentNames`): it resolves a typeName / full name to the short form, else falls back to the leaf segment.
 *
 * The field split is on the LAST dot — field names are plain identifiers (no dots), so everything before it is
 * the component name however many namespace segments it carries.
 */
export function friendlyComponentCellLabel(cell: DbContentCell, shorten: (name: string) => string): string {
  if (cell.kind === 'componentHeader') {
    return shorten(cell.label);
  }
  if (cell.kind === 'field') {
    const dot = cell.label.lastIndexOf('.');
    if (dot > 0) {
      return `${shorten(cell.label.slice(0, dot))}.${cell.label.slice(dot + 1)}`;
    }
  }
  return cell.label;
}
