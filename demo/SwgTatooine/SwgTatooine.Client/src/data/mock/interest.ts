import type { SpatialBins } from './bins';
import type { EntitySet } from './world';

/**
 * A client-positioned region of interest with the engine's policies (`claude/design/Subscriptions/V2/01-model.md` § 4):
 *
 * - **hysteresis**: enter within `radius`, leave beyond `radius + max(32 m, 5 %)`;
 * - **enter budget**: at most `enterBudget` enters per frame, the nearest ones; `viewComplete` once the backlog is drained;
 * - **near budget**: when the estimated count over the region (per-cell counts, no entity visited) exceeds `nearBudget`, the
 *   effective radius shrinks to fit; it grows back one step at a time after a sustained stretch under 0.8 × budget;
 * - **bounded region**: the requested radius is clamped to `maxRadius` (the profile's `maxEdgeM`).
 *
 * netIds are global and dense: allocated when an entity becomes watched, released when it leaves, reused only after a
 * quarantine. A netId released this frame stays readable ({@link ArchetypeInterest.netIdOf}) so events of the same tick
 * still name the entity — the client applies leaves last for exactly that (`03-wire-protocol.md` § 5).
 */
export interface InterestOptions {
  readonly enterBudget: number;
  readonly nearBudget: number;
  readonly maxRadius: number;
  readonly netIdQuarantineTicks: number;
}

export const DEFAULT_INTEREST: InterestOptions = {
  enterBudget: 500,
  nearBudget: 10_000,
  maxRadius: 4096,
  netIdQuarantineTicks: 50,
};

const GROW_AFTER_TICKS = 20;
const GROW_STEP = 1.05;

export class NetIdAllocator {
  private next = 1;
  private free: Uint32Array = new Uint32Array(1024);
  private freeCount = 0;
  /** Released ids and their release ticks, oldest first, as a ring. */
  private qIds = new Uint32Array(1024);
  private qTicks = new Uint32Array(1024);
  private qHead = 0;
  private qCount = 0;
  private readonly quarantineTicks: number;

  constructor(quarantineTicks: number) {
    this.quarantineTicks = quarantineTicks;
  }

  allocate(tick: number): number {
    while (this.qCount > 0 && tick - this.qTicks[this.qHead] >= this.quarantineTicks) {
      if (this.freeCount === this.free.length) {
        this.free = grow(this.free, this.free.length * 2);
      }

      this.free[this.freeCount++] = this.qIds[this.qHead];
      this.qHead = (this.qHead + 1) % this.qIds.length;
      this.qCount--;
    }

    return this.freeCount > 0 ? this.free[--this.freeCount] : this.next++;
  }

  release(id: number, tick: number): void {
    if (this.qCount === this.qIds.length) {
      const size = this.qIds.length * 2;
      const ids = new Uint32Array(size);
      const ticks = new Uint32Array(size);
      for (let k = 0; k < this.qCount; k++) {
        ids[k] = this.qIds[(this.qHead + k) % this.qIds.length];
        ticks[k] = this.qTicks[(this.qHead + k) % this.qIds.length];
      }

      this.qIds = ids;
      this.qTicks = ticks;
      this.qHead = 0;
    }

    const at = (this.qHead + this.qCount) % this.qIds.length;
    this.qIds[at] = id;
    this.qTicks[at] = tick;
    this.qCount++;
  }
}

export class ArchetypeInterest {
  readonly set: EntitySet;
  /** netId of each entity while watched, else 0. */
  readonly netIds: Uint32Array;
  /** Watched entity indices, dense in `[0, watchedCount)`. */
  readonly watched: Int32Array;
  watchedCount = 0;
  private readonly position: Int32Array;
  /** netId an entity had when it left, and the tick it left at. */
  private readonly leftNetId: Uint32Array;
  private readonly leftTick: Uint32Array;
  private tick = 0;

  /** This frame's enters (entity indices) and leaves (entity index + the netId it had). */
  readonly enters: Int32Array;
  enterCount = 0;
  readonly leaves: Int32Array;
  readonly leaveNetIds: Uint32Array;
  leaveCount = 0;

