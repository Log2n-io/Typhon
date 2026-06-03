import { useSessionStore } from '@/stores/useSessionStore';
import { useGetApiSessionsSessionIdSchemaComponentsTypeName } from '@/api/generated/schema/schema';
import type { ComponentSchemaDto } from '@/api/generated/model/componentSchemaDto';

/**
 * Field list + per-field metadata for a single component — drives the WHERE popover's field-picker (which greys
 * out non-indexed fields per design §4.3) and the ORDER BY chip's field picker (indexed-only).
 *
 * Stale-time 30 s matches the rest of the Workbench's schema caching — a session's schema is effectively static
 * (binaries → schema hash → file), so an aggressive cache doesn't risk staleness.
 */
export function useComponentSchema(typeName: string | null | undefined): {
  schema: ComponentSchemaDto | null;
  isLoading: boolean;
} {
  const sessionId = useSessionStore((s) => s.sessionId);
  const query = useGetApiSessionsSessionIdSchemaComponentsTypeName(sessionId ?? '', typeName ?? '', {
    query: { enabled: !!sessionId && !!typeName, staleTime: 30_000 },
  });
  return {
    schema: query.data?.data ?? null,
    isLoading: query.isLoading,
  };
}

/**
 * Operators valid for a given field type. The engine's `ExpressionParser` accepts ==, !=, >, <, >=, <= for any
 * comparable type; for booleans only equality makes sense. Strings allow equality. Falls back to all-six when the
 * type isn't recognised so the picker doesn't silently swallow operators (the compiler will reject if invalid).
 */
const NUMERIC_OPS = ['==', '!=', '>', '<', '>=', '<='] as const;
const EQ_ONLY_OPS = ['==', '!='] as const;

// Typhon's `FieldType` enum (Typhon.Schema.Definition.FieldType) — schema endpoints emit `f.Type.ToString()` so
// these are the *Typhon* names, NOT the .NET CLR names. Char is numeric-ish but treated as eq-only since users
// rarely range-compare it. Reference/composite types (String*, Point*, AABB*, Variant, Component, Collection,
// EntityRef…) fall through to EQ_ONLY_OPS at the call site.
const NUMERIC_TYPE_NAMES = new Set([
  'Byte', 'UByte', 'Short', 'UShort', 'Int', 'UInt', 'Long', 'ULong',
  'Float', 'Double',
]);

export type ComparisonOp = typeof NUMERIC_OPS[number];

export function operatorsForType(typeName: string | null | undefined): readonly ComparisonOp[] {
  if (!typeName) return NUMERIC_OPS;
  if (NUMERIC_TYPE_NAMES.has(typeName)) return NUMERIC_OPS;
  if (typeName === 'Boolean') return EQ_ONLY_OPS;
  // Strings / enums / EntityId / Variant / Point* / etc. — equality + inequality only. Range comparison on these
  // is either undefined (strings: no canonical ordering for String64) or engine-rejected.
  return EQ_ONLY_OPS;
}

/** Whether the field type editor should be a numeric `<input type="number">` (true) or text `<input>` (false). */
export function isNumericType(typeName: string | null | undefined): boolean {
  return !!typeName && NUMERIC_TYPE_NAMES.has(typeName);
}
