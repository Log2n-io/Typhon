import { useMemo, useRef } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useQueryConsoleStore } from '@/stores/useQueryConsoleStore';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';
import type { QueryResultDto } from '@/api/generated/model/queryResultDto';
import type { QueryCellDto } from '@/api/generated/model/queryCellDto';

const ROW_HEIGHT_PX = 22; // Workbench convention — matches Data Browser grid
const ENTITY_ID_WIDTH_PX = 128; // fixed; entityId is always a decimal long, no need to flex

/**
 * Phase-1 result grid. Div-based (not `<table>`) because virtualisation + native table layout conflict — browsers
 * compute column widths from `<thead>` rows participating in the table flow, but absolutely-positioned virtual
 * rows escape that flow entirely. Splitting into divs lets head and body share the same flex layout, so columns
 * align consistently. ARIA roles preserve the grid semantics for assistive tech.
 *
 * Columns come from the first row's cells: `fieldName` (server-populated) is the header label; `(typeName,
 * fieldId)` is the join key into the schema cache for future polish (sort indicator, type icon, etc.).
 */
export function ResultGrid() {
  const result = useQueryConsoleStore((s) => s.lastResult);
  const runState = useQueryConsoleStore((s) => s.runState);
  const errorCode = useQueryConsoleStore((s) => s.runErrorCode);
  const errorMessage = useQueryConsoleStore((s) => s.runErrorMessage);

  if (runState === 'idle') {
    return (
      <div className="flex h-full items-center justify-center text-sm text-muted-foreground">
        Press <span className="mx-1 rounded border border-border bg-muted px-1.5 py-0.5 font-mono text-xs">Run</span> to
        execute the query.
      </div>
    );
  }
  if (runState === 'running') {
    return <div className="flex h-full items-center justify-center text-sm text-muted-foreground">Running…</div>;
  }
  if (runState === 'error') {
    return (
      <div className="flex h-full flex-col items-center justify-center gap-2 p-4 text-sm">
        {/* select-text opts back in to copyability — body sets user-select:none by default (see globals.css).
            The error code + detail are the things users most need to paste into Slack / search / GitHub. */}
        <div className="select-text rounded border border-red-500/40 bg-red-500/5 px-3 py-2 text-red-300">
          <div className="font-semibold">{errorCode ?? 'error'}</div>
          <div className="text-xs">{errorMessage}</div>
        </div>
      </div>
    );
  }
  if (!result) return null;

  return <GridBody result={result} />;
}

function GridBody({ result }: { result: QueryResultDto }) {
  // Defensive: DTO fields are nullable per the OpenAPI schema. Memoize the coalescing so `rows`/`warnings` are
  // stable references when the underlying nullable is non-null; without the useMemo the `?? []` fallback creates
  // a fresh empty array each render and defeats the downstream useMemo's dependency check.
  const rows = useMemo(() => result.rows ?? [], [result.rows]);
  const warnings = useMemo(() => result.warnings ?? [], [result.warnings]);
  const { label: nameLabel } = useComponentNames();

  // Column metadata is derived from the first row's cells. The server already populates `fieldName`, so the
  // header label is the resolved field name (not the field id). For multi-component results we prefix the
  // header with the component's smart short label (`label(typeName).fieldName`) to disambiguate identically-named
  // fields across components.
  const columns = useMemo(() => {
    const first = rows[0];
    const cells = first?.cells ?? [];
    const typeNames = new Set(cells.map((c) => c.typeName ?? ''));
    const showTypePrefix = typeNames.size > 1;
    return cells.map((c) => ({
      key: `${c.typeName ?? '?'}::${c.fieldId}`,
      label: showTypePrefix
        ? `${nameLabel(c.typeName)}.${c.fieldName ?? `Field[${c.fieldId}]`}`
        : (c.fieldName ?? `Field[${c.fieldId}]`),
      title: `${c.typeName ?? '?'}.${c.fieldName ?? `Field[${c.fieldId}]`}`,
    }));
  }, [rows, nameLabel]);

  const parentRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => ROW_HEIGHT_PX,
    overscan: 16,
  });

  const virtualRows = virtualizer.getVirtualItems();
  const totalSize = virtualizer.getTotalSize();

  return (
    <div className="flex h-full flex-col">
      {/* Toolbar strip: counts + timing + revision badge */}
      <div className="flex items-center justify-between border-b border-border bg-muted/30 px-2 py-1 text-xs">
        <div>
          <span className="font-mono">{rows.length}</span> of{' '}
          <span className="font-mono">{result.totalCountEstimate}</span> rows ·
          <span className="ml-1 font-mono">{(Number(result.executionWallNs ?? 0) / 1_000_000).toFixed(1)} ms</span>
          {result.hasMore && (
            <span className="ml-2 rounded bg-yellow-500/10 px-1.5 py-0.5 text-yellow-300">truncated</span>
          )}
        </div>
        <div className="font-mono text-muted-foreground">TSN {result.resolvedRevisionTsn}</div>
      </div>

      {warnings.length > 0 && (
        <div className="select-text border-b border-border bg-yellow-500/5 px-2 py-1 text-xs text-yellow-300">
          {warnings.map((w) => (
            <div key={w.code}>
              <span className="font-mono">{w.code}</span>: {w.message}
            </div>
          ))}
        </div>
      )}

      {/* Head + body share the same flex layout so columns line up. Roles preserve grid semantics for a11y. */}
      <div ref={parentRef} role="grid" className="flex-1 select-text overflow-auto">
        {/* Header row — sticky so it survives body scroll. Same column layout as body rows below. */}
        <div
          role="row"
          className="sticky top-0 z-10 flex border-b border-border bg-background text-xs text-muted-foreground"
        >
          <div
            role="columnheader"
            className="shrink-0 px-2 py-1 font-mono"
            style={{ width: `${ENTITY_ID_WIDTH_PX}px` }}
          >
            EntityId
          </div>
          {columns.map((c) => (
            <div key={c.key} role="columnheader" title={c.title} className="flex-1 truncate px-2 py-1 font-mono">
              {c.label}
            </div>
          ))}
        </div>

        {/* Virtualised body — each row is absolutely positioned inside a height-known container. */}
        <div style={{ height: `${totalSize}px`, position: 'relative' }}>
          {virtualRows.map((virtualRow) => {
            const row = rows[virtualRow.index];
            const cells = row.cells ?? [];
            return (
              <div
                key={row.entityId}
                role="row"
                style={{
                  position: 'absolute',
                  top: 0,
                  left: 0,
                  right: 0,
                  height: `${virtualRow.size}px`,
                  transform: `translateY(${virtualRow.start}px)`,
                }}
                className="flex border-b border-border/50 text-xs hover:bg-muted/30"
              >
                <div
                  role="gridcell"
                  className="shrink-0 truncate px-2 py-0.5 font-mono"
                  style={{ width: `${ENTITY_ID_WIDTH_PX}px` }}
                >
                  {row.entityId}
                </div>
                {cells.map((cell, i) => (
                  <div
                    key={i}
                    role="gridcell"
                    className="flex-1 truncate px-2 py-0.5 font-mono"
                    title={String(cell.value ?? cell.raw ?? '')}
                  >
                    {formatCell(cell)}
                  </div>
                ))}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}

function formatCell(cell: QueryCellDto): string {
  if (cell.value === null || cell.value === undefined) {
    return cell.raw ? `0x${cell.raw}` : '—';
  }
  if (typeof cell.value === 'number') return String(cell.value);
  if (typeof cell.value === 'boolean') return cell.value ? 'true' : 'false';
  return String(cell.value);
}