  constructor(set: EntitySet) {
    this.set = set;
    this.netIds = new Uint32Array(set.count);
    this.watched = new Int32Array(set.count);
    this.position = new Int32Array(set.count).fill(-1);
    this.leftNetId = new Uint32Array(set.count);
    this.leftTick = new Uint32Array(set.count);
    this.enters = new Int32Array(set.count);
    this.leaves = new Int32Array(set.count);
    this.leaveNetIds = new Uint32Array(set.count);
  }

  isWatched(i: number): boolean {
    return this.netIds[i] !== 0;
  }

  /** The netId naming entity `i` in this frame: its current one, or the one it had if it left this frame; else 0. */
  netIdOf(i: number): number {
    const current = this.netIds[i];
    if (current !== 0) {
      return current;
    }

    return this.leftTick[i] === this.tick ? this.leftNetId[i] : 0;
  }

  watch(i: number, netId: number): void {
    this.netIds[i] = netId;
    this.position[i] = this.watchedCount;
    this.watched[this.watchedCount++] = i;
    this.enters[this.enterCount++] = i;
  }

  unwatch(i: number): number {
    const netId = this.netIds[i];
    const at = this.position[i];
    const last = this.watched[--this.watchedCount];
    this.watched[at] = last;
    this.position[last] = at;
    this.position[i] = -1;
    this.netIds[i] = 0;
    this.leftNetId[i] = netId;
    this.leftTick[i] = this.tick;
    this.leaves[this.leaveCount] = i;
    this.leaveNetIds[this.leaveCount++] = netId;
    return netId;
  }

  beginFrame(tick: number): void {
    this.tick = tick;
    this.enterCount = 0;
    this.leaveCount = 0;
  }
}

export class InterestManager {
  readonly archetypes: readonly ArchetypeInterest[];
  centerX = 0;
  centerZ = 0;
  requestedRadius = 0;
  effectiveRadius = 0;
  viewComplete = false;
  /** False until a region has been received: nothing is watched before the client says where it looks. */
  active = false;

  private readonly options: InterestOptions;
  private readonly netIds: NetIdAllocator;
  private underBudgetTicks = 0;
  private candidateIndex = new Int32Array(4096);
  private candidateDist = new Float64Array(4096);
  private candidateArch = new Uint8Array(4096);
  private scratchIndex = new Int32Array(65536);
  private scratchDist = new Float64Array(65536);

  constructor(sets: readonly EntitySet[], options: InterestOptions = DEFAULT_INTEREST) {
    this.options = options;
    this.netIds = new NetIdAllocator(options.netIdQuarantineTicks);
    this.archetypes = sets.map((set) => new ArchetypeInterest(set));
  }

  setRegion(x: number, z: number, radius: number): void {
    this.centerX = x;
    this.centerZ = z;
    const requested = Math.min(Math.max(0, radius), this.options.maxRadius);
    if (requested !== this.requestedRadius) {
      const budgetLimited = this.effectiveRadius < this.requestedRadius;
      this.requestedRadius = requested;
      // A new radius applies at once unless the budget is holding the region smaller; then only a shrink applies.
      this.effectiveRadius = budgetLimited ? Math.min(this.effectiveRadius, requested) : requested;
    }

    this.active = true;
  }

  get watchedTotal(): number {
    let n = 0;
    for (const a of this.archetypes) {
      n += a.watchedCount;
    }

    return n;
  }

