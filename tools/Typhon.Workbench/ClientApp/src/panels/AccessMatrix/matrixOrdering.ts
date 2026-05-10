import type { Track } from '@/panels/DataFlow/trackBuilding';
import type { AccessMatrix, Column } from './matrixBuilding';

/**
 * Pure column / row ordering for the Access Matrix. Two strategies per axis — see {@link useAccessMatrixViewStore}.
 *
 * The default `phase-then-dependency` column order matches the System DAG's swim-lane skeleton: phase groups
 * preserve the user's declared mental model, dependency-order within each group encodes the runtime causality.
 *
 * The cluster reorder uses **cosine similarity** over each axis's access vectors. For columns the vector is
 * "which rows does this system touch"; for rows it's "which columns touch this row". A greedy nearest-neighbor
 * walk produces a sequence where adjacent items have similar access patterns — the visual signal users come
 * to the matrix for ("which systems do similar work?").
 *
 * Determinism: tied scores break by index, so the output is stable across renders given the same input.
 */

/**
 * Phase + dependency ordering for columns. Two-pass:
 * 1. Group columns by their `phaseName` in `phases` order; columns whose phase isn't in the list (or empty)
 *    fall into a final "no-phase" bucket.
 * 2. Within each group, topological sort by predecessor edges (root systems first; if no predecessors data is
 *    available, fall back to declaration order).
 *
 * Returns the columns in their final render order.
 */
export function orderColumnsByPhaseDependency(
  columns: readonly Column[],
  phases: readonly string[],
  predecessorsByName: Readonly<Map<string, readonly string[]>>,
): Column[] {
  if (columns.length === 0) return [];

  // Group by phase. Use a Map so insertion order matches `phases` order; unknown-phase columns go last.
  const groups = new Map<string, Column[]>();
  for (const phase of phases) groups.set(phase, []);
  groups.set('', []); // catch-all for empty / unknown phases

  for (const col of columns) {
    const key = phases.includes(col.phaseName) ? col.phaseName : '';
    const bucket = groups.get(key) ?? [];
    bucket.push(col);
    groups.set(key, bucket);
  }

  // Sort each group topologically. Algorithm: Kahn's BFS over the predecessor graph restricted to the bucket.
  const out: Column[] = [];
  for (const [, bucket] of groups) {
    if (bucket.length === 0) continue;
    out.push(...topologicalSortWithinBucket(bucket, predecessorsByName));
  }
  return out;
}

function topologicalSortWithinBucket(
  bucket: readonly Column[],
  predecessorsByName: Readonly<Map<string, readonly string[]>>,
): Column[] {
  const inBucket = new Set(bucket.map((c) => c.systemName));
  // Count predecessors that are also in this bucket.
  const remaining = new Map<string, number>();
  for (const col of bucket) {
    const preds = predecessorsByName.get(col.systemName) ?? [];
    let count = 0;
    for (const p of preds) if (inBucket.has(p)) count++;
    remaining.set(col.systemName, count);
  }

  const ready: Column[] = bucket.filter((c) => (remaining.get(c.systemName) ?? 0) === 0);
  // Stable order within the ready set — match the input order so the layout is deterministic across builds.
  ready.sort((a, b) => declOrderIndex(a, bucket) - declOrderIndex(b, bucket));

  const out: Column[] = [];
  // Successor lookup: the inverse of predecessorsByName, restricted to the bucket.
  const successors = new Map<string, string[]>();
  for (const col of bucket) {
    const preds = predecessorsByName.get(col.systemName) ?? [];
    for (const p of preds) {
      if (!inBucket.has(p)) continue;
      const arr = successors.get(p) ?? [];
      arr.push(col.systemName);
      successors.set(p, arr);
    }
  }

  while (ready.length > 0) {
    const next = ready.shift()!;
    out.push(next);
    const succs = successors.get(next.systemName) ?? [];
    for (const sName of succs) {
      const r = (remaining.get(sName) ?? 0) - 1;
      remaining.set(sName, r);
      if (r === 0) {
        const col = bucket.find((c) => c.systemName === sName);
        if (col) ready.push(col);
      }
    }
  }

  // Defensive: if there are unresolved cycles, append remaining columns in declaration order so we never
  // drop systems silently. Cycles in a topology are illegal but we degrade gracefully.
  if (out.length < bucket.length) {
    const seen = new Set(out.map((c) => c.systemName));
    for (const col of bucket) {
      if (!seen.has(col.systemName)) out.push(col);
    }
  }

  return out;
}

function declOrderIndex(c: Column, bucket: readonly Column[]): number {
  return bucket.indexOf(c);
}

