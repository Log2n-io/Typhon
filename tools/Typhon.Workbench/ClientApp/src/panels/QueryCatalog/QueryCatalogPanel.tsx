import { useDeferredValue, useMemo } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { QueryDefinitionDto } from '@/api/generated/model';
import { useGetApiSessionsSessionIdProfilerMetadata } from '@/api/generated/profiler/profiler';
import { useSessionStore } from '@/stores/useSessionStore';
import { QueryCatalogTable } from './QueryCatalogTable';
import { QueryCatalogToolbar } from './QueryCatalogToolbar';
import { useQueryCatalogStore } from './useQueryCatalogStore';
import { useQueryDefinitions } from './useQueryDefinitions';
import { findDuplicateDefinitions } from './duplicate-detection';
import { passesFilter } from './filter';
import { toNumber } from './numeric';

/**
 * Workbench Query Catalog panel — the first user-visible artifact of the Query Profiling umbrella
 * (#342). Lists every query definition observed in the loaded trace, with filtering, search, and
 * a go-to-source link wired to <c>useOptionsStore.openInEditor</c>.
 *
 * Design doc: claude/design/Profiler/11-query-definition-export.md §5.1.
 * Issue #338 (P5 of #342).
 */
export default function QueryCatalogPanel(_props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const sessionKind = useSessionStore((s) => s.kind);
  const { definitions, isLoading, isError } = useQueryDefinitions();

  // Metadata gives us component-type and system name lookups for resolving IDs → display strings.
  const metadataQuery = useGetApiSessionsSessionIdProfilerMetadata(
    sessionId ?? '',
    { query: { enabled: !!sessionId, staleTime: Infinity } },
  );
  const metadata = metadataQuery.data?.data;

  // A query definition's TargetComponentType is either a ComponentType id (Component-WHERE queries)
  // or an Archetype id (pull-mode views over a whole archetype like tx.Query&lt;Ant&gt;().ToView()). We
  // merge both lookups — archetypes win because they're the more user-meaningful label when both
  // match, and in practice ArchetypeId range (≥100) doesn't collide with ComponentType ids.
  const archetypeNames = useMemo(() => {
    const m = new Map<number, string>();
    for (const ct of metadata?.componentTypes ?? []) {
      m.set(Number(ct.componentTypeId), ct.name ?? `Component[${ct.componentTypeId}]`);
    }
    for (const a of metadata?.archetypes ?? []) {
      m.set(Number(a.archetypeId), a.name ?? `Archetype[${a.archetypeId}]`);
    }
    return m;
  }, [metadata]);

  const systemNames = useMemo(() => {
    const m = new Map<number, string>();
    for (const sys of metadata?.systems ?? []) {
      m.set(Number(sys.index), sys.name ?? `System[${sys.index}]`);
    }
    return m;
  }, [metadata]);

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
      for (const sid of d.ownerSystemIds ?? []) ids.add(Number(sid));
    }
    return Array.from(ids)
      .map((id) => ({ id, name: systemNames.get(id) ?? `System[${id}]` }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [definitions, systemNames]);

  const search = useQueryCatalogStore((s) => s.search);
  const systemFilter = useQueryCatalogStore((s) => s.systemFilter);
  const archetypeFilter = useQueryCatalogStore((s) => s.archetypeFilter);
  const deferredSearch = useDeferredValue(search);

  const filtered: QueryDefinitionDto[] = useMemo(() => {
    const filter = {
      search: deferredSearch.trim().toLowerCase(),
      systemFilter,
      archetypeFilter,
    };
    const names = {
      archetypeName: (id: number) => archetypeNames.get(id) ?? '',
      systemName: (id: number) => systemNames.get(id) ?? '',
    };
    return definitions.filter((d) => passesFilter(d, filter, names));
  }, [definitions, deferredSearch, systemFilter, archetypeFilter, archetypeNames, systemNames]);

  const duplicateRowIds = useMemo(() => findDuplicateDefinitions(definitions), [definitions]);

  // Session-kind gate. Trace and Attach are the modes that produce query data; Open / none mean
  // no profiler context at all, render a neutral message.
  if (sessionKind !== 'trace' && sessionKind !== 'attach') {
    return (
      <CenteredMessage>
        <p>Query Catalog is available in Trace and Attach sessions only.</p>
      </CenteredMessage>
    );
  }

  if (isError) {
    return (
      <CenteredMessage tone="error">
        <p>Failed to load query catalog.</p>
      </CenteredMessage>
    );
  }

  if (isLoading) {
    return <CenteredMessage>Loading query catalog…</CenteredMessage>;
  }

  if (definitions.length === 0) {
    return (
      <CenteredMessage>
        <p>No queries were observed in this trace.</p>
        <p className="mt-1 text-fs-sm">
          Query Definition Export (issue #342) emits <code className="rounded bg-muted px-1">QueryDefinitionDescribe</code> events
          when the profiler is active and the engine runs user queries (<code className="rounded bg-muted px-1">tx.Query&lt;T&gt;()</code>,{' '}
          <code className="rounded bg-muted px-1">ToView()</code>, etc.). Older traces (v8 and earlier) don't carry this data.
        </p>
      </CenteredMessage>
    );
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <QueryCatalogToolbar
        totalCount={definitions.length}
        filteredCount={filtered.length}
        archetypeOptions={archetypeOptions}
        systemOptions={systemOptions}
      />
      <div className="flex-1 overflow-auto">
        {filtered.length === 0 ? (
          <div className="p-3 text-fs-base text-muted-foreground">No definitions match the current filters.</div>
        ) : (
          <QueryCatalogTable
            definitions={filtered}
            archetypeNames={archetypeNames}
            systemNames={systemNames}
            duplicateRowIds={duplicateRowIds}
          />
        )}
      </div>
    </div>
  );
}

function CenteredMessage({ children, tone }: { children: React.ReactNode; tone?: 'error' }) {
  return (
    <div className="flex h-full w-full items-center justify-center bg-background p-4 text-center">
      <div className={tone === 'error' ? 'text-fs-base text-destructive' : 'text-fs-base text-muted-foreground'}>
        {children}
      </div>
    </div>
  );
}
