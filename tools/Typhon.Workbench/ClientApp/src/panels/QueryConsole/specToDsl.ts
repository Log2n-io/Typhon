import type { PredicateNodeDto } from '@/api/generated/model/predicateNodeDto';
import type { QuerySpecDto } from '@/api/generated/model/querySpecDto';
import type { SpatialClauseDto } from '@/api/generated/model/spatialClauseDto';

// All `string[]` fields on QuerySpecDto come through as `string[] | null` from the OpenAPI schema. Coalesce at the
// access site so callers never have to think about null.
const arr = (xs: string[] | null | undefined): string[] => xs ?? [];

/** The implicit default row cap — a spec carrying exactly this means "no explicit TAKE". Matches {@link EMPTY_SPEC}. */
export const DEFAULT_TAKE = 1000;

/**
 * Whether a TAKE clause is explicitly present — i.e. a positive, non-default cap. Single source of truth shared by
 * {@link specToDsl} (whether to emit a `TAKE` line) and the chip editor (whether to show the TAKE chip), so the two
 * can never drift — the drift that hid a DSL-parsed `TAKE` from chip mode while it was still applied.
 */
export function hasExplicitTake(take: number | string | null | undefined): boolean {
  const n = Number(take ?? DEFAULT_TAKE);
  return n > 0 && n !== DEFAULT_TAKE;
}

/**
 * Serialize a {@link QuerySpecDto} to the round-trip DSL form. Split out of {@link ./SpecChips.tsx} so the chip
 * file only exports its component (react-refresh fast-refresh constraint).
 */
export function specToDsl(spec: QuerySpecDto): string {
  const lines: string[] = [];
  if (spec.archetype) {
    lines.push(`FROM ${spec.archetype}${spec.polymorphic ? '' : ' exact'}`);
  }
  if (arr(spec.with).length) lines.push(`WITH ${arr(spec.with).join(', ')}`);
  if (arr(spec.without).length) lines.push(`WITHOUT ${arr(spec.without).join(', ')}`);
  if (arr(spec.exclude).length) lines.push(`EXCLUDE ${arr(spec.exclude).join(', ')}`);
  if (arr(spec.enabled).length) lines.push(`ENABLED ${arr(spec.enabled).join(', ')}`);
  if (arr(spec.disabled).length) lines.push(`DISABLED ${arr(spec.disabled).join(', ')}`);
  const whereText = whereToText(spec.where);
  if (whereText) lines.push(`WHERE ${whereText}`);
  // SPATIAL sits between WHERE and ORDER BY in the stage stack (§3). Each clause round-trips through the §5.1 grammar.
  for (const sp of spec.spatial ?? []) {
    const line = spatialToDsl(sp);
    if (line) lines.push(line);
  }
  // SELECT — explicit projection: the components whose fields become result columns. Component-list grammar, mirrors
  // WITH. Serialized after SPATIAL / before ORDER BY (projection follows filtering). Omitted when empty so the
  // no-SELECT default (columns driven by WHERE) round-trips unchanged.
  if (arr(spec.select).length) lines.push(`SELECT ${arr(spec.select).join(', ')}`);
  if (spec.orderBy) {
    lines.push(`ORDER BY ${spec.orderBy.component}.${spec.orderBy.field}${spec.orderBy.descending ? ' DESC' : ''}`);
  }
  // Orval emits int as `string | number` for safety; coerce to Number for comparisons.
  const skip = Number(spec.skip ?? 0);
  if (skip > 0) lines.push(`SKIP ${skip}`);
  if (hasExplicitTake(spec.take)) lines.push(`TAKE ${Number(spec.take ?? DEFAULT_TAKE)}`);
  return lines.join('\n');
}

export function specToDslExceptWhere(spec: QuerySpecDto): string {
  return specToDsl({ ...spec, where: null as unknown as QuerySpecDto['where'] });
}

/**
 * Serialize one SPATIAL clause to its §5.1 DSL form. Parameter layout matches {@link SpatialClauseDto}:
 * NEARBY `[cx,cy,cz,radius]`, AABB `[minX,minY,minZ,maxX,maxY,maxZ]`, RAY `[ox,oy,oz,dx,dy,dz,maxDist]`. Returns
 * null for an unknown kind or a missing component so a half-built chip never emits broken DSL.
 */
export function spatialToDsl(sp: SpatialClauseDto): string | null {
  const component = sp.component;
  if (!component) return null;
  const p = (sp.parameters ?? []).map((v) => {
    const n = Number(v);
    return Number.isFinite(n) ? String(n) : '0';
  });
  const at = (i: number): string => p[i] ?? '0';
  switch ((sp.kind ?? '').toLowerCase()) {
    case 'nearby':
      return `SPATIAL ${component} NEARBY ${at(0)}, ${at(1)}, ${at(2)} RADIUS ${at(3)}`;
    case 'aabb':
      return `SPATIAL ${component} AABB ${at(0)}, ${at(1)}, ${at(2)}, ${at(3)}, ${at(4)}, ${at(5)}`;
    case 'ray':
      return `SPATIAL ${component} RAY ${at(0)}, ${at(1)}, ${at(2)}, ${at(3)}, ${at(4)}, ${at(5)}, ${at(6)}`;
    default:
      return null;
  }
}

export function whereToText(node: QuerySpecDto['where']): string {
  if (!node) return '';
  if (node.kind === 'cmp') {
    return `${node.component}.${node.field} ${node.op} ${formatValue(node.value)}`;
  }
  const children = node.children as PredicateNodeDto[] | null | undefined;
  if (children && children.length) {
    const op = node.kind === 'and' ? ' AND ' : ' OR ';
    return children.map(whereToText).join(op);
  }
  return '';
}

function formatValue(v: unknown): string {
  if (typeof v === 'string') return /^[A-Za-z_][A-Za-z0-9_]*$/.test(v) ? v : JSON.stringify(v);
  if (v === null || v === undefined) return '';
  return String(v);
}
