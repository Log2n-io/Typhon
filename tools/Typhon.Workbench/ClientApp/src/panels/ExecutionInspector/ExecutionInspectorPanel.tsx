import { useEffect, useMemo } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useGetApiSessionsSessionIdProfilerMetadata } from '@/api/generated/profiler/profiler';
import { useSessionStore } from '@/stores/useSessionStore';
import { toNumber } from '../QueryCatalog/numeric';
import { useQueryPlan } from '../QueryPlanTree/useQueryPlan';
import { ExecutionInspectorList } from './ExecutionInspectorList';
import { ExecutionInspectorTable } from './ExecutionInspectorTable';
import { ExecutionInspectorToolbar } from './ExecutionInspectorToolbar';
import { useExecutions } from './useExecutions';
import { useExecutionInspectorStore } from './useExecutionInspectorStore';

/**
 * Workbench Execution Inspector — drill into per-execution phase breakdowns for a single query
 * definition (issue #340, P7 of the Query Profiling umbrella #342). Opened from a Query Catalog
 * row "Inspect executions" affordance, or from the (future) timeline-span click.
 *
 * <para><b>Layout:</b> two-pane — left sidebar lists the recent executions for the focused
 * definition; right pane shows the phase breakdown for the selected execution, plus a "Show tree"
 * hand-off to the Plan Tree panel (P6) in execution mode.</para>
 */
export default function ExecutionInspectorPanel(_props: IDockviewPanelProps) {
  const focus = useExecutionInspectorStore((s) => s.focus);
  const selected = useExecutionInspectorStore((s) => s.selected);
  const setSelected = useExecutionInspectorStore((s) => s.setSelected);
  const sessionId = useSessionStore((s) => s.sessionId);
  const sessionKind = useSessionStore((s) => s.kind);

  const { executions, isLoading: execsLoading, isError: execsError } = useExecutions(focus);
  const { definition } = useQueryPlan(focus);

  const metadataQuery = useGetApiSessionsSessionIdProfilerMetadata(
    sessionId ?? '',
    { query: { enabled: !!sessionId, staleTime: Infinity } },
  );
  const metadata = metadataQuery.data?.data;

  // Merge ComponentType + Archetype tables — see QueryCatalogPanel for the rationale.
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

  // Default selection: first execution in the list when nothing is selected yet.
  useEffect(() => {
    if (!selected && executions.length > 0) {
      setSelected({
        tickIndex: toNumber(executions[0].tickIndex),
        systemId: toNumber(executions[0].systemId),
      });
    }
  }, [selected, executions, setSelected]);

  const selectedExecution = useMemo(() => {
    if (!selected) return null;
    return executions.find(
      (e) => toNumber(e.tickIndex) === selected.tickIndex && toNumber(e.systemId) === selected.systemId,
    ) ?? null;
  }, [selected, executions]);

  if (sessionKind !== 'trace' && sessionKind !== 'attach') {
    return <CenteredMessage><p>Execution Inspector is available in Trace and Attach sessions only.</p></CenteredMessage>;
  }
  if (!focus) {
    return (
      <CenteredMessage>
        <p>No query selected.</p>
        <p className="mt-1 text-fs-sm">Open a row from the Query Catalog and choose "Inspect executions".</p>
      </CenteredMessage>
    );
  }
  if (execsError) {
    return <CenteredMessage tone="error"><p>Failed to load executions.</p></CenteredMessage>;
  }
  if (execsLoading) {
    return <CenteredMessage>Loading executions…</CenteredMessage>;
  }

  const archetypeId = definition ? toNumber(definition.targetComponentType) : -1;
  const archetypeName = archetypeId >= 0 ? (archetypeNames.get(archetypeId) ?? `Component[${archetypeId}]`) : '';
  const systemName = selectedExecution
    ? (toNumber(selectedExecution.systemId) < 0
        ? ''
        : (systemNames.get(toNumber(selectedExecution.systemId)) ?? `System[${toNumber(selectedExecution.systemId)}]`))
    : '';

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <ExecutionInspectorToolbar
        definition={definition}
        execution={selectedExecution}
        archetypeName={archetypeName}
        systemName={systemName}
      />
      <div className="flex flex-1 overflow-hidden">
        <ExecutionInspectorList executions={executions} systemNames={systemNames} />
        <div className="flex-1 overflow-auto" data-testid="execution-inspector-detail">
          {selectedExecution ? (
            <ExecutionInspectorTable execution={selectedExecution} />
          ) : (
            <div className="p-3 text-fs-base text-muted-foreground">Pick an execution from the list.</div>
          )}
        </div>
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
