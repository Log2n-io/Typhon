import { useDeferredValue, useEffect, useMemo, useRef, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { Tree, type NodeRendererProps, type TreeApi } from 'react-arborist';
import { ArrowDown, ArrowUp, Layers, RefreshCw, Table2 } from 'lucide-react';
import Fuse from 'fuse.js';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { StatusBadge } from '@/components/ui/status-badge';
import { useDensityRowHeight } from '@/hooks/useDensityRowHeight';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import { useComponentList } from '@/hooks/schema/useComponentList';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSchemaExplorerPrefsStore } from '@/stores/useSchemaExplorerPrefsStore';
import { openArchetypeInspector, openComponentInspector } from '@/shell/commands/openSchemaBrowser';
import type { StorageMode } from '@/hooks/schema/types';
import {
  buildArchetypeTree,
  filterArchetypeTree,
  applyArchetypeFilters,
  applyComponentFilters,
  sortComponents,
  type ArchetypeTreeNode,
  type ComponentChildNode,
  type ArchetypeFilters,
  type ComponentFilters,
  type ComponentSortKey,
  type SchemaExplorerMode,
  type SortDir,
} from './schemaExplorerModel';

/**
 * Schema Explorer (Stage 2, GAP-02) — the archetype-rooted schema navigator and Open-session default
 * center. Consolidates the former Component + Archetype browsers into one surface with two modes:
 * Archetypes (a tree of archetypes → their component types) and Types (a flat component list, counts as
 * type-global totals — SE-2). Selection drives the unified bus leaf → right-rail Inspector. A thin view
 * over {@link ./schemaExplorerModel} (unit-tested join/filter/sort); the tree is proven by E2E.
 */

type SchemaTreeNode = ArchetypeTreeNode | ComponentChildNode;

function stripNamespace(fullName: string): string {
  const dot = fullName.lastIndexOf('.');
  return dot === -1 ? fullName : fullName.slice(dot + 1);
}

/** react-arborist row — switches on node kind; selection writes the bus leaf, internal nodes toggle. */
function SchemaNodeRow({ node, style }: NodeRendererProps<SchemaTreeNode>) {
  const data = node.data;
  const indent = node.level * 12;
  const select = useSelectionStore.getState().select;

  if (data.kind === 'archetype') {
    const a = data.archetype;
    return (
      <div
        style={{ ...style, paddingLeft: indent + 4 }}
        className="flex cursor-pointer items-center gap-1.5 px-1 text-[11px] leading-none hover:bg-primary/20"
        title={`Archetype #${a.archetypeId} — ${a.componentTypes.length} components`}
        data-testid="schema-explorer-archetype"
        data-archetype-id={a.archetypeId}
        onClick={() => {
          node.select();
          select('archetype', a.archetypeId);
          if (node.isInternal) node.toggle();
        }}
        onDoubleClick={() => {
          // Open in → the Archetype Inspector deep view (J1 step 4). Single click already set the leaf.
          select('archetype', a.archetypeId);
          openArchetypeInspector();
        }}
      >
        <span className="w-3.5 shrink-0 text-[13px] leading-none text-muted-foreground">
          {node.isInternal ? (node.isOpen ? '▾' : '▸') : ''}
        </span>
        <Layers className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
        <span className="shrink-0 font-mono text-foreground">#{a.archetypeId}</span>
        <span className="min-w-0 flex-1 truncate text-muted-foreground">
          {a.componentTypes.map(stripNamespace).join(', ')}
        </span>
        {a.entityCount > 0 && (
          <Badge variant="secondary" className="h-4 shrink-0 px-1 text-[10px] leading-none">
            {a.entityCount.toLocaleString()}
          </Badge>
        )}
        {a.storageMode === 'cluster' && a.chunkCount > 0 && (
          <span className="shrink-0 tabular-nums text-[10px] text-muted-foreground">{a.occupancyPct.toFixed(0)}%</span>
        )}
      </div>
    );
  }

  return (
    <div
      style={{ ...style, paddingLeft: indent + 4 }}
      className="flex cursor-pointer items-center gap-1.5 px-1 text-[11px] leading-none hover:bg-primary/20"
      title={data.fullName}
      data-testid="schema-explorer-component"
      data-type-name={data.typeName}
      onClick={() => {
        node.select();
        select('component', data.typeName);
      }}
      onDoubleClick={() => {
        select('component', data.typeName);
        openComponentInspector();
      }}
    >
      <span className="w-3.5 shrink-0" />
      <Table2 className="h-3 w-3 shrink-0 text-muted-foreground" />
      <span className="min-w-0 flex-1 truncate font-mono text-foreground">{data.typeName}</span>
      {data.summary && (
        <span className="shrink-0 tabular-nums text-[10px] text-muted-foreground">
          {data.summary.storageSize}B{data.summary.indexCount > 0 ? ` · ${data.summary.indexCount} idx` : ''}
        </span>
      )}
    </div>
  );
}

