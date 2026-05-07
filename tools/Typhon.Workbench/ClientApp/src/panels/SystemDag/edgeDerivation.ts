import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';

/**
 * Edge derivation for the System DAG view (RFC 07 §Q4 / `09-system-dag.md` §4.3).
 *
 * Pure function — no React, no DOM, fully testable. Mirrors the engine's `Build()`-time DAG
 * derivation: every edge is derived from declared access (`Reads*` / `Writes*` / event queues /
 * resources) plus explicit `.After()` / `.Before()` overrides. Deduplication is by
 * `(source, target, kind)` — multiple shared components produce a single edge per kind, with the
 * combined component list in {@link DerivedEdge.via}.
 *
 * Inter-phase edges are NOT emitted: the lane order *is* the phase contract (per RFC 07's
 * "phases are the skeleton"). Cross-phase pairs share components but the scheduler enforces order
 * via the phase fence, so rendering them as edges would be O(systems²) noise.
 */
export type DerivedEdgeKind = 'fresh' | 'snapshot' | 'manual' | 'event' | 'resource';

export interface DerivedEdge {
  /** Stable id usable as React Flow edge id. */
  id: string;
  /** System name of the writer / producer / earlier system. */
  source: string;
  /** System name of the reader / consumer / later system. */
  target: string;
  kind: DerivedEdgeKind;
  /** Component / queue / resource names that justify the edge. Sorted alphabetically. */
  via: string[];
  /** One-line natural-language reason rendered in the tooltip. */
  reason: string;
}

/**
 * Build the full edge set for a topology. Returns edges in declaration-stable order: edges with
 * the same (source, target, kind) are merged, the merged `via` is sorted, and the array is
 * sorted by `(kind, source, target)` so test snapshots are deterministic.
 */
