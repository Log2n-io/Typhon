import { useEffect, useRef } from 'react';
import { ChevronDown, ChevronRight, Copy, ExternalLink, ListTree, Network } from 'lucide-react';
import { TableCell, TableRow } from '@/components/ui/table';
import { useOptionsStore } from '@/stores/useOptionsStore';
import type { QueryDefinitionDto } from '@/api/generated/model';
import { openViewExecutionInspector, openViewQueryPlanTree } from '@/shell/commands/profilerCommands';
import { useQueryPlanStore } from '@/panels/QueryPlanTree/useQueryPlanStore';
import { useExecutionInspectorStore } from '@/panels/ExecutionInspector/useExecutionInspectorStore';
import { useQueryCatalogStore, rowIdOf } from './useQueryCatalogStore';
import { toNumber } from './numeric';

/**
 * Single row in the Query Catalog table + the matching detail-expansion row when the row is
 * selected. Renders a "definition strip" of compact columns; clicking the row toggles the detail
 * panel below it. Clicking the source-link icon opens the definition's call site in the user's
 * configured editor via <c>useOptionsStore.openInEditor</c>.
 *
 * Issue #338 (P5 of #342).
 */
interface RowProps {
  definition: QueryDefinitionDto;
  archetypeName: string;
  ownerSystemNames: string[];
  isDuplicate: boolean;
}

export function QueryCatalogRow({ definition, archetypeName, ownerSystemNames, isDuplicate }: RowProps) {
  const kind = toNumber(definition.instanceId.kind);
  const localId = toNumber(definition.instanceId.localId);
  const rowId = rowIdOf(kind, localId);

  const expandedRowId = useQueryCatalogStore((s) => s.expandedRowId);
  const toggleExpanded = useQueryCatalogStore((s) => s.toggleExpanded);
  const openInEditor = useOptionsStore((s) => s.openInEditor);
  const rowRef = useRef<HTMLTableRowElement | null>(null);

  const isExpanded = expandedRowId === rowId;

  // Scroll the just-expanded row into view when expansion came from an external hand-off (e.g. the
  // System DAG "Queries" badge calling setExpanded on a system that owns this query). 'nearest' is
  // intentional — if the row is already visible we don't yank the scroll position.
  useEffect(() => {
    if (isExpanded && rowRef.current) {
      rowRef.current.scrollIntoView({ block: 'nearest', behavior: 'auto' });
    }
  }, [isExpanded]);
  const evaluators = definition.evaluators ?? [];
  const agg = definition.aggregate;
  const src = definition.userSource;

  const kindLabel = kind === 0 ? 'View' : 'EcsQuery';
  const idLabel = `${kindLabel} #${localId}`;
  const filtersLabel = evaluators.length === 0 ? '—' : `${evaluators.length}`;
  const ownersLabel = ownerSystemNames.length === 0 ? '—' : ownerSystemNames.join(', ');
  const archetypeLabel = archetypeName || '—';
  const execCount = toNumber(agg.executionCount);
  const avgWallNs = toNumber(agg.avgWallNs);

  const sourceFile = src.file ?? '';
  const sourceLine = toNumber(src.line);
  const sourceMethod = src.method ?? '';
  const hasSource = sourceFile.length > 0 && sourceLine > 0;

  return (
    <>
      <TableRow
        ref={rowRef}
        className="cursor-pointer hover:bg-accent focus-visible:bg-accent outline-none focus-visible:ring-1 focus-visible:ring-ring"
        onClick={() => toggleExpanded(rowId)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            toggleExpanded(rowId);
          }
        }}
        tabIndex={0}
        role="button"
        aria-expanded={isExpanded}
        aria-label={`${idLabel} — ${archetypeLabel}`}
        data-testid="query-catalog-row"
        data-row-id={rowId}
        data-duplicate={isDuplicate || undefined}
      >
        <TableCell className="w-[20px]">
          {isExpanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
        </TableCell>
        <TableCell className="font-mono text-fs-base">{idLabel}</TableCell>
        <TableCell className="text-fs-base">{ownersLabel}</TableCell>
        <TableCell className="font-mono text-fs-sm text-muted-foreground">{archetypeLabel}</TableCell>
        <TableCell className="text-right tabular-nums text-fs-base">{filtersLabel}</TableCell>
        <TableCell className="text-right tabular-nums text-fs-base">{formatThousands(execCount)}</TableCell>
        <TableCell className="text-right tabular-nums text-fs-base">{formatNs(avgWallNs)}</TableCell>
        <TableCell className="text-fs-sm">
          {hasSource ? (
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                void openInEditor(sourceFile, sourceLine);
              }}
              className="inline-flex items-center gap-1 text-foreground hover:underline"
              title={`${sourceFile}:${sourceLine}`}
              aria-label={`Open ${sourceFile}:${sourceLine} in editor`}
              data-testid="query-catalog-open-in-editor"
            >
              <ExternalLink className="h-3 w-3" />
              <span className="font-mono">{sourceMethod || '<unattributed>'}</span>
            </button>
          ) : (
            <span className="text-muted-foreground">—</span>
          )}
        </TableCell>
        <TableCell className="w-[28px]">
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              useQueryPlanStore.getState().setFocus({ kind, localId });
              openViewQueryPlanTree();
            }}
            className="inline-flex items-center text-muted-foreground hover:text-foreground"
            title="Show plan tree"
            aria-label={`Show plan tree for ${idLabel}`}
            data-testid="query-catalog-show-plan"
          >
            <Network className="h-3.5 w-3.5" />
          </button>
        </TableCell>
        <TableCell className="w-[28px]">
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              useExecutionInspectorStore.getState().setFocus({ kind, localId });
              openViewExecutionInspector();
            }}
            className="inline-flex items-center text-muted-foreground hover:text-foreground"
            title="Inspect executions"
            aria-label={`Inspect executions for ${idLabel}`}
            data-testid="query-catalog-inspect-executions"
          >
            <ListTree className="h-3.5 w-3.5" />
          </button>
        </TableCell>
        <TableCell className="w-[24px]">
          {isDuplicate && (
            <Copy
              className="h-3.5 w-3.5 text-amber-500"
              aria-label="Duplicate definition: another definition has the same structural shape"
              data-testid="query-catalog-duplicate-marker"
            />
          )}
        </TableCell>
      </TableRow>
      {isExpanded && (
        <TableRow data-testid="query-catalog-detail-row">
          <TableCell colSpan={11} className="bg-muted/30 px-4 py-2 text-fs-base">
            <DetailBody definition={definition} archetypeName={archetypeName} />
          </TableCell>
        </TableRow>
      )}
    </>
  );
}

