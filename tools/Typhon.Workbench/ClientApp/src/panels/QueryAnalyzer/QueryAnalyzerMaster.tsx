import { useDeferredValue, useMemo } from 'react';
import { ArrowDown, ArrowUp, Copy } from 'lucide-react';
import type { QueryDefinitionDto } from '@/api/generated/model';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useProfilerNameMaps } from '@/hooks/useProfilerNameMaps';
import { useQueryDefinitions } from './useQueryDefinitions';
import { QueryCatalogToolbar } from './QueryCatalogToolbar';
import { useQueryCatalogStore, rowIdOf, type SortKey, type SortState } from './useQueryCatalogStore';
import { passesFilter } from './filter';
import { findDuplicateDefinitions } from './duplicate-detection';
import { toNumber } from './numeric';
import { useQueryAnalyzerStore, selectValidQuery, type QueryRef } from './useQueryAnalyzerStore';
import { formatNs, formatSelectivity, formatThousands, predicateSummary, queryKindLabel } from './format';
import { categoricalColor } from '@/libs/color/categorical';
import { rgbCss } from '@/libs/color/contrast';

/**
 * The Query Analyzer master — the ranked, filterable query catalog (design §4.1). One row per
 * `(Kind, LocalId)`; **default sort is `Aggregate.TotalWallNs` desc** (the canonical cost metric),
 * with click-to-sort on the numeric columns. Single click selects the query: it writes the
 * {@link useQueryAnalyzerStore} selection (drives the detail pane) AND the bus `query` leaf (drives
 * the Inspector + nav history). Reuses the Catalog toolbar/filters/dup-detection from #342; the old
 * expansion-based table is superseded here and removed in 4D.
 */
function sortValue(d: QueryDefinitionDto, key: SortKey): number {
  const agg = d.aggregate;
  switch (key) {
    case 'id':
      return toNumber(d.instanceId.kind) * 1_000_000 + toNumber(d.instanceId.localId);
    case 'count':
      return toNumber(agg.executionCount);
    case 'total':
      return toNumber(agg.totalWallNs);
    case 'selectivity':
      return toNumber(agg.avgSelectivity);
  }
}