export function deriveEdges(systems: SystemDefinitionDto[]): DerivedEdge[] {
  const buckets = new Map<string, DerivedEdge>();

  for (let i = 0; i < systems.length; i++) {
    const a = systems[i];
    const aName = a.name ?? '';
    if (!aName) continue;

    // Manual `.After()` / `.Before()` — explicit overrides, always within or across phases. The
    // design says cross-phase edges are not drawn, but we don't have phase info here; the layout
    // layer filters by phase. Emit unconditionally; the renderer drops cross-phase ones.
    for (const earlier of a.explicitAfter ?? []) {
      addEdge(buckets, earlier, aName, 'manual', earlier, `Manual edge: ${aName}.After(${earlier})`);
    }
    for (const later of a.explicitBefore ?? []) {
      addEdge(buckets, aName, later, 'manual', later, `Manual edge: ${aName}.Before(${later})`);
    }

    for (let j = 0; j < systems.length; j++) {
      if (i === j) continue;
      const b = systems[j];
      const bName = b.name ?? '';
      if (!bName) continue;

      // Same-phase rule (RFC 07): fresh-reads + snapshot-reads only fire inside one phase. We
      // emit unconditionally — the layout layer filters cross-phase edges.
      const samePhase = (a.phaseName ?? '') === (b.phaseName ?? '') && (a.phaseName ?? '') !== '';

      // ReadsFresh<T> ← Writes<T>: the reader runs AFTER the writer. Edge points writer → reader.
      if (samePhase) {
        const sharedFresh = intersect(a.writes, b.readsFresh);
        for (const t of sharedFresh) {
          addEdge(
            buckets,
            aName,
            bName,
            'fresh',
            t,
            `${bName} reads ${t} fresh; ${aName} writes ${t} → ${bName} runs after ${aName}`,
          );
        }

        // Writes<T> → ReadsSnapshot<T>: the snapshot reader runs BEFORE the writer. Edge points
        // reader → writer (so the layout puts the reader earlier).
        const sharedSnap = intersect(a.writes, b.readsSnapshot);
        for (const t of sharedSnap) {
          addEdge(
            buckets,
            bName,
            aName,
            'snapshot',
            t,
            `${bName} reads ${t} snapshot; ${aName} writes ${t} → ${bName} runs before ${aName}`,
          );
        }
      }

      // Event queue: producer (writesEvents) → consumer (readsEvents). Cross-phase allowed by
      // design (events buffer across phase fences); however the renderer still filters to
      // intra-phase to honour the "no inter-phase edges" rule.
      if (samePhase) {
        const sharedEvents = intersect(a.writesEvents, b.readsEvents);
        for (const q of sharedEvents) {
          addEdge(
            buckets,
            aName,
            bName,
            'event',
            q,
            `${aName} produces ${q}; ${bName} consumes`,
          );
        }
      }

      // Named-resource conflict: any pair touching the same resource where at least one writes.
      // Edge direction = writer → reader (or for write-write, both directions get recorded as one
      // edge in alphabetical (source, target) so the dedup is stable).
      if (samePhase) {
        const aTouches = unionOf(a.writesResources, a.readsResources);
        const bTouches = unionOf(b.writesResources, b.readsResources);
        const shared = intersectArrays(aTouches, bTouches);
        for (const r of shared) {
          // Dedup symmetric pairs: only emit if (i < j) so the bucket key wins once.
          if (i >= j) continue;
          const aWrites = (a.writesResources ?? []).includes(r);
          const bWrites = (b.writesResources ?? []).includes(r);
          if (!aWrites && !bWrites) continue; // both read-only → no scheduling conflict
          // Direction: writer → other; if both write, alphabetical.
          let src = aName;
          let tgt = bName;
          if (aWrites && !bWrites) {
            src = aName;
            tgt = bName;
          } else if (!aWrites && bWrites) {
            src = bName;
            tgt = aName;
          } else {
            // Both write — pick alphabetical for stability.
            src = aName < bName ? aName : bName;
            tgt = aName < bName ? bName : aName;
          }
          addEdge(buckets, src, tgt, 'resource', r, resourceReason(src, tgt, r));
        }
      }
    }
  }

  const out = [...buckets.values()];
  out.sort((x, y) => {
    if (x.kind !== y.kind) return x.kind.localeCompare(y.kind);
    if (x.source !== y.source) return x.source.localeCompare(y.source);
    return x.target.localeCompare(y.target);
  });
  for (const e of out) e.via.sort();
  return out;
}

function addEdge(
  buckets: Map<string, DerivedEdge>,
  source: string,
  target: string,
  kind: DerivedEdgeKind,
  via: string,
  reason: string,
): void {
  if (source === target) return;
  const key = `${kind}|${source}|${target}`;
  const existing = buckets.get(key);
  if (existing) {
    if (!existing.via.includes(via)) {
      existing.via.push(via);
      existing.reason = `${existing.reason}; ${reason}`;
    }
    return;
  }
  buckets.set(key, {
    id: `e-${kind}-${source}-${target}`,
    source,
    target,
    kind,
    via: [via],
    reason,
  });
}

function intersect(a: string[] | null | undefined, b: string[] | null | undefined): string[] {
  if (!a || !b || a.length === 0 || b.length === 0) return [];
  const setB = new Set(b);
  const out: string[] = [];
  for (const x of a) {
    if (setB.has(x)) out.push(x);
  }
  return out;
}

function unionOf(a: string[] | null | undefined, b: string[] | null | undefined): string[] {
  const set = new Set<string>();
  for (const x of a ?? []) set.add(x);
  for (const x of b ?? []) set.add(x);
  return [...set];
}

function intersectArrays(a: string[], b: string[]): string[] {
  if (a.length === 0 || b.length === 0) return [];
  const setB = new Set(b);
  const out: string[] = [];
  for (const x of a) {
    if (setB.has(x)) out.push(x);
  }
  return out;
}

function resourceReason(src: string, tgt: string, resource: string): string {
  return `Both touch resource ${resource} (${src} writes / ${tgt} reads or writes)`;
}