function DetailBody({ definition, archetypeName }: { definition: QueryDefinitionDto; archetypeName: string }) {
  const agg = definition.aggregate;
  const evaluators = definition.evaluators ?? [];
  const fieldDeps = definition.fieldDependencies ?? [];
  const primaryIdx = toNumber(definition.primaryIndexFieldIdx);
  const sortIdx = toNumber(definition.sortFieldIdx);
  const sortDir = definition.sortDescending ? 'DESC' : 'ASC';

  return (
    <div className="grid grid-cols-2 gap-x-6 gap-y-1">
      <DetailKv label="Archetype" value={archetypeName || '—'} mono />
      <DetailKv label="Primary index" value={primaryIdx >= 0 ? `Field[${primaryIdx}]` : '—'} mono />
      <DetailKv
        label="Sort"
        value={sortIdx >= 0 ? `Field[${sortIdx}] ${sortDir}` : '—'}
        mono
      />
      <DetailKv label="Field deps" value={fieldDeps.length === 0 ? '—' : fieldDeps.map(toNumber).join(', ')} mono />

      <DetailKv label="Executions" value={formatThousands(toNumber(agg.executionCount))} />
      <DetailKv label="Total wall" value={formatNs(toNumber(agg.totalWallNs))} />
      <DetailKv label="p50 / p95 / p99" value={`${formatNs(toNumber(agg.p50WallNs))} / ${formatNs(toNumber(agg.p95WallNs))} / ${formatNs(toNumber(agg.p99WallNs))}`} />
      <DetailKv label="Rows scanned → returned" value={`${formatThousands(toNumber(agg.totalRowsScanned))} → ${formatThousands(toNumber(agg.totalRowsReturned))}`} />
      <DetailKv label="Avg selectivity" value={formatSelectivity(toNumber(agg.avgSelectivity))} />

      <div className="col-span-2 pt-2">
        <div className="text-fs-sm font-semibold uppercase tracking-wide text-muted-foreground">Evaluators</div>
        {evaluators.length === 0 ? (
          <div className="text-fs-sm text-muted-foreground">No filter evaluators on this query.</div>
        ) : (
          <ul className="mt-1 space-y-0.5">
            {evaluators.map((e, i) => (
              <li key={i} className="font-mono text-fs-sm">
                <span className="text-foreground">{e.fieldName || `Field[${toNumber(e.fieldIdx)}]`}</span>{' '}
                <span className="text-muted-foreground">{e.opDisplay || `op${toNumber(e.op)}`}</span>{' '}
                <span className="text-muted-foreground">?</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function DetailKv({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex gap-2">
      <span className="text-fs-sm text-muted-foreground min-w-[100px]">{label}:</span>
      <span className={`text-fs-base ${mono ? 'font-mono' : ''}`}>{value}</span>
    </div>
  );
}

// ── formatters ──

function formatThousands(n: number): string {
  return n.toLocaleString('en-US');
}

function formatNs(ns: number): string {
  if (ns === 0) return '0 ns';
  if (ns < 1_000) return `${ns} ns`;
  if (ns < 1_000_000) return `${(ns / 1_000).toFixed(1)} µs`;
  if (ns < 1_000_000_000) return `${(ns / 1_000_000).toFixed(2)} ms`;
  return `${(ns / 1_000_000_000).toFixed(2)} s`;
}

function formatSelectivity(value: number | undefined | null): string {
  if (value == null || !Number.isFinite(value)) return '—';
  return `${(value * 100).toFixed(1)}%`;
}
