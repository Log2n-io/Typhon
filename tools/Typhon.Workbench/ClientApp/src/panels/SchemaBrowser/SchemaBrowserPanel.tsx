import { useDeferredValue, useMemo, useState } from 'react';
import Fuse from 'fuse.js';
import type { IDockviewPanelProps } from 'dockview-react';
import { ArrowDown, ArrowUp, RefreshCw } from 'lucide-react';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { useComponentList } from '@/hooks/schema/useComponentList';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { toggleViewSchemaLayout } from '@/shell/commands/openSchemaBrowser';
import type { ComponentSummary } from '@/hooks/schema/types';
import SchemaBrowserContextMenu from './SchemaBrowserContextMenu';

type SortKey = 'typeName' | 'storageSize' | 'fieldCount' | 'entityCount' | 'indexCount';
type SortDir = 'asc' | 'desc';
type QuickFilter = 'noEntities' | 'noIndexes' | 'large';

const LARGE_THRESHOLD = 128;

/**
 * Dedicated dockable panel listing every registered component type with triage-friendly columns.
 * The main entry point when a developer wants to survey the schema rather than drill into one known
 * type. Double-click a row → open the Layout panel focused on that component.
 */
export default function SchemaBrowserPanel(_props: IDockviewPanelProps) {
  const { list, isLoading, isError, refetch, isFetching } = useComponentList();
  const selectComponent = useSchemaInspectorStore((s) => s.selectComponent);

  const [query, setQuery] = useState('');
  const deferredQuery = useDeferredValue(query);
  const [sortKey, setSortKey] = useState<SortKey>('typeName');
  const [sortDir, setSortDir] = useState<SortDir>('asc');
  const [filters, setFilters] = useState<Set<QuickFilter>>(() => new Set());

  const fuse = useMemo(
    () =>
      new Fuse(list, {
        keys: [
          { name: 'typeName', weight: 0.7 },
          { name: 'fullName', weight: 0.3 },
        ],
        threshold: 0.35,
        ignoreLocation: true,
      }),
    [list],
  );

  const filtered = useMemo(() => {
    const base =
      deferredQuery.trim().length === 0
        ? list
        : fuse.search(deferredQuery).map((r) => r.item);

    return base.filter((c) => {
      if (filters.has('noEntities') && c.entityCount !== 0) return false;
      if (filters.has('noIndexes') && c.indexCount !== 0) return false;
      if (filters.has('large') && c.storageSize < LARGE_THRESHOLD) return false;
      return true;
    });
  }, [list, fuse, deferredQuery, filters]);

  const sorted = useMemo(() => {
    const arr = [...filtered];
    arr.sort((a, b) => {
      const va = readSortField(a, sortKey);
      const vb = readSortField(b, sortKey);
      if (typeof va === 'string' && typeof vb === 'string') {
        return sortDir === 'asc' ? va.localeCompare(vb) : vb.localeCompare(va);
      }
      const na = Number(va);
      const nb = Number(vb);
      return sortDir === 'asc' ? na - nb : nb - na;
    });
    return arr;
  }, [filtered, sortKey, sortDir]);

  // Hide archetypeCount column entirely when every row is null (Phase 1 — see design §4.1).
  const showArchetypeCol = list.some((c) => c.archetypeCount != null);

  const openLayoutFor = (typeName: string) => {
    selectComponent(typeName);
    toggleViewSchemaLayout();
  };

  const toggleSort = (key: SortKey) => {
    if (sortKey === key) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortKey(key);
      setSortDir('asc');
    }
  };

  const toggleFilter = (f: QuickFilter) => {
    setFilters((prev) => {
      const next = new Set(prev);
      if (next.has(f)) {
        next.delete(f);
      } else {
        next.add(f);
      }
      return next;
    });
  };

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <div className="flex items-center gap-2 border-b border-border px-2 py-1.5">
        <Input
          placeholder="Search components… (name)"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          className="h-7 text-[12px]"
        />
        <Button
          size="sm"
          variant="ghost"
          className="h-7 w-7 p-0"
          onClick={() => refetch()}
          title="Refresh"
          aria-label="Refresh component list"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} />
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-1 border-b border-border px-2 py-1">
        <span className="text-[11px] text-muted-foreground">Quick filters:</span>
        <FilterChip label="No entities" active={filters.has('noEntities')} onClick={() => toggleFilter('noEntities')} />
        <FilterChip label="No indexes" active={filters.has('noIndexes')} onClick={() => toggleFilter('noIndexes')} />
        <FilterChip label="Large (≥128B)" active={filters.has('large')} onClick={() => toggleFilter('large')} />
        <span className="ml-auto text-[11px] text-muted-foreground">
          {sorted.length} of {list.length} · double-click a row to open in Layout
        </span>
      </div>

      <div className="flex-1 overflow-auto">
        {isError && (
          <p className="p-3 text-[12px] text-destructive">Failed to load schema.</p>
        )}
        {isLoading && !list.length && (
          <p className="p-3 text-[12px] text-muted-foreground">Loading schema…</p>
        )}
        {!isLoading && list.length === 0 && (
          <p className="p-3 text-[12px] text-muted-foreground">
            No components registered in this session.
          </p>
        )}
        {list.length > 0 && (
          <Table className="text-[12px]">
            <TableHeader>
              <TableRow>
                <SortableHead sortKey="typeName" label="Name" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <SortableHead sortKey="storageSize" label="Size" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <SortableHead sortKey="fieldCount" label="Fields" current={sortKey} dir={sortDir} onClick={toggleSort} />
                {showArchetypeCol && (
                  <TableHead className="py-1 text-[11px]">Archetypes</TableHead>
                )}
                <SortableHead sortKey="entityCount" label="Entities" current={sortKey} dir={sortDir} onClick={toggleSort} />
                <SortableHead sortKey="indexCount" label="Indexes" current={sortKey} dir={sortDir} onClick={toggleSort} />
              </TableRow>
            </TableHeader>
            <TableBody>
              {sorted.map((c) => (
                <SchemaBrowserContextMenu
                  key={c.fullName}
                  component={c}
                  onOpenInLayout={openLayoutFor}
                >
                  <TableRow
                    className="cursor-pointer hover:bg-accent"
                    onClick={() => selectComponent(c.typeName)}
                    onDoubleClick={() => openLayoutFor(c.typeName)}
                    data-testid="schema-row"
                    data-type-name={c.typeName}
                    title="Double-click to open Component Layout"
                  >
                  <TableCell className="py-1 font-mono">
                    <div className="flex flex-col">
                      <span className="text-foreground">{c.typeName}</span>
                      {c.fullName !== c.typeName && (
                        <span className="text-[10px] text-muted-foreground">{c.fullName}</span>
                      )}
                    </div>
                  </TableCell>
                  <TableCell className="py-1 text-right font-mono tabular-nums">{c.storageSize}B</TableCell>
                  <TableCell className="py-1 text-right tabular-nums">{c.fieldCount}</TableCell>
                  {showArchetypeCol && (
                    <TableCell className="py-1 text-right tabular-nums">
                      {c.archetypeCount ?? '—'}
                    </TableCell>
                  )}
                  <TableCell className="py-1 text-right tabular-nums">{c.entityCount}</TableCell>
                  <TableCell className="py-1 text-right tabular-nums">{c.indexCount}</TableCell>
                  </TableRow>
                </SchemaBrowserContextMenu>
              ))}
            </TableBody>
          </Table>
        )}
      </div>
    </div>
  );
}

function FilterChip({ label, active, onClick }: { label: string; active: boolean; onClick: () => void }) {
  return (
    <Badge
      variant={active ? 'default' : 'outline'}
      className="cursor-pointer select-none px-2 py-0 text-[11px]"
      onClick={onClick}
    >
      {label}
    </Badge>
  );
}

function SortableHead({
  sortKey,
  label,
  current,
  dir,
  onClick,
}: {
  sortKey: SortKey;
  label: string;
  current: SortKey;
  dir: SortDir;
  onClick: (key: SortKey) => void;
}) {
  const active = current === sortKey;
  return (
    <TableHead
      className="cursor-pointer select-none py-1 text-[11px]"
      onClick={() => onClick(sortKey)}
    >
      <span className="inline-flex items-center gap-1">
        {label}
        {active && (dir === 'asc' ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />)}
      </span>
    </TableHead>
  );
}

function readSortField(row: ComponentSummary, key: SortKey): string | number {
  switch (key) {
    case 'typeName':
      return row.typeName;
    case 'storageSize':
      return row.storageSize;
    case 'fieldCount':
      return row.fieldCount;
    case 'entityCount':
      return row.entityCount;
    case 'indexCount':
      return row.indexCount;
  }
}