/**
 * Cluster reorder via cosine similarity. The greedy walk: start from the column whose access vector has the
 * highest L2 norm (the "busiest" system), then repeatedly append the unvisited column with the highest cosine
 * similarity to the most recently appended one.
 *
 * The vector for each column is `{ rowId → 1 if cell exists with non-none access kind, 0 otherwise }`. Touch
 * counts are intentionally ignored — the user wants "which systems use *similar* data", not "which systems are
 * *similarly busy*". Squashing to binary makes the metric robust to per-tick noise.
 *
 * Complexity: O(N²·M) where N = columns, M = average non-none cells per column. For N ≈ 200, M ≈ 5–10, that's
 * a few hundred thousand ops — well under a millisecond on every reorder.
 *
 * Determinism: ties in similarity break by index so the same input always produces the same output.
 */
export function clusterReorderColumns(matrix: AccessMatrix): Column[] {
  const { rows, columns, cells } = matrix;
  if (columns.length <= 1) return [...columns];

  // Build per-column access vectors. Use a row-id list captured once; vector entries are 0/1.
  const rowIds = rows.map((r) => r.id);
  const vectors = new Map<string, number[]>();
  for (const col of columns) {
    const v = new Array<number>(rowIds.length).fill(0);
    for (let i = 0; i < rowIds.length; i++) {
      const cell = cells.get(`${rowIds[i]}|${col.systemName}`);
      v[i] = cell && cell.accessKind !== 'none' ? 1 : 0;
    }
    vectors.set(col.systemName, v);
  }

  return greedyCosineWalk(columns, vectors);
}

/**
 * Same algorithm but for rows. Vector for a row is `{ columnSystemName → 1 if non-none cell exists }`. Useful
 * for matching the row order to the column order — when two systems' usage of two components is similar, those
 * components appear adjacent.
 */
export function clusterReorderRows(matrix: AccessMatrix): Track[] {
  const { rows, columns, cells } = matrix;
  if (rows.length <= 1) return [...rows];

  const colNames = columns.map((c) => c.systemName);
  const vectors = new Map<string, number[]>();
  for (const row of rows) {
    const v = new Array<number>(colNames.length).fill(0);
    for (let i = 0; i < colNames.length; i++) {
      const cell = cells.get(`${row.id}|${colNames[i]}`);
      v[i] = cell && cell.accessKind !== 'none' ? 1 : 0;
    }
    vectors.set(row.id, v);
  }

  return greedyCosineWalk(rows, vectors, (r) => r.id);
}

/**
 * Generic greedy walk parameterized by item identity. Picks the highest-norm seed, then iteratively appends
 * the unvisited item with the highest cosine similarity to the last-appended one. Ties (same similarity score
 * to multiple candidates) break by item index in the original list — keeps the output deterministic.
 */
function greedyCosineWalk<T>(
  items: readonly T[],
  vectors: Map<string, number[]>,
  idOf: (item: T) => string = (item) => (item as unknown as { id?: string; systemName?: string }).id
    ?? (item as unknown as { id?: string; systemName?: string }).systemName
    ?? '',
): T[] {
  if (items.length === 0) return [];
  if (items.length === 1) return [...items];

  const norms = new Map<string, number>();
  for (const item of items) {
    const v = vectors.get(idOf(item)) ?? [];
    norms.set(idOf(item), Math.sqrt(dot(v, v)));
  }

  // Pick seed: highest-norm vector. Tie-break by original index.
  let seedIdx = 0;
  let seedNorm = norms.get(idOf(items[0])) ?? 0;
  for (let i = 1; i < items.length; i++) {
    const n = norms.get(idOf(items[i])) ?? 0;
    if (n > seedNorm) {
      seedIdx = i;
      seedNorm = n;
    }
  }

  const out: T[] = [items[seedIdx]];
  const visited = new Set<string>([idOf(items[seedIdx])]);

  while (out.length < items.length) {
    const last = out[out.length - 1];
    const lastVec = vectors.get(idOf(last)) ?? [];
    const lastNorm = norms.get(idOf(last)) ?? 0;

    // If the seed had zero norm, every subsequent similarity is 0 — fall back to original order for the rest.
    let bestIdx = -1;
    let bestSim = -1;
    for (let i = 0; i < items.length; i++) {
      const cand = items[i];
      const id = idOf(cand);
      if (visited.has(id)) continue;
      const cv = vectors.get(id) ?? [];
      const cn = norms.get(id) ?? 0;
      // cosine = dot(a,b) / (|a| · |b|); guard against zero norms.
      const denom = lastNorm * cn;
      const sim = denom === 0 ? 0 : dot(lastVec, cv) / denom;
      if (sim > bestSim) {
        bestSim = sim;
        bestIdx = i;
      }
    }

    if (bestIdx === -1) {
      // No unvisited remaining — shouldn't happen, but defensively append remaining in order.
      for (let i = 0; i < items.length; i++) {
        if (!visited.has(idOf(items[i]))) {
          out.push(items[i]);
          visited.add(idOf(items[i]));
        }
      }
      break;
    }

    out.push(items[bestIdx]);
    visited.add(idOf(items[bestIdx]));
  }

  return out;
}

function dot(a: readonly number[], b: readonly number[]): number {
  const n = Math.min(a.length, b.length);
  let s = 0;
  for (let i = 0; i < n; i++) s += a[i] * b[i];
  return s;
}
