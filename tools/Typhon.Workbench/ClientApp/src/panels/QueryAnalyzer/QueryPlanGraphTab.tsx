import type { QueryDefinitionDto } from '@/api/generated/model';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import QueryPlanGraph from './QueryPlanGraph';
import type { ArchetypeLookup } from './queryPlanLayout';
import { useQueryAnalyzerStore, type QueryPlanMode } from './useQueryAnalyzerStore';

/**
 * Plan tab body — the structural/execution mode toggle + the React-Flow plan graph (reused
 * verbatim from the former Query Plan Tree panel). This module is the **lazy boundary**: it is the
 * only place `@xyflow/react` is statically imported, so `React.lazy(() => import('./QueryPlanGraphTab'))`
 * keeps React Flow out of the Query Analyzer panel's static import graph (AC10 — the cold-state view
 * stays jsdom-mountable, and the heavy graph lib loads only when the Plan tab actually renders).
 *
 * Mode + execution come from the unified {@link useQueryAnalyzerStore}; the resolved execution DTO is
 * passed in by the parent (it owns the executions fetch).
 */
export interface QueryPlanGraphTabProps {
  definition: QueryDefinitionDto;
  /** The currently-selected execution (for the execution-mode overlay), or null. */
  execution: QueryExecutionDto | null;
  archetypeLookup: ArchetypeLookup;
}

export default function QueryPlanGraphTab({ definition, execution, archetypeLookup }: QueryPlanGraphTabProps) {
  const planMode = useQueryAnalyzerStore((s) => s.planMode);
  const hasExecution = useQueryAnalyzerStore((s) => s.selectedExecution !== null);
  const setPlanMode = useQueryAnalyzerStore((s) => s.setPlanMode);

  // Only overlay actual stats in execution mode AND when an execution is resolved.
  const overlay = planMode === 'execution' ? execution : null;

  return (
    <div className="flex h-full w-full flex-col overflow-hidden">
      <div className="flex items-center gap-2 border-b border-border bg-card px-3 py-1.5">
        <ModeToggle mode={planMode} hasExecution={hasExecution} onChange={setPlanMode} />
      </div>
      <div className="min-h-0 flex-1" data-testid="query-plan-canvas">
        <QueryPlanGraph definition={definition} execution={overlay} archetypeName={archetypeLookup} />
      </div>
    </div>
  );
}

function ModeToggle({
  mode,
  hasExecution,
  onChange,
}: {
  mode: QueryPlanMode;
  hasExecution: boolean;
  onChange: (m: QueryPlanMode) => void;
}) {
  return (
    <div className="flex rounded-md border border-border" role="group" aria-label="Plan display mode">
      <button
        type="button"
        className={`px-2 py-0.5 text-fs-sm ${mode === 'structural' ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground'}`}
        onClick={() => onChange('structural')}
        data-testid="query-plan-mode-structural"
      >
        Structural
      </button>
      <button
        type="button"
        className={`px-2 py-0.5 text-fs-sm ${mode === 'execution' ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground'} disabled:opacity-40`}
        onClick={() => onChange('execution')}
        disabled={!hasExecution}
        title={hasExecution ? '' : 'Select an execution in the Executions tab to overlay per-run stats'}
        data-testid="query-plan-mode-execution"
      >
        Execution
      </button>
    </div>
  );
}