export function QueryAnalyzerMaster() {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { definitions } = useQueryDefinitions();
  const { archetypeNames, systemNames } = useProfilerNameMaps();

  const search = useQueryCatalogStore((s) => s.search);
  const systemFilter = useQueryCatalogStore((s) => s.systemFilter);
  const archetypeFilter = useQueryCatalogStore((s) => s.archetypeFilter);
  const deferredSearch = useDeferredValue(search);

  const sort = useQueryCatalogStore((s) => s.sort);
  const setSort = useQueryCatalogStore((s) => s.setSort);

  const selectedQuery = useQueryAnalyzerStore((s) => selectValidQuery(s, sessionId));
  const selectedRowId = selectedQuery ? rowIdOf(selectedQuery.kind, selectedQuery.localId) : null;

  const archetypeOptions = useMemo(() => {
    const ids = new Set<number>();
    for (const d of definitions) ids.add(toNumber(d.targetComponentType));
    return Array.from(ids)
      .map((id) => ({ id, name: archetypeNames.get(id) ?? `Component[${id}]` }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [definitions, archetypeNames]);

  const systemOptions = useMemo(() => {
    const ids = new Set<number>();
    for (const d of definitions) {
      for (const sid of d.ownerSystemIds ?? []) ids.add(toNumber(sid));
    }
    return Array.from(ids)
      .map((id) => ({ id, name: systemNames.get(id) ?? `System[${id}]` }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [definitions, systemNames]);

  const ranked = useMemo(() => {
    const filter = { search: deferredSearch.trim().toLowerCase(), systemFilter, archetypeFilter };
    const names = {
      archetypeName: (id: number) => archetypeNames.get(id) ?? '',
      systemName: (id: number) => systemNames.get(id) ?? '',
    };
    const rows = definitions.filter((d) => passesFilter(d, filter, names));
    const factor = sort.dir === 'asc' ? 1 : -1;
    // Copy before sort — `definitions` is the memoised query result, never mutate it in place.
    return [...rows].sort((a, b) => factor * (sortValue(a, sort.key) - sortValue(b, sort.key)));
  }, [definitions, deferredSearch, systemFilter, archetypeFilter, archetypeNames, systemNames, sort]);

  const duplicateRowIds = useMemo(() => findDuplicateDefinitions(definitions), [definitions]);

  function onSelect(ref: QueryRef) {
    if (!sessionId) return;
    useQueryAnalyzerStore.getState().setSelectedQuery(sessionId, ref);
    // The bus carries a fresh ref each click; `select` re-stamps recency and pushes nav history.
    useSelectionStore.getState().select('query', ref);
  }

  // AC3.11 / view §6 — arrow-key catalog navigation. ↑/↓ moves selection one row, Home/End jumps to ends; Enter on
  // a row is already handled per-TableRow (existing tabIndex={0} keydown). Selection drives the store + bus via
  // `onSelect`, and we focus the newly-selected row so the focus ring follows. The handler lives on the catalog
  // scroll container so it fires regardless of which row currently has DOM focus.
  function onCatalogKeyDown(e: React.KeyboardEvent<HTMLDivElement>) {
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp' && e.key !== 'Home' && e.key !== 'End') return;
    if (ranked.length === 0) return;
    const currentIdx = selectedQuery
      ? ranked.findIndex((d) => toNumber(d.instanceId.kind) === selectedQuery.kind && toNumber(d.instanceId.localId) === selectedQuery.localId)
      : -1;
    let nextIdx: number;
    switch (e.key) {
      case 'ArrowDown': nextIdx = currentIdx < 0 ? 0 : Math.min(currentIdx + 1, ranked.length - 1); break;
      case 'ArrowUp':   nextIdx = currentIdx < 0 ? 0 : Math.max(currentIdx - 1, 0); break;
      case 'Home':      nextIdx = 0; break;
      case 'End':       nextIdx = ranked.length - 1; break;
      default: return;
    }
    if (nextIdx === currentIdx) return;
    e.preventDefault();
    const d = ranked[nextIdx];
    const nextRef: QueryRef = { kind: toNumber(d.instanceId.kind), localId: toNumber(d.instanceId.localId) };
    onSelect(nextRef);
    // Focus the new row so the focus ring follows the keyboard. Defer until React renders the new aria-selected state.
    const container = e.currentTarget;
    queueMicrotask(() => {
      const el = container.querySelector<HTMLElement>(`[data-row-id="${nextRef.kind}:${nextRef.localId}"]`);
      el?.focus();
    });
  }

  function toggleSort(key: SortKey) {
    // New column: numeric columns default to desc ("biggest first"), id to asc.
    const next: SortState = sort.key === key
      ? { key, dir: sort.dir === 'asc' ? 'desc' : 'asc' }
      : { key, dir: key === 'id' ? 'asc' : 'desc' };
    setSort(next);
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden">
      <QueryCatalogToolbar
        totalCount={definitions.length}
        filteredCount={ranked.length}
        archetypeOptions={archetypeOptions}
        systemOptions={systemOptions}
      />
      <div className="flex-1 overflow-auto" onKeyDown={onCatalogKeyDown} data-testid="query-analyzer-catalog">
        {ranked.length === 0 ? (
          <div className="p-3 text-fs-base text-muted-foreground">No definitions match the current filters.</div>
        ) : (
          <Table className="text-fs-base">
            <TableHeader>
              <TableRow>
                <TableHead className="w-[20px] text-fs-sm" />
                <SortHeader label="ID" col="id" sort={sort} onSort={toggleSort} />
                <TableHead className="text-fs-sm">Target</TableHead>
                <TableHead className="text-fs-sm">Predicate</TableHead>
                <TableHead className="text-fs-sm">Systems</TableHead>
                <SortHeader label="Count" col="count" sort={sort} onSort={toggleSort} align="right" />
                <SortHeader label="Total" col="total" sort={sort} onSort={toggleSort} align="right" />
                <TableHead className="text-right text-fs-sm">p50 / p95 / p99</TableHead>
                <SortHeader label="Sel" col="selectivity" sort={sort} onSort={toggleSort} align="right" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {ranked.map((d) => {
                const kind = toNumber(d.instanceId.kind);
                const localId = toNumber(d.instanceId.localId);
                const rowId = rowIdOf(kind, localId);
                const agg = d.aggregate;
                const target = archetypeNames.get(toNumber(d.targetComponentType)) ?? '—';
                const owners = (d.ownerSystemIds ?? [])
                  .map((id) => systemNames.get(toNumber(id)))
                  .filter((n): n is string => !!n);
                const isSelected = selectedRowId === rowId;
                return (
                  <TableRow
                    key={rowId}
                    className={
                      'cursor-pointer outline-none focus-visible:ring-1 focus-visible:ring-ring ' +
                      (isSelected ? 'bg-accent' : 'hover:bg-accent/50')
                    }
                    onClick={() => onSelect({ kind, localId })}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        onSelect({ kind, localId });
                      }
                    }}
                    tabIndex={0}
                    role="row"
                    aria-selected={isSelected}
                    data-testid="query-analyzer-row"
                    data-row-id={rowId}
                    data-selected={isSelected || undefined}
                    data-duplicate={duplicateRowIds.has(rowId) || undefined}
                  >
                    <TableCell className="w-[20px]">
                      {duplicateRowIds.has(rowId) && (
                        <Copy
                          className="h-3.5 w-3.5 text-amber-500"
                          aria-label="Duplicate definition: another definition has the same structural shape"
                          data-testid="query-analyzer-duplicate-marker"
                        />
                      )}
                    </TableCell>
                    <TableCell className="font-mono text-fs-base">{`${queryKindLabel(kind)} #${localId}`}</TableCell>
                    <TableCell className="font-mono text-fs-sm text-muted-foreground">{target}</TableCell>
                    <TableCell className="font-mono text-fs-sm">{predicateSummary(d)}</TableCell>
                    <TableCell className="text-fs-base">
                      {owners.length === 0 ? '—' : (
                        <span className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
                          {owners.map((name) => (
                            <span key={name} className="inline-flex items-center gap-1">
                              <span
                                aria-hidden
                                className="inline-block h-2 w-2 shrink-0 rounded-sm"
                                style={{ backgroundColor: rgbCss(categoricalColor(name)) }}
                              />
                              {name}
                            </span>
                          ))}
                        </span>
                      )}
                    </TableCell>
                    <TableCell className="text-right tabular-nums text-fs-base">{formatThousands(toNumber(agg.executionCount))}</TableCell>
                    <TableCell className="text-right tabular-nums text-fs-base font-medium">{formatNs(toNumber(agg.totalWallNs))}</TableCell>
                    <TableCell className="text-right tabular-nums text-fs-sm text-muted-foreground">
                      {`${formatNs(toNumber(agg.p50WallNs))} / ${formatNs(toNumber(agg.p95WallNs))} / ${formatNs(toNumber(agg.p99WallNs))}`}
                    </TableCell>
                    <TableCell className="text-right tabular-nums text-fs-base">{formatSelectivity(toNumber(agg.avgSelectivity))}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </div>
    </div>
  );
}

function SortHeader({
  label,
  col,
  sort,
  onSort,
  align = 'left',
}: {
  label: string;
  col: SortKey;
  sort: SortState;
  onSort: (key: SortKey) => void;
  align?: 'left' | 'right';
}) {
  const active = sort.key === col;
  return (
    <TableHead className={`text-fs-sm ${align === 'right' ? 'text-right' : ''}`}>
      <button
        type="button"
        onClick={() => onSort(col)}
        className={`inline-flex items-center gap-1 hover:text-foreground ${active ? 'text-foreground' : 'text-muted-foreground'}`}
        data-testid={`query-analyzer-sort-${col}`}
        aria-label={`Sort by ${label}`}
      >
        <span>{label}</span>
        {active && (sort.dir === 'asc' ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />)}
      </button>
    </TableHead>
  );
}
