import type { QueryDefinitionDto } from '@/api/generated/model';
import { toNumber } from './numeric';

/**
 * Display formatters for the Query Analyzer (#376 Phase 4). Lifted from the formatters that were
 * private to `QueryCatalogRow` / `ExecutionInspector/phaseRows` so the consolidated master/detail
 * view shares one copy. The old private copies are removed with their panels in 4D.
 */

/** Integer thousands grouping (`8421` → `8,421`). */
export function formatThousands(n: number): string {
  return n.toLocaleString('en-US');
}

/** Nanoseconds → the most readable unit (ns / µs / ms / s). */
export function formatNs(ns: number): string {
  if (ns === 0) return '0 ns';
  if (ns < 1_000) return `${ns} ns`;
  if (ns < 1_000_000) return `${(ns / 1_000).toFixed(1)} µs`;
  if (ns < 1_000_000_000) return `${(ns / 1_000_000).toFixed(2)} ms`;
  return `${(ns / 1_000_000_000).toFixed(2)} s`;
}

/** Selectivity ratio (0..1) → percentage, or em-dash when absent / non-finite. */
export function formatSelectivity(value: number | undefined | null): string {
  if (value == null || !Number.isFinite(value)) return '—';
  return `${(value * 100).toFixed(1)}%`;
}

/** Catalog "kind" label — Kind 0 = pull-mode View, anything else = an ECS query. */
export function queryKindLabel(kind: number): string {
  return kind === 0 ? 'View' : 'EcsQuery';
}

/**
 * One-line predicate shape for the catalog "predicate" column / detail header — the first evaluator
 * rendered as `field op ?`, with `+N` when the query carries more. Em-dash when it has no filters.
 */
export function predicateSummary(d: QueryDefinitionDto): string {
  const evals = d.evaluators ?? [];
  if (evals.length === 0) return '—';
  const first = evals[0];
  const field = first.fieldName || `Field[${toNumber(first.fieldIdx)}]`;
  const op = first.opDisplay || `op${toNumber(first.op)}`;
  const base = `${field} ${op} ?`;
  return evals.length > 1 ? `${base} +${evals.length - 1}` : base;
}