export default function SchemaExplorerPanel(_props: IDockviewPanelProps) {
  const { list: archetypes, isLoading: aLoading, isError: aError, refetch: aRefetch, isFetching: aFetching } = useArchetypeList();
  const { list: components, isLoading: cLoading, isError: cError, refetch: cRefetch, isFetching: cFetching } = useComponentList();
  const select = useSelectionStore((s) => s.select);

  // Mode (Archetypes / Types) persists per-file (PC-1 / AC2.16): a saved mode for the current file wins,
  // else fall back to local state (which also keeps the toggle working in a no-file session, e.g. tests).
  const filePath = useSessionStore((s) => s.filePath);
  const savedMode = useSchemaExplorerPrefsStore((s) => (filePath ? s.modeByFile[filePath] : undefined));
  const setSavedMode = useSchemaExplorerPrefsStore((s) => s.setMode);
  const [localMode, setLocalMode] = useState<SchemaExplorerMode>('archetypes');
  const mode = savedMode ?? localMode;
  const setMode = (m: SchemaExplorerMode) => {
    setLocalMode(m);
    if (filePath) {
      setSavedMode(filePath, m);
    }
  };
  const [query, setQuery] = useState('');
  const deferredQuery = useDeferredValue(query);
  const [archFilters, setArchFilters] = useState<ArchetypeFilters>({});
  const [compFilters, setCompFilters] = useState<ComponentFilters>({});
  const [sortKey, setSortKey] = useState<ComponentSortKey>('typeName');
  const [sortDir, setSortDir] = useState<SortDir>('asc');

  const rowHeight = useDensityRowHeight();
  const containerRef = useRef<HTMLDivElement>(null);
  const treeRef = useRef<TreeApi<SchemaTreeNode> | null>(null);
  const [treeHeight, setTreeHeight] = useState(400);

  const isLoading = aLoading || cLoading;
  const isError = aError || cError;
  const isFetching = aFetching || cFetching;
  const isEmpty = !isLoading && archetypes.length === 0 && components.length === 0;
  const refresh = () => {
    aRefetch();
    cRefetch();
  };

  // Archetypes mode: filter archetypes → join with components → query-filter the tree (substring, like the
  // Resource Tree — react-arborist's fuzzy match leaves too many siblings visible).
  const archTree = useMemo(
    () => filterArchetypeTree(buildArchetypeTree(applyArchetypeFilters(archetypes, archFilters), components), deferredQuery),
    [archetypes, components, archFilters, deferredQuery],
  );

  // Types mode: fuzzy component search (matches the former Component Browser) → quick filters → sort.
  const typesFuse = useMemo(
    () => new Fuse(components, { keys: [{ name: 'typeName', weight: 0.7 }, { name: 'fullName', weight: 0.3 }], threshold: 0.35, ignoreLocation: true }),
    [components],
  );
  const typesRows = useMemo(() => {
    const base = deferredQuery.trim().length === 0 ? components : typesFuse.search(deferredQuery).map((r) => r.item);
    return sortComponents(applyComponentFilters(base, compFilters), sortKey, sortDir);
  }, [components, typesFuse, deferredQuery, compFilters, sortKey, sortDir]);

  // Track the tree viewport height (react-arborist is virtualized → needs an explicit height).
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const obs = new ResizeObserver(([entry]) => setTreeHeight(entry.contentRect.height));
    obs.observe(el);
    return () => obs.disconnect();
  }, []);

  // Open matching branches when filtering so matches aren't buried under collapsed archetypes (rAF-deferred
  // so it runs after react-arborist reconciles the filtered data).
  useEffect(() => {
    if (!deferredQuery.trim()) return;
    const id = requestAnimationFrame(() => treeRef.current?.openAll());
    return () => cancelAnimationFrame(id);
  }, [deferredQuery, archTree]);

  const toggleSort = (key: ComponentSortKey) => {
    if (sortKey === key) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortKey(key);
      setSortDir('asc');
    }
  };

  const count = mode === 'archetypes' ? `${archTree.length} of ${archetypes.length} archetypes` : `${typesRows.length} of ${components.length} types`;

  return (
    <div data-testid="schema-explorer" className="flex h-full w-full flex-col overflow-hidden bg-background">
      <div className="wb-pane-header flex items-center gap-2 border-b border-border px-2 py-1.5">
        <Input
          placeholder={mode === 'archetypes' ? 'Search archetypes / components…' : 'Search component types…'}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          className="h-6 flex-1 text-[12px]"
        />
        <div className="flex shrink-0 overflow-hidden rounded border border-border">
          <ModeButton active={mode === 'archetypes'} onClick={() => setMode('archetypes')} icon={Layers} label="Archetypes" />
          <ModeButton active={mode === 'types'} onClick={() => setMode('types')} icon={Table2} label="Types" />
        </div>
        <button
          type="button"
          onClick={refresh}
          title="Refresh schema"
          aria-label="Refresh schema"
          className="flex h-5 w-5 shrink-0 items-center justify-center rounded text-muted-foreground hover:bg-muted hover:text-foreground"
        >
          <RefreshCw className={`h-3 w-3 ${isFetching ? 'animate-spin' : ''}`} />
        </button>
      </div>

      <div className="flex flex-wrap items-center gap-1 border-b border-border px-2 py-1">
        {mode === 'archetypes' ? (
          <>
            <FilterChip label="No entities" active={!!archFilters.noEntities} onClick={() => setArchFilters((f) => ({ ...f, noEntities: !f.noEntities }))} />
            <FilterChip label="Legacy" active={!!archFilters.legacy} onClick={() => setArchFilters((f) => ({ ...f, legacy: !f.legacy }))} />
          </>
        ) : (
          <>
            <FilterChip label="No entities" active={!!compFilters.noEntities} onClick={() => setCompFilters((f) => ({ ...f, noEntities: !f.noEntities }))} />
            <FilterChip label="Indexed" active={!!compFilters.indexed} onClick={() => setCompFilters((f) => ({ ...f, indexed: !f.indexed }))} />
            <FilterChip label="Large (≥128B)" active={!!compFilters.large} onClick={() => setCompFilters((f) => ({ ...f, large: !f.large }))} />
          </>
        )}
        <span className="ml-auto text-[11px] text-muted-foreground">{count}</span>
      </div>

      <div ref={containerRef} className="min-h-0 flex-1 overflow-hidden" tabIndex={-1}>
        {isError && <p className="p-3 text-[12px] text-destructive">Failed to load schema.</p>}
        {isLoading && archetypes.length === 0 && components.length === 0 && (
          <p className="p-3 text-[12px] text-muted-foreground">Loading schema…</p>
        )}
        {isEmpty && <p className="p-3 text-[12px] text-muted-foreground">No schema registered in this session.</p>}

        {!isError && !isEmpty && mode === 'archetypes' && (
          <Tree
            ref={treeRef}
            data={archTree}
            idAccessor="id"
            childrenAccessor={(d) => (d.kind === 'archetype' && d.children.length > 0 ? d.children : null)}
            rowHeight={rowHeight}
            openByDefault={false}
            width="100%"
            height={treeHeight}
            indent={0}
          >
            {SchemaNodeRow}
          </Tree>
        )}

        {!isError && !isEmpty && mode === 'types' && (
          <div className="h-full overflow-auto">
            <Table className="text-[12px]">
              <TableHeader>
                <TableRow>
                  <SortableHead sortKey="typeName" label="Name" current={sortKey} dir={sortDir} onClick={toggleSort} />
                  <SortableHead sortKey="storageSize" label="Size" current={sortKey} dir={sortDir} onClick={toggleSort} />
                  <SortableHead sortKey="fieldCount" label="Fields" current={sortKey} dir={sortDir} onClick={toggleSort} />
                  <SortableHead sortKey="archetypeCount" label="Used in" current={sortKey} dir={sortDir} onClick={toggleSort} />
                  <SortableHead sortKey="entityCount" label="Entities" current={sortKey} dir={sortDir} onClick={toggleSort} />
                  <SortableHead sortKey="indexCount" label="Indexes" current={sortKey} dir={sortDir} onClick={toggleSort} />
                </TableRow>
              </TableHeader>
              <TableBody>
                {typesRows.map((c) => (
                  <TableRow
                    key={c.fullName}
                    className="cursor-pointer hover:bg-accent"
                    data-testid="schema-explorer-type-row"
                    data-type-name={c.typeName}
                    onClick={() => select('component', c.typeName)}
                    onDoubleClick={() => {
                      select('component', c.typeName);
                      openComponentInspector();
                    }}
                  >
                    <TableCell className="py-1 font-mono">
                      <div className="flex flex-col">
                        <span className="text-foreground">{c.typeName}</span>
                        {c.fullName !== c.typeName && <span className="text-[10px] text-muted-foreground">{c.fullName}</span>}
                      </div>
                    </TableCell>
                    <TableCell className="py-1 text-right font-mono tabular-nums">{c.storageSize}B</TableCell>
                    <TableCell className="py-1 text-right tabular-nums">{c.fieldCount}</TableCell>
                    <TableCell className="py-1 text-right tabular-nums">{c.archetypeCount ?? '—'}</TableCell>
                    <TableCell className="py-1 text-right tabular-nums">{c.entityCount.toLocaleString()}</TableCell>
                    <TableCell className="py-1 text-right tabular-nums">{c.indexCount}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </div>
    </div>
  );
}

function ModeButton({ active, onClick, icon: Icon, label }: { active: boolean; onClick: () => void; icon: React.ElementType; label: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      title={label}
      className={`flex items-center gap-1 px-2 py-0.5 text-[11px] ${active ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-muted'}`}
    >
      <Icon className="h-3 w-3" />
      {label}
    </button>
  );
}

function FilterChip({ label, active, onClick }: { label: string; active: boolean; onClick: () => void }) {
  return (
    <Badge variant={active ? 'default' : 'outline'} className="cursor-pointer select-none px-2 py-0 text-[11px]" onClick={onClick}>
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
  sortKey: ComponentSortKey;
  label: string;
  current: ComponentSortKey;
  dir: SortDir;
  onClick: (key: ComponentSortKey) => void;
}) {
  const active = current === sortKey;
  return (
    <TableHead className="cursor-pointer select-none py-1 text-[11px]" onClick={() => onClick(sortKey)}>
      <span className="inline-flex items-center gap-1">
        {label}
        {active && (dir === 'asc' ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />)}
      </span>
    </TableHead>
  );
}

// Re-exported for the (future) Archetype Inspector storage-strategy pill parity; kept local for now.
export function StorageModePill({ mode }: { mode: StorageMode }) {
  return mode === 'cluster' ? (
    <StatusBadge tone="success">cluster</StatusBadge>
  ) : (
    <StatusBadge tone="warn">legacy</StatusBadge>
  );
}
