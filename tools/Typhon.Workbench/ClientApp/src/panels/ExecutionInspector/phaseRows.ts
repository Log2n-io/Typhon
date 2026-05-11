import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import type { QueryExecutionPhaseDto } from '@/api/generated/model/queryExecutionPhaseDto';
import { toNumber } from '../QueryCatalog/numeric';

export interface PhaseRow {
  phaseName: string;
  estimate: number | null;
  actual: number | null;
  /** (actual - estimate) / estimate, expressed as a fraction (-0.22 = -22%). null when either side is missing or estimate is 0. */
  delta: number | null;
  wallNs: number;
  notes: string;
}

/**
 * Convert a {@link QueryExecutionDto} into a flat list of typed rows ready for tabular rendering.
 * Pure function — testable without React. Preserves trace order; does not insert "Total" rows
 * (the table component aggregates total wall-time independently).
 */
export function buildPhaseRows(execution: QueryExecutionDto | null): PhaseRow[] {
  if (!execution) return [];
  return (execution.phases ?? []).map((p) => toRow(p));
}

/** Sum of <c>wallNs</c> over all phases. Useful for the "Total" footer row. */
export function totalWallNs(rows: PhaseRow[]): number {
  let sum = 0;
  for (const r of rows) sum += r.wallNs;
  return sum;
}

function toRow(phase: QueryExecutionPhaseDto): PhaseRow {
  const estimate = phase.estimate == null ? null : toNumber(phase.estimate);
  const actual = phase.actual == null ? null : toNumber(phase.actual);
  return {
    phaseName: phase.phaseName ?? '<unnamed>',
    estimate,
    actual,
    delta: deltaOf(estimate, actual),
    wallNs: toNumber(phase.wallNs),
    notes: phase.notes ?? '',
  };
}

function deltaOf(estimate: number | null, actual: number | null): number | null {
  if (estimate == null || actual == null) return null;
  if (estimate === 0) return null;
  return (actual - estimate) / estimate;
}

// ── formatters (shared with the table component, but co-located here so tests can verify them) ──

export function formatNs(ns: number): string {
  if (ns === 0) return '0';
  if (ns < 1_000) return `${ns} ns`;
  if (ns < 1_000_000) return `${(ns / 1_000).toFixed(1)} µs`;
  if (ns < 1_000_000_000) return `${(ns / 1_000_000).toFixed(2)} ms`;
  return `${(ns / 1_000_000_000).toFixed(2)} s`;
}

export function formatDelta(delta: number | null): string {
  if (delta == null) return '—';
  const sign = delta >= 0 ? '+' : '';
  return `${sign}${(delta * 100).toFixed(0)}%`;
}

export function formatCount(n: number | null): string {
  if (n == null) return '—';
  return n.toLocaleString('en-US');
}
