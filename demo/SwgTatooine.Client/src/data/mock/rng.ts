/** Deterministic PRNG (mulberry32): a seed reproduces the whole mock world and its behaviour. */
export class Rng {
  private state: number;

  constructor(seed: number) {
    this.state = seed >>> 0 || 0x9e3779b9;
  }

  /** Uniform in [0, 1). */
  next(): number {
    this.state = (this.state + 0x6d2b79f5) >>> 0;
    let t = this.state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  }

  range(min: number, max: number): number {
    return min + (max - min) * this.next();
  }

  /** Integer in [min, max). */
  int(min: number, max: number): number {
    return min + Math.floor((max - min) * this.next());
  }

  /** A point uniform by area in a disc; written to `out[0]`, `out[1]`. */
  pointInDisc(cx: number, cz: number, radius: number, out: Float64Array): void {
    const a = this.next() * Math.PI * 2;
    const r = radius * Math.sqrt(this.next());
    out[0] = cx + Math.cos(a) * r;
    out[1] = cz + Math.sin(a) * r;
  }
}
