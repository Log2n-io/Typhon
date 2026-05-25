import { useEffect, useMemo, useRef } from 'react';
import { Clock, Link, Unlink, Network } from 'lucide-react';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore, selectEffectiveScope } from '@/stores/useProfilerViewStore';
import { timeToTickRange } from '@/panels/SystemDag/tickRangeMapping';
import { ExecutionInspectorTable } from './ExecutionInspectorTable';
import { toNumber } from './numeric';
import { jumpToTimeRange } from '@/shell/commands/profilerCommands';
import { formatNs } from './format';
import { useQueryAnalyzerStore } from './useQueryAnalyzerStore';

/**
 * Executions tab body (design §4.4). Two-pane: a thin execution list (left) + the per-phase
 * breakdown (right, reusing the prop-driven {@link ExecutionInspectorTable}). The list is bound to
 * the **global time window** (GAP-11) — when linked (default) it shows only executions whose tick
 * falls in the profiler's effective scope (`selectEffectiveScope`), filtered client-side via the
 * same `timeToTickRange` mapping the scheduling cluster uses; an unlink toggle reverts to whole-trace.
 *
 * Selection + the unlink flag live in the unified {@link useQueryAnalyzerStore}; the old
 * `ExecutionInspectorList`/`useExecutionInspectorStore` (store-coupled) are superseded here.
 */
interface Props {
  executions: QueryExecutionDto[];
  systemNames: Map<number, string>;
}

