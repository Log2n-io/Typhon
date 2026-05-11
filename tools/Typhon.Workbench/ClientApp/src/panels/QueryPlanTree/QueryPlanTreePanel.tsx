import { useMemo } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useGetApiSessionsSessionIdProfilerMetadata } from '@/api/generated/profiler/profiler';
import { useSessionStore } from '@/stores/useSessionStore';
import { toNumber } from '../QueryCatalog/numeric';
import QueryPlanGraph from './QueryPlanGraph';
import QueryPlanToolbar from './QueryPlanToolbar';
import { useQueryPlan } from './useQueryPlan';
import { useQueryPlanStore } from './useQueryPlanStore';

/**
 * Workbench Query Plan Tree panel — graphical view of a single query definition (issue #339, P6 of
 * the Query Profiling umbrella #342). Reads the focus / mode from {@link useQueryPlanStore}; opened
 * from a Query Catalog row's "Show plan" affordance or from the (future) Execution Inspector.
 *
 * <para><b>Display modes:</b> <c>structural</c> renders the static plan (no per-execution stats);
 * <c>execution</c> overlays actual phase stats from the store's currently-selected execution. The
 * mode toggle in the toolbar swaps display without re-fetching.</para>
 */
export default function QueryPlanTreePanel(_props: IDockviewPanelProps) {
  const focus = useQueryPlanStore((s) => s.focus);
  const mode = useQueryPlanStore((s) => s.mode);
  const selectedExecution = useQueryPlanStore((s) => s.selectedExecution);
  const sessionId = useSessionStore((s) => s.sessionId);
  const sessionKind = useSessionStore((s) => s.kind);

  const { definition, isLoading, isError } = useQueryPlan(focus);
  const metadataQuery = useGetApiSessionsSessionIdProfilerMetadata(
    sessionId ?? '',
    { query: { enabled: !!sessionId, staleTime: Infinity } },
  );
  const metadata = metadataQuery.data?.data;

  // Merge ComponentType + Archetype tables — pull-mode views over an entire archetype use the
  // ArchetypeId as the descriptor's TargetComponentType. See QueryCatalogPanel for the rationale.
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

  const archetypeLookup = useMemo(
    () => (id: number) => archetypeNames.get(id) ?? `Component[${id}]`,
    [archetypeNames],
  );

  if (sessionKind !== 'trace' && sessionKind !== 'attach') {
    return (
      <CenteredMessage>
        <p>Query Plan Tree is available in Trace and Attach sessions only.</p>
      </CenteredMessage>
    );
  }

  if (!focus) {
    return (
      <CenteredMessage>
        <p>No query selected.</p>
        <p className="mt-1 text-[11px]">Open a row from the Query Catalog to view its plan.</p>
      </CenteredMessage>
    );
  }

  if (isError) {
    return (
      <CenteredMessage tone="error">
        <p>Failed to load query plan.</p>
      </CenteredMessage>
    );
  }

  if (isLoading || !definition) {
    return <CenteredMessage>Loading plan…</CenteredMessage>;
  }

  const archetypeId = toNumber(definition.targetComponentType);
  const archetypeName = archetypeLookup(archetypeId);
  const execution = mode === 'execution' ? selectedExecution : null;

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <QueryPlanToolbar definition={definition} archetypeName={archetypeName} />
      <div className="flex-1" data-testid="query-plan-canvas">
        <QueryPlanGraph definition={definition} execution={execution} archetypeName={archetypeLookup} />
      </div>
    </div>
  );
}

function CenteredMessage({ children, tone }: { children: React.ReactNode; tone?: 'error' }) {
  return (
    <div className="flex h-full w-full items-center justify-center bg-background p-4 text-center">
      <div className={tone === 'error' ? 'text-[12px] text-destructive' : 'text-[12px] text-muted-foreground'}>
        {children}
      </div>
    </div>
  );
}
