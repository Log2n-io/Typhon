import { lazy, Suspense, useMemo } from 'react';
import type { QueryDefinitionDto } from '@/api/generated/model';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { useProfilerNameMaps } from '@/hooks/useProfilerNameMaps';
import { useExecutions } from './useExecutions';
import { toNumber } from './numeric';
import { QueryDetailHeader } from './QueryDetailHeader';
import { QueryExecutionsTab } from './QueryExecutionsTab';
import { useQueryAnalyzerStore, type QueryDetailTab } from './useQueryAnalyzerStore';

// Lazy so the React-Flow plan graph (and `@xyflow/react`) stays out of the panel's static import
// graph — the cold-state view mounts in jsdom for conformance, and the heavy lib loads on demand.
const QueryPlanGraphTab = lazy(() => import('./QueryPlanGraphTab'));

/**
 * The Query Analyzer detail (right pane): header + `Plan` / `Executions` tabs for the focused query.
 * Owns the executions fetch (one source for both tabs) and resolves the selected execution DTO that
 * the Plan tab overlays. Tab + selection state live in the unified {@link useQueryAnalyzerStore}.
 */
export function QueryDetail({ definition }: { definition: QueryDefinitionDto }) {
  const { archetypeNames, systemNames, componentTypeIds } = useProfilerNameMaps();
  const kind = toNumber(definition.instanceId.kind);
  const localId = toNumber(definition.instanceId.localId);

  const archetypeLookup = useMemo(
    () => (id: number) => archetypeNames.get(id) ?? `Component[${id}]`,
    [archetypeNames],
  );
  const targetId = toNumber(definition.targetComponentType);
  const archetypeName = archetypeLookup(targetId);
  const ownerNames = (definition.ownerSystemIds ?? [])
    .map((id) => systemNames.get(toNumber(id)))
    .filter((n): n is string => !!n);

  const { executions } = useExecutions({ kind, localId });
  const selected = useQueryAnalyzerStore((s) => s.selectedExecution);
  // Resolve against the RAW executions (not the Executions-tab time filter) so the Plan overlay shows
  // whatever the user picked regardless of the tab's window scoping.
  const selectedDto = useMemo(() => {
    if (!selected) return null;
    return (
      executions.find((e) => toNumber(e.tickIndex) === selected.tickIndex && toNumber(e.systemId) === selected.systemId) ??
      null
    );
  }, [executions, selected]);

  const activeTab = useQueryAnalyzerStore((s) => s.activeTab);
  const setActiveTab = useQueryAnalyzerStore((s) => s.setActiveTab);

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <QueryDetailHeader
        definition={definition}
        archetypeName={archetypeName}
        ownerNames={ownerNames}
        targetId={targetId}
        targetIsComponent={componentTypeIds.has(targetId)}
      />
      <Tabs
        value={activeTab}
        onValueChange={(v) => setActiveTab(v as QueryDetailTab)}
        className="flex min-h-0 flex-1 flex-col"
      >
        <TabsList className="mx-3 mt-2 h-8 w-fit shrink-0">
          <TabsTrigger value="plan" className="text-fs-sm" data-testid="query-detail-tab-plan">Plan</TabsTrigger>
          <TabsTrigger value="executions" className="text-fs-sm" data-testid="query-detail-tab-executions">Executions</TabsTrigger>
        </TabsList>
        <TabsContent value="plan" className="mt-2 min-h-0 flex-1 overflow-hidden">
          <Suspense fallback={<div className="p-3 text-fs-sm text-muted-foreground">Loading plan…</div>}>
            <QueryPlanGraphTab definition={definition} execution={selectedDto} archetypeLookup={archetypeLookup} />
          </Suspense>
        </TabsContent>
        <TabsContent value="executions" className="mt-2 min-h-0 flex-1 overflow-hidden">
          <QueryExecutionsTab executions={executions} systemNames={systemNames} />
        </TabsContent>
      </Tabs>
    </div>
  );
}
