/**
 * Coerce a `number | string` field from an Orval-generated DTO to a plain `number`.
 *
 * Orval renders large numeric JSON fields as `number | string` to avoid JS precision loss on
 * 64-bit ints. The Query Catalog DTOs use 32-bit IDs and small counts in practice, so we can
 * safely coerce to `number` at the panel boundary.
 *
 * Issue #338 (P5 of #342).
 */
export function toNumber(value: number | string | null | undefined): number {
  if (value == null) return 0;
  if (typeof value === 'number') return value;
  const n = Number(value);
  return Number.isFinite(n) ? n : 0;
}
