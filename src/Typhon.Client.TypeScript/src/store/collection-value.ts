import type { FieldPlan } from '../protocol/catalog.js';
import { ValueKind } from '../protocol/catalog.js';

/**
 * One entity's collection (W34): how many elements it holds, how many the server sent, and the elements as
 * structure-of-arrays over the element's fields.
 *
 * A collection is re-sent whole when it changes, so a decode overwrites it: `count` elements are the list, and nothing
 * past them means anything; `total` above `count` says the server cut it at its `maxCount`. An element field's numbers
 * are one `Float64Array` of `capacity × components`, its 64-bit integers one `Uint32Array` of lo/hi words, its text one
 * string per element. The capacity grows to the largest list received and is kept, so a steady stream of lists of one
 * size allocates nothing but the strings of the text that changed.
 */
export class CollectionValue {
  readonly field: FieldPlan;
  /** How many elements the entity holds — above {@link count} when the server truncated the list. */
  total = 0;
  /** How many elements were sent and are held here, in `[0, count)`. */
  count = 0;
  /** Elements allocated. */
  capacity = 0;
  /** Per element field index: `capacity × components` numbers, or `null` for a field that is not numeric. */
  readonly numbers: (Float64Array | null)[];
  /** Per element field index: `2 × capacity × components` words — component `i`'s low word first — or `null`. */
  readonly words: (Uint32Array | null)[];
  /** Per element field index: the text per element, or `null` for a field that is not text. */
  readonly texts: (string[] | null)[];

  constructor(field: FieldPlan) {
    this.field = field;
    const fields = field.elementSection!.fields;
    this.numbers = fields.map(() => null);
    this.words = fields.map(() => null);
    this.texts = fields.map(() => null);
    this.grow(1);
  }

  /** Whether the list arrived cut at the codec's `maxCount`. */
  get truncated(): boolean {
    return this.count < this.total;
  }

  /** An element field's component, by name; for a test or a tool, not a loop. */
  number(index: number, name: string, component = 0): number {
    const plan = this.find(index, name);
    return this.numbers[plan.index]![index * plan.components + component]!;
  }

  /** An element field's text, by name; for a test or a tool, not a loop. */
  text(index: number, name: string): string {
    return this.texts[this.find(index, name).index]![index]!;
  }

  /** An element field's 64-bit component as a bigint (its bit pattern, unsigned), by name; for a test or a tool. */
  integer64(index: number, name: string, component = 0): bigint {
    const plan = this.find(index, name);
    const at = 2 * (index * plan.components + component);
    const words = this.words[plan.index]!;
    return (BigInt(words[at + 1]!) << 32n) | BigInt(words[at]!);
  }

  /** Starts a list: `sent` elements follow, of `total`. */
  begin(total: number, sent: number): void {
    if (sent > this.capacity) {
      let capacity = Math.max(this.capacity, 1);
      while (capacity < sent) {
        capacity *= 2;
      }

      this.grow(capacity);
    }

    this.total = total;
    this.count = sent;
  }

  /** Empties it for the slot's next occupant, keeping its columns. */
  reset(): void {
    this.total = 0;
    this.count = 0;
  }

  setNumber(field: FieldPlan, index: number, values: Float64Array): void {
    const components = field.components;
    this.numbers[field.index]!.set(values.subarray(0, components), index * components);
  }

  setInteger64(field: FieldPlan, index: number, words: Uint32Array): void {
    const components = field.components;
    this.words[field.index]!.set(words.subarray(0, 2 * components), 2 * index * components);
  }

  setText(field: FieldPlan, index: number, value: string): void {
    this.texts[field.index]![index] = value;
  }

  private find(index: number, name: string): FieldPlan {
    if (!(index >= 0 && index < this.count)) {
      throw new RangeError(`the collection holds ${this.count} element(s)`);
    }

    const plan = this.field.elementSection!.fields.find((f) => f.name === name);
    if (plan === undefined) {
      throw new Error(`collection '${this.field.name}' has no element field '${name}'`);
    }

    return plan;
  }

  private grow(capacity: number): void {
    const fields = this.field.elementSection!.fields;
    for (let i = 0; i < fields.length; i++) {
      const f = fields[i]!;
      if (f.valueKind === ValueKind.Number) {
        const next = new Float64Array(capacity * f.components);
        const previous = this.numbers[i];
        if (previous != null) {
          next.set(previous);
        }

        this.numbers[i] = next;
      } else if (f.valueKind === ValueKind.Integer64) {
        const next = new Uint32Array(2 * capacity * f.components);
        const previous = this.words[i];
        if (previous != null) {
          next.set(previous);
        }

        this.words[i] = next;
      } else if (f.valueKind === ValueKind.Text) {
        const next = this.texts[i] ?? [];
        while (next.length < capacity) {
          next.push('');
        }

        this.texts[i] = next;
      }
    }

    this.capacity = capacity;
  }
}