  /** Recomputes enters and leaves for this tick. `bins` must reflect this tick's positions. */
  update(bins: readonly SpatialBins[], tick: number): void {
    for (const a of this.archetypes) {
      a.beginFrame(tick);
    }

    if (!this.active) {
      this.viewComplete = true;
      return;
    }

    this.adjustRadius(bins);
    const enterR = this.effectiveRadius;
    const leaveR = enterR + Math.max(32, enterR * 0.05);
    const leaveR2 = leaveR * leaveR;
    const cx = this.centerX;
    const cz = this.centerZ;

    for (const a of this.archetypes) {
      const xs = a.set.x;
      const zs = a.set.z;
      const watched = a.watched;
      for (let k = a.watchedCount - 1; k >= 0; k--) {
        const i = watched[k];
        const dx = xs[i] - cx;
        const dz = zs[i] - cz;
        if (dx * dx + dz * dz > leaveR2) {
          this.netIds.release(a.unwatch(i), tick);
        }
      }
    }

    let candidates = 0;
    for (let arch = 0; arch < this.archetypes.length; arch++) {
      const a = this.archetypes[arch];
      const b = bins[arch];
      let n = b.queryRadius(a.set, cx, cz, enterR, this.scratchIndex, this.scratchDist);
      while (n === this.scratchIndex.length) {
        this.scratchIndex = new Int32Array(this.scratchIndex.length * 2);
        this.scratchDist = new Float64Array(this.scratchDist.length * 2);
        n = b.queryRadius(a.set, cx, cz, enterR, this.scratchIndex, this.scratchDist);
      }

      const index = this.scratchIndex;
      const dist = this.scratchDist;
      for (let k = 0; k < n; k++) {
        const i = index[k];
        if (a.isWatched(i)) {
          continue;
        }

        if (candidates === this.candidateIndex.length) {
          this.growCandidates();
        }

        this.candidateIndex[candidates] = i;
        this.candidateDist[candidates] = dist[k];
        this.candidateArch[candidates] = arch;
        candidates++;
      }
    }

    const budget = this.options.enterBudget;
    if (candidates > budget) {
      this.selectNearest(candidates, budget);
    }

    const take = Math.min(candidates, budget);
    for (let c = 0; c < take; c++) {
      this.archetypes[this.candidateArch[c]].watch(this.candidateIndex[c], this.netIds.allocate(tick));
    }

    this.viewComplete = candidates <= budget;
  }

  /** Budget from the per-cell counts over the region, as the engine estimates it (O(cells), no entity visited). */
  private adjustRadius(bins: readonly SpatialBins[]): void {
    const budget = this.options.nearBudget;
    const estimate = (radius: number): number => {
      let n = 0;
      for (const b of bins) {
        n += b.estimateDisc(this.centerX, this.centerZ, radius);
      }

      return n;
    };

    let radius = this.effectiveRadius;
    let current = estimate(radius);
    if (current > budget) {
      while (radius > 64 && current > budget) {
        radius *= Math.max(0.5, Math.sqrt(budget / current) * 0.98);
        current = estimate(radius);
      }

      this.effectiveRadius = radius;
      this.underBudgetTicks = 0;
      return;
    }

    if (radius < this.requestedRadius && current < budget * 0.8) {
      if (++this.underBudgetTicks >= GROW_AFTER_TICKS) {
        const grown = Math.min(this.requestedRadius, radius * GROW_STEP);
        if (estimate(grown) <= budget) {
          this.effectiveRadius = grown;
        }

        this.underBudgetTicks = 0;
      }
    } else {
      this.underBudgetTicks = 0;
    }
  }

  /** Moves the `k` nearest candidates to `[0, k)` (quickselect on distance, O(n)). */
  private selectNearest(n: number, k: number): void {
    let lo = 0;
    let hi = n - 1;
    const dist = this.candidateDist;
    while (lo < hi) {
      const pivot = dist[(lo + hi) >> 1];
      let i = lo;
      let j = hi;
      while (i <= j) {
        while (dist[i] < pivot) i++;
        while (dist[j] > pivot) j--;
        if (i <= j) {
          this.swap(i, j);
          i++;
          j--;
        }
      }

      if (k - 1 <= j) {
        hi = j;
      } else if (k - 1 >= i) {
        lo = i;
      } else {
        return;
      }
    }
  }

  private swap(a: number, b: number): void {
    const index = this.candidateIndex;
    const dist = this.candidateDist;
    const arch = this.candidateArch;
    const ti = index[a];
    index[a] = index[b];
    index[b] = ti;
    const td = dist[a];
    dist[a] = dist[b];
    dist[b] = td;
    const ta = arch[a];
    arch[a] = arch[b];
    arch[b] = ta;
  }

  private growCandidates(): void {
    const size = this.candidateIndex.length * 2;
    const index = new Int32Array(size);
    index.set(this.candidateIndex);
    const dist = new Float64Array(size);
    dist.set(this.candidateDist);
    const arch = new Uint8Array(size);
    arch.set(this.candidateArch);
    this.candidateIndex = index;
    this.candidateDist = dist;
    this.candidateArch = arch;
  }
}

function grow(array: Uint32Array, size: number): Uint32Array {
  const next = new Uint32Array(size);
  next.set(array);
  return next;
}
