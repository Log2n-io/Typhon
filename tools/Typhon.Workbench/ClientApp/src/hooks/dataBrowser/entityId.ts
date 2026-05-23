// The raw 64-bit EntityId packs the 12-bit ArchetypeId in its low bits (engine `EntityId.cs`:
// `_value = (entityKey << 12) | (archetypeId & 0xFFF)`). So an entity id alone tells you which archetype it belongs to,
// which lets the Data Browser's "Go to id" jump to any entity without the user picking the archetype first.

const DIGITS = /^\d+$/;

/** Returns true if <paramref/> is a non-empty run of decimal digits (a valid raw entity id on the wire). */
export function isRawEntityId(raw: string): boolean {
  return DIGITS.test(raw.trim());
}

/**
 * Extracts the ArchetypeId (decimal string) from a raw entity id, or <c>null</c> when the input isn't a numeric id.
 * Uses BigInt because the 64-bit raw value exceeds JS Number precision.
 */
export function archetypeIdFromRawEntityId(raw: string): string | null {
  const trimmed = raw.trim();
  if (!DIGITS.test(trimmed)) return null;
  return (BigInt(trimmed) & 0xfffn).toString();
}