export function QueryExecutionsTab({ executions, systemNames }: Props) {
  const linked = useQueryAnalyzerStore((s) => s.execScopeLinked);
  const setLinked = useQueryAnalyzerStore((s) => s.setExecScopeLinked);
  const selected = useQueryAnalyzerStore((s) => s.selectedExecution);
  const setSelectedExecution = useQueryAnalyzerStore((s) => s.setSelectedExecution);
  const showExecutionInPlan = useQueryAnalyzerStore((s) => s.showExecutionInPlan);

  const effective = useProfilerViewStore(selectEffectiveScope);
  const tickSummaries = useProfilerSessionStore((s) => s.metadata?.tickSummaries ?? null);

  const filtered = useMemo(() => {
    if (!linked) return executions;
    // Degenerate window ({0,0} = "nothing selected") → whole trace rather than a confusing blank list.
    if (effective.endUs <= effective.startUs) return executions;
    const range = timeToTickRange(effective, tickSummaries);
    if (!range) return []; // a real window that overlaps no tick → genuinely empty for this scope.
    return executions.filter((e) => {
      const t = toNumber(e.tickIndex);
      return t >= range.from && t <= range.to;
    });
  }, [executions, linked, effective, tickSummaries]);

  // Keep a valid selection: default to the first row whenever the current pick isn't in the visible set.
  useEffect(() => {
    const present =
      selected != null &&
      filtered.some((e) => toNumber(e.tickIndex) === selected.tickIndex && toNumber(e.systemId) === selected.systemId);
    if (!present && filtered.length > 0) {
      setSelectedExecution({ tickIndex: toNumber(filtered[0].tickIndex), systemId: toNumber(filtered[0].systemId) });
    }
  }, [filtered, selected, setSelectedExecution]);

  const selectedDto = useMemo(() => {
    if (!selected) return null;
    return (
      filtered.find((e) => toNumber(e.tickIndex) === selected.tickIndex && toNumber(e.systemId) === selected.systemId) ??
      null
    );
  }, [filtered, selected]);

  return (
    <div className="flex h-full w-full flex-col overflow-hidden">
      <div className="flex items-center gap-2 border-b border-border bg-card px-3 py-1.5">
        <button
          type="button"
          onClick={() => setLinked(!linked)}
          className={`inline-flex items-center gap-1 rounded border border-border px-2 py-0.5 text-fs-sm ${linked ? 'text-foreground' : 'text-muted-foreground'}`}
          title={linked ? 'Following the timeline window — click to show all executions' : 'Showing all executions — click to follow the timeline window'}
          data-testid="query-executions-scope-toggle"
          aria-pressed={linked}
        >
          {linked ? <Link className="h-3 w-3" /> : <Unlink className="h-3 w-3" />}
          {linked ? 'Window' : 'All'}
        </button>
        <span className="text-fs-sm text-muted-foreground tabular-nums" data-testid="query-executions-count">
          {linked && filtered.length !== executions.length ? `${filtered.length} / ${executions.length}` : `${executions.length}`}
        </span>
        <div className="flex-1" />
        <button
          type="button"
          onClick={() => selectedDto && jumpToTimeRange(toNumber(selectedDto.startTs) / 1000, toNumber(selectedDto.endTs) / 1000)}
          disabled={!selectedDto}
          className="inline-flex items-center gap-1 rounded border border-border px-2 py-0.5 text-fs-sm text-muted-foreground hover:text-foreground disabled:opacity-40"
          title="Narrow the timeline's global window to this execution"
          data-testid="query-executions-jump-to-time"
        >
          <Clock className="h-3 w-3" />
          Jump to time
        </button>
        <button
          type="button"
          onClick={() => selected && showExecutionInPlan(selected)}
          disabled={!selected}
          className="inline-flex items-center gap-1 rounded border border-border px-2 py-0.5 text-fs-sm text-muted-foreground hover:text-foreground disabled:opacity-40"
          title="Overlay this execution's actual stats on the Plan graph"
          data-testid="query-executions-show-in-plan"
        >
          <Network className="h-3 w-3" />
          Show in plan
        </button>
      </div>
      <div className="flex min-h-0 flex-1 overflow-hidden">
        <ExecutionScopeList
          executions={filtered}
          systemNames={systemNames}
          selectedTickIndex={selected?.tickIndex ?? null}
          selectedSystemId={selected?.systemId ?? null}
          onSelect={(tickIndex, systemId) => setSelectedExecution({ tickIndex, systemId })}
        />
        <div className="flex-1 overflow-auto" data-testid="query-executions-detail">
          {selectedDto ? (
            <ExecutionInspectorTable execution={selectedDto} />
          ) : (
            <div className="p-3 text-fs-base text-muted-foreground">
              {filtered.length === 0 ? 'No executions in the current time window.' : 'Pick an execution from the list.'}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

/**
 * Thin execution list driving the unified store (the prop-coupled replacement for the old
 * `ExecutionInspectorList`, which was hard-wired to `useExecutionInspectorStore`). Same row shape:
 * tick · system · wall-time, with the selected row highlighted and scrolled into view.
 */
function ExecutionScopeList({
  executions,
  systemNames,
  selectedTickIndex,
  selectedSystemId,
  onSelect,
}: {
  executions: QueryExecutionDto[];
  systemNames: Map<number, string>;
  selectedTickIndex: number | null;
  selectedSystemId: number | null;
  onSelect: (tickIndex: number, systemId: number) => void;
}) {
  const selectedRef = useRef<HTMLButtonElement | null>(null);
  useEffect(() => {
    // Optional-call: jsdom (test env) doesn't implement scrollIntoView — a no-op there, real elsewhere.
    selectedRef.current?.scrollIntoView?.({ block: 'nearest', behavior: 'auto' });
  }, [selectedTickIndex, selectedSystemId]);

  if (executions.length === 0) {
    return (
      <div className="flex h-full w-[220px] shrink-0 items-center justify-center border-r border-border bg-muted/10 text-fs-sm text-muted-foreground">
        No executions.
      </div>
    );
  }

  return (
    <div className="h-full w-[220px] shrink-0 overflow-y-auto border-r border-border bg-card text-fs-base">
      <ul className="divide-y divide-border/50">
        {executions.map((e) => {
          const tickIndex = toNumber(e.tickIndex);
          const systemId = toNumber(e.systemId);
          const startTs = toNumber(e.startTs);
          const endTs = toNumber(e.endTs);
          const isSelected = selectedTickIndex === tickIndex && selectedSystemId === systemId;
          const systemLabel = systemId < 0 ? '<unattributed>' : (systemNames.get(systemId) ?? `System[${systemId}]`);
          return (
            <li key={`${tickIndex}-${systemId}`}>
              <button
                ref={isSelected ? selectedRef : undefined}
                type="button"
                onClick={() => onSelect(tickIndex, systemId)}
                className={`flex w-full flex-col gap-0.5 px-2 py-1.5 text-left hover:bg-accent ${isSelected ? 'bg-accent/60' : ''}`}
                data-testid="execution-list-row"
                data-tick-index={tickIndex}
                data-system-id={systemId}
                aria-selected={isSelected}
              >
                <span className="font-mono text-fs-sm text-foreground">tick {tickIndex.toLocaleString()}</span>
                <span className="truncate text-fs-sm text-muted-foreground">{systemLabel}</span>
                <span className="font-mono text-fs-xs text-muted-foreground">{formatNs(Math.max(0, endTs - startTs))}</span>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
