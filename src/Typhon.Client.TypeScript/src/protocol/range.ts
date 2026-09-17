/**
 * `value` when it is an integer in [0, `max`], else a `RangeError`. The writer's integer primitives take the low bits of
 * whatever they are given, so a value out of range must be refused before it reaches one: `2^32 + 1` written as a `u8`
 * would travel as 1.
 */
export function uintInRange(value: number, max: number, what: string): number {
  if (!(Number.isInteger(value) && value >= 0 && value <= max)) {
    throw new RangeError(`${what} ${value} is not an integer in [0, ${max}]`);
  }

  return value;
}
