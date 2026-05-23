import type { ComponentSchema } from '@/hooks/schema/types';

/** A preview column = one field of one component. */
export interface PreviewField {
  typeName: string;
  fieldId: number;
}

// Field types we render as a meaningful inline scalar (FieldType.ToString() values from the server). Complex types
// (vectors, AABBs, collections, components) are excluded from the *default* — the user can still add them via the picker,
// where they show a hex fallback.
const SCALAR_FIELD_TYPES = new Set([
  'Boolean', 'Byte', 'UByte', 'Short', 'UShort', 'Int', 'UInt', 'Long', 'ULong',
  'Float', 'Double', 'Char', 'String64', 'String1024', 'Variant',
]);

export const DEFAULT_PREVIEW_LIMIT = 4;

export function samePreviewField(a: PreviewField, b: PreviewField): boolean {
  return a.typeName === b.typeName && a.fieldId === b.fieldId;
}

export function serializePreview(fields: PreviewField[]): string {
  return fields.map((f) => `${f.typeName}:${f.fieldId}`).join(',');
}

/**
 * The default preview columns for an archetype: the first <paramref name="max"/> scalar fields walked in component order then
 * field-offset order. <paramref name="componentNames"/> are registered component names (def.Name) that key
 * <paramref name="schemas"/>. Returns an empty list until the component schemas have loaded.
 */
export function defaultPreviewFields(
  componentNames: string[],
  schemas: Map<string, ComponentSchema>,
  max = DEFAULT_PREVIEW_LIMIT,
): PreviewField[] {
  const out: PreviewField[] = [];
  for (const typeName of componentNames) {
    const schema = schemas.get(typeName);
    if (!schema) continue;
    for (const f of schema.fields) {
      if (!SCALAR_FIELD_TYPES.has(f.typeName)) continue;
      out.push({ typeName, fieldId: f.fieldId });
      if (out.length >= max) return out;
    }
  }
  return out;
}
