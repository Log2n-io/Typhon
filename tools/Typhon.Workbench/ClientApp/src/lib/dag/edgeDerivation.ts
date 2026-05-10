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
 * Inter-phase edges ARE emitted, but the lane-based layouts filter them out by default — they
 * would clutter the swim-lane view. The "Show cross-phase edges" toolbar toggle (and the compact
 * / circular layouts) surface them. Direction for cross-phase edges is always
 * earlier-phase → later-phase (phase order is the disambiguator, matching the engine's
 * `AccessDagDeriver.HasCrossPhaseConflict` rule). Within a phase, snapshot reads still flip the
 * arrow (reader → writer) to enable parallelism; that flip does NOT apply across phases.
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

  // Phase index map keyed by phase NAME — needed to disambiguate "earlier" vs "later" phase
  // for cross-phase derivation. Phases not present in `topology.phases` are bucketed as -1
  // (synthetic) and treated as "before everything" for edge direction; in practice this only
  // affects pre-RFC-07 traces.
  const phaseIndex = buildPhaseIndex(systems);

  for (let i = 0; i < systems.length; i++) {
    const a = systems[i];
    const aName = a.name ?? '';
    if (!aName) continue;

    // Manual `.After()` / `.Before()` — explicit overrides, always within or across phases.
    // The lane-based layout filters cross-phase edges by default; the "Show cross-phase edges"
    // toggle (and the compact / circular layouts) surface them.
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

      const aPhase = phaseIndex.get(aName) ?? -1;
      const bPhase = phaseIndex.get(bName) ?? -1;
      const samePhase = (a.phaseName ?? '') === (b.phaseName ?? '') && (a.phaseName ?? '') !== '';

      if (samePhase) {
        // ── Within-phase rules (unchanged) ─────────────────────────────
        // ReadsFresh<T> ← Writes<T>: reader runs AFTER writer. Edge writer→reader.
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

        // Writes<T> → ReadsSnapshot<T>: snapshot reader runs BEFORE writer (parallelism).
        // Edge reader→writer (so the layout puts the reader earlier).
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

        // Event queue: producer→consumer (events also derive across phases, but only emit once
        // per ordered pair from the cross-phase block below; here we cover the same-phase case).
        const sharedEvents = intersect(a.writesEvents, b.readsEvents);
        for (const q of sharedEvents) {
          addEdge(buckets, aName, bName, 'event', q, `${aName} produces ${q}; ${bName} consumes`);
        }

        // Named-resource conflict: writer→reader (alphabetical for W×W). Symmetric, so only
        // emit when i<j to avoid duplicates.
        const aTouches = unionOf(a.writesResources, a.readsResources);
        const bTouches = unionOf(b.writesResources, b.readsResources);
        const shared = intersectArrays(aTouches, bTouches);
        for (const r of shared) {
          if (i >= j) continue;
          const aWrites = (a.writesResources ?? []).includes(r);
          const bWrites = (b.writesResources ?? []).includes(r);
          if (!aWrites && !bWrites) continue;
          let src: string;
          let tgt: string;
          if (aWrites && !bWrites) {
            src = aName;
            tgt = bName;
          } else if (!aWrites && bWrites) {
            src = bName;
            tgt = aName;
          } else {
            src = aName < bName ? aName : bName;
            tgt = aName < bName ? bName : aName;
          }
          addEdge(buckets, src, tgt, 'resource', r, resourceReason(src, tgt, r));
        }
      } else if (aPhase >= 0 && bPhase >= 0 && aPhase < bPhase) {
        // ── Cross-phase rules (mirrors engine `AccessDagDeriver.HasCrossPhaseConflict`) ──
        // Direction is always earlier-phase → later-phase regardless of role; phase order is
        // the disambiguator. The pair-level guard (aPhase < bPhase) ensures we emit each
        // ordered pair once — no need to check (i, j) order.

        // ED-05a: a (earlier) writes T, b (later) reads or writes T.
        for (const t of (a.writes ?? [])) {
          if ((b.writes ?? []).includes(t) ||
              (b.readsFresh ?? []).includes(t) ||
              (b.readsSnapshot ?? []).includes(t) ||
              (b.reads ?? []).includes(t)) {
            addEdge(
              buckets,
              aName,
              bName,
              'fresh',
              t,
              `${aName} writes ${t} (${a.phaseName}); ${bName} accesses ${t} (${b.phaseName}) — phase order serialises`,
            );
          }
        }

        // ED-05b: a (earlier) reads T, b (later) writes T. Edge still earlier→later.
        for (const t of (b.writes ?? [])) {
          const aReads = (a.reads ?? []).includes(t)
            || (a.readsFresh ?? []).includes(t)
            || (a.readsSnapshot ?? []).includes(t);
          if (aReads) {
            addEdge(
              buckets,
              aName,
              bName,
              'fresh',
              t,
              `${aName} reads ${t} (${a.phaseName}); ${bName} writes ${t} (${b.phaseName}) — earlier phase reads first`,
            );
          }
        }

        // ED-05c: cross-phase event producer (earlier) → consumer (later).
        const crossEvents = intersect(a.writesEvents, b.readsEvents);
        for (const q of crossEvents) {
          addEdge(
            buckets,
            aName,
            bName,
            'event',
            q,
            `${aName} produces ${q} (${a.phaseName}); ${bName} consumes (${b.phaseName})`,
          );
        }

        // ED-05d: cross-phase resource conflict (any pair with at least one writer).
        for (const r of (a.writesResources ?? [])) {
          if ((b.writesResources ?? []).includes(r) || (b.readsResources ?? []).includes(r)) {
            addEdge(buckets, aName, bName, 'resource', r, resourceReason(aName, bName, r));
          }
        }
        for (const r of (a.readsResources ?? [])) {
          if ((b.writesResources ?? []).includes(r)) {
            addEdge(buckets, aName, bName, 'resource', r, resourceReason(aName, bName, r));
          }
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

/**
 * Map system name → phase ordinal. The deriver uses this to direct cross-phase edges from the
 * earlier phase to the later one (matching the engine's AccessDagDeriver semantics). We don't
 * have access to `topology.phases` here so we infer the ordinal from first-appearance order in
 * the systems list, treating each unique phaseName as a fresh ordinal. This is fine for our
 * purpose — we only need a partial order to disambiguate "which side is earlier?" — but it
 * means if two systems with the same phaseName appear out of phase-list order in the array,
 * they still get the same ordinal (they're in the same phase, no cross-phase edge possible).
 *
 * Systems with empty phaseName get a sentinel ordinal that orders them after all named phases —
 * matching the synthetic `(unphased)` lane the layout puts at the end.
 */
function buildPhaseIndex(systems: SystemDefinitionDto[]): Map<string, number> {
  const phaseOrder = new Map<string, number>();
  let next = 0;
  for (const s of systems) {
    const p = s.phaseName ?? '';
    if (p && !phaseOrder.has(p)) phaseOrder.set(p, next++);
  }
  const SYNTHETIC_PHASE_ORDINAL = next;
  const result = new Map<string, number>();
  for (const s of systems) {
    const name = s.name ?? '';
    if (!name) continue;
    const p = s.phaseName ?? '';
    result.set(name, p ? (phaseOrder.get(p) ?? SYNTHETIC_PHASE_ORDINAL) : SYNTHETIC_PHASE_ORDINAL);
  }
  return result;
}
